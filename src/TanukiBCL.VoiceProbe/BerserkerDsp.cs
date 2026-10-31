using NAudio.Dsp;

namespace TanukiBCL.VoiceProbe;

// NoS Berserker's 80 Hz high-pass, saturation, 4.2 kHz low-pass colour. Room and echo are controlled by the shared effect chain.
internal sealed class BerserkerDsp
{
    private readonly BiQuadFilter highPass;
    private readonly BiQuadFilter lowPass;
    private float previousHighPass;
    private float envelope;
    private readonly float attack;
    private readonly float release;
    private float appliedStrength;
    private readonly float strengthStep;
    internal bool IsInactive => appliedStrength == 0;

    public BerserkerDsp(int sampleRate)
    {
        strengthStep = 1f / Math.Max(1, sampleRate / 100);
        var q = WebAudioBiquadQ.ToLinear((float)Math.Sqrt(0.5d));
        highPass = BiQuadFilter.HighPassFilter(sampleRate, 80, q);
        lowPass = BiQuadFilter.LowPassFilter(sampleRate, 4_200, q);
        attack = 1f - MathF.Exp(-1f / (sampleRate * 0.005f));
        release = 1f - MathF.Exp(-1f / (sampleRate * 0.08f));
    }

    public void Process(float[] samples, int offset, int count, double strength, bool smoothStrength = false)
    {
        var target = (float)Math.Clamp(strength, 0d, 1d);
        for (var index = offset; index < offset + count; index++)
        {
            appliedStrength = smoothStrength ? Math.Clamp(target, appliedStrength - strengthStep, appliedStrength + strengthStep) : target;
            var amount = appliedStrength;
            var dry = float.IsFinite(samples[index]) ? samples[index] : 0f;
            var high = highPass.Transform(dry);
            var shaped = OversampledSaturation(high);
            var filtered = lowPass.Transform(shaped);
            var mixed = high * 0.8f + filtered * 0.12f;
            samples[index] = Math.Clamp(dry * (1f - amount) + Compress(mixed) * 0.78f * amount, -1f, 1f);
        }
    }

    private float OversampledSaturation(float current)
    {
        var sum = 0f;
        for (var phase = 1; phase <= 4; phase++)
        {
            var interpolated = previousHighPass + (current - previousHighPass) * phase / 4f;
            sum += MathF.Tanh(5f * interpolated) / MathF.Tanh(5f);
        }
        previousHighPass = current;
        return sum / 4f;
    }

    private float Compress(float sample)
    {
        var level = MathF.Abs(sample);
        envelope += (level > envelope ? attack : release) * (level - envelope);
        const float threshold = 0.2f;
        var gain = envelope > threshold ? MathF.Pow(threshold / envelope, 2f / 3f) : 1f;
        return sample * gain;
    }

}
