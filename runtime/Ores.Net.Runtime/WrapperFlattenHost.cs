namespace Ores.Net.Runtime;

/// <summary>
/// CLR host-side reference substrate only. The scalar Oreslang frontend does
/// not yet admit source-level Option/Future or actor await lowering.
/// </summary>
public readonly struct HostOption<T>
{
    private readonly T _value;
    public bool IsSome { get; }
    public bool IsNone => !IsSome;

    private HostOption(T value)
    {
        _value = value;
        IsSome = true;
    }

    public static HostOption<T> None => default;
    public static HostOption<T> Some(T value) => new(value);
    public T Value => IsSome ? _value : throw new InvalidOperationException("Option is None");
}

public static class WrapperFlattenHost
{
    // Absence at any nested level maps to None, but nesting is never
    // removed implicitly. Values move through CLR references; this is not
    // an ownership-safe guest Oreslang implementation.
    public static HostOption<T> FlattenOption<T>(HostOption<HostOption<T>> nested) =>
        nested.IsSome ? nested.Value : HostOption<T>.None;

    public static HostOption<T> FlattenOption3<T>(
        HostOption<HostOption<HostOption<T>>> nested) =>
        FlattenOption(FlattenOption(nested));

    // WaitAsync cancels only the observer; it does not cancel either shared
    // producer Task. No actor guest code is dispatched in these host helpers.
    public static async Task<T> FlattenFuture<T>(
        Task<Task<T>> nested, CancellationToken observerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(nested);
        Task<T> inner = await nested.WaitAsync(observerCancellation).ConfigureAwait(false);
        if (inner is null) throw new InvalidOperationException("Nested Future was null");
        return await inner.WaitAsync(observerCancellation).ConfigureAwait(false);
    }

    public static async Task<T> FlattenFuture3<T>(
        Task<Task<Task<T>>> nested, CancellationToken observerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(nested);
        Task<Task<T>> middle =
            await nested.WaitAsync(observerCancellation).ConfigureAwait(false);
        if (middle is null) throw new InvalidOperationException("Nested Future was null");
        return await FlattenFuture(middle, observerCancellation).ConfigureAwait(false);
    }
}
