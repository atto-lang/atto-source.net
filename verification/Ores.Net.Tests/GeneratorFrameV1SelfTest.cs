using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ores.Net.Runtime;

internal static class GeneratorFrameV1SelfTest
{
    [ModuleInitializer]
    internal static void Run()
    {
        var frame = default(GeneratorFrameV1);
        if (frame.Pull(3, out _) != -1) throw new Exception("fresh generator pull accepted");
        if (frame.Close() != -1) throw new Exception("fresh generator close accepted");
        if (frame.Initialize() != 1 || frame.Initialize() != 0)
            throw new Exception("generator init is not one-shot");

        for (var expected = 0; expected < 3; expected++)
        {
            if (frame.Pull(3, out var slot) != 1 || slot != expected)
                throw new Exception("generator slot order/replay failure");
        }

        if (frame.Pull(3, out var doneSlot) != 0 || doneSlot != -1)
            throw new Exception("generator missing DONE");
        if (!frame.IsTerminal || frame.Pull(3, out _) != 0 || frame.Close() != 0)
            throw new Exception("generator DONE is not sticky");
        if (frame.Initialize() != 0)
            throw new Exception("terminal generator resurrected");

        var early = default(GeneratorFrameV1);
        if (early.Initialize() != 1 || early.Pull(3, out var first) != 1 || first != 0)
            throw new Exception("early-close generator setup failed");
        if (early.Close() != 1 || !early.IsTerminal ||
            early.Pull(3, out _) != 0 || early.Close() != 0)
            throw new Exception("generator close is not terminal/idempotent");

        var empty = default(GeneratorFrameV1);
        if (empty.Initialize() != 1 || empty.Pull(0, out _) != 0 || !empty.IsTerminal)
            throw new Exception("empty generator completion failed");

        var bounded = default(GeneratorFrameV1);
        if (bounded.Initialize() != 1 ||
            bounded.Pull(GeneratorFrameV1.MaxYields + 1, out _) != -1)
            throw new Exception("generator yield-count budget not enforced");

        var forgedState = default(GeneratorFrameV1);
        var forgedStateWords = MemoryMarshal.Cast<GeneratorFrameV1, int>(
            MemoryMarshal.CreateSpan(ref forgedState, 1));
        forgedStateWords[0] = 99;
        if (forgedState.Initialize() != -1 || forgedState.IsTerminal)
            throw new Exception("unknown generator frame state was accepted");

        var forgedActive = default(GeneratorFrameV1);
        var forgedActiveWords = MemoryMarshal.Cast<GeneratorFrameV1, int>(
            MemoryMarshal.CreateSpan(ref forgedActive, 1));
        forgedActiveWords[0] = 1;
        forgedActiveWords[1] = GeneratorFrameV1.MaxYields + 1;
        if (forgedActive.Initialize() != -1 ||
            forgedActive.Pull(3, out _) != -1)
            throw new Exception("forged active generator index was accepted");

        var forgedTerminal = default(GeneratorFrameV1);
        var forgedTerminalWords = MemoryMarshal.Cast<GeneratorFrameV1, int>(
            MemoryMarshal.CreateSpan(ref forgedTerminal, 1));
        forgedTerminalWords[0] = 2;
        forgedTerminalWords[1] = 1;
        if (forgedTerminal.Initialize() != -1 || forgedTerminal.IsTerminal ||
            forgedTerminal.Pull(3, out _) != -1 || forgedTerminal.Close() != -1)
            throw new Exception("forged terminal generator index was accepted");
    }
}
