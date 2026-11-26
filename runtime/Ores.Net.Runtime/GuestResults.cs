namespace Ores.Net.Runtime;

/// <summary>The owned Error value passed between generated guest callables.</summary>
public sealed class GuestError
{
    internal GuestError(string name, string message) { Name = name; Message = message; }
    public string Name { get; }
    public string Message { get; }
}

public abstract class GuestSignal : Exception
{
    protected GuestSignal(GuestError error) : base(error.Message) => Error = error;
    public GuestError Error { get; }
}

public sealed class GuestThrowSignal : GuestSignal
{
    internal GuestThrowSignal(GuestError error) : base(error) { }
}

public sealed class GuestPanicSignal : GuestSignal
{
    internal GuestPanicSignal(GuestError error) : base(error) { }
}

public sealed class GuestRaiseSignal : GuestSignal
{
    internal GuestRaiseSignal(GuestError error) : base(error) { }
}

// The CFG types distinguish the two closed Result shapes. Both use the same
// CIL representation and retain the same Error object when forwarded.
public readonly struct GuestResultI64
{
    internal GuestResultI64(long value, bool optionPresent, GuestError? error, string? textError = null)
    { Value = value; OptionPresent = optionPresent; Error = error; TextError = textError; }
    internal long Value { get; }
    internal bool OptionPresent { get; }
    internal GuestError? Error { get; }
    internal string? TextError { get; }
}

public static class GuestResults
{
    public static GuestError CreateError(string message) => new("Error", message);
    public static Exception Throw(GuestError error) => new GuestThrowSignal(error);
    public static Exception Panic(GuestError error) => new GuestPanicSignal(error);
    public static Exception Raise(GuestError error) => new GuestRaiseSignal(error);

    public static GuestResultI64 Ok(long value) => new(value, false, null);
    public static GuestResultI64 OkOption(bool hasValue, long value) => new(value, hasValue, null);
    public static GuestResultI64 Err(GuestError error) => new(0, false, error);
    public static GuestResultI64 ErrText(string error) => new(0, false, null, error);
    public static GuestResultI64 FromThrow(GuestThrowSignal signal) => Err(signal.Error);
    public static GuestResultI64 FromOverflow() =>
        Err(new GuestError("ArithmeticOverflowError", "integer arithmetic overflow"));
    public static GuestResultI64 FromDivisionByZero() =>
        Err(new GuestError("DivisionByZeroError", "integer division by zero"));

    public static bool IsOk(GuestResultI64 value) => value.Error is null && value.TextError is null;
    public static bool IsErr(GuestResultI64 value) => !IsOk(value);
    public static long Unwrap(GuestResultI64 value) => IsOk(value) ? value.Value
        : throw new GuestPanicSignal(CreateError("called Result::unwrap() on Err"));
    public static bool UnwrapOptionTag(GuestResultI64 value) => IsOk(value) ? value.OptionPresent
        : throw new GuestPanicSignal(CreateError("called Result::unwrap() on Err"));
    public static long UnwrapOr(GuestResultI64 value, long fallback) => IsOk(value) ? value.Value : fallback;
    public static GuestError UnwrapErr(GuestResultI64 value) => value.Error
        ?? throw new GuestPanicSignal(CreateError("called Result::unwrap_err() on Ok"));
    public static string UnwrapTextErr(GuestResultI64 value) => value.TextError
        ?? throw new GuestPanicSignal(CreateError("called Result::unwrap_err() on Ok"));
    public static bool TextEquals(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);
    public static long UnwrapOption(bool present, long value) => present ? value
        : throw new GuestPanicSignal(CreateError("called Option::unwrap() on None"));
    public static bool IsErrorType(GuestError error, string name) => name == "Error" || error.Name == name;
    public static bool ErrorHasMessage(GuestError error, string message) =>
        string.Equals(error.Message, message, StringComparison.Ordinal);
}
