using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

internal sealed class BitVoiceSampleProvider(ISampleProvider source) : ISampleProvider
{
    private static readonly int[] Rates = [4000, 6000, 8000, 16000, 32000, 44100];
    private static readonly int[] Levels = [7, 31, 127, 2047, 32767, 8388607];
    private volatile int requested = -1;
    private int previous = -1;
    private double phase;
    private float[] held = [];
    public WaveFormat WaveFormat => source.WaveFormat;
    public void SetStage(int stage) => requested = stage >= 0 && stage < Rates.Length ? stage : -1;
    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        int stage = requested;
        if (stage != previous) { phase = 0; held = []; previous = stage; }
        if (stage < 0) return read;
        int channels = WaveFormat.Channels;
        if (held.Length != channels) held = new float[channels];
        for (int frame = 0; frame < read; frame += channels)
        {
            if (phase <= 0)
            {
                Capture(buffer, offset + frame, channels, Levels[stage]);
                phase += WaveFormat.SampleRate / (double)Math.Min(WaveFormat.SampleRate, Rates[stage]);
            }
            for (int c = 0; c < channels; c++) buffer[offset + frame + c] = held[c];
            phase--;
        }
        return read;
    }
    private void Capture(float[] buffer, int offset, int channels, int levels)
    {
        for (int c = 0; c < channels; c++)
        {
            double sample = float.IsFinite(buffer[offset + c]) ? Math.Clamp(buffer[offset + c], -1, 1) : 0;
            double compressed = Math.Sign(sample) * Math.Log(1 + 255 * Math.Abs(sample)) / Math.Log(256);
            double quantized = Math.Floor(compressed * levels + .5) / levels;
            held[c] = (float)(Math.Sign(quantized) * (Math.Exp(Math.Abs(quantized) * Math.Log(256)) - 1) / 255);
        }
    }
}
