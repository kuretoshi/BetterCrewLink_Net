using NAudio.Wave;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed class NosInterviewerRecording(Func<long>? monotonicClock = null) : ISampleProvider
{
    private const int Rate = 48_000;
    private readonly Func<long> clock = monotonicClock ?? (() => Environment.TickCount64);
    private readonly object gate = new();
    private readonly Dictionary<int, Slot> slots = [];
    private bool audible;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    private sealed class Slot(NosInterviewState entry, long start)
    {
        public NosInterviewState Entry = entry;
        public readonly float[] Data = new float[(int)Math.Ceiling(entry.Seconds * Rate)];
        public readonly long Start = start;
        public long End = start + (long)(entry.Seconds * 1000);
        public long Handled = entry.PlayStarted;
        public int Position, Limit;
        public bool Playing;
        public float Peak = 1;
        public readonly Dictionary<int, int> Positions = [];
    }

    public void Update(IEnumerable<NosInterviewState> entries, bool active, bool meeting, bool canHear, long now)
    {
        lock (gate)
        {
            audible = canHear;
            var valid = active ? entries.Where(e => e.IsValid(now)).Take(32).ToArray() : [];
            var retained = valid.Select(e => e.PlayerId).ToHashSet();
            foreach (var id in slots.Keys.Where(id => !retained.Contains(id)).ToArray()) slots.Remove(id);
            foreach (var entry in valid) UpdateSlot(entry, meeting, now);
        }
    }
    private void UpdateSlot(NosInterviewState entry, bool meeting, long now)
    {
        if (!slots.TryGetValue(entry.PlayerId, out var slot) || slot.Entry.Started != entry.Started)
        {
            slot = new Slot(entry, clock() - (now - entry.Started));
            slots[entry.PlayerId] = slot;
        }
        slot.Entry = entry;
        slot.End = slot.Start + (entry.Stopped > 0 ? entry.Stopped - entry.Started : (long)(entry.Seconds * 1000));
        if (!meeting) { slot.Playing = false; slot.Handled = entry.PlayStarted; return; }
        if (!audible) slot.Playing = false;
        if (entry.PlayStarted == 0 || slot.Handled == entry.PlayStarted) return;
        slot.Handled = entry.PlayStarted;
        slot.Limit = Math.Min(slot.Data.Length, (int)((entry.Stopped - entry.Started) * (Rate / 1000)));
        slot.Position = (int)(Math.Max(0, now - entry.PlayStarted) * (Rate / 1000));
        slot.Playing = audible && slot.Limit > 0 && slot.Position < slot.Limit;
        slot.Peak = 1;
        for (int i = 0; i < slot.Limit; i++) slot.Peak = Math.Max(slot.Peak, Math.Abs(slot.Data[i]));
    }
    public void Record(int playerId, ReadOnlySpan<short> pcm, int channels)
    {
        if (playerId < 0 || channels is not (1 or 2)) return;
        lock (gate)
            foreach (var slot in slots.Values) RecordSlot(slot, playerId, pcm, channels);
    }
    private void RecordSlot(Slot slot, int id, ReadOnlySpan<short> pcm, int channels)
    {
        int frames = pcm.Length / channels;
        if (!slot.Entry.Visible.Contains(id) || clock() - frames * 1000L / Rate >= slot.End) return;
        int offset = (int)((clock() - slot.Start) * (Rate / 1000)) - frames;
        if (slot.Positions.TryGetValue(id, out var previous)) offset = Math.Max(offset, previous);
        int limit = Math.Min(slot.Data.Length, (int)((slot.End - slot.Start) * (Rate / 1000)));
        int first = Math.Max(0, -offset), last = Math.Min(frames, limit - offset);
        for (int i = first; i < last; i++)
            slot.Data[offset + i] += channels == 1 ? pcm[i] / 32768f : (pcm[i * 2] + pcm[i * 2 + 1]) / 65536f;
        slot.Positions[id] = Math.Max(0, offset + last);
    }
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (gate)
            foreach (var slot in slots.Values) MixSlot(slot, buffer, offset, count);
        return count;
    }
    private void MixSlot(Slot slot, float[] buffer, int offset, int count)
    {
        for (int i = 0; audible && slot.Playing && i + 1 < count; i += 2)
        {
            float sample = slot.Data[slot.Position++] / slot.Peak;
            buffer[offset + i] += sample; buffer[offset + i + 1] += sample;
            if (slot.Position >= slot.Limit) slot.Playing = false;
        }
    }
    public void Clear() { lock (gate) slots.Clear(); }
    public void SetAudible(bool value)
    {
        lock (gate)
        {
            audible = value;
            if (!value) foreach (var slot in slots.Values) slot.Playing = false;
        }
    }
}
