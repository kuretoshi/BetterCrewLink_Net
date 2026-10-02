using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 radio echo: 90 ms delay, 0.92 dry, 0.2 wet, 0.12 feedback.
internal sealed class RadioEchoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly float[] delayLine;
    private int delayPosition;
    private bool wasEnabled;
    public volatile bool Enabled;

    public RadioEchoSampleProvider(ISampleProvider source)
    {
        this.source = source;
        delayLine = new float[checked((int)Math.Round(source.WaveFormat.SampleRate * 0.09d) *
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
            delayLine[delayPosition] = input + 0.12f * delayed;
            buffer[index] = 0.92f * input + 0.2f * delayed;
            if (++delayPosition == delayLine.Length) delayPosition = 0;
        }
        return read;
    }
}
