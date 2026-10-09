using NAudio.Dsp;

namespace TanukiBCL.VoiceProbe;

// NoS Berserker's 80 Hz high-pass, saturation, 4.2 kHz low-pass and short room tail.
internal sealed class BerserkerDsp
{
    private readonly BiQuadFilter highPass;
    private readonly BiQuadFilter lowPass;
    private readonly float[] room;
    private readonly int[] taps;
    private readonly float[] weights = [0.48f, 0.29f, 0.17f, 0.09f];
    private int cursor;
    private float previousHighPass;
    private float envelope;
    private readonly float attack;
    private readonly float release;

    public BerserkerDsp(int sampleRate)
    {
        var q = WebAudioBiquadQ.ToLinear((float)Math.Sqrt(0.5d));
        highPass = BiQuadFilter.HighPassFilter(sampleRate, 80, q);
        lowPass = BiQuadFilter.LowPassFilter(sampleRate, 4_200, q);
        room = new float[(int)(sampleRate * 0.24d)];
        taps = new[] { 12, 53, 119, 211 }
            .Select(milliseconds => sampleRate * milliseconds / 1000).ToArray();
        attack = 1f - MathF.Exp(-1f / (sampleRate * 0.005f));
        release = 1f - MathF.Exp(-1f / (sampleRate * 0.08f));
    }

    public void Process(float[] samples, int offset, int count)
    {
        for (var index = offset; index < offset + count; index++)
        {
            var dry = float.IsFinite(samples[index]) ? samples[index] : 0f;
            var high = highPass.Transform(dry);
            var shaped = OversampledSaturation(high);
            var filtered = lowPass.Transform(shaped);
            room[cursor] = filtered;
            var reverberant = RoomSample();
            var mixed = high * 0.8f + filtered * 0.12f + reverberant * 0.06f;
            samples[index] = Math.Clamp(Compress(mixed) * 0.78f, -1f, 1f);
            cursor = (cursor + 1) % room.Length;
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

    private float RoomSample()
    {
        var sum = 0f;
        for (var index = 0; index < taps.Length; index++)
            sum += room[(cursor + room.Length - taps[index]) % room.Length] * weights[index];
        return sum;
    }
}
