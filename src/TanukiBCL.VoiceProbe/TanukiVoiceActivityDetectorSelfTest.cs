using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class TanukiVoiceActivityDetectorSelfTest
{
    public static int Run()
    {
        var detector = new TanukiVoiceActivityDetector();
        var sampleOffset = 0;

        for (var block = 0; block < 10; block++)
        {
            detector.ProcessPcm16(CreateTone(150d, 0.5d, ref sampleOffset));
        }
        if (!detector.IsTalking)
        {
            Console.Error.WriteLine($"[FAIL] 150Hz voice-band tone did not start VAD; band={detector.LastBandLevel:0.000}");
            return 1;
        }

        for (var block = 0; block < 36; block++)
        {
            detector.ProcessPcm16(new byte[1024 * sizeof(short)]);
        }
        if (detector.IsTalking)
        {
            Console.Error.WriteLine("[FAIL] Silence did not stop VAD.");
            return 1;
        }

        var outsideBand = new TanukiVoiceActivityDetector();
        sampleOffset = 0;
        for (var block = 0; block < 12; block++)
        {
            outsideBand.ProcessPcm16(CreateTone(1000d, 0.5d, ref sampleOffset));
        }
        if (outsideBand.IsTalking)
        {
            Console.Error.WriteLine($"[FAIL] 1000Hz out-of-band tone started VAD; band={outsideBand.LastBandLevel:0.000}");
            return 1;
        }

        var highThreshold = new TanukiVoiceActivityDetector();
        highThreshold.SetMinimumNoiseLevel(0.8d);
        var defaultThreshold = new TanukiVoiceActivityDetector();
        sampleOffset = 0;
        for (var block = 0; block < 12; block++)
        {
            var quietTone = CreateTone(150d, 0.01d, ref sampleOffset);
            highThreshold.ProcessPcm16(quietTone);
            defaultThreshold.ProcessPcm16(quietTone);
        }
        if (highThreshold.IsTalking || !defaultThreshold.IsTalking)
        {
            Console.Error.WriteLine($"[FAIL] Sensitivity levels did not split quiet speech; default={defaultThreshold.IsTalking} high={highThreshold.IsTalking} band={highThreshold.LastBandLevel:0.000}");
            return 1;
        }

        if (!MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.Voice, false, false) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToTalk, false, false) ||
            !MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToTalk, true, false) ||
            !MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToMute, false, false) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToMute, true, false) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.Voice, true, true) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.Voice, false, false, true) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToTalk, true, false, true) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToMute, false, false, true) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.Voice, false, false, false, true) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToTalk, true, false, false, true) ||
            MicrophoneActivationPolicy.AllowsAudio(MicrophoneActivationMode.PushToMute, false, false, false, true))
        {
            Console.Error.WriteLine("[FAIL] Microphone activation, manual mute, deafen, or jam policy.");
            return 1;
        }

        var nosState = new AmongUsState
        {
            Mod = AmongUsModType.NebulaOnTheShip,
            Players =
            [
                new Player { IsLocal = true, NosPlayer = new NosPlayerData { IsJammed = true } },
                new Player { NosPlayer = new NosPlayerData { IsJammed = true } }
            ]
        };
        var jamBlockOn = new LobbySettings { NosFixerJammingVoiceBlock = true };
        var jamBlockOff = new LobbySettings { NosFixerJammingVoiceBlock = false };
        if (!LocalJammingPolicy.IsJammed(nosState, jamBlockOn) ||
            LocalJammingPolicy.IsJammed(nosState, jamBlockOff))
        {
            Console.Error.WriteLine("[FAIL] NoS local jamming transmit policy.");
            return 1;
        }
        nosState.Mod = AmongUsModType.None;
        if (LocalJammingPolicy.IsJammed(nosState, jamBlockOn))
        {
            Console.Error.WriteLine("[FAIL] Non-NoS local jamming incorrectly blocked the microphone.");
            return 1;
        }
        nosState.Mod = AmongUsModType.NebulaOnTheShip;
        nosState.Players[0].NosPlayer!.IsJammed = false;
        if (LocalJammingPolicy.IsJammed(nosState, jamBlockOn) ||
            LocalJammingPolicy.IsJammed(null, jamBlockOn))
        {
            Console.Error.WriteLine("[FAIL] Remote NoS jamming incorrectly blocked the local microphone.");
            return 1;
        }
        nosState.GameState = GameState.Tasks;
        nosState.Players[0].ShiftedColor = 7;
        if (!LocalVadVisibilityPolicy.IsHidden(nosState, jamBlockOn))
        {
            Console.Error.WriteLine("[FAIL] Shifted local player VAD was visible during Tasks.");
            return 1;
        }
        nosState.GameState = GameState.Discussion;
        if (LocalVadVisibilityPolicy.IsHidden(nosState, jamBlockOn))
        {
            Console.Error.WriteLine("[FAIL] Shifted local player VAD remained hidden during discussion.");
            return 1;
        }
        nosState.Players[0].NosPlayer!.IsJammed = true;
        if (!LocalVadVisibilityPolicy.IsHidden(nosState, jamBlockOn) ||
            LocalVadVisibilityPolicy.IsHidden(nosState, jamBlockOff) ||
            LocalVadVisibilityPolicy.IsHidden(null, jamBlockOn))
        {
            Console.Error.WriteLine("[FAIL] NoS jamming VAD visibility did not follow active settings.");
            return 1;
        }

        Console.WriteLine("[PASS] 3.2.8 frequency-band VAD, activation, deafen, NoS jamming, and VAD visibility policy");
        return 0;
    }

    private static byte[] CreateTone(double frequency, double amplitude, ref int sampleOffset)
    {
        var pcm = new byte[1024 * sizeof(short)];
        for (var sample = 0; sample < 1024; sample++)
        {
            var value = (short)Math.Round(Math.Sin(2d * Math.PI * frequency * sampleOffset++ / 48_000d) *
                                          amplitude * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(sample * sizeof(short), sizeof(short)), value);
        }
        return pcm;
    }
}
