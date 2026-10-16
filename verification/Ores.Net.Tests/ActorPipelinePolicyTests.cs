using Ores.Net.Runtime;
using System.Text.Json;

public static class ActorPipelinePolicyTests
{
    public static void Run()
    {
        static void Check(bool yes, string message)
        {
            if (!yes) throw new Exception("Actor routing parity: " + message);
        }
        static void Reject(Action action)
        {
            try { action(); } catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            throw new Exception("Missing actor routing rejection");
        }
        var stages = new List<ActorPipelinePolicy.Stage> {
            new("src", "Producer", "readable", "", "Event"),
            new("worker-a", "Worker", "writable", "Event", ""),
            new("worker-b", "Worker", "writable", "Event", ""),
            new("worker-c", "Worker", "writable", "Event", "")
        };
        var edges = new List<ActorPipelinePolicy.Edge> {
            new("src", "worker-a", "Event"), new("src", "worker-b", "Event"),
            new("src", "worker-c", "Event")
        };
        var p = new ActorPipelinePolicy(stages, edges);
        Check(string.Join(",", p.ForwardAll("src").Select(x => x.Target)) ==
              "worker-a,worker-b,worker-c", "fanout");
        for (int i = 0; i < 12; i++)
            Check(p.ForwardRoundRobin("src").Target ==
                "worker-" + "abc"[i % 3], "round robin");
        Reject(() => p.Forward("src"));
        Reject(() => p.ForwardTo("src", "unknown"));
        Check(p.ForwardTo("src", "worker-b").Target == "worker-b", "explicit target");
        foreach (var (key, destination, hash) in new[] {
            ("customer-7", "worker-a", 0x660308ab83c6fdf1UL),
            ("alpha", "worker-a", 0x8ac625bb85ed202bUL),
            ("beta", "worker-c", 0x7627619b954620a7UL),
            ("🌍", "worker-a", 0xff430438753bca3bUL),
            ("", "worker-c", 0xcbf29ce484222325UL)
        })
        {
            Check(ActorPipelinePolicy.HashKey(key) == hash, "hash bytes");
            Check(p.ForwardByKey("src", key).Target == destination, "key route");
        }
        var duplicate = edges.Concat(new[] { edges[0] }).ToArray();
        Reject(() => new ActorPipelinePolicy(stages, duplicate));
        Reject(() => new ActorPipelinePolicy(stages, new[] {
            new ActorPipelinePolicy.Edge("src", "worker-a", "Wrong") }));
        Reject(() => new ActorPipelinePolicy(withDuplicateId(), edges));
        Reject(() => ActorPipelinePolicy.HashKey("\ud800"));
        Reject(() => ActorPipelinePolicy.HashKey(new string('x', 4097)));
        var cycles = new[] {
            new ActorPipelinePolicy.Stage("a", "Worker", "transform", "Event", "Event"),
            new ActorPipelinePolicy.Stage("b", "Worker", "transform", "Event", "Event")
        };
        Reject(() => new ActorPipelinePolicy(cycles, new[] {
            new ActorPipelinePolicy.Edge("a", "b", "Event"),
            new ActorPipelinePolicy.Edge("b", "a", "Event") }));
        p.Close();
        Reject(() => p.ForwardAll("src"));
        Check(new ActorPipelinePolicy(stages, edges).ForwardRoundRobin("src").Target ==
              "worker-a", "new generation resets cursor");
        var pEdges = new[] { "worker-a", "worker-b", "worker-c" };
        using (var golden = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "actor-pipeline-routing-v1.json"))))
        {
            var operations = golden.RootElement.GetProperty("operations");
            var selected = operations.GetProperty("forward_all").EnumerateArray()
                .Select(x => x.GetString()).ToArray();
            Check(selected.SequenceEqual(pEdges), "canonical fixture fan-out order");
            var rr = operations.GetProperty("forward_round_robin").EnumerateArray()
                .Select(x => x.GetString()).ToArray();
            Check(rr.SequenceEqual(Enumerable.Range(0, 6)
                .Select(i => "worker-" + "abc"[i % 3])), "canonical fixture RR");
            foreach (var item in operations.GetProperty("by_key").EnumerateArray())
            {
                string key = item.GetProperty("key").GetString()!;
                string expectedHash = item.GetProperty("fnv1a64_hex").GetString()!;
                string expectedTarget = item.GetProperty("target").GetString()!;
                Check(ActorPipelinePolicy.HashKey(key).ToString("x16") == expectedHash,
                    "canonical fixture key hash");
                Check(new ActorPipelinePolicy(stages, edges).ForwardByKey("src", key).Target ==
                    expectedTarget, "canonical fixture keyed target");
            }
        }
        // Boundary/ownership regressions: stage identifiers are bounded,
        // strict UTF-8, but routing keys may contain NUL and be 4096 bytes.
        Check(ActorPipelinePolicy.HashKey("\0") == 0xaf63bd4c8601b7dfUL,
            "NUL must be hashed as a byte, not treated as terminator");
        _ = ActorPipelinePolicy.HashKey(string.Concat(Enumerable.Repeat("🌍", 1024)));
        Reject(() => ActorPipelinePolicy.HashKey(string.Concat(Enumerable.Repeat("🌍", 1025))));
        Reject(() => ActorPipelinePolicy.HashKey("\udc00"));
        var unicodeStages = stages.Select((stage, i) =>
            i == 0 ? stage with { Id = new string('é', 128) } : stage).ToArray();
        var unicodeEdges = edges.Select(edge =>
            edge with { Source = new string('é', 128) }).ToArray();
        _ = new ActorPipelinePolicy(unicodeStages, unicodeEdges);
        Reject(() => new ActorPipelinePolicy(
            unicodeStages.Select((stage, i) =>
                i == 0 ? stage with { Id = new string('é', 129) } : stage).ToArray(),
            unicodeEdges));
        Reject(() => new ActorPipelinePolicy(
            stages.Select((stage, i) =>
                i == 0 ? stage with { Id = "src\0spoof" } : stage).ToArray(),
            edges));
        // Two classes of mutable input are copied on publication.
        var mutableEdges = edges.ToList();
        var frozenPlan = new ActorPipelinePolicy(stages, mutableEdges);
        mutableEdges[0] = mutableEdges[0] with { Target = "evil" };
        mutableEdges.Add(edges[0]);
        Check(frozenPlan.ForwardAll("src")[0].Target == "worker-a",
            "publisher mutation changed accepted route");
        Check(frozenPlan.ForwardAll("src") is not IList<ActorPipelinePolicy.Edge> published
              || published.IsReadOnly, "publisher can mutate route receipts");
        var counts = new int[3];
        for (int i = 0; i < 12000; i++)
        {
            var target = frozenPlan.ForwardRoundRobin("src").Target;
            counts[target[^1] - 'a']++;
        }
        Check(counts.All(x => x == 4000), "12k round robin selections not fair");
        var diamondStages = new[] {
            new ActorPipelinePolicy.Stage("root", "P", "readable", "", "Event"),
            new ActorPipelinePolicy.Stage("a", "W", "transform", "Event", "Event"),
            new ActorPipelinePolicy.Stage("b", "W", "transform", "Event", "Event"),
            new ActorPipelinePolicy.Stage("sink", "S", "writable", "Event", "")
        };
        var diamondEdges = new[] {
            new ActorPipelinePolicy.Edge("root", "a", "Event"),
            new ActorPipelinePolicy.Edge("root", "b", "Event"),
            new ActorPipelinePolicy.Edge("a", "sink", "Event"),
            new ActorPipelinePolicy.Edge("b", "sink", "Event")
        };
        Check(new ActorPipelinePolicy(diamondStages, diamondEdges)
            .ForwardAll("root").Count == 2, "valid diamond DAG rejected");
        Reject(() => new ActorPipelinePolicy(diamondStages,
            diamondEdges.Concat(new[] {
                new ActorPipelinePolicy.Edge("a", "b", "Event"),
                new ActorPipelinePolicy.Edge("b", "a", "Event") }).ToArray()));
        Reject(() => new ActorPipelinePolicy(diamondStages,
            diamondEdges.Concat(new[] {
                new ActorPipelinePolicy.Edge("a", "a", "Event") }).ToArray()));
        // The connected source and generation are checked before key parsing.
        try
        {
            frozenPlan.ForwardByKey("unconnected-source", "\ud800");
            throw new Exception("unknown source accepted");
        }
        catch (ArgumentException denied) when (denied.Message.Contains("Source has no downstream"))
        {
        }
        frozenPlan.Close();
        Reject(() => frozenPlan.ForwardRoundRobin("src"));
        try
        {
            frozenPlan.ForwardByKey("src", "\ud800");
            throw new Exception("retired source accepted");
        }
        catch (InvalidOperationException denied) when (denied.Message.Contains("retired"))
        {
        }
        // Tuple endpoint identity, not delimiter-concatenated labels.
        var trickyStages = new[] {
            new ActorPipelinePolicy.Stage("a:b", "Producer", "readable", "", "E"),
            new ActorPipelinePolicy.Stage("a", "Producer", "readable", "", "E"),
            new ActorPipelinePolicy.Stage("c", "Sink", "writable", "E", ""),
            new ActorPipelinePolicy.Stage("b:c", "Sink", "writable", "E", "")
        };
        var trickyEdges = new[] {
            new ActorPipelinePolicy.Edge("a:b", "c", "E"),
            new ActorPipelinePolicy.Edge("a", "b:c", "E")
        };
        var trickyPlan = new ActorPipelinePolicy(trickyStages, trickyEdges);
        Check(trickyPlan.Forward("a:b").Target == "c", "colon-tuple collision source one");
        Check(trickyPlan.Forward("a").Target == "b:c", "colon-tuple collision source two");
        Reject(() => new ActorPipelinePolicy(trickyStages,
            trickyEdges.Concat(new[] { trickyEdges[0] }).ToArray()));

        // UTF-8 identity is byte-exact: composed and decomposed labels differ.
        var unicodeIdentity = new[] {
            trickyStages[0] with { Id = "é" },
            trickyStages[1] with { Id = "e\u0301" },
            trickyStages[2], trickyStages[3]
        };
        var unicodeIdentityEdges = new[] {
            trickyEdges[0] with { Source = "é" },
            trickyEdges[1] with { Source = "e\u0301" }
        };
        var unicodeIdentityPlan =
            new ActorPipelinePolicy(unicodeIdentity, unicodeIdentityEdges);
        Check(unicodeIdentityPlan.Forward("é").Target == "c",
            "composed instance identity");
        Check(unicodeIdentityPlan.Forward("e\u0301").Target == "b:c",
            "decomposed instance identity");
        Reject(() => new ActorPipelinePolicy(
            Enumerable.Repeat(trickyStages[0], 1025).ToArray(), trickyEdges));
        Reject(() => new ActorPipelinePolicy(trickyStages,
            Enumerable.Repeat(trickyEdges[0], 4097).ToArray()));
        var max256 = string.Concat(Enumerable.Repeat("🌍", 64));
        var boundaryStages = trickyStages.ToArray();
        boundaryStages[0] = boundaryStages[0] with { Id = max256 };
        var boundaryEdges = trickyEdges.ToArray();
        boundaryEdges[0] = boundaryEdges[0] with { Source = max256 };
        Check(new ActorPipelinePolicy(boundaryStages, boundaryEdges)
            .Forward(max256).Target == "c", "valid 256-byte identity");
        boundaryStages[0] = boundaryStages[0] with { Id = max256 + "🌍" };
        Reject(() => new ActorPipelinePolicy(boundaryStages, boundaryEdges));
        // Declared output port is not an actual connected outbound edge.
        Reject(() => new ActorPipelinePolicy(
            new[] {
                new ActorPipelinePolicy.Stage("src", "P", "readable", "", "Event"),
                new ActorPipelinePolicy.Stage("terminal", "T", "transform", "Event", "Event")
            },
            new[] { new ActorPipelinePolicy.Edge("src", "terminal", "Event") }));
        Console.WriteLine("ORES_NET_HOST_ACTOR_PIPELINE_POLICY_PASS");

        ActorPipelinePolicy.Stage[] withDuplicateId() =>
            new[] { stages[0], stages[0] };
    }
}
