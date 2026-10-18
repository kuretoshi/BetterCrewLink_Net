using NAudio.Dsp;
using NAudio.Wave;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed class NosDyingVoiceRecording : ISampleProvider
{
    private const int SampleRate = 48_000;
    private const int ChunkSize = SampleRate / 5;
    private readonly object gate = new();
    private readonly Func<long> clock;
    private readonly List<float[]> chunks = [];
    private long started, recordUntil;
    private int samples, capacity, position;
    private bool recording, complete, inside, playing, loop;
    private float volume;
    private BiQuadFilter high = BiQuadFilter.HighPassFilter(SampleRate, 400, .707f);
    private BiQuadFilter low = BiQuadFilter.LowPassFilter(SampleRate, 3000, .707f);
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
    public NosDyingVoiceRecording(Func<long>? monotonicClock = null) => clock = monotonicClock ?? (() => Environment.TickCount64);
    internal int RecordedSamples { get { lock (gate) return samples; } }
    internal bool IsPlaying { get { lock (gate) return playing; } }

    internal static bool InRange(NosDyingVoiceState? entry, Player listener, GameState state) =>
        entry is not null && state == GameState.Tasks && !listener.IsDead && !listener.Disconnected &&
        Math.Sqrt(Math.Pow(listener.X - entry.X, 2) + Math.Pow(listener.Y - entry.Y, 2)) <= entry.Range;

    public void Update(NosDyingVoiceState? entry, bool active, bool near, double playbackVolume, long now)
    {
        lock (gate)
        {
            volume = double.IsFinite(playbackVolume) ? (float)Math.Clamp(playbackVolume, 0d, 2d) : 0;
            if (!active || entry is null) { CancelCore(); return; }
            if (entry.Started != started)
            {
                CancelCore(); started = entry.Started;
                capacity = (int)(SampleRate * Math.Clamp(entry.Seconds, 1, 30));
                var remainingMs = Math.Max(0, entry.Started + entry.Seconds * 1000 - now);
                recordUntil = clock() + (long)remainingMs;
                complete = remainingMs <= 0;
            }
            loop = entry.Loop;
            recording = !complete && now < entry.Started + entry.Seconds * 1000;
            if (!recording) complete = true;
            if (!near || volume == 0) { inside = false; playing = false; return; }
            if (!complete || inside) return;
            inside = true;
            if (samples == 0) return;
            position = 0; playing = true;
            high = BiQuadFilter.HighPassFilter(SampleRate, 400, .707f);
            low = BiQuadFilter.LowPassFilter(SampleRate, 3000, .707f);
        }
    }

    public void RecordStereoPcm16(ReadOnlySpan<short> pcm)
    {
        lock (gate)
        {
            if (!recording) return;
            int count = (int)Math.Min(Math.Min(pcm.Length / 2, capacity - samples),
                Math.Max(0, recordUntil - clock()) * (SampleRate / 1000));
            for (int i = 0; i < count; i++)
            {
                if (samples % ChunkSize == 0) chunks.Add(new float[ChunkSize]);
                chunks[^1][samples % ChunkSize] = (pcm[i * 2] + pcm[i * 2 + 1]) / 65536f;
                samples++;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (gate)
        {
            for (int i = 0; playing && i + 1 < count; i += 2)
            {
                float sample = low.Transform(high.Transform(chunks[position / ChunkSize][position % ChunkSize])) * volume;
                buffer[offset + i] = sample; buffer[offset + i + 1] = sample;
                if (++position >= samples)
                {
                    position = 0;
                    if (!loop) playing = false;
                }
            }
        }
        return count;
    }

    public void Cancel() { lock (gate) CancelCore(); }
    private void CancelCore()
    {
        chunks.Clear(); started = 0; samples = 0; capacity = 0; position = 0;
        recording = false; complete = false; inside = false; playing = false;
    }
}
