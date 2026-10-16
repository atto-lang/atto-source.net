using Ores.Net.Runtime;
using Ores.Net.Compiler;
using System.Text.Json;

internal static class ActorPipelineSelectorTests
{
    private static void Check(bool ok, string why)
    {
        if (!ok) throw new Exception("Actor routing parity failure: " + why);
    }

    private static void Deny(Action action, string why)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Actor routing should reject: " + why);
    }

    internal static void Run()
    {
        // Fail-closed guest compiler contract: these source constructs
        // must not become accepted no-op routing calls before guest lowering.
        static void RejectSource(string text)
        {
            try { OresParser.Parse(text); }
            catch (FormatException) { return; }
            throw new Exception("unsupported actor source was accepted by the .NET compiler");
        }
        RejectSource("define readable pipe actor Source {}");
        RejectSource("fnc main():int{return self.forward(1);}");
        try
        {
            OresStructuredParser.Parse("fnc main():int{return self.reply(1);}");
            throw new Exception("structured compiler accepted unsupported actor reply");
        }
        catch (FormatException) { }
        var stages = new List<PipelineStage> {
            new("source","Producer",PipelineRole.Readable,"","Event"),
            new("worker-a","Worker",PipelineRole.Writable,"Event",""),
            new("worker-b","Worker",PipelineRole.Writable,"Event",""),
            new("worker-c","Worker",PipelineRole.Writable,"Event","")
        };
        var edges = new List<PipelineEdge> {
            new("source","worker-a","Event"),
            new("source","worker-b","Event"),
            new("source","worker-c","Event")
        };
        var selectors = new ActorPipelineSelector(stages,edges,7);
        // Exercise the exact machine-readable contract, not only copied
        // inline vectors. Each backend pins the same Git blob SHA.
        using (var shared = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "actor-pipeline-routing-v1.json"))))
        {
            Check(shared.RootElement.GetProperty("schema").GetString() ==
                "oreslang.actor-pipeline-routing.v1", "contract schema drift");
            foreach (var item in shared.RootElement.GetProperty("operations")
                         .GetProperty("by_key").EnumerateArray())
            {
                string key = item.GetProperty("key").GetString()!;
                string expected = item.GetProperty("target").GetString()!;
                Check(selectors.ForwardByKey("source",key).Target == expected,
                    "shared JSON key-affinity fixture mismatch");
            }
        }
        foreach (var (key, slot) in new[] {
            ("customer-7",0),("alpha",0),("beta",2),("🌍",0),("",2),("\0",0)
        })
        {
            Check(ActorPipelineKey.Slot(key,3)==slot,"unsigned FNV-1a vector");
            Check(selectors.ForwardByKey("source",key).Target==edges[slot].Target,
                "key affinity");
        }
        Check(selectors.ForwardAll("source").Count==3,"fan-out");
        selectors.RequireIngressRoot("source");
        Deny(()=>selectors.RequireIngressRoot("worker-a"),"forged ingress root");
        Deny(()=>selectors.Forward("source"),"ambiguous single forward");
        Check(selectors.ForwardTo("source","worker-b").Target=="worker-b",
            "named destination");
        Deny(()=>selectors.ForwardTo("source","worker-z"),"unknown connected target");
        Deny(()=>selectors.ForwardTo("worker-a","source"),"reverse edge");
        for (int i=0;i<999;i++)
            Check(selectors.ForwardRoundRobin("source").Target==edges[i%3].Target,
                "round-robin stable connection order");
        Deny(()=>selectors.AdvanceGeneration(8,false,false),"unfenced epoch");
        Check(selectors.Generation==7,"failed generation transition mutated state");
        selectors.AdvanceGeneration(8,true,false);
        Check(selectors.ForwardRoundRobin("source").Target=="worker-a",
            "generation resets source cursor");
        Deny(()=>selectors.AdvanceGeneration(8,true,false),"non-increasing epoch");
        Deny(()=>ActorPipelineKey.Slot("\ud800",3),"surrogate key");
        Deny(()=>ActorPipelineKey.Slot(new string('x',4097),3),"oversized key");
        Deny(()=>ActorPipelineKey.Slot("x",0),"zero connected edges");
        var duplicate = new List<PipelineStage>(stages) { stages[1] };
        Deny(()=>new ActorPipelineSelector(duplicate,edges),"duplicate instance IDs");
        duplicate = new List<PipelineStage>(stages);
        duplicate[1] = duplicate[1] with {Id="\0bad"};
        Deny(()=>new ActorPipelineSelector(duplicate,edges),"NUL stage identity");
        duplicate[1] = duplicate[1] with {Id=new string('é',129)};
        Deny(()=>new ActorPipelineSelector(duplicate,edges),"UTF-8 name byte bound");
        var wrongEdges = new List<PipelineEdge>(edges) { edges[0] };
        Deny(()=>new ActorPipelineSelector(stages,wrongEdges),"duplicate edge");
        wrongEdges = new List<PipelineEdge>(edges);
        wrongEdges[0] = wrongEdges[0] with {Payload="Different"};
        Deny(()=>new ActorPipelineSelector(stages,wrongEdges),"nominal type mismatch");
        var cycles = new List<PipelineStage> {
            new("root","Root",PipelineRole.Readable,"","X"),
            new("a","Node",PipelineRole.Duplex,"X","X"),
            new("b","Node",PipelineRole.Duplex,"X","X")
        };
        var cycleEdges = new List<PipelineEdge> {
            new("root","a","X"),new("a","b","X"),new("b","a","X")
        };
        Deny(()=>new ActorPipelineSelector(cycles,cycleEdges),"cyclic graph");
        // Source+generation RR is one serialized cursor even when multiple
        // host threads attempt selection concurrently. This is NOT proof
        // of parallel guest actor execution.
        var concurrent = new ActorPipelineSelector(stages,edges,101);
        var counts = new int[3];
        var runners = Enumerable.Range(0,8).Select(_ => Task.Run(() => {
            for (int i=0;i<1000;i++)
            {
                var target = concurrent.ForwardRoundRobin("source").Target;
                int index = target switch {
                    "worker-a" => 0, "worker-b" => 1, "worker-c" => 2,
                    _ => throw new Exception("unexpected RR target")
                };
                Interlocked.Increment(ref counts[index]);
            }
        })).ToArray();
        Task.WaitAll(runners);
        Check(counts.Sum()==8000 && counts.Max()-counts.Min() <= 1,
            "concurrent RR lost or duplicated selection turns");
        Check(concurrent.Generation==101, "concurrent RR changed generation");
        concurrent.AdvanceGeneration(102,true,false);
        Check(concurrent.ForwardRoundRobin("source").Target=="worker-a",
            "generation barrier did not reset concurrent RR cursor");

        // STOP is an irreversible authority barrier. Every selection and
        // future generation transition fails after retirement.
        var retiring = new ActorPipelineSelector(stages,edges,11);
        retiring.Retire();
        static void DenyRetired(Action action)
        {
            try { action(); }
            catch (InvalidOperationException error) when (
                error.Message.Contains("retired", StringComparison.Ordinal)) { return; }
            throw new Exception("Retired actor selector retained routing authority");
        }
        DenyRetired(()=>retiring.Forward("source"));
        DenyRetired(()=>retiring.ForwardAll("source"));
        DenyRetired(()=>retiring.ForwardRoundRobin("source"));
        DenyRetired(()=>retiring.ForwardTo("source","worker-a"));
        DenyRetired(()=>retiring.ForwardByKey("source","alpha"));
        DenyRetired(()=>retiring.RequireIngressRoot("source"));
        DenyRetired(()=>retiring.AdvanceGeneration(12,true,false));
        DenyRetired(()=>retiring.AdvanceGeneration(12,false,true));
        Check(retiring.Generation==11,"STOP mutated routing generation");
        retiring.Retire(); // idempotent terminal transition
        // Race host selection with STOP. An operation that began before
        // retirement may linearize either way; each of the 8,000 operations
        // deliberately begun AFTER Retire() returns must fail without
        // entering a new generation or consuming an RR turn.
        var racing = new ActorPipelineSelector(stages,edges,21);
        int ready = 0, attempts = 0, stopPublished = 0;
        int acceptedAfter = 0, deniedAfter = 0;
        using (var startGate = new ManualResetEventSlim(false))
        {
            var racers = Enumerable.Range(0,8).Select(t => Task.Run(() => {
                Interlocked.Increment(ref ready);
                startGate.Wait();
                while (Volatile.Read(ref stopPublished) == 0)
                {
                    try {
                        if ((t & 1) == 0) racing.ForwardRoundRobin("source");
                        else racing.ForwardTo("source","worker-a");
                    }
                    catch (InvalidOperationException) { /* STOP raced */ }
                    Interlocked.Increment(ref attempts);
                }
                for (int i=0;i<1000;i++)
                {
                    try
                    {
                        switch (i % 4)
                        {
                            case 0: racing.ForwardAll("source"); break;
                            case 1: racing.ForwardRoundRobin("source"); break;
                            case 2: racing.ForwardTo("source","worker-b"); break;
                            default: racing.ForwardByKey("source","alpha"); break;
                        }
                        Interlocked.Increment(ref acceptedAfter);
                    }
                    catch (InvalidOperationException error) when (
                        error.Message.Contains("retired",StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref deniedAfter);
                    }
                }
            })).ToArray();
            Check(SpinWait.SpinUntil(
                () => Volatile.Read(ref ready)==8,TimeSpan.FromSeconds(10)),
                "concurrent STOP workers did not start");
            startGate.Set();
            Check(SpinWait.SpinUntil(
                () => Volatile.Read(ref attempts)>=800,TimeSpan.FromSeconds(10)),
                "STOP race did not execute initial selections");
            racing.Retire();
            Volatile.Write(ref stopPublished,1);
            Task.WaitAll(racers);
        }
        Check(acceptedAfter==0 && deniedAfter==8000,
            "retired .NET selector admitted a post-STOP route");
        DenyRetired(()=>racing.AdvanceGeneration(22,true,false));
        Check(racing.Generation==21,
            "retired .NET selector reopened a stale generation");


        // Original caller collections cannot mutate the snapshotted routes.
        edges[0] = edges[0] with {Target="imposter"};
        Check(selectors.ForwardAll("source")[0].Target=="worker-a",
            "selector graph snapshot changed after caller mutation");
    }
}
