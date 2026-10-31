namespace Ores.Net.Compiler;

/// <summary>
/// Backend-neutral exceptional effect discriminants. Numeric guest failures use
/// these values as language identity; CLR exception classes are transport only.
/// </summary>
public enum GuestEffectKind : byte
{
    Return = 0,
    Throw = 1,
    Raise = 2,
    Panic = 3,
    Cancel = 4,
    Fatal = 5,
    ProcessExit = 6
}

/// <summary>Stable numeric Error codes shared by every Oreslang backend.</summary>
public enum NumericGuestErrorCode : uint
{
    ArithmeticOverflowError = 65537,
    DivisionByZeroError = 65538,
    InexactConversionError = 65539,
    NumericRangeError = 65540,
    NumericParseError = 65541,
    NumericContextError = 65542,
    NumericResourceError = 65543,
    NonterminatingDecimalError = 65544
}

public readonly record struct GuestEffectStatus(GuestEffectKind Kind, uint Code)
{
    public static GuestEffectStatus Return =>
        new(GuestEffectKind.Return, 0);

    public static GuestEffectStatus ArithmeticOverflow =>
        new(GuestEffectKind.Throw,
            (uint)NumericGuestErrorCode.ArithmeticOverflowError);
}

/// <summary>
/// Verification/runtime transport for an Oreslang guest effect. Its CLR class
/// is deliberately not guest type identity; callers must inspect Status/Name.
/// </summary>
public sealed class GuestEffectTransportException : Exception
{
    public GuestEffectTransportException(
        GuestEffectStatus status,
        string guestErrorName,
        string message,
        Exception? inner = null) : base(message, inner)
    {
        Status = status;
        GuestErrorName = guestErrorName;
    }

    public GuestEffectStatus Status { get; }
    public string GuestErrorName { get; }

    public static GuestEffectTransportException ArithmeticOverflow(
        string operation,
        Exception? inner = null) =>
        new(
            GuestEffectStatus.ArithmeticOverflow,
            nameof(NumericGuestErrorCode.ArithmeticOverflowError),
            $"ArithmeticOverflowError: checked signed 64-bit {operation}",
            inner);
}

public readonly record struct GuestI64EffectResult(
    long Value,
    GuestEffectStatus Status)
{
    public bool IsReturn => Status.Kind == GuestEffectKind.Return;

    public static GuestI64EffectResult Return(long value) =>
        new(value, GuestEffectStatus.Return);

    public static GuestI64EffectResult ArithmeticOverflow() =>
        new(0, GuestEffectStatus.ArithmeticOverflow);
}
