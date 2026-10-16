using Ores.Net.Runtime;

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
        Console.WriteLine("ORES_NET_HOST_ACTOR_PIPELINE_POLICY_PASS");

        ActorPipelinePolicy.Stage[] withDuplicateId() =>
            new[] { stages[0], stages[0] };
    }
}
