using System.Text.Json;
using NAudio.Wave;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class SpatialVoicePolicySelfTest
{
    public static int Run()
    {
        var failures = 0;
        foreach (var gameState in new[] { GameState.Tasks, GameState.Discussion })
        {
            var state = new AmongUsState { GameState = gameState };
            var settings = new SpatialVoiceSettings(ImpostorRadioEnabled: true);
            var sender = new Player { IsImpostor = true, X = 20 };

            Check($"{gameState}: impostor receives radio", true,
                SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true }, sender, settings, true).Audible);
            var radioMix = SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true }, sender, settings, true);
            Check($"{gameState}: radio high-pass", true, radioMix.RadioHighPass);
            Check($"{gameState}: radio echo", true, radioMix.RadioEcho);
            Check($"{gameState}: crewmate cannot receive radio", false,
                SpatialVoicePolicy.Calculate(state, new Player(), sender, settings, true).Audible);
            Check($"{gameState}: ghost receives radio", true,
                SpatialVoicePolicy.Calculate(state, new Player { IsDead = true }, sender, settings, true).Audible);
            Check($"{gameState}: radio stops when disabled", false,
                SpatialVoicePolicy.Calculate(state, new Player(), sender,
                    settings with { ImpostorRadioEnabled = false }, true).Audible);
            Check($"{gameState}: living cannot hear dead radio sender", false,
                SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true },
                    sender.WithDead(), settings, true).Audible);
        }

        var discussion = new AmongUsState { GameState = GameState.Discussion };
        Check("meeting: normal living conversation remains audible", true,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player(), new SpatialVoiceSettings()).Audible);
        Check("meeting: normal ghost voice remains private", false,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player { IsDead = true },
                new SpatialVoiceSettings()).Audible);

        var radioOnlyPolicy = new SpatialVoiceSettings(ImpostorRadioEnabled: true,
            ImpostorRadioOnlyMode: true, MeetingGhostOnly: true);
        var tasks = new AmongUsState { GameState = GameState.Tasks };
        Check("radio-only: proximity silenced", false,
            SpatialVoicePolicy.Calculate(tasks, new Player { IsImpostor = true },
                new Player { IsImpostor = true }, radioOnlyPolicy).Audible);
        Check("radio-only: impostor radio audible", true,
            SpatialVoicePolicy.Calculate(tasks, new Player { IsImpostor = true },
                new Player { IsImpostor = true }, radioOnlyPolicy, true).Audible);
        Check("radio-only: crew cannot hear radio", false,
            SpatialVoicePolicy.Calculate(tasks, new Player(),
                new Player { IsImpostor = true }, radioOnlyPolicy, true).Audible);

        var commsTasks = new AmongUsState { GameState = GameState.Tasks, CommsSabotaged = true };
        var commsPolicy = new SpatialVoiceSettings(CommsSabotage: true, ImpostorRadioEnabled: true);
        Check("comms: living crew cannot hear proximity", false,
            SpatialVoicePolicy.Calculate(commsTasks, new Player(), new Player(), commsPolicy).Audible);
        Check("comms: impostor still hears proximity", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player { IsImpostor = true },
                new Player(), commsPolicy).Audible);
        Check("comms: ghost still hears proximity", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player { IsDead = true },
                new Player(), commsPolicy).Audible);
        Check("comms: radio overrides block for eligible receiver", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player { IsImpostor = true },
                new Player { IsImpostor = true }, commsPolicy, true).Audible);
        Check("comms: disabled option leaves crew audible", true,
            SpatialVoicePolicy.Calculate(commsTasks, new Player(), new Player(),
                commsPolicy with { CommsSabotage = false }).Audible);

        var ghostListener = new Player { IsDead = true };
        var livingSpeaker = new Player();
        var ghostVolumePolicy = new SpatialVoiceSettings(CrewVolumeAsGhost: 0.35d,
            GhostVolumeAsImpostor: 0.1d, Haunting: true, ImpostorRadioEnabled: true);
        CheckGain("ghost volume: tasks proximity", 0.35d,
            SpatialVoicePolicy.Calculate(tasks, ghostListener, livingSpeaker, ghostVolumePolicy).Gain);
        CheckGain("ghost volume: meeting", 0.35d,
            SpatialVoicePolicy.Calculate(discussion, ghostListener, livingSpeaker, ghostVolumePolicy).Gain);
        CheckGain("ghost volume: radio", 0.35d,
            SpatialVoicePolicy.Calculate(tasks, ghostListener,
                new Player { IsImpostor = true, X = 20 }, ghostVolumePolicy, true).Gain);
        CheckGain("impostor hearing ghost: tasks", 0.1d,
            SpatialVoicePolicy.Calculate(tasks, new Player { IsImpostor = true },
                new Player { IsDead = true }, ghostVolumePolicy).Gain);

        var ventPolicy = new SpatialVoiceSettings(HearImpostorsInVents: true,
            ImpostorsHearImpostorsInVents: true, ImpostorRadioEnabled: true);
        var ventSpeaker = new Player { IsImpostor = true, InVent = true };
        var impostorListener = new Player { IsImpostor = true };
        var ventMix = SpatialVoicePolicy.Calculate(tasks, impostorListener, ventSpeaker, ventPolicy);
        Check("vent: proximity voice is muffled", true, ventMix.Muffled);
        CheckGain("vent: unmodified gain is halved", 0.5d, ventMix.Gain);
        Check("vent: meeting voice is not muffled", false,
            SpatialVoicePolicy.Calculate(discussion, impostorListener, ventSpeaker, ventPolicy).Muffled);
        var ventRadioMix = SpatialVoicePolicy.Calculate(tasks, impostorListener, ventSpeaker, ventPolicy, true);
        Check("vent: radio high-pass replaced by low-pass", true,
            ventRadioMix.Muffled && !ventRadioMix.RadioHighPass && ventRadioMix.RadioEcho);
        CheckGain("vent: radio gain is halved", 0.5d, ventRadioMix.Gain);
        var lowTone = MeasureFilteredRms(500d);
        var highTone = MeasureFilteredRms(8_000d);
        Check("vent DSP: low frequency remains audible", true, lowTone > 0.1d);
        Check("vent DSP: high frequency attenuated", true, highTone < lowTone * 0.25d);
        var radioLowTone = MeasureRadioFilteredRms(200d);
        var radioHighTone = MeasureRadioFilteredRms(4_000d);
        Check("radio DSP: low frequency attenuated", true, radioLowTone < radioHighTone * 0.25d);
        Check("radio DSP: high frequency remains audible", true, radioHighTone > 0.1d);
        var echo = new RadioEchoSampleProvider(new TestImpulseStereoSource()) { Enabled = true };
        var echoSamples = new float[17_282];
        echo.Read(echoSamples, 0, echoSamples.Length);
        CheckGain("radio echo DSP: dry signal", 0.92d, echoSamples[0]);
        CheckGain("radio echo DSP: first 90 ms reflection", 0.2d, echoSamples[8_640]);
        CheckGain("radio echo DSP: second reflection", 0.024d, echoSamples[17_280]);
        CheckGain("radio echo DSP: channels remain separate", 0d, echoSamples[8_641]);

        var lobbySettings = new LobbySettings
        {
            MaxDistance = 7.4d,
            JackalRadioEnabled = true,
            NosSizeVoiceEffect = false,
            NosFixerJammingVoiceBlock = false,
            ImpostorRadioEnabled = true,
            PublicLobbyOn = true,
            PublicLobbyTitle = "互換テスト"
        };
        var wire = lobbySettings.ToWireJson();
        using (var document = JsonDocument.Parse(wire))
        {
            Check("lobby wire: publicLobby_on", true,
                document.RootElement.TryGetProperty("publicLobby_on", out _));
            Check("lobby wire: jackalRadioEnabled", true,
                document.RootElement.TryGetProperty("jackalRadioEnabled", out _));
            Check("lobby wire: nosFixerJammingVoiceBlock", true,
                document.RootElement.TryGetProperty("nosFixerJammingVoiceBlock", out _));
        }
        Check("lobby wire: round trip", true,
            JsonSerializer.Deserialize<LobbySettings>(wire, LobbySettings.WireJsonOptions) == lobbySettings);

        var beforeRadioOnly = lobbySettings with
        {
            ImpostorRadioEnabled = false,
            HearImpostorsInVents = true,
            DeadOnly = true,
            MeetingGhostOnly = false,
            JackalRadioEnabled = true
        };
        var radioOnly = beforeRadioOnly.EnableImpostorRadioOnlyMode();
        Check("radio-only preset: enabled", true,
            radioOnly.ImpostorRadioOnlyMode && radioOnly.ImpostorRadioEnabled &&
            radioOnly.MeetingGhostOnly && !radioOnly.DeadOnly &&
            !radioOnly.HearImpostorsInVents && !radioOnly.JackalRadioEnabled);
        var editedRadioOnly = radioOnly with { Haunting = true };
        Check("radio-only preset: restore forced fields, retain free fields", true,
            editedRadioOnly.DisableImpostorRadioOnlyMode(beforeRadioOnly) ==
            (beforeRadioOnly with { Haunting = true }));

        Console.WriteLine(failures == 0
            ? "[PASS] 3.2.7 radio, listener-volume, and vent-muffle policy"
            : $"[FAIL] 3.2.7 radio, listener-volume, and vent-muffle policy: {failures} cases failed");
        return failures == 0 ? 0 : 1;

        void Check(string name, bool expected, bool actual)
        {
            if (expected == actual) return;
            failures++;
            Console.Error.WriteLine($"[FAIL] {name}: expected={expected} actual={actual}");
        }

        void CheckGain(string name, double expected, double actual)
        {
            if (Math.Abs(expected - actual) < 0.0001d) return;
            failures++;
            Console.Error.WriteLine($"[FAIL] {name}: expected={expected:0.###} actual={actual:0.###}");
        }
    }

    private static Player WithDead(this Player source) => new()
    {
        IsImpostor = source.IsImpostor,
        IsDead = true,
        X = source.X,
        Y = source.Y
    };

    private static double MeasureFilteredRms(double frequency)
    {
        var filter = new VentMuffleSampleProvider(new TestSineSource(frequency)) { Enabled = true };
        var samples = new float[4_800];
        filter.Read(samples, 0, samples.Length);
        var steadySamples = samples.AsSpan(2_400);
        var energy = 0d;
        foreach (var sample in steadySamples) energy += sample * sample;
        return Math.Sqrt(energy / steadySamples.Length);
    }

    private static double MeasureRadioFilteredRms(double frequency)
    {
        var filter = new RadioHighPassSampleProvider(new TestSineSource(frequency)) { Enabled = true };
        var samples = new float[4_800];
        filter.Read(samples, 0, samples.Length);
        var steadySamples = samples.AsSpan(2_400);
        var energy = 0d;
        foreach (var sample in steadySamples) energy += sample * sample;
        return Math.Sqrt(energy / steadySamples.Length);
    }

    private sealed class TestImpulseStereoSource : ISampleProvider
    {
        private bool sent;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            if (!sent && count > 0)
            {
                buffer[offset] = 1f;
                sent = true;
            }
            return count;
        }
    }

    private sealed class TestSineSource(double frequency) : ISampleProvider
    {
        private long position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var index = 0; index < count; index++)
            {
                buffer[offset + index] = 0.25f * (float)Math.Sin(2d * Math.PI * frequency * position++ / 48_000d);
            }
            return count;
        }
    }
}
