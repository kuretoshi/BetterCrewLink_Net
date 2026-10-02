using NAudio.Dsp;
using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 spatialAudio.ts: living vent audio uses a 2 kHz
// low-pass biquad with Web Audio Q=20 dB. Apply it before spatial panning.
internal sealed class VentMuffleSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly BiQuadFilter filter = BiQuadFilter.LowPassFilter(48_000, 2_000,
        WebAudioBiquadQ.ToLinear(20f));
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
