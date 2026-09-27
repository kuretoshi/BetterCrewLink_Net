using NAudio.Wave;

namespace BetterCrewLinkKai.DotNet.Models;

public sealed class VoiceAudioFrame
{
    public VoiceAudioFrame(byte[] buffer, WaveFormat format, double level, bool isTransmitting)
    {
        Buffer = buffer;
        Format = format;
        Level = level;
        IsTransmitting = isTransmitting;
        CapturedAt = DateTimeOffset.UtcNow;
    }

    public byte[] Buffer { get; }

    public WaveFormat Format { get; }

    public double Level { get; }

    public bool IsTransmitting { get; }

    public DateTimeOffset CapturedAt { get; }
}
