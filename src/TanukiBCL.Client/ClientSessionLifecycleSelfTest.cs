using System.IO;

namespace TanukiBCL.Client;

internal static class ClientSessionLifecycleSelfTest
{
    // The test uses no microphone, socket, game process, or WPF dispatcher. Faults
    // are injected at the same ownership boundaries used by MainWindow.
    public static void Run() => Task.Run(VerifyAsync).GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        await VerifyPartialStartupFailureAsync();
        await VerifyStopAndStaleCallbacksAsync();
        await VerifyCancellationCallbackFailureAsync();
        await VerifyCompletionFailureAsync();
    }

    private static async Task VerifyPartialStartupFailureAsync()
    {
        var coordinator = new ClientSessionCoordinator();
        Require(coordinator.TryStart(out var session), "First startup was rejected.");
        var order = new List<string>();
        ClientSessionCoordinator.SessionResult? result = null;
        await session.RunAsync(() =>
        {
            session.AddCleanup(() => { order.Add("reset-ui"); return ValueTask.CompletedTask; });
            session.AddCleanup(() => { order.Add("dispose-probe"); throw new IOException("dispose fault"); });
            // Simulate options/configuration/hotkey setup throwing after a probe
            // was created. Teardown must still release it and reset the UI.
            throw new InvalidOperationException("setup fault");
        }, value => result = value);
        Require(order.SequenceEqual(["dispose-probe", "reset-ui"]), "A disposal fault skipped later cleanup.");
        Require(result?.Errors.Count == 2 && result.Errors[0].Message == "setup fault" &&
            result.Errors[1].Message == "dispose fault", "Startup and teardown faults were not both reported.");
        Require(session.Completion.IsCompleted && coordinator.Current is null, "Failed startup retained its lease.");
        Require(coordinator.TryStart(out var retry), "Retry after startup failure was rejected.");
        await retry.RunAsync(() => Task.CompletedTask, _ => { });
    }

    private static async Task VerifyStopAndStaleCallbacksAsync()
    {
        var coordinator = new ClientSessionCoordinator();
        Require(coordinator.TryStart(out var first), "First startup was rejected.");
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        ClientSessionCoordinator.SessionResult? outcome = null;
        var delayedCallback = first.Guard(() => callbacks++);
        delayedCallback();
        var run = first.RunAsync(async () =>
        {
            first.AddCleanup(async () =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
            });
            await Task.Delay(Timeout.Infinite, first.Token);
        }, result => outcome = result);
        Require(!coordinator.TryStart(out _), "Duplicate startup overlapped a running session.");
        first.RequestStop();
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        delayedCallback();
        Require(callbacks == 1, "A queued callback ran during teardown.");
        Require(!first.Completion.IsCompleted && !coordinator.TryStart(out _),
            "A second session started before the old resources finished disposing.");
        releaseCleanup.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Require(outcome is { Error: null }, "Normal stop reported a failure.");
        Require(first.Completion.IsCompleted, "Stop completion was not signaled.");
        Require(coordinator.TryStart(out var next), "Restart after shutdown was rejected.");
        delayedCallback();
        next.Guard(() => callbacks += 10)();
        Require(callbacks == 11, "An old callback modified the replacement session.");
        first.RequestStop(); // A late close/stop must be safe after CTS disposal.
        Require(!next.Token.IsCancellationRequested, "Stopping an old lease canceled the replacement.");
        await next.RunAsync(() => Task.CompletedTask, _ => { });
    }

    private static async Task VerifyCancellationCallbackFailureAsync()
    {
        var coordinator = new ClientSessionCoordinator();
        Require(coordinator.TryStart(out var session), "Startup was rejected.");
        var disposedResource = false;
        ClientSessionCoordinator.SessionResult? outcome = null;
        using var registration = session.Token.Register(() => throw new InvalidOperationException("cancel fault"));
        await session.RunAsync(() =>
        {
            session.AddCleanup(() => { disposedResource = true; return ValueTask.CompletedTask; });
            session.RequestStop();
            return Task.FromCanceled(session.Token);
        }, value => outcome = value);
        Require(disposedResource && outcome?.Errors.Count == 1, "Cancel callback fault skipped cleanup or was hidden.");
        Require(coordinator.Current is null && session.Completion.IsCompleted, "Cancel callback fault retained its lease.");
    }

    private static async Task VerifyCompletionFailureAsync()
    {
        var coordinator = new ClientSessionCoordinator();
        Require(coordinator.TryStart(out var session), "Startup was rejected.");
        await session.RunAsync(() => Task.CompletedTask, _ => throw new InvalidOperationException("UI completion fault"));
        Require(coordinator.Current is null && session.Completion.IsCompleted, "UI completion fault left restart waiting.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
