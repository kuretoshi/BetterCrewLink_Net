using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 radio echo: 90 ms delay, 0.92 dry, 0.2 wet, 0.12 feedback.
internal sealed class RadioEchoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int delaySamples;
    private readonly float dryGain;
    private readonly float wetGain;
    private readonly float feedbackGain;
    private float[]? delayLine;
    private int delayPosition;
    private bool wasEnabled;
    public volatile bool Enabled;
    public volatile float Strength = 1f;

    public RadioEchoSampleProvider(ISampleProvider source, double delaySeconds = 0.09d,
        float dryGain = 0.92f, float wetGain = 0.2f, float feedbackGain = 0.12f)
    {
        this.source = source;
        this.dryGain = dryGain;
        this.wetGain = wetGain;
        this.feedbackGain = feedbackGain;
        delaySamples = checked((int)Math.Round(source.WaveFormat.SampleRate * delaySeconds) *
            source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat => source.WaveFormat;
    internal int DelayCapacitySamples => delayLine?.Length ?? 0;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var enabled = Enabled;
        if (enabled != wasEnabled)
        {
            delayLine = enabled ? new float[delaySamples] : null;
            delayPosition = 0;
            wasEnabled = enabled;
        }
        if (!enabled) return read;

        var line = delayLine!;
        var strength = Math.Clamp(Strength, 0f, 1f);
        for (var index = offset; index < offset + read; index++)
        {
            var input = buffer[index];
            var delayed = line[delayPosition];
            line[delayPosition] = input + feedbackGain * delayed;
            buffer[index] = (1f - (1f - dryGain) * strength) * input + wetGain * strength * delayed;
            if (++delayPosition == line.Length) delayPosition = 0;
        }
        return read;
    }
}
