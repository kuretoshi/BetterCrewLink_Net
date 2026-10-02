using NAudio.Dsp;
using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 radio muffle: Web Audio high-pass at 1 kHz, Q=10.
internal sealed class RadioHighPassSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly BiQuadFilter filter = BiQuadFilter.HighPassFilter(48_000, 1_000, 10f);
    public WaveFormat WaveFormat => source.WaveFormat;
    public volatile bool Enabled;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (!Enabled) return read;
        for (var index = offset; index < offset + read; index++)
        {
            buffer[index] = filter.Transform(buffer[index]);
        }
        return read;
    }
}
