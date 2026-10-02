using System.IO;

namespace TanukiBCL.Client;

internal static class ClientSettingsApplicationSelfTest
{
    public static void Run() => Task.Run(VerifyAsync).GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        VerifyRestartClassification();
        await VerifyLatestSettingsAndMuteStateAsync();
        await VerifyExplicitStopAsync();
        await VerifyFailureRecoveryAsync();
        await VerifyReplacementChangeAsync();
    }

    private static void VerifyRestartClassification()
    {
        var initial = new ClientSettings();
        var key = ClientSessionSettings.From(initial);
        foreach (Action<ClientSettings> change in new Action<ClientSettings>[]
        {
            settings => settings.ServerUrl = "https://example.invalid",
            settings => settings.MicrophoneName = "another mic",
            settings => settings.SpeakerName = "another speaker",
            settings => settings.EchoCancellation = false,
            settings => settings.NoiseSuppression = false,
            settings => settings.AutoGainControl = true
        })
        {
            var changed = new ClientSettings();
            change(changed);
            Require(ClientSessionSettings.From(changed) != key, "A fixed session setting did not request restart.");
        }
        initial.MasterVolume = 24;
        initial.MicrophoneGain = 240;
        initial.MicrophoneGainEnabled = true;
        initial.MicSensitivity = 0.4;
        initial.MicSensitivityEnabled = true;
        initial.NatFix = true;
        initial.MobileHost = false;
        initial.EnableSpatialAudio = false;
        initial.PushToTalkMode = TanukiBCL.VoiceProbe.MicrophoneActivationMode.PushToTalk;
        initial.MyLobbySettings = initial.MyLobbySettings with { MaxDistance = 9 };
        initial.HideCode = true;
        initial.EnableOverlay = true;
        Require(ClientSessionSettings.From(initial) == key, "A live setting unnecessarily requested a session restart.");
    }

    private static async Task VerifyLatestSettingsAndMuteStateAsync()
    {
        var queue = new CoalescedSessionRestart();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = "server A";
        string? startedWith = null;
        var stops = 0;
        var starts = 0;
        var errors = new List<Exception>();
        // Stopping clears runtime UI flags; a pending restart must retain the
        // flags from the original running session, not a later settings event.
        var runtimeMuted = true;
        var runtimeDeafened = true;
        var savedMuted = runtimeMuted;
        var savedDeafened = runtimeDeafened;
        var first = queue.Request(async () =>
        {
            stops++;
            runtimeMuted = runtimeDeafened = false;
            await stopped.Task;
        }, () => true, () =>
        {
            starts++;
            startedWith = latest;
            runtimeMuted = savedMuted;
            runtimeDeafened = savedDeafened;
        }, errors.Add);
        latest = "server B";
        var second = queue.Request(() => throw new InvalidOperationException("duplicate stop"),
            () => true, () => throw new InvalidOperationException("stale restart"), errors.Add);
        latest = "server C";
        Require(ReferenceEquals(first, second), "Concurrent settings changes were not coalesced.");
        Require(stops == 1 && starts == 0, "Restart overlapped the old session teardown.");
        stopped.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Require(stops == 1 && starts == 1 && startedWith == "server C", "Restart did not use only the latest settings.");
        Require(runtimeMuted && runtimeDeafened && errors.Count == 0, "Restart lost mute/deafen state or failed.");
    }

    private static async Task VerifyExplicitStopAsync()
    {
        var queue = new CoalescedSessionRestart();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = 1;
        var capturedIntent = intent;
        var starts = 0;
        var errors = new List<Exception>();
        var request = queue.Request(() => stopped.Task, () => capturedIntent == intent,
            () => starts++, errors.Add);
        intent++; // Explicit Stop, Close, or manual Start supersedes this request.
        stopped.TrySetResult();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        Require(starts == 0 && errors.Count == 0, "Settings restart ignored an explicit user intent.");
    }

    private static async Task VerifyFailureRecoveryAsync()
    {
        var queue = new CoalescedSessionRestart();
        var errors = new List<Exception>();
        var starts = 0;
        await queue.Request(() => throw new IOException("stop fault"), () => true,
            () => starts++, errors.Add);
        await queue.Request(() => Task.CompletedTask, () => true,
            () => throw new InvalidOperationException("start fault"), errors.Add);
        await queue.Request(() => Task.CompletedTask, () => true, () => starts++, errors.Add);
        Require(errors.Count == 2 && errors[0].Message == "stop fault" &&
            errors[1].Message == "start fault" && starts == 1,
            "A failed restart hid the error or prevented subsequent requests.");
    }

    private static async Task VerifyReplacementChangeAsync()
    {
        var queue = new CoalescedSessionRestart();
        var secondStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        var starts = 0;
        Task? followup = null;
        await queue.Request(() => Task.CompletedTask, () => true, () =>
        {
            starts++;
            // This change concerns the replacement, and must not be swallowed by
            // the finishing request for the previous session.
            followup = queue.Request(() => secondStop.Task, () => true, () => starts++, errors.Add);
        }, errors.Add);
        Require(followup is { IsCompleted: false }, "Replacement settings change was swallowed.");
        secondStop.TrySetResult();
        await followup!.WaitAsync(TimeSpan.FromSeconds(5));
        Require(starts == 2 && errors.Count == 0, "Replacement settings did not converge.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
