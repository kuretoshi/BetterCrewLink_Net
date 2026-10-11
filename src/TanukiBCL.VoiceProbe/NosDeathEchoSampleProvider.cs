using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

/// <summary>Short live tail for NoS Bibiri/self-destruction; no recording is persisted.</summary>
internal sealed class NosDeathEchoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly object gate = new();
    private float[]? delay;
    private int position;
    private int remaining;
    private int total;
    private long seen;
    private bool wasAlive;
    private bool active;
    private bool dead;

    public NosDeathEchoSampleProvider(ISampleProvider source) => this.source = source;

    public WaveFormat WaveFormat => source.WaveFormat;

    public void Update(bool enabled, bool isDead, long? eventUnixMs, long now)
    {
        lock (gate)
        {
            if (!enabled)
            {
                StopCore();
                wasAlive = false;
                return;
            }
            active = true;
            dead = isDead;
            if (isDead && wasAlive && eventUnixMs is { } timestamp && timestamp != seen &&
                now - timestamp <= 1500 && now >= timestamp - 100)
            {
                seen = timestamp;
                total = checked((int)(2.4d * WaveFormat.SampleRate * WaveFormat.Channels));
                remaining = total;
            }
            if (!isDead) wasAlive = true;
        }
    }

    public void Stop()
    {
        lock (gate) StopCore();
    }

    private void StopCore()
    {
        active = false;
        dead = false;
        remaining = 0;
        delay = null;
        position = 0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        lock (gate)
        {
            if (!active) return read;
            delay ??= new float[checked((int)(0.3d * WaveFormat.SampleRate * WaveFormat.Channels))];
            for (var index = offset; index < offset + read; index++) ProcessSample(buffer, index);
        }
        return read;
    }

    private void ProcessSample(float[] buffer, int index)
    {
        var history = delay!;
        var previous = history[position];
        var input = dead ? 0f : buffer[index];
        history[position] = input + previous * 0.56f;
        if (remaining > 0)
        {
            var progress = 1d - remaining / (double)total;
            var decay = 0.65d * Math.Exp(Math.Log(0.001d / 0.65d) * progress);
            buffer[index] = input + (float)(previous * decay);
            remaining--;
        }
        if (++position == history.Length) position = 0;
    }
}
