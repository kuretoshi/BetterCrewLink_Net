using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

internal static class EffectTransitionSelfTest
{
    public static void Verify(Action<string, bool, bool> check)
    {
        var assertions = 0;
        void Assert(string name, bool result)
        {
            assertions++;
            check("Effect transitions: " + name, true, result);
        }
        var source = new TestSource();
        var tone = new TunedVoiceSampleProvider(source);
        var block = new float[960];
        tone.Read(new float[48_000], 0, 48_000);
        for (var repetition = 0; repetition < 3; repetition++)
        {
            var position = source.Position;
            tone.SetTuning(new VoiceEffectTuning(50));
            tone.Read(block, 0, block.Length);
            Assert("pitch ON bridges the unfilled FFT output with unchanged dry speech", block.Select((x, i) => x == TestSource.Sample(position + i)).All(x => x));
            tone.Read(new float[3_840], 0, 3_840);
            tone.SetTuning(new VoiceEffectTuning());
            tone.Read(new float[960], 0, 960);
            Assert("pitch OFF releases DSP after the short fade", !tone.PitchPrepared);
        }

        var continuous = new TunedVoiceSampleProvider(new TestSource());
        var chunked = new TunedVoiceSampleProvider(new TestSource());
        continuous.SetTuning(new VoiceEffectTuning(-50), .5);
        chunked.SetTuning(new VoiceEffectTuning(-50), .5);
        var single = new float[48_000]; continuous.Read(single, 0, single.Length);
        var parts = new float[single.Length];
        for (var offset = 0; offset < parts.Length; offset += 960) chunked.Read(parts, offset, Math.Min(960, parts.Length - offset));
        Assert("warmup and fades are independent of callback block size", single.SequenceEqual(parts));

        var changing = new TunedVoiceSampleProvider(new TestSource());
        changing.SetTuning(new VoiceEffectTuning(-50));
        changing.Read(new float[48_000], 0, 48_000);
        changing.SetTuning(new VoiceEffectTuning(50)); changing.Read(block, 0, block.Length);
        Assert("nonzero pitch changes retain active history without a new silent warmup", block.All(float.IsFinite) && Rms(block) > .001);

        foreach (var rate in new[] { 16_000, 44_100, 48_000 })
        foreach (var channels in new[] { 1, 2 })
        {
            var rateTone = new TunedVoiceSampleProvider(new TestSource(constant: true, sampleRate: rate, channels: channels));
            rateTone.SetTuning(new VoiceEffectTuning(50));
            var warmup = new float[2_048 * channels]; rateTone.Read(warmup, 0, warmup.Length);
            var transition = new float[(rate / 100 + 1) * channels]; rateTone.Read(transition, 0, transition.Length);
            rateTone.SetTuning(new VoiceEffectTuning()); rateTone.Read(transition, 0, transition.Length);
            Assert($"{rate} Hz/{channels} channels warm up dry and release within 10 ms", warmup.All(x => x == .2f) &&
                transition.All(float.IsFinite) && !rateTone.PitchPrepared);
        }

        var firstSource = new TestSource(); var controlSource = new TestSource();
        var first = new TunedVoiceSampleProvider(firstSource); var control = new TunedVoiceSampleProvider(controlSource);
        first.SetTuning(new VoiceEffectTuning(0, 100), .25); control.SetTuning(new VoiceEffectTuning(0, 100), .25);
        first.Read(new float[48_000], 0, 48_000); control.Read(new float[48_000], 0, 48_000);
        first.SetTuning(new VoiceEffectTuning(0, 100), .75);
        var changed = new float[960]; var unchanged = new float[960];
        first.Read(changed, 0, changed.Length); control.Read(unchanged, 0, unchanged.Length);
        Assert("grit amount change starts without a discontinuous gain step", Math.Abs(changed[0] - unchanged[0]) < .001);

        var echoA = new RadioEchoSampleProvider(new TestSource(constant: true), .18, 1, .12f, .22f) { SmoothTransitions = true, Enabled = true, Strength = .25f };
        var echoB = new RadioEchoSampleProvider(new TestSource(constant: true), .18, 1, .12f, .22f) { SmoothTransitions = true, Enabled = true, Strength = .25f };
        echoA.Read(new float[48_000], 0, 48_000); echoB.Read(new float[48_000], 0, 48_000);
        echoA.Strength = .75f; echoA.Read(changed, 0, changed.Length); echoB.Read(unchanged, 0, unchanged.Length);
        Assert("echo amount change ramps its wet gain", Math.Abs(changed[0] - unchanged[0]) < .0001 && Math.Abs(changed[959] - unchanged[959]) > .005);

        var roomSource = new TestSource(); var room = new VoiceEffectChain(roomSource);
        room.SetTuning(new VoiceEffectTuning(0, 100));
        var initial = new float[2_048]; room.Output.Read(initial, 0, initial.Length);
        Assert("room ON keeps dry speech until its first convolution block is ready", initial.Where((_, i) => i % 2 == 0).Select((x, i) => x == TestSource.Sample(i)).All(x => x));
        room.Output.Read(new float[96_000], 0, 96_000);
        room.SetTuning(new VoiceEffectTuning());
        room.Output.Read(new float[40], 0, 40);
        Assert("room fade retains history until wet gain reaches zero", room.Reverb.RetainedHistoryBytes > 0);
        room.Output.Read(new float[960], 0, 960);
        Assert("room fade releases its buffers after reaching dry", room.Reverb.RetainedHistoryBytes == 0);
        room.SetTuning(new VoiceEffectTuning(50, 100, 100));
        room.Output.Read(new float[96_000], 0, 96_000);
        roomSource.Silent = true;
        room.SetTuning(new VoiceEffectTuning(), immediate: true);
        var muted = new float[1_920]; room.Output.Read(muted, 0, muted.Length);
        Assert("mute immediately discards pitch, room and echo histories", muted.All(x => x == 0));
        Console.WriteLine($"Effect transition regressions: {assertions} assertions");
    }

    private static double Rms(float[] values) => Math.Sqrt(values.Sum(x => (double)x * x) / values.Length);
    private sealed class TestSource(bool constant = false, int sampleRate = 48_000, int channels = 1) : ISampleProvider
    {
        public int Position;
        public bool Silent;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        public static float Sample(int frame) => (float)(.2 * Math.Sin(2 * Math.PI * 440 * frame / 48_000 + .37));
        public int Read(float[] buffer, int offset, int count)
        {
            for (var index = 0; index < count; index++) buffer[offset + index] = Silent ? 0 : constant ? .2f : Sample(Position + index);
            Position += count;
            return count;
        }
    }
}
