using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 radio echo: 90 ms delay, 0.92 dry, 0.2 wet, 0.12 feedback.
internal sealed class RadioEchoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly float[] delayLine;
    private readonly float dryGain;
    private readonly float wetGain;
    private readonly float feedbackGain;
    private int delayPosition;
    private bool wasEnabled;
    public volatile bool Enabled;

    public RadioEchoSampleProvider(ISampleProvider source, double delaySeconds = 0.09d,
        float dryGain = 0.92f, float wetGain = 0.2f, float feedbackGain = 0.12f)
    {
        this.source = source;
        this.dryGain = dryGain;
        this.wetGain = wetGain;
        this.feedbackGain = feedbackGain;
        delayLine = new float[checked((int)Math.Round(source.WaveFormat.SampleRate * delaySeconds) *
            source.WaveFormat.Channels)];
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var enabled = Enabled;
        if (enabled != wasEnabled)
        {
            Array.Clear(delayLine);
            delayPosition = 0;
            wasEnabled = enabled;
        }
        if (!enabled) return read;

        for (var index = offset; index < offset + read; index++)
        {
            var input = buffer[index];
            var delayed = delayLine[delayPosition];
            delayLine[delayPosition] = input + feedbackGain * delayed;
            buffer[index] = dryGain * input + wetGain * delayed;
            if (++delayPosition == delayLine.Length) delayPosition = 0;
        }
        return read;
    }
}
