using NAudio.Dsp;
using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 camera reception: 2.3 kHz low-pass, Web Audio Q=-15 dB.
internal sealed class CameraMuffleSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly BiQuadFilter filter = BiQuadFilter.LowPassFilter(48_000, 2_300,
        WebAudioBiquadQ.ToLinear(-15f));
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
