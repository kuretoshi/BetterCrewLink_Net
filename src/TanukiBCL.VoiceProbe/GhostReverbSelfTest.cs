using NAudio.Wave;
using System.Diagnostics;

namespace TanukiBCL.VoiceProbe;

internal static class GhostReverbSelfTest
{
    public static bool Verify()
    {
        var source = new ImpulseSource();
        var impulse = new float[1026 * 2];
        impulse[0] = 1f;
        impulse[3] = 0.5f;
        impulse[1025 * 2] = 0.25f;
        var reverb = new GhostReverbSampleProvider(source, impulse)
        {
            Enabled = true
        };
        var result = new float[4 * 1024 * 2];
        var offset = 0;
        while (offset < result.Length)
        {
            var chunk = Math.Min(960 * 2, result.Length - offset);
            if (reverb.Read(result, offset, chunk) != chunk) return false;
            offset += chunk;
        }
        if (reverb.RetainedHistoryBytes == 0) return false;
        if (!Near(result[1024 * 2], 1f) ||
            !Near(result[1025 * 2 + 1], 0.5f) ||
            !Near(result[2049 * 2], 0.25f) ||
            !Near(result[1024 * 2 + 1], 0f) ||
            !Near(result[0], 0f)) return false;

        reverb.Enabled = false;
        source.PulseNext = true;
        var bypass = new float[2];
        reverb.Read(bypass, 0, bypass.Length);
        if (!Near(bypass[0], 1f) || !Near(bypass[1], 1f)) return false;
        if (reverb.RetainedHistoryBytes == 0) return false;
        reverb.Enabled = true;
        var reset = new float[2];
        reverb.Read(reset, 0, reset.Length);
        if (!Near(reset[0], 0f) || !Near(reset[1], 0f)) return false;
        if (reverb.RetainedHistoryBytes == 0) return false;
        reverb.Enabled = false;
        var disabledAudio = new float[48_000 * 2];
        for (var second = 0; second < 10; second++)
            reverb.Read(disabledAudio, 0, disabledAudio.Length);
        if (reverb.RetainedHistoryBytes != 0) return false;
        reverb.Enabled = true;
        reverb.Read(reset, 0, reset.Length);
        if (reverb.RetainedHistoryBytes == 0 || !Near(reset[0], 0f)) return false;

        // Also exercise loading the released, normalized stereo response.
        var releasedSource = new ImpulseSource();
        var released = new GhostReverbSampleProvider(releasedSource) { Enabled = true };
        var releasedOutput = new float[48_000 * 2];
        var timer = Stopwatch.StartNew();
        released.Read(releasedOutput, 0, releasedOutput.Length);
        timer.Stop();
        Console.WriteLine($"ghost reverb: 1 s stereo DSP {timer.ElapsedMilliseconds} ms");
        var producedAudio = releasedOutput.Skip(1024 * 2).Any(sample =>
            float.IsFinite(sample) && Math.Abs(sample) > 0.000001f);
        var retainedHistoryBytes = released.RetainedHistoryBytes;
        released.Enabled = false;
        for (var second = 0; second < 10; second++)
            released.Read(releasedOutput, 0, releasedOutput.Length);
        if (released.RetainedHistoryBytes != 0) return false;
        Console.WriteLine($"ghost reverb: released {retainedHistoryBytes / 1024d / 1024d:F1} MiB after 10 s inactive");
        return producedAudio;
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 0.0001f;

    private sealed class ImpulseSource : ISampleProvider
    {
        public bool PulseNext { get; set; } = true;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            if (PulseNext && count >= 2)
            {
                buffer[offset] = 1f;
                buffer[offset + 1] = 1f;
                PulseNext = false;
            }
            return count;
        }
    }
}
