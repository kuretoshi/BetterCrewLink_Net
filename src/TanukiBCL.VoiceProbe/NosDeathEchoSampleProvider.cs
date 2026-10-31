using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

/// <summary>Short live tail for NoS Bibiri/self-destruction; no recording is persisted.</summary>
internal sealed class NosDeathEchoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly bool separateTail;
    private int pendingTailCount;
    private readonly object gate = new();
    private readonly TailSource tail;
    private VoiceEffectChain effects;
    private float[]? delay;
    private float[] tailBuffer = [];
    private int position;
    private int remaining;
    private int total;
    private long seen;
    private bool wasAlive;
    private bool active;
    private bool dead;
    private VoiceEffectTuning tuning = new(0, 0, 100);

    public NosDeathEchoSampleProvider(ISampleProvider source, bool separateTail = false)
    {
        this.source = source;
        this.separateTail = separateTail;
        tail = new TailSource(source.WaveFormat);
        effects = new VoiceEffectChain(tail);
    }

    public WaveFormat WaveFormat => source.WaveFormat;
    internal bool HasHistory => delay is not null;
    internal bool IsReplaying => remaining > 0;

    public void Update(bool enabled, bool isDead, long? eventUnixMs, long now, float effectStrength = 1f) =>
        Update(enabled, isDead, eventUnixMs, now, new VoiceEffectTuning(0, 0, Math.Clamp(effectStrength, 0, 1) * 100));

    public void Update(bool enabled, bool isDead, long? eventUnixMs, long now, VoiceEffectTuning effectTuning)
    {
        lock (gate)
        {
            tuning = effectTuning;
            if (!enabled || tuning.IsNeutral)
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
            // Echo belongs to the recording's 300 ms feedback loop. Pitch and
            // room process only that event playback, never ordinary live voice.
            effects.SetTuning(remaining > 0 ? tuning with { Echo = 0 } : new VoiceEffectTuning(), includeEcho: false);
            if (!isDead) wasAlive = true;
        }
    }

    public void Stop()
    {
        lock (gate) StopCore();
    }

    private void StopCore()
    {
        if (active || delay is not null) effects = new VoiceEffectChain(tail);
        active = false;
        dead = false;
        remaining = 0;
        delay = null;
        tailBuffer = [];
        pendingTailCount = 0;
        position = 0;
        effects.SetTuning(new VoiceEffectTuning());
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        lock (gate)
        {
            pendingTailCount = 0;
            if (!active) return read;
            delay ??= new float[checked((int)(0.3d * WaveFormat.SampleRate * WaveFormat.Channels))];
            if (tailBuffer.Length < read) tailBuffer = new float[read];
            Array.Clear(tailBuffer, 0, read);
            var replaySamples = Math.Min(read, remaining);
            var beforeRemaining = remaining;
            for (var index = 0; index < read; index++)
            {
                var previous = delay[position];
                var input = dead ? 0f : buffer[offset + index];
                // Keep the pre-event ring alive while the death RPC is pending.
                // Once playback begins, zero echo consumes it exactly once.
                var feedback = remaining > 0 ? 0.56d * tuning.Echo / 100d : 0.56d;
                delay[position] = input + (float)(previous * feedback);
                if (remaining > 0)
                {
                    tailBuffer[index] = previous;
                    remaining--;
                }
                if (++position == delay.Length) position = 0;
            }
            if (replaySamples > 0)
            {
                tail.Set(tailBuffer, read);
                effects.Output.Read(tailBuffer, 0, read);
                for (var index = 0; index < replaySamples; index++)
                {
                    var progress = 1d - (beforeRemaining - index) / (double)total;
                    var decay = 0.65d * Math.Exp(Math.Log(0.001d / 0.65d) * progress);
                    tailBuffer[index] = EffectPeakSafety.Apply(tailBuffer[index] * decay);
                }
                Array.Clear(tailBuffer, replaySamples, read - replaySamples);
                pendingTailCount = read;
                if (!separateTail) MixTailCore(buffer, offset, read);
                if (remaining == 0) effects = new VoiceEffectChain(tail);
            }
        }
        return read;
    }

    // The live graph mixes this after its ordinary role/ghost/radio effects,
    // so a recorded event receives its own axes exactly once.
    public void MixTail(float[] buffer, int offset, int count)
    {
        lock (gate) MixTailCore(buffer, offset, count);
    }

    private void MixTailCore(float[] buffer, int offset, int count)
    {
        for (var index = 0; index < Math.Min(count, pendingTailCount); index++)
            buffer[offset + index] += tailBuffer[index];
        pendingTailCount = 0;
    }

    private sealed class TailSource(WaveFormat format) : ISampleProvider
    {
        private float[] samples = [];
        private int length;
        public WaveFormat WaveFormat => format;
        public void Set(float[] value, int count) { samples = value; length = count; }
        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, length);
            Array.Copy(samples, 0, buffer, offset, read);
            return read;
        }
    }
}

internal sealed class BibiriTailMixSampleProvider(ISampleProvider source, NosDeathEchoSampleProvider recording) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        recording.MixTail(buffer, offset, read);
        return read;
    }
}
