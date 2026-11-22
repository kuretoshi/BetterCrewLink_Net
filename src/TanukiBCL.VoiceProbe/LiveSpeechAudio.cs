namespace TanukiBCL.VoiceProbe;

// Cheap scheduling gate only; Silero VAD validates every submitted utterance.
internal sealed class LiveSpeechAudio
{
    private readonly Queue<(long At, byte[] Pcm)> chunks = new();
    private long started, voiced, submitted, lastAt;
    private bool dirty;
    public void Append(byte[] pcm, long at)
    {
        if (pcm.Length is 0 or > 32000 || pcm.Length % 2 != 0) return;
        if (lastAt > 0 && at - lastAt > 500) Reset();
        lastAt = at;
        long end = at + pcm.Length / 32;
        double energy = 0;
        for (int i = 0; i < pcm.Length; i += 2) { double value = (short)(pcm[i] | pcm[i + 1] << 8) / 32768.0; energy += value * value; }
        chunks.Enqueue((at, pcm));
        while (chunks.Count > 0 && chunks.Peek().At + chunks.Peek().Pcm.Length / 32 <= end - 8000) chunks.Dequeue();
        if (energy / (pcm.Length / 2) >= 0.000001) { if (started == 0) started = at; voiced = end; dirty = true; }
        if (started == 0) while (chunks.Count > 0 && chunks.Peek().At <= end - 300) chunks.Dequeue();
    }
    public byte[]? Take(long now)
    {
        if (started == 0 || !dirty || now - started < 150 || (now - voiced < 350 && now - Math.Max(started, submitted) < 1200)) return null;
        byte[] pcm = chunks.SelectMany(c => c.Pcm).ToArray();
        submitted = now; dirty = false;
        if (now - voiced >= 350) Reset();
        return pcm.Length >= 3200 ? pcm : null;
    }
    public void Reset() { chunks.Clear(); started = voiced = submitted = lastAt = 0; dirty = false; }
}
