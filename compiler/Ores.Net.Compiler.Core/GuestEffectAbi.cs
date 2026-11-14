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

    // Externally produced / native ABI status tags are untrusted. A Return
    // carrying a nonzero guest error code must never become success; unknown
    // discriminants are terminal even if they otherwise resemble a return.
    public GuestEffectStatus Normalize()
    {
        if ((byte)Kind > (byte)GuestEffectKind.ProcessExit ||
            (Kind == GuestEffectKind.Return && Code != 0) ||
            // Code zero is reserved for success; a thrown guest Error
            // must have a nonzero identity before any typed catch can run.
            (Kind == GuestEffectKind.Throw && Code == 0))
            return new GuestEffectStatus(GuestEffectKind.Fatal, 0xFFFFFFFDu);
        return this;
    }
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
        // Transport never becomes an authority to forge a successful guest
        // return or an unknown effect discriminant.
        Status = status.Normalize();
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

/// <summary>
/// Compiler-validated nominal guest type identities. These constants are
/// *not* CLR exception types; they are checked against a normalized guest
/// Throw effect and the shared numeric error registry.
/// </summary>
public enum NominalGuestErrorType : uint
{
    Error = 0,
    ArithmeticOverflowError = (uint)NumericGuestErrorCode.ArithmeticOverflowError,
    DivisionByZeroError = (uint)NumericGuestErrorCode.DivisionByZeroError,
    InexactConversionError = (uint)NumericGuestErrorCode.InexactConversionError,
    NumericRangeError = (uint)NumericGuestErrorCode.NumericRangeError,
    NumericParseError = (uint)NumericGuestErrorCode.NumericParseError,
    NumericContextError = (uint)NumericGuestErrorCode.NumericContextError,
    NumericResourceError = (uint)NumericGuestErrorCode.NumericResourceError,
    NonterminatingDecimalError = (uint)NumericGuestErrorCode.NonterminatingDecimalError
}

public readonly record struct GuestI64EffectResult(
    long Value,
    GuestEffectStatus Status)
{
    public GuestEffectStatus NormalizedStatus => Status.Normalize();
    public bool IsReturn => NormalizedStatus.Kind == GuestEffectKind.Return;

    public GuestI64EffectResult Forward()
    {
        var effective = NormalizedStatus;
        return effective.Kind == GuestEffectKind.Return
            ? new GuestI64EffectResult(Value, effective)
            : new GuestI64EffectResult(0, effective);
    }

    // The wire-level numeric matcher is intentionally exact. It does not
    // authorize catching CLR exceptions or non-Throw terminal effects.
    public bool CatchMatches(uint code) =>
        code != 0 && NormalizedStatus.Kind == GuestEffectKind.Throw &&
        NormalizedStatus.Code == code;

    // The Error root nominally catches every properly normalized guest Throw.
    // The numeric children have explicit identities; unknown enum casts
    // cannot manufacture an otherwise valid catch type.
    public bool CatchMatches(NominalGuestErrorType type)
    {
        var status = NormalizedStatus;
        if (status.Kind != GuestEffectKind.Throw || status.Code == 0)
            return false;
        return type switch
        {
            NominalGuestErrorType.Error => true,
            NominalGuestErrorType.ArithmeticOverflowError or
            NominalGuestErrorType.DivisionByZeroError or
            NominalGuestErrorType.InexactConversionError or
            NominalGuestErrorType.NumericRangeError or
            NominalGuestErrorType.NumericParseError or
            NominalGuestErrorType.NumericContextError or
            NominalGuestErrorType.NumericResourceError or
            NominalGuestErrorType.NonterminatingDecimalError =>
                status.Code == (uint)type,
            _ => false
        };
    }

    public GuestI64EffectResult Recover(uint code, long value) =>
        CatchMatches(code) ? Return(value) : Forward();

    public GuestI64EffectResult Recover(NominalGuestErrorType type, long value) =>
        CatchMatches(type) ? Return(value) : Forward();

    public static GuestI64EffectResult Return(long value) =>
        new(value, GuestEffectStatus.Return);

    public static GuestI64EffectResult ArithmeticOverflow() =>
        new(0, GuestEffectStatus.ArithmeticOverflow);
}
