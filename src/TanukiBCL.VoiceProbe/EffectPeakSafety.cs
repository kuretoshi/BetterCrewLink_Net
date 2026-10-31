using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

internal static class EffectPeakSafety
{
    // Unity below the knee, with matching slope/curvature at the boundary.
    // No lookahead, fixed attenuation or effect-parameter changes are needed.
    public static float Apply(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var magnitude = Math.Abs(value);
        if (magnitude <= .8d) return (float)value;
        return (float)Math.CopySign(.8d + .18d * Math.Tanh((magnitude - .8d) / .18d), value);
    }
}

internal sealed class EffectPeakSafetySampleProvider(ISampleProvider source, Func<bool> active) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        // An OFF transition may finish inside this callback. Protect that final
        // mixed block too, then restore the exact raw bypass on the next read.
        var protect = active();
        var read = source.Read(buffer, offset, count);
        if (!protect && !active()) return read;
        for (var index = offset; index < offset + read; index++) buffer[index] = EffectPeakSafety.Apply(buffer[index]);
        return read;
    }
}
