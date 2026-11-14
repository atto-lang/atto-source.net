using Ores.Net.Compiler;

internal static class HeapContainerSourceBoundaryTests
{
    // Real scalar OresParser -> typed IR boundary; no Box/Arc/Vector CIL
    // lowering is claimed. Do not count the host .NET heap as guest support.
    public static void Run()
    {
        var scalar = OresIrLowering.Lower(
            OresParser.Parse("pub fnc main():int{return 21+21;}"));
        if (OresIrReferenceEvaluator.Evaluate(scalar) != 42)
            throw new Exception("heap-container boundary scalar positive control failed");

        var unsupported = new (string Name, string Source)[]
        {
            ("box-owner", "pub fnc main():int{val Box<int> b=Box<int>(42);return 42;}"),
            ("atomic-arc", "pub fnc main():int{val Arc<int> a=Arc<int>(42);return 42;}"),
            ("weak-arc", "pub fnc main():int{val Weak<int> w=null;return 42;}"),
            ("resizable-vector", "pub fnc main():int{val Vector[int] v=[42];return 42;}"),
            ("resizable-array", "pub fnc main():int{val Array<int> v=new Array<int>();v.add(42);return 42;}"),
            ("heap-move", "pub fnc main():int{let int x=42;let moved=rt take x;return x;}"),
            ("shared-loan", "pub fnc main():int{let int x=42;let ref=rt borrow x;return 42;}"),
            ("mutable-loan", "pub fnc main():int{let int x=42;let ref=rt mut borrow x;return 42;}"),
            ("unguarded-mut-share", "pub fnc main():int{let int x=42;rt mut share x;return 42;}"),
            ("mutable-container", "define class Counter as state{} end pub fnc main():int{return 42;}")
        };
        foreach (var (name, source) in unsupported)
        {
            try
            {
                // If parsing succeeds, lowering must also fail: otherwise the
                // scalar CIL compiler may silently erase unproved heap effects.
                var typed = OresIrLowering.Lower(OresParser.Parse(source));
                throw new Exception("Unverified " + name +
                    " emitted typed IR: " + typed.GetType().Name);
            }
            catch (FormatException ex)
            {
                if (string.IsNullOrWhiteSpace(ex.Message))
                    throw new Exception("Missing compiler-owned diagnostic for " + name);
            }
            catch (NotSupportedException ex)
            {
                if (string.IsNullOrWhiteSpace(ex.Message))
                    throw new Exception("Missing unsupported diagnostic for " + name);
            }
        }
    }
}
