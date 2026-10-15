namespace Ores.Net.Runtime;

public enum ExecutionError { None, ArithmeticOverflow, DivisionByZero }
public readonly record struct ExecutionResult(int ExitCode, long? Value, ExecutionError Error);

/// <summary>Executes generated CIL without any console, file or network I/O.</summary>
public static class RuntimeHost
{
    public static ExecutionResult Evaluate(Func<long> entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try { return new(0, entry(), ExecutionError.None); }
        catch (OverflowException) { return new(70, null, ExecutionError.ArithmeticOverflow); }
        catch (DivideByZeroException) { return new(70, null, ExecutionError.DivisionByZero); }
    }

    // Existing embedding ABI retains its status, but never writes to ambient streams.
    public static int Execute(Func<long> entry) => Evaluate(entry).ExitCode;
}
