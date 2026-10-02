using System.IO;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

internal static class ClientSettingsTransactionSelfTest
{
    public static int Run()
    {
        try
        {
            VerifyNoOpAndNormalization();
            VerifyPreviewAndFlush();
            VerifyPropertyScopedPersistence();
            VerifySaveFailureAndRetry();
            VerifySnapshotsAndUnrelatedProperties();
            Console.WriteLine("[PASS] settings transactions: no-op, preview/commit, key-scoped persistence, failed-save retry and isolated snapshots");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[FAIL] settings transaction: {error.Message}");
            return 1;
        }
    }

    private static void VerifyNoOpAndNormalization()
    {
        var target = CreateSettings();
        target.MasterVolume = 200;
        var writes = 0;
        var notifications = 0;
        var transaction = new ClientSettingsTransaction(target, _ => writes++);
        transaction.Changed += _ => notifications++;
        var candidate = target.Clone();
        candidate.MasterVolume = 999;
        candidate.PlayerConfigMap = candidate.PlayerConfigMap.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert(!transaction.Apply(candidate, true), "normalized no-op reported a runtime change");
        transaction.Flush();
        Assert(writes == 0 && notifications == 0, "normalized no-op wrote or notified");
        Assert(candidate.MasterVolume == 999, "normalization mutated the caller's candidate");
        candidate.MicSensitivity = double.NaN;
        candidate.MyLobbySettings = candidate.MyLobbySettings with { MaxDistance = double.PositiveInfinity };
        Assert(transaction.Apply(candidate, false), "non-finite input did not normalize");
        Assert(target.MicSensitivity == 0.15d && target.MyLobbySettings.MaxDistance == 5.32d,
            "non-finite settings reached runtime");
    }

    private static void VerifyPreviewAndFlush()
    {
        var target = CreateSettings();
        var saved = new List<ClientSettings>();
        var changes = new List<ClientSettingsChange>();
        var transaction = new ClientSettingsTransaction(target, settings => saved.Add(settings));
        transaction.Changed += changes.Add;
        var candidate = target.Clone();
        candidate.MasterVolume = 75;
        Assert(transaction.Apply(candidate, false), "preview was not applied");
        Assert(target.MasterVolume == 75 && saved.Count == 0 && changes.Count == 1, "preview saved prematurely or missed notification");
        Assert(!transaction.Apply(candidate, true), "commit of the same preview emitted another change");
        Assert(saved.Count == 1 && saved[0].MasterVolume == 75 && changes.Count == 1, "same-value preview commit was not saved exactly once");
        transaction.Flush();
        Assert(saved.Count == 1, "clean flush wrote settings again");
        candidate.MasterVolume = 65;
        transaction.Apply(candidate, false);
        transaction.Flush();
        transaction.Flush();
        Assert(saved.Count == 2 && saved[1].MasterVolume == 65 && changes.Count == 2, "flush did not save dirty runtime exactly once without a notification");
    }

    private static void VerifyPropertyScopedPersistence()
    {
        var target = CreateSettings();
        var saved = new List<ClientSettings>();
        var notifications = 0;
        var transaction = new ClientSettingsTransaction(target, settings => saved.Add(settings));
        transaction.Changed += _ => notifications++;
        var staleCandidate = target.Clone();
        var volume = target.Clone();
        volume.MasterVolume = 37;
        transaction.Apply(volume, false, nameof(ClientSettings.MasterVolume));
        staleCandidate.AlwaysOnTop = !target.AlwaysOnTop;
        transaction.Apply(staleCandidate, true, nameof(ClientSettings.AlwaysOnTop));
        Assert(target.MasterVolume == 37, "unrelated key commit reverted a preview from a stale candidate");
        Assert(saved.Count == 1 && saved[0].MasterVolume == 100 && saved[0].AlwaysOnTop == target.AlwaysOnTop,
            "unrelated key commit persisted an uncommitted volume preview");
        Assert(!transaction.Apply(target.Clone(), true, nameof(ClientSettings.MasterVolume)), "preview commit notified despite unchanged runtime");
        Assert(saved.Count == 2 && saved[1].MasterVolume == 37 && saved[1].AlwaysOnTop == target.AlwaysOnTop && notifications == 2,
            "volume commit did not preserve the other committed key");
        transaction.Flush();
        Assert(saved.Count == 2, "key commits did not clear their dirty values");
    }

