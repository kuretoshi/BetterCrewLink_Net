using NAudio.Wave;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class VoiceEffectRuntimeSelfTest
{
    public static void Verify(Action<string, bool, bool> check)
    {
        var checks = 0;
        var failures = 0;
        void Assert(string name, bool result)
        {
            checks++;
            if (!result) failures++;
            check("Voice tuning runtime: " + name, true, result);
        }
        foreach (var id in VoiceEffectTunings.Ids)
        {
            var source = new Samples(1, Tone(440, 4_800));
            var chain = new VoiceEffectChain(source, bibiriPreview: id == "nosBibiri");
            chain.SetTuning(new VoiceEffectTuning(), id == "nosBerserker" ? 1 : 0);
            var output = new float[9_600];
            chain.Output.Read(output, 0, output.Length);
            Assert(id + " all-zero is sample-exact raw voice without IR/echo", source.Data.SelectMany(x => new[] { x, x })
                .SequenceEqual(output) && chain.Reverb.PreparedImpulseBytes == 0 && chain.Echo.DelayCapacitySamples == 0);
        }
        foreach (var pitch in new[] { -100d, -50d, 50d, 100d })
        {
            var original = Voiced(120, 96_000);
            var chain = new VoiceEffectChain(new Samples(1, original));
            chain.SetTuning(new VoiceEffectTuning(pitch));
            var output = new float[192_000];
            chain.Output.Read(output, 0, output.Length);
            var mono = output.Where((_, index) => index % 2 == 0).Skip(48_000).ToArray();
            var expected = 120 * Math.Pow(2, pitch / 100);
            var amplitude = Amplitude(mono, expected);
            var originalAmplitude = Amplitude(original.Skip(48_000).ToArray(), expected);
            Assert($"pitch {pitch} shifts voiced 120 Hz to {expected} Hz without room/echo", output.All(float.IsFinite) &&
                amplitude > .0001 && !mono.SequenceEqual(original.Skip(48_000)) &&
                (Math.Abs(pitch) != 50 || amplitude > originalAmplitude * 3) &&
                chain.Reverb.PreparedImpulseBytes == 0 && chain.Echo.DelayCapacitySamples == 0);
            Console.WriteLine($"Voice tuning pitch {pitch}: expected={expected:F3}Hz amplitude={amplitude:F6}, raw-bin={originalAmplitude:F6}");
        }
        var stereo = new float[192_000];
        var voiceLeft = Voiced(120, 96_000);
        var voiceRight = Voiced(170, 96_000);
        for (var frame = 0; frame < stereo.Length / 2; frame++)
        {
            stereo[frame * 2] = voiceLeft[frame];
            stereo[frame * 2 + 1] = voiceRight[frame];
        }
        var stereoTone = new TunedVoiceSampleProvider(new Samples(2, stereo));
        stereoTone.SetTuning(new VoiceEffectTuning(100));
        var shifted = new float[stereo.Length];
        stereoTone.Read(shifted, 0, shifted.Length);
        var left = shifted.Where((_, index) => index % 2 == 0).Skip(48_000).ToArray();
        var right = shifted.Where((_, index) => index % 2 != 0).Skip(48_000).ToArray();
        Assert("stereo pitch keeps independent left/right frequencies", Amplitude(left, 240) > .0001 &&
            Amplitude(right, 340) > .0001 && Amplitude(left, 340) < Amplitude(left, 240) / 3 &&
            Amplitude(right, 240) < Amplitude(right, 340) / 3);

        var voiced = Voiced(120, 24_000);
        var monoTone = new TunedVoiceSampleProvider(new Samples(1, voiced));
        var liveChain = new VoiceEffectChain(new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(monoTone), toneProvider: monoTone);
        var previewChain = new VoiceEffectChain(new Samples(1, voiced));
        var allAxes = new VoiceEffectTuning(35, 27, 41);
        liveChain.SetTuning(allAxes, .41); previewChain.SetTuning(allAxes, .41);
        var liveOutput = new float[48_000]; var previewOutput = new float[48_000];
        liveChain.Output.Read(liveOutput, 0, liveOutput.Length);
        previewChain.Output.Read(previewOutput, 0, previewOutput.Length);
        Assert("live retains one mono pitch DSP and matches preview processing", monoTone.PitchChannelCount == 1 &&
            liveOutput.SequenceEqual(previewOutput));
        var expectedOrder = voiced.ToArray();
        var expectedPitch = new SourceFilterDsp(48_000); expectedPitch.SetParameters(allAxes.PitchRatio, 1, 0);
        expectedPitch.Process(expectedOrder, 0, expectedOrder.Length);
        new BerserkerDsp(48_000).Process(expectedOrder, 0, expectedOrder.Length, .41);
        var ordered = new TunedVoiceSampleProvider(new Samples(1, voiced)); ordered.SetTuning(allAxes, .41);
        var actualOrder = new float[voiced.Length]; ordered.Read(actualOrder, 0, actualOrder.Length);
        Assert("pitch precedes nonlinear Berserker grit after the entry fade", actualOrder.Skip(20_000)
            .Zip(expectedOrder.Skip(20_000), (actual, expected) => Math.Abs(actual - expected)).Max() < .0001);

        var pulse = new float[48_000]; pulse[0] = .5f;
        var echoChain = new VoiceEffectChain(new Samples(1, pulse));
        echoChain.SetTuning(new VoiceEffectTuning(0, 0, 100));
        var echoes = new float[96_000]; echoChain.Output.Read(echoes, 0, echoes.Length);
        Assert("generic echo uses 180 ms, .12 wet and .22 feedback without pitch/IR", Math.Abs(echoes[17_280] - .06f) < 1e-6 &&
            Math.Abs(echoes[34_560] - .0132f) < 1e-6 && echoChain.Reverb.PreparedImpulseBytes == 0);
        var roomA = new VoiceEffectChain(new Samples(1, pulse));
        var roomB = new VoiceEffectChain(new Samples(1, pulse));
        roomA.SetTuning(new VoiceEffectTuning(0, 50)); roomB.SetTuning(new VoiceEffectTuning(0, 80));
        Assert("room uses the shared released impulse", roomA.Reverb.SharesImpulseWith(roomB.Reverb));
        roomA.SetTuning(new VoiceEffectTuning());
        var dry = new float[96_000]; roomA.Output.Read(dry, 0, dry.Length);
        Assert("clearing a profile removes all room/echo tails", dry.SequenceEqual(pulse.SelectMany(x => new[] { x, x })));

        VerifySelection(Assert);
        VerifyBibiri(Assert);
        Console.WriteLine($"[{(failures == 0 ? "PASS" : "FAIL")}] Voice tuning runtime: {checks} assertions; signed voice frequencies, mono/stereo processing, gates and Bibiri event tails");
    }

    private static void VerifySelection(Action<string, bool> assert)
    {
        var listener = new Player { Id = 1 };
        var speaker = new Player { Id = 2, Name = "original", AppearanceName = "changed" };
        var state = new AmongUsState { GameState = GameState.Tasks, Players = [listener, speaker] };
        var lobby = new LobbySettings { VoiceEffectEnabled = true };
        var values = VoiceEffectTunings.Ids.ToDictionary(id => id, _ => new VoiceEffectTuningOverride { Pitch = 20, Reverb = 30, Echo = 40 });
        var strengths = new VoiceEffectStrengths { Tunings = values, SnrJumboVoiceEffectStrength = 0,
            NosSizeVoiceEffectStrength = 0, NosRokurokubiVoiceEffectStrength = 0, NosBerserkerVoiceEffectStrength = 0,
            NosCitrusVoiceEffectStrength = 0, NosRainbowStarEchoStrength = 0, NosVoiceJammingEffectStrength = 0,
            NosMegaphoneEffectStrength = 0, NosBibiriEchoStrength = 0, SnrPushReverbStrength = 0 };
        SelectedVoiceEffect Select(bool audible = true) => VoiceEffectRuntimePolicy.Select(state, listener, speaker,
            lobby, new SpatialVoiceSettings(), new PeerVoiceMix(audible ? 1 : 0, 0, 0, "test"), strengths, 0, false);
        bool Expected() => Select().Tuning == new VoiceEffectTuning(20, 30, 40);
        assert("disguise explicit axes activate despite legacy zero", Expected());
        state.Mod = AmongUsModType.SuperNewRoles;
        speaker.SnrRole = new SnrRoleData(1, "Crewmate", 1, "JumboModifier", null, null, JumboCurrentSize: 2, JumboMaxSize: 2);
        assert("Jumbo explicit axes keep live-size gate", Expected());
        speaker.SnrRole = null; speaker.AppearanceName = speaker.Name;
        speaker.IsDead = true; speaker.SnrPushedFalling = true;
        assert("SNR push axes require the real falling flag", Expected() && Select().PushActive);
        speaker.SnrPushedFalling = false;
        assert("SNR fall end immediately clears all three axes", Select().Tuning.IsNeutral && !Select().PushActive);
        speaker.IsDead = false;
        state.Mod = AmongUsModType.NebulaOnTheShip;
        speaker.NosPlayer = new NosPlayerData { BodyRateX = 2, BodyRateY = 2 };
        assert("NoS size explicit axes keep size gate", Expected());
        speaker.NosPlayer = new NosPlayerData { BodyType = 3, NeckLength = 40 };
        assert("Rokuro axes keep extended-neck gate", Expected());
        speaker.NosPlayer = new NosPlayerData { BodyType = 2 };
        speaker.NosRole = new NosRoleData(1, "berserker", "Berserker", "Berserker.Instance", false, 2, true);
        assert("Berserker grit follows its own largest axis", Expected() && Select().Grit == .4);
        speaker.NosPlayer = new NosPlayerData { Hat = new NosCostumeData("noshat_catudon_Citrus_Orange") };
        speaker.NosRole = null;
        assert("Citrus axes keep costume gate", Expected());
        speaker.NosRole = new NosRoleData(1, "star", "Star", "Star.Instance", true);
        assert("Star combines pitch once and wet axes by max", Select().Tuning == new VoiceEffectTuning(40, 30, 40));
        speaker.NosPlayer = null;
        assert("Star alone can use all three axes", Expected());
        speaker.NosRole = null;
        state.GameState = GameState.Discussion;
        state.NosVoiceJamming = [new NosVoiceJammingState { PlayerId = 2, Pitch = 1.5 }];
        assert("meeting jamming explicit axes keep target gate", Expected());
        state.NosVoiceJamming = []; state.GameState = GameState.Tasks;
        state.NosMegaphones = [new NosMegaphoneState { PlayerId = 2, Energy = .5 }];
        assert("megaphone explicit axes keep charging gate", Expected());
        assert("inaudible peers bypass role profiles", Select(false).Tuning.IsNeutral);
        assert("Bibiri resolves its independent profile", Select().BibiriTuning == new VoiceEffectTuning(20, 30, 40));
    }

    private static void VerifyBibiri(Action<string, bool> assert)
    {
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Tasks };
        var enabled = new VoiceEffectTuning(0, 0, 100);
        assert("NoS without Tanuki addon cannot start a recording", !VoiceEffectRuntimePolicy.ShouldRecordBibiri(state, enabled));
        state.NosAddonIds = ["tanuki.bcl.roles"];
        assert("Tanuki addon permits non-neutral event recording", VoiceEffectRuntimePolicy.ShouldRecordBibiri(state, enabled));
        assert("neutral event profile cannot start a recording", !VoiceEffectRuntimePolicy.ShouldRecordBibiri(state, new VoiceEffectTuning()));
        state.GameState = GameState.Lobby;
        assert("lobby cannot retain a Tanuki recording", !VoiceEffectRuntimePolicy.ShouldRecordBibiri(state, enabled));
        var input = new Samples(2, Tone(440, 48_000).SelectMany(x => new[] { x, 0f }).ToArray());
        var recording = new NosDeathEchoSampleProvider(input);
        var tuning = new VoiceEffectTuning(100, 0, 0);
        recording.Update(true, false, null, 10_000, tuning);
        var live = new float[96_000]; recording.Read(live, 0, live.Length);
        assert("Bibiri axes leave ordinary live voice sample-exact", live.SequenceEqual(input.Data));
        input.Silent = true;
        recording.Update(true, true, null, 10_000, tuning);
        recording.Read(new float[9_600], 0, 9_600);
        recording.Update(true, true, 10_100, 10_100, tuning);
        var tail = new float[96_000]; recording.Read(tail, 0, tail.Length);
        assert("Bibiri echo-zero still plays a pitched event once after delayed RPC", tail.Take(38_400).Any(x => Math.Abs(x) > .001) &&
            tail.Skip(48_000).All(x => Math.Abs(x) < .0001) && tail.Where((_, i) => i % 2 != 0).All(x => x == 0));
        recording.Update(true, true, 10_100, 10_101, new VoiceEffectTuning());
        assert("Bibiri all-zero releases its recording and replay", !recording.HasHistory && !recording.IsReplaying);
        var after = new float[9_600]; recording.Read(after, 0, after.Length);
        assert("Bibiri all-zero has no leaked pitch/IR tail", after.All(x => x == 0));
        foreach (var stop in new[] { false, true })
        {
            input.Reset(); input.Silent = false;
            recording.Update(true, false, null, 20_000, new VoiceEffectTuning(0, 100, 100));
            recording.Read(live, 0, live.Length); input.Silent = true;
            recording.Update(true, true, 20_000 + (stop ? 1 : 0), 20_001, new VoiceEffectTuning(0, 100, 100));
            if (stop) recording.Stop();
            var output = new float[48_000 * 2 * 3]; recording.Read(output, 0, output.Length);
            assert(stop ? "Bibiri force-stop silences an enabled room immediately" : "Bibiri final envelope cuts room at 2.4 seconds",
                (stop ? output : output.Skip(230_400)).All(x => x == 0));
        }
    }

    private static float[] Voiced(double fundamental, int count)
    {
        var samples = new float[count];
        for (var frequency = fundamental; frequency < 6_000; frequency += fundamental)
        {
            var amplitude = .03 + Math.Exp(-Math.Pow((frequency - 700) / 200, 2)) +
                .8 * Math.Exp(-Math.Pow((frequency - 1800) / 300, 2));
            for (var index = 0; index < count; index++)
                samples[index] += (float)(amplitude * Math.Sin(2 * Math.PI * frequency * index / 48_000));
        }
        var peak = samples.Max(x => Math.Abs(x));
        for (var index = 0; index < count; index++) samples[index] *= .25f / peak;
        return samples;
    }

    private static float[] Tone(double hz, int count) => Enumerable.Range(0, count)
        .Select(index => (float)(.2 * Math.Sin(2 * Math.PI * hz * index / 48_000))).ToArray();
    private static double Amplitude(float[] samples, double hz)
    {
        double real = 0, imaginary = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            var angle = 2 * Math.PI * hz * index / 48_000;
            real += samples[index] * Math.Cos(angle); imaginary += samples[index] * Math.Sin(angle);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }
    private sealed class Samples(int channels, float[] data) : ISampleProvider
    {
        private int position;
        public float[] Data => data;
        public bool Silent;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, channels);
        public void Reset() => position = 0;
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            var take = Math.Min(count, Math.Max(0, data.Length - position));
            if (!Silent) Array.Copy(data, position, buffer, offset, take);
            position += take;
            return count;
        }
    }
}
