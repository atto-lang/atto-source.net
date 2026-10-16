using Ores.Net.Runtime;

internal static class WrapperFlattenHostSelfTest
{
    public static async Task RunAsync()
    {
        var option = HostOption<HostOption<HostOption<int>>>.Some(
            HostOption<HostOption<int>>.Some(HostOption<int>.Some(42)));
        var flat = WrapperFlattenHost.FlattenOption3(option);
        if (!flat.IsSome || flat.Value != 42)
            throw new Exception("three-deep Option did not flatten");

        var nestedNone = HostOption<HostOption<int>>.Some(HostOption<int>.None);
        if (!nestedNone.IsSome || !WrapperFlattenHost.FlattenOption(nestedNone).IsNone)
            throw new Exception("Some(None) must remain distinguishable until explicit flatten");
        if (!WrapperFlattenHost.FlattenOption(HostOption<HostOption<int>>.None).IsNone)
            throw new Exception("None must remain None");

        var outer = new TaskCompletionSource<Task<int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> observer = WrapperFlattenHost.FlattenFuture(outer.Task);
        outer.SetResult(inner.Task);
        if (observer.IsCompleted)
            throw new Exception("outer Future must not finish before inner");
        inner.SetResult(17);
        if (await observer.ConfigureAwait(false) != 17)
            throw new Exception("inner payload lost");

        var failed = Task.FromResult(Task.FromException<int>(new InvalidOperationException("boom")));
        try
        {
            await WrapperFlattenHost.FlattenFuture(failed).ConfigureAwait(false);
            throw new Exception("inner failure was swallowed");
        }
        catch (InvalidOperationException error) when (error.Message == "boom") { }

        var shared = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var cancelled = WrapperFlattenHost.FlattenFuture(
            Task.FromResult(shared.Task), cancel.Token);
        cancel.Cancel();
        try
        {
            await cancelled.ConfigureAwait(false);
            throw new Exception("observer cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        shared.SetResult(11);
        if (await shared.Task.ConfigureAwait(false) != 11)
            throw new Exception("observer cancelled shared producer");

        var triple = Task.FromResult(Task.FromResult(Task.FromResult(9)));
        if (await WrapperFlattenHost.FlattenFuture3(triple).ConfigureAwait(false) != 9)
            throw new Exception("three-deep Task did not flatten");
    }
}
