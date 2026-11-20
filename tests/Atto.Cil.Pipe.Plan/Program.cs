using Atto.Cil.Compiler;

static class Program
{
    static int Checks;
    static void Ok(bool good, string why)
    {
        if (!good) throw new Exception(why);
        Checks++;
    }
    static void Reject(string code, Action action)
    {
        try { action(); }
        catch (ArgumentException e) when (e.Message.Contains(code, StringComparison.Ordinal))
        {
            Checks++; return;
        }
        throw new Exception("missing diagnostic " + code);
    }
    static DirectedPipeStage A(string id, string? input, string? output,
        string tenant = "tenant_a") =>
        new(id, tenant, DirectedPipeEndpoint.Actor, input, output);
    static DirectedPipeStage C(string id, string type = "int") =>
        new(id, "tenant_a", DirectedPipeEndpoint.Channel, type, type);

    static int Main()
    {
        var p = A("producer", "int", "int");
        var c = A("consumer", "int", null);
        var forward = DirectedPipePlanner.Plan(new[] { p, c }, DirectedPipeDirection.Pipe);
        var reverse = DirectedPipePlanner.Plan(new[] { c, p }, DirectedPipeDirection.Rpipe);
        Ok(forward.DataEdges[0].Source == "producer", "pipe direction");
        Ok(reverse.DataEdges[0].Source == "producer", "rpipe direction");
        Ok(forward.DataEdges[0].Sink == reverse.DataEdges[0].Sink, "same target");
        Ok(reverse.FlowIndices.SequenceEqual(new[] { 1, 0 }), "right-to-left indices");
        Ok(reverse.Written[0].Id == "consumer", "lexical order retained");
        var mixed = DirectedPipePlanner.Plan(new[] { p, C("middle"), c },
            DirectedPipeDirection.Pipe);
        var mirror = DirectedPipePlanner.Plan(new[] { c, C("middle"), p },
            DirectedPipeDirection.Rpipe);
        Ok(mixed.DataEdges.Select(x => x.Source)
            .SequenceEqual(mirror.DataEdges.Select(x => x.Source)), "channel parity");
        Ok(mixed.DataEdges.Count == 2, "two DATA edges");
        Ok(mirror.DataEdges[0].SourceIndex == 2, "reverse-written index");
        Reject("E_PIPE_MISSING_EMITS", () => DirectedPipePlanner.Plan(
            new[] { A("p", "int", null), c }, DirectedPipeDirection.Pipe));
        Reject("E_PIPE_MISSING_RECEIVES", () => DirectedPipePlanner.Plan(
            new[] { p, new DirectedPipeStage("reader","tenant_a",
                DirectedPipeEndpoint.StreamReadable,null,"int") }, DirectedPipeDirection.Pipe));
        Reject("E_PIPE_TYPE_MISMATCH", () => DirectedPipePlanner.Plan(
            new[] { p, A("other", "bool", null) }, DirectedPipeDirection.Pipe));
        Reject("E_PIPE_TENANT_BOUNDARY", () => DirectedPipePlanner.Plan(
            new[] { p, A("foreign", "int", null, "tenant_b") }, DirectedPipeDirection.Pipe));
        Reject("E_PIPE_DUPLICATE_STAGE", () => DirectedPipePlanner.Plan(
            new[] { p, p }, DirectedPipeDirection.Pipe));
        Reject("E_PIPE_STAGE_LIMIT", () => DirectedPipePlanner.Plan(
            new[] { p }, DirectedPipeDirection.Pipe));
        Reject("E_PIPE_STAGE_LIMIT", () => DirectedPipePlanner.Plan(
            Enumerable.Repeat(p, 17).ToArray(), DirectedPipeDirection.Pipe));
        Reject("E_PIPE_CHANNEL_PORTS", () => DirectedPipePlanner.Plan(new[] {
            p, new DirectedPipeStage("bad","tenant_a",DirectedPipeEndpoint.Channel,"int","bool")
        },DirectedPipeDirection.Pipe));
        Reject("E_PIPE_ID", () => DirectedPipePlanner.Plan(
            new[] { p, A("bad\u202e", "int", null) }, DirectedPipeDirection.Pipe));
        Reject("E_PIPE_DUPLICATE_EDGE", () =>
            DirectedPipePlanner.CheckUnicast(new[] { forward, reverse }));
        var other = DirectedPipePlanner.Plan(new[] {
            p, new DirectedPipeStage("sink","tenant_a",
                DirectedPipeEndpoint.StreamSink,"int",null)
        }, DirectedPipeDirection.Pipe);
        Reject("E_PIPE_OUTPUT_OCCUPIED", () =>
            DirectedPipePlanner.CheckUnicast(new[] { forward, other }));
        var independent = DirectedPipePlanner.Plan(new[] {
            A("other_producer","int","int"), A("other_consumer","int",null)
        }, DirectedPipeDirection.Pipe);
        DirectedPipePlanner.CheckUnicast(new[] { forward, independent });
        Ok(true, "disjoint pipelines");
        Console.WriteLine($"ATTO_NET_DIRECTED_PIPE_HOST_PASS checks={Checks}");
        Console.WriteLine("ATTO_NET_PIPE_GUEST_EXECUTION=NOT_EXECUTED");
        return 0;
    }
}