    private static void VerifySaveFailureAndRetry()
    {
        var target = CreateSettings();
        var baseline = target.Clone();
        var fail = true;
        var attempts = 0;
        var notifications = 0;
        var saved = new List<ClientSettings>();
        var transaction = new ClientSettingsTransaction(target, settings =>
        {
            attempts++;
            if (fail)
            {
                settings.PlayerConfigMap.Clear();
                settings.MasterVolume = 1;
                throw new IOException("simulated settings write failure");
            }
            saved.Add(settings);
        });
        transaction.Changed += _ => notifications++;
        var candidate = target.Clone();
        candidate.MasterVolume = 42;
        ExpectWriteFailure(() => transaction.Apply(candidate, true));
        Assert(target.ContentEquals(baseline) && notifications == 0 && attempts == 1, "failed apply changed runtime or notified");
        fail = false;
        Assert(transaction.Apply(candidate, true) && target.MasterVolume == 42 && saved.Count == 1 && notifications == 1,
            "failed apply could not retry successfully");

        candidate.MasterVolume = 23;
        transaction.Apply(candidate, false, nameof(ClientSettings.MasterVolume));
        fail = true;
        ExpectWriteFailure(transaction.Flush);
        Assert(target.MasterVolume == 23 && target.PlayerConfigMap.Count == baseline.PlayerConfigMap.Count && notifications == 2,
            "failed flush corrupted preview runtime or emitted a change");
        fail = false;
        transaction.Flush();
        transaction.Flush();
        Assert(attempts == 4 && saved.Count == 2 && saved[1].MasterVolume == 23 && notifications == 2,
            "failed flush lost its dirty state or did not clear it after a successful retry");
    }

    private static void VerifySnapshotsAndUnrelatedProperties()
    {
        var target = CreateSettings();
        var baseline = target.Clone();
        ClientSettings? persistedArgument = null;
        var changes = new List<ClientSettingsChange>();
        var transaction = new ClientSettingsTransaction(target, settings => persistedArgument = settings);
        transaction.Changed += changes.Add;
        var candidate = target.Clone();
        candidate.MasterVolume = 67;
        transaction.Apply(candidate, true);
        var expected = baseline.Clone();
        expected.MasterVolume = 67;
        Assert(target.ContentEquals(expected), "changing one property lost unrelated settings");
        Assert(changes[0].Previous.ContentEquals(baseline) && changes[0].Current.ContentEquals(expected),
            "notification snapshots did not describe full previous/current runtime");

        candidate.ServerUrls.Clear();
        candidate.PlayerConfigMap.Clear();
        changes[0].Previous.ServerUrls.Clear();
        changes[0].Current.PlayerConfigMap.Clear();
        persistedArgument!.ServerUrls.Clear();
        persistedArgument.PlayerConfigMap.Clear();
        Assert(target.ContentEquals(expected), "candidate, notification or persistence snapshot aliases shared runtime");
        var currentSnapshot = changes[0].Current;
        target.ServerUrls.Add("https://new.example.test");
        Assert(!currentSnapshot.ServerUrls.Contains("https://new.example.test"), "later runtime mutation changed an old notification snapshot");

        var copied = new ClientSettings();
        copied.CopyFrom(expected);
        Assert(copied.ContentEquals(expected), "whole settings copy omitted a property");
        copied.ServerUrls.Clear();
        copied.PlayerConfigMap.Clear();
        Assert(expected.ServerUrls.Count > 0 && expected.PlayerConfigMap.Count == 2, "CopyFrom did not deep-copy nested collections");
    }

    private static ClientSettings CreateSettings()
    {
        var settings = new ClientSettings
        {
            ServerUrl = "https://voice.example.test", ServerUrls = ["https://bettercrewl.ink", "https://voice.example.test"],
            MicrophoneName = "test microphone", SpeakerName = "test speaker", EnableOverlay = true, CompactOverlay = true,
            MeetingOverlay = false, OverlayPosition = "bottom_left", HideCode = true, ObsOverlay = true, ObsSecret = "ABC123XYZ",
            NatFix = true, MobileHost = false, EnableSpatialAudio = false, EchoCancellation = false, NoiseSuppression = false,
            AutoGainControl = true, VoiceEffectStrength = 42, CrewVolumeAsGhost = 65, GhostVolumeAsImpostor = 26,
            MicrophoneGain = 136, MicrophoneGainEnabled = true, MicSensitivity = 0.4d, MicSensitivityEnabled = true,
            PushToTalkMode = MicrophoneActivationMode.PushToTalk, PushToTalkShortcut = "G", ImpostorRadioShortcut = "T",
            MuteShortcut = "F7", DeafenShortcut = "F8",
            MyLobbySettings = new LobbySettings { MaxDistance = 7.2d, ImpostorRadioEnabled = true, WallsBlockAudio = true },
            RadioOnlyBackup = new LobbySettings { MaxDistance = 3.7d, Haunting = true },
            PlayerConfigMap = new() { [3] = new PlayerAudioConfig(0.7d, true), [8] = new PlayerAudioConfig(1.4d) }
        };
        settings.Normalize();
        return settings;
    }

    private static void ExpectWriteFailure(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("simulated write failure was not propagated");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
