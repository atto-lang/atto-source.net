using Ores.Net.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Region = Ores.Net.Runtime.OwnedF32RegionV1;

internal static class OwnedF32RegionV1Tests
{
    private static long _checks;
    private static void Need(bool yes, string message)
    {
        Interlocked.Increment(ref _checks);
        if (!yes) throw new Exception("owned F32 region: " + message);
    }
    private static void Reject(Action action, string why)
    {
        Interlocked.Increment(ref _checks);
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (OverflowException) { return; }
        throw new Exception("owned F32 operation admitted: " + why);
    }

    internal static void Run()
    {
        foreach (Region.Layout layout in Enum.GetValues<Region.Layout>())
        {
            var shape = new Region.Shape(layout, 17, 4,
                layout == Region.Layout.AoSoA ? 4 : 1);
            var input = new Region.HostBuffer(shape);
            var output = new Region.HostBuffer(shape);
            for (int row = 0; row < 17; ++row)
                for (int field = 0; field < 4; ++field)
                    input.Write(row, field, row * 4 + field);
            Region.Area source = input.Region(Region.Mode.Read, 2, 3, 11);
            Region.Area destination = output.Region(Region.Mode.Write, 2, 3, 11);
            Region.ExecuteCpuRegion(input, output, source, destination, 2, 1);
            for (int row = 0; row < 17; ++row)
                for (int field = 0; field < 4; ++field)
                    Need(output.Read(row, field) ==
                        (field == 2 && row >= 3 && row < 11 ?
                            2 * (row * 4 + field) + 1 : 0), "row/field layout");

            Reject(() => Region.ExecuteCpuRegion(input, output,
                input.Region(Region.Mode.Write, 2, 3, 11), destination, 1, 0),
                "source privilege");
            Reject(() => Region.ExecuteCpuRegion(input, output, source,
                output.Region(Region.Mode.Read, 2, 3, 11), 1, 0),
                "output privilege");
            Reject(() => Region.ExecuteCpuRegion(output, input, source, destination, 1, 0),
                "forged ownership");
            Reject(() => Region.ExecuteCpuRegion(input, output, source, destination,
                float.NaN, 0), "nonfinite scale");
            Reject(() => input.Region(Region.Mode.Read, 4, 0, 17), "bad field");
            Reject(() => input.Region(Region.Mode.Read, 1, -1, 17), "negative row");
            Reject(() => input.Region(Region.Mode.Read, 1, 0, 18), "oversize row");
            using (var hold = output.BorrowRead())
            {
                Reject(() => output.BorrowWrite(), "borrowed write");
                Reject(() => output.Close(), "close borrowed");
                Reject(() => Region.ExecuteCpuRegion(input, output, source, destination, 1, 0),
                    "bridge double writer");
                Need(hold.Read(4, 2) == 37, "loan mutation when rejected");
            }
            using (var hold = output.BorrowWrite())
            {
                Reject(() => output.BorrowRead(), "read during write loan");
                Reject(() => output.Close(), "close during write");
                hold.Write(0, 0, 5);
                Need(hold.Read(0, 0) == 5, "own write loan");
            }
            Region.ExecuteCpuRegion(input, output,
                input.Region(Region.Mode.Read, 0, 0, 0),
                output.Region(Region.Mode.Write, 0, 0, 0), 1, 0);
            output.Close();
            Need(!output.Owns(destination, Region.Mode.Write), "closed area remains live");
            Reject(() => output.Read(2, 2), "read closed");
            Reject(() => output.Region(Region.Mode.Read, 2, 3, 11), "region closed");
        }

        Reject(() => new Region.HostBuffer(new Region.Shape(
            Region.Layout.AoSoA, 17, 4, 3)), "AoSoA lanes not power of two");
        Reject(() => new Region.HostBuffer(new Region.Shape(
            Region.Layout.AoS, 17, 0)), "no F32 fields");
        Reject(() => new Region.HostBuffer(new Region.Shape(
            Region.Layout.AoS, long.MaxValue, 4)), "host byte budget");
        Reject(() => new Region.HostBuffer(new Region.Shape(
            Region.Layout.AoS, 100_000_000, 4)), "excessive rows");

        // Identical deterministic stream and geometry to the Forge region test.
        uint seed = 0xD0F2026;
        uint Next() { seed = unchecked(seed * 1664525u + 1013904223u); return seed; }
        for (int trial = 0; trial < 450; ++trial)
        {
            var layout = (Region.Layout)(trial % 3);
            long rows = Next() % 40;
            int fields = 1 + (int)(Next() % 5);
            var shape = new Region.Shape(layout, rows, fields,
                layout == Region.Layout.AoSoA ? 4 : 1);
            var input = new Region.HostBuffer(shape);
            var output = new Region.HostBuffer(shape);
            int field = (int)(Next() % (uint)fields);
            long begin = rows == 0 ? 0 : Next() % (uint)(rows + 1);
            long end = begin + (rows == begin ? 0 : Next() % (uint)(rows - begin + 1));
            for (long row = 0; row < rows; ++row)
                for (int f = 0; f < fields; ++f)
                    input.Write(row, f, 3 * row + f);
            Region.ExecuteCpuRegion(input, output,
                input.Region(Region.Mode.Read, field, begin, end),
                output.Region(Region.Mode.Write, field, begin, end), 3, -2);
            for (long row = 0; row < rows; ++row)
                for (int f = 0; f < fields; ++f)
                    Need(output.Read(row, f) ==
                        (f == field && row >= begin && row < end ?
                            3 * (3 * row + f) - 2 : 0), "randomized region slice");
        }

        var concurrent = new Region.HostBuffer(new Region.Shape(Region.Layout.AoS, 8, 2));
        using (var hold = concurrent.BorrowRead())
        {
            bool rejected = Task.Run(() =>
            {
                try { concurrent.Write(0, 0, 42); return false; }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            Need(rejected, "cross-thread writer bypassed physical loan");
            Need(hold.Read(0, 0) == 0, "conflicting writer mutated bytes");
        }
        Console.WriteLine($"PASS {_checks} .NET owned F32 CPU region assertions");
    }
}
