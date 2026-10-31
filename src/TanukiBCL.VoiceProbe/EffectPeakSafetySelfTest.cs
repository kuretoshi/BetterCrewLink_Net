using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

internal static class EffectPeakSafetySelfTest
{
    public static void Verify(Action<string, bool, bool> check)
    {
        var assertions = 0;
        void Assert(string name, bool result) { assertions++; check("Effect peak safety: " + name, true, result); }
        foreach (var value in new[] { -.8f, -.25f, -0f, 0f, .25f, .8f })
            Assert("low samples are bit-exact", BitConverter.SingleToInt32Bits(value) == BitConverter.SingleToInt32Bits(EffectPeakSafety.Apply(value)));
        var previous = 0f;
        var bounded = true;
        for (var index = 0; index <= 10_000; index++)
        {
            var value = index / 1000d;
            var result = EffectPeakSafety.Apply(value);
            bounded &= float.IsFinite(result) && result >= previous && result < 1 && result == -EffectPeakSafety.Apply(-value);
            previous = result;
        }
        Assert("strong peaks remain finite, symmetric, monotonic and below full scale", bounded);
        Assert("invalid samples cannot become loud output", EffectPeakSafety.Apply(double.NaN) == 0 && EffectPeakSafety.Apply(double.PositiveInfinity) == 0);

        var strong = Sine(100, .95f, 144_000);
        var neutralSource = new Samples(1, strong);
        var neutral = new VoiceEffectChain(neutralSource); neutral.SetTuning(new VoiceEffectTuning());
        var plain = new float[strong.Length * 2]; neutral.Output.Read(plain, 0, plain.Length);
        Assert("neutral startup bypasses even .95-peak input exactly", plain.SequenceEqual(strong.SelectMany(x => new[] { x, x })));

        var rawEcho = new RadioEchoSampleProvider(new Samples(1, strong), .18, 1, .12f, .22f) { Enabled = true };
        var unprotected = new float[strong.Length]; rawEcho.Read(unprotected, 0, unprotected.Length);
        var source = new Samples(1, strong.Concat(strong).ToArray());
        var effect = new VoiceEffectChain(source); effect.SetTuning(new VoiceEffectTuning(0, 0, 100));
        var protectedAudio = new float[strong.Length * 2]; effect.Output.Read(protectedAudio, 0, protectedAudio.Length);
        Assert("100 Hz/.95-peak echo overflows before protection and stays bounded after", unprotected.Max(x => Math.Abs(x)) > 1.09 &&
            protectedAudio.All(x => float.IsFinite(x) && Math.Abs(x) < 1));
        Assert("settled generic echo uses the same analytic safety function", protectedAudio.Where((_, i) => i % 2 == 0).Skip(24_000)
            .SequenceEqual(unprotected.Skip(24_000).Select(x => EffectPeakSafety.Apply(x))));
        effect.SetTuning(new VoiceEffectTuning());
        var ending = new float[1_920]; effect.Output.Read(ending, 0, ending.Length);
        Assert("OFF fade remains protected through its final block", ending.All(x => Math.Abs(x) < 1));
        var position = source.Position;
        var dryAgain = new float[1_920]; effect.Output.Read(dryAgain, 0, dryAgain.Length);
        Assert("finished OFF fade restores exact strong raw voice", dryAgain.Where((_, i) => i % 2 == 0).SequenceEqual(source.Data.Skip(position).Take(960)));

        var preview = new VoiceEffectChain(new Samples(1, strong), bibiriPreview: true);
        preview.SetTuning(new VoiceEffectTuning(0, 0, 100));
        var previewAudio = new float[strong.Length * 2]; preview.Output.Read(previewAudio, 0, previewAudio.Length);
        Assert("Bibiri preview feedback tail remains finite and below full scale", previewAudio.All(x => float.IsFinite(x) && Math.Abs(x) < 1) &&
            previewAudio.Any(x => Math.Abs(x) > .8));

        var voice = Voiced(144_000);
        var pitched = new VoiceEffectChain(new Samples(1, voice)); pitched.SetTuning(new VoiceEffectTuning(-100));
        var pitchedAudio = new float[voice.Length * 2]; pitched.Output.Read(pitchedAudio, 0, pitchedAudio.Length);
        Assert("strong octave-down voice has no hard-clipped samples", pitchedAudio.All(x => float.IsFinite(x) && Math.Abs(x) < 1) &&
            pitchedAudio.Skip(48_000).Any(x => Math.Abs(x) > .5));

        var recordingSource = new Samples(2, strong.SelectMany(x => new[] { x, x }).ToArray());
        var recording = new NosDeathEchoSampleProvider(recordingSource);
        var bibiri = new VoiceEffectTuning(0, 0, 100);
        recording.Update(true, false, null, 10_000, bibiri);
        recording.Read(new float[strong.Length * 2], 0, strong.Length * 2);
        recording.Update(true, true, 10_000, 10_000, bibiri);
        var tail = new float[28_800]; recording.Read(tail, 0, tail.Length);
        Assert("live Bibiri event protects accumulated feedback after its final envelope", tail.All(x => float.IsFinite(x) && Math.Abs(x) < 1) && tail.Any(x => Math.Abs(x) > .8));
        recording.Stop(); recording.Read(tail, 0, tail.Length);
        Assert("peak protection does not retain audio after an explicit stop", tail.All(x => x == 0));
        Console.WriteLine($"Effect peak regressions: {assertions} assertions");
    }

    private static float[] Sine(double frequency, float peak, int count) => Enumerable.Range(0, count)
        .Select(index => (float)(peak * Math.Sin(2 * Math.PI * frequency * index / 48_000))).ToArray();

    private static float[] Voiced(int count)
    {
        var result = new float[count];
        for (var frequency = 120; frequency < 6_000; frequency += 120)
        {
            var amplitude = .03 + Math.Exp(-Math.Pow((frequency - 700) / 200d, 2)) + .8 * Math.Exp(-Math.Pow((frequency - 1800) / 300d, 2));
            for (var index = 0; index < count; index++) result[index] += (float)(amplitude * Math.Sin(2 * Math.PI * frequency * index / 48_000));
        }
        var peak = result.Max(x => Math.Abs(x));
        for (var index = 0; index < result.Length; index++) result[index] *= .95f / peak;
        return result;
    }

    private sealed class Samples(int channels, float[] data) : ISampleProvider
    {
        public int Position;
        public float[] Data => data;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            var take = Math.Min(count, Math.Max(0, data.Length - Position));
            Array.Copy(data, Position, buffer, offset, take);
            Position += take;
            return count;
        }
    }
}
