namespace TanukiBCL.VoiceProbe;
internal static class VoiceStoneAudio
{
    internal static ReadOnlySpan<byte> Window(byte[] bytes, long at, long started, long until)
    {
        int start = (int)Math.Clamp((started - at) * 32, 0, bytes.Length);
        int end = (int)Math.Clamp((until - at) * 32, 0, bytes.Length);
        return end > start ? bytes.AsSpan(start, end - start) : ReadOnlySpan<byte>.Empty;
    }
    internal static byte[] Wav(byte[] pcm)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return output.ToArray();
    }
}
