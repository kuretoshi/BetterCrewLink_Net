using NAudio.Wave;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

// All captures come from decoded input streams, never the output mixer.
internal sealed class NosParrotRecording(Func<long>? monotonicClock = null) : ISampleProvider
{
    private const int Rate = 16000, HistoryLength = 4800;
    private readonly Func<long> clock = monotonicClock ?? (() => Environment.TickCount64);
    private readonly object gate = new();
    private readonly Dictionary<(int, int), Slot> slots = [];
    private readonly Dictionary<int, Input> inputs = [];
    private readonly Queue<(int Owner, int Bird, int Source)> requests = new();
    private AmongUsState? state;
    private Player? listener;
    private bool tasks, audible;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private sealed class Clip(int source, long start, double seconds)
    {
        public readonly float[] Data = new float[(int)Math.Ceiling(seconds * Rate)];
        public readonly int Source = source;
        public readonly long Start = start;
    }
    private sealed class Slot(NosParrotState entry)
    {
        public NosParrotState Entry = entry;
        public readonly Dictionary<int, Clip> Clips = [];
        public int Handled, Position;
        public long Requested, LastMix = long.MinValue;
        public int PlayedFrames;
        public Clip? Playing;
        public float Left, Right;
    }
    private sealed class Input
    {
        public bool Allowed;
        public float Volume;
        public readonly float[] History = new float[HistoryLength];
        public long End;
    }
    public void SetSource(int id, bool allowed, double volume)
    {
        if (id is < 0 or > 255) return;
        lock (gate)
        {
            if (!inputs.TryGetValue(id, out var input)) inputs[id] = input = new();
            input.Allowed = allowed;
            input.Volume = double.IsFinite(volume) ? (float)Math.Clamp(volume, 0, 2) : 0;
            if (!allowed) Array.Clear(input.History);
            foreach (var slot in slots.Values) Gains(slot);
        }
    }
    public void Update(AmongUsState next, bool canHear, long now, IEnumerable<NosParrotState>? entries = null)
    {
        lock (gate)
        {
            state = next; listener = next.Players.FirstOrDefault(p => p.IsLocal);
            bool active = next.Mod == AmongUsModType.NebulaOnTheShip && next.GameState is GameState.Tasks or GameState.Discussion;
            tasks = active && next.GameState == GameState.Tasks; audible = canHear;
            var valid = active ? (entries ?? next.NosParrots).Where(p => p != null && p.IsValid(now)).Take(120).ToArray() : [];
            var retained = valid.Select(p => (p.OwnerId, p.BirdId)).ToHashSet();
            foreach (var key in slots.Keys.Where(k => !retained.Contains(k)).ToArray()) slots.Remove(key);
            foreach (var entry in valid) UpdateSlot(entry, now);
            if (!tasks) { requests.Clear(); foreach (var input in inputs.Values) Array.Clear(input.History); }
            if (!active) inputs.Clear();
        }
    }
    private void UpdateSlot(NosParrotState entry, long now)
    {
        var key = (entry.OwnerId, entry.BirdId);
        if (!slots.TryGetValue(key, out var slot)) slots[key] = slot = new(entry);
        slot.Entry = entry;
        var keep = entry.Clips.ToHashSet(); if (tasks && entry.RecordId > 0) keep.Add(entry.RecordId);
        foreach (int id in slot.Clips.Keys.Where(id => !keep.Contains(id)).ToArray()) slot.Clips.Remove(id);
        if (tasks && entry.RecordId > 0 && !slot.Clips.ContainsKey(entry.RecordId) && now < entry.Started + entry.Seconds * 1000)
        {
            var clip = new Clip(entry.SourceId, clock() - (now - entry.Started) - (entry.Mode == "clone" ? 0 : 150), entry.Seconds);
            slot.Clips[entry.RecordId] = clip; Seed(clip);
        }
        if (!tasks || !audible) slot.Playing = null;
        if (slot.Handled != entry.PlayId || entry.Mode == "clone" && slot.Playing == null && tasks && audible)
        {
            slot.Handled = entry.PlayId; slot.Playing = null; slot.LastMix = long.MinValue; slot.PlayedFrames = 0;
            double elapsed = Math.Max(0, now - entry.PlayStarted);
            slot.Position = (int)(entry.Mode == "clone" ? elapsed % (entry.Seconds * 1000) : Math.Min(elapsed, entry.Seconds * 1000)) * 48;
            if (tasks && audible && entry.PlayId > 0 && slot.Clips.TryGetValue(entry.PlayClip, out var clip) && slot.Position < clip.Data.Length * 3) slot.Playing = clip;
        }
        Gains(slot);
    }
    private void Seed(Clip clip)
    {
        if (!inputs.TryGetValue(clip.Source, out var input) || !input.Allowed) return;
        long first = input.End - HistoryLength;
        for (int i = 0; i < HistoryLength; i++)
        {
            long index = first + i, offset = index - clip.Start * 16;
            if (index >= 0 && offset >= 0 && offset < clip.Data.Length) clip.Data[offset] = input.History[index % HistoryLength];
        }
    }
    private void Gains(Slot slot)
    {
        slot.Left = slot.Right = 0;
        if (!tasks || !audible || state == null || !slot.Entry.Near(listener, state) || slot.Playing == null || listener == null) return;
        if (!inputs.TryGetValue(slot.Playing.Source, out var input)) return;
        double dx = slot.Entry.X - listener.X, dy = slot.Entry.Y - listener.Y;
        float gain = input.Volume * (float)Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / slot.Entry.Range);
        float pan = (float)Math.Clamp(dx / slot.Entry.Range, -1, 1);
        slot.Left = gain * (pan > 0 ? 1 - pan : 1); slot.Right = gain * (pan < 0 ? 1 + pan : 1);
    }
    public void Record(int id, ReadOnlySpan<short> pcm, int channels, bool allowed = true)
    {
        if (id < 0 || channels is not (1 or 2)) return;
        lock (gate)
        {
            if (!tasks || !audible || slots.Count == 0 || !allowed || !inputs.TryGetValue(id, out var input) || !input.Allowed) return;
            int frames = pcm.Length / channels;
            long end = clock() * 16, begin = end - frames / 3;
            double energy = 0;
            for (int i = 0; i < frames / 3; i++) energy += StoreSample(input, pcm, channels, i, begin);
            input.End = end;
            foreach (var slot in slots.Values) RecordSlot(slot, id, input, begin, end, Math.Sqrt(energy / Math.Max(1, frames / 3)));
        }
    }
    private static double StoreSample(Input input, ReadOnlySpan<short> pcm, int channels, int i, long begin)
    {
        int at = i * 3 * channels;
        float sample = channels == 1 ? pcm[at] / 32768f : (pcm[at] + pcm[at + 1]) / 65536f;
        long index = begin + i;
        if (index >= 0) input.History[index % HistoryLength] = sample;
        return sample * sample;
    }
    private void RecordSlot(Slot slot, int id, Input input, long begin, long end, double rms)
    {
        var player = state?.Players.FirstOrDefault(p => p.Id == id);
        if (state == null || player == null || player.IsDead || slot.Entry.Mode != "clone" && !slot.Entry.Near(player, state)) return;
        Detect(slot, id, rms);
        if (!slot.Clips.TryGetValue(slot.Entry.RecordId, out var clip) || clip.Source != id) return;
        long first = Math.Max(begin, clip.Start * 16), last = Math.Min(end, clip.Start * 16 + clip.Data.Length);
        for (long i = Math.Max(0, first); i < last; i++) clip.Data[i - clip.Start * 16] = input.History[i % HistoryLength];
    }
    private void Detect(Slot slot, int source, double rms)
    {
        if (slot.Entry.Mode == "clone") return;
        if (state?.IsHost != true || rms < .008 || clock() < slot.Requested) return;
        if (slot.Clips.TryGetValue(slot.Entry.RecordId, out var clip) && clock() < clip.Start + clip.Data.Length / 16 + 150) return;
        slot.Requested = clock() + (long)(slot.Entry.Seconds * 1000) + 200;
        if (requests.Count < 120) requests.Enqueue((slot.Entry.OwnerId, slot.Entry.BirdId, source));
    }
    public (int Owner, int Bird, int Source)[] DrainRequests()
    {
        lock (gate) { var result = requests.ToArray(); requests.Clear(); return result; }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (gate) foreach (var slot in slots.Values) Mix(slot, buffer, offset, count);
        return count;
    }
    private void Mix(Slot slot, float[] buffer, int offset, int count)
    {
        if (!tasks || !audible || slot.Playing == null) return;
        for (int i = 0; i + 1 < count; i += 2)
        {
            if (slot.Position >= slot.Playing.Data.Length * 3)
            {
                if (slot.Entry.Mode != "clone") break;
                slot.Position = 0;
            }
            float sample = slot.Playing.Data[slot.Position++ / 3];
            buffer[offset + i] += sample * slot.Left; buffer[offset + i + 1] += sample * slot.Right;
        }
        slot.LastMix = clock(); slot.PlayedFrames = Math.Min(5760, slot.PlayedFrames + count / 2);
        if (slot.Entry.Mode != "clone" && slot.Position >= slot.Playing.Data.Length * 3) slot.Playing = null;
    }
    // Display-only VAD from the samples actually mixed, never the owner's live microphone.
    public bool IsCloneTalking(int owner)
    {
        lock (gate) return slots.TryGetValue((owner, 1000000 + owner), out var slot) && CloneSpeaking(slot);
    }
    private bool CloneSpeaking(Slot slot)
    {
        if (!tasks || !audible || slot.Entry.Mode != "clone" || slot.Entry.SpoofTalking != true ||
            slot.Playing == null || Math.Max(slot.Left, slot.Right) <= 0 ||
            slot.LastMix == long.MinValue || clock() - slot.LastMix > 250) return false;
        var data = slot.Playing.Data;
        int count = Math.Min(1920, Math.Min(slot.PlayedFrames / 3, data.Length));
        int end = slot.Position / 3;
        double energy = 0;
        for (int i = 0; i < count; i++)
        {
            float sample = data[((end - 1 - i) % data.Length + data.Length) % data.Length];
            energy += sample * sample;
        }
        return count > 0 && energy / count >= .008 * .008;
    }
    public void SetAudible(bool value)
    {
        lock (gate) { audible = value; if (!value) foreach (var slot in slots.Values) slot.Playing = null; }
    }
    public void Clear() { lock (gate) { tasks = false; slots.Clear(); inputs.Clear(); requests.Clear(); } }
}
