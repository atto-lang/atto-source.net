namespace Ores.Net.Compiler;

/// <summary>
/// Explicit compile-time grant for the closed-world .NET host-import bootstrap.
/// An embedding host must derive this authority from its trusted build policy:
/// callers cannot grant application runtime privileges by importing a type.
/// </summary>
public sealed class DotnetHostImportPolicy
{
    private DotnetHostImportPolicy(bool mathI64)
    {
        AllowsMathI64 = mathI64;
    }

    /// <summary>No .NET host imports permitted by default.</summary>
    public static DotnetHostImportPolicy DenyAll { get; } = new(false);

    /// <summary>
    /// Permits only System.Math.Abs(Int64), Min(Int64, Int64), and Max(Int64, Int64)
    /// in the experimental structured compiler. This is a pure scalar-method
    /// host adapter, not arbitrary CLR interop or an actor heap guarantee.
    /// </summary>
    public static DotnetHostImportPolicy MathI64V0 { get; } = new(true);

    internal bool AllowsMathI64 { get; }

    internal void RequireMathI64()
    {
        if (!AllowsMathI64)
            throw new FormatException(
                "DOTNET_INTEROP_CAPABILITY_DENIED: import dotnet requires " +
                "explicit compiler capability interop.dotnet.math-i64");
    }
}
