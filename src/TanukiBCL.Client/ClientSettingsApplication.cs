namespace TanukiBCL.Client;

// These are currently fixed when the .NET probe/audio session is constructed.
// Levels, activation mode, NAT policy, lobby rules, and appearance are live values.
internal sealed record ClientSessionSettings(string ServerUrl, string? MicrophoneName,
    string? SpeakerName, bool EchoCancellation, bool NoiseSuppression, bool AutoGainControl)
{
    public static ClientSessionSettings From(ClientSettings settings) => new(
        settings.ServerUrl, settings.MicrophoneName, settings.SpeakerName,
        settings.EchoCancellation, settings.NoiseSuppression, settings.AutoGainControl);
}

// Settings can change repeatedly while native audio/socket teardown is awaiting.
// Keep the original stop/mute state, then start once using the latest settings.
// Callbacks execute on the caller's context (the WPF dispatcher in MainWindow).
internal sealed class CoalescedSessionRestart
{
    private readonly object gate = new();
    private TaskCompletionSource? pending;

    public Task Request(Func<Task> stopAsync, Func<bool> canRestart,
        Action restartLatest, Action<Exception> reportFailure)
    {
        TaskCompletionSource request;
        lock (gate)
        {
            if (pending is not null) return pending.Task;
            request = pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RunAsync(request, stopAsync, canRestart, restartLatest, reportFailure);
        return request.Task;
    }

    private async Task RunAsync(TaskCompletionSource request, Func<Task> stopAsync,
        Func<bool> canRestart, Action restartLatest, Action<Exception> reportFailure)
    {
        try
        {
            await stopAsync();
            if (canRestart())
            {
                // A setting changed after the replacement starts belongs to its
                // own restart, not the already-completed stop of the old session.
                Release(request);
                restartLatest();
            }
        }
        catch (Exception error)
        {
            try { reportFailure(error); }
            catch (Exception reportingError)
            {
                System.Diagnostics.Trace.TraceError($"Settings restart reporting failed: {reportingError}");
            }
        }
        finally
        {
            Release(request);
            request.TrySetResult();
        }
    }

    private void Release(TaskCompletionSource request)
    {
        lock (gate)
        {
            if (ReferenceEquals(pending, request)) pending = null;
        }
    }
}
