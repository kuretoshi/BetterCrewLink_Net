namespace TanukiBCL.VoiceProbe;

// The upstream Web Audio gate releases over 20 ms. Keep hard mute and PTT
// instantaneous, but taper the first frame after VAD closes.
internal sealed class MicrophoneGate
{
    private bool wasOpen;

    internal void Apply(Span<byte> pcm, bool allowed, bool sensitivityEnabled, bool talking)
    {
        var open = allowed && (!sensitivityEnabled || talking);
        if (open)
        {
            wasOpen = true;
            return;
        }
        if (!allowed || !sensitivityEnabled || !wasOpen)
        {
            pcm.Clear();
            wasOpen = false;
            return;
        }
        wasOpen = false;
        var samples = pcm.Length / sizeof(short);
        for (var index = 0; index < samples; index++)
        {
            var sample = BitConverter.ToInt16(pcm.Slice(index * sizeof(short), sizeof(short)));
            var tapered = (short)Math.Round(sample * (samples - index - 1d) / samples);
            BitConverter.TryWriteBytes(pcm.Slice(index * sizeof(short), sizeof(short)), tapered);
        }
    }

    internal static void Verify()
    {
        var gate = new MicrophoneGate();
        var frame = new byte[MicrophoneProcessor.BytesPerFrame];
        for (var index = 0; index < frame.Length; index += 2)
            BitConverter.TryWriteBytes(frame.AsSpan(index, 2), (short)10_000);
        gate.Apply(frame, true, true, true);
        gate.Apply(frame, true, true, false);
        if (BitConverter.ToInt16(frame, 0) != 9_990 || BitConverter.ToInt16(frame, frame.Length - 2) != 0)
            throw new InvalidOperationException("The microphone VAD gate did not release over one frame.");
        gate.Apply(frame, true, true, false);
        if (frame.Any(value => value != 0))
            throw new InvalidOperationException("The microphone VAD gate leaked a second closed frame.");
        gate.Apply(frame, true, true, true);
        gate.Apply(frame, false, true, true);
        if (frame.Any(value => value != 0))
            throw new InvalidOperationException("Hard microphone mute was delayed by the VAD release.");
    }
}
