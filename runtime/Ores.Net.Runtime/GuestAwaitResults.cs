namespace Ores.Net.Runtime;

/// <summary>
/// Closed Future&lt;int&gt; trap bridge. The source-level Error/Result ABI is
/// the exact GuestResults implementation from #239. No Task blocking,
/// broad exception catch, or scheduler authority inside the trap.
/// </summary>
internal static class GuestAwaitResults
{
    // Task completion is monotonic. Only these two reviewed sites may
    // observe the CLR result synchronously, and neither may park a carrier.
    internal static void ObserveCompleted(Task pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (!pending.IsCompleted)
            throw new InvalidOperationException("await result not ready");
        pending.GetAwaiter().GetResult();
    }

    internal static long ReadCompletedI64(Task<long> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (!pending.IsCompleted)
            throw new InvalidOperationException("await result not ready");
        return pending.GetAwaiter().GetResult();
    }

    internal static GuestResultI64 TrapCompletedI64(Task<long> pending)
    {
        try {
            return GuestResults.Ok(ReadCompletedI64(pending));
        }
        catch (GuestThrowSignal signal) {
            return GuestResults.FromThrow(signal);
        }
        catch (OverflowException) {
            return GuestResults.FromOverflow();
        }
        catch (DivideByZeroException) {
            return GuestResults.FromDivisionByZero();
        }
        // GuestPanicSignal, GuestRaiseSignal, OperationCanceledException,
        // host faults and all unknown exceptions deliberately escape.
    }
}
