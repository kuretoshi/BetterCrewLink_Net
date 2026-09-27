namespace BetterCrewLinkKai.DotNet.Models;

public sealed class PlayerVoiceMix
{
    public int PlayerId { get; init; }

    public int ClientId { get; init; }

    public string Name { get; init; } = string.Empty;

    public double Volume { get; init; }

    public double PanX { get; init; }

    public double PanY { get; init; }

    public bool IsAudible => Volume > 0;

    public bool IsMuffled { get; init; }

    public bool UsesGhostReverb { get; init; }

    public bool UsesVoiceEffect { get; init; }

    public double VoiceEffectStrength { get; init; }

    public string Reason { get; init; } = string.Empty;
}
