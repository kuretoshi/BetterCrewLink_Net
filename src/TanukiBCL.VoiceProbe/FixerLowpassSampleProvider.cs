using NAudio.Dsp;
using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// NoS v3.2.21: Web Audio 1.2 kHz low-pass, Q=sqrt(1/2).
internal sealed class FixerLowpassSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly BiQuadFilter filter = BiQuadFilter.LowPassFilter(48_000, 1_200,
        WebAudioBiquadQ.ToLinear((float)Math.Sqrt(0.5d)));

    public WaveFormat WaveFormat => source.WaveFormat;
    public volatile bool Enabled;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (!Enabled) return read;
        for (var index = offset; index < offset + read; index++)
            buffer[index] = filter.Transform(buffer[index]);
        return read;
    }
}
