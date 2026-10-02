using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 src/common/ISettings.d.ts (ILobbySettings).
internal sealed record LobbySettings
{
    internal static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    public double MaxDistance { get; init; } = 5.32d;
    public bool VisionHearing { get; init; }
    public bool Haunting { get; init; }
    public bool SnrJumboVoice { get; init; }
    public bool JackalHaunting { get; init; }
    public bool JackalHearOutsideVents { get; init; }
    public bool JackalTalkInVents { get; init; }
    public bool JackalRadioEnabled { get; init; }
    public bool SidekickHaunting { get; init; }
    public bool SidekickHearOutsideVents { get; init; }
    public bool SidekickTalkInVents { get; init; }
    public bool NosNeutralKillerHaunting { get; init; }
    public bool NosVoicePositions { get; init; }
    public bool NosSizeVoiceEffect { get; init; } = true;
    public bool NosFixerJammingVoiceBlock { get; init; } = true;
    public bool TohNeutralKillerHaunting { get; init; }
    public bool HearImpostorsInVents { get; init; }
    public bool ImpostersHearImpostersInvent { get; init; }
    public bool ImpostorRadioEnabled { get; init; }
    public bool ImpostorRadioOnlyMode { get; init; }
    public bool CommsSabotage { get; init; }
    public bool VoiceEffectEnabled { get; init; } = true;
    public bool DeadOnly { get; init; }
    public bool MeetingGhostOnly { get; init; }
    public bool HearThroughCameras { get; init; }
    public bool WallsBlockAudio { get; init; }

    [JsonPropertyName("publicLobby_on")]
    public bool PublicLobbyOn { get; init; }

    [JsonPropertyName("publicLobby_title")]
    public string PublicLobbyTitle { get; init; } = string.Empty;

    [JsonPropertyName("publicLobby_language")]
    public string PublicLobbyLanguage { get; init; } = "ja";

    public LobbySettings Normalize() => this with
    {
        MaxDistance = double.IsFinite(MaxDistance) ? Math.Clamp(MaxDistance, 1d, 10d) : 5.32d,
        PublicLobbyTitle = PublicLobbyTitle ?? string.Empty,
        PublicLobbyLanguage = string.IsNullOrWhiteSpace(PublicLobbyLanguage) ? "ja" : PublicLobbyLanguage
    };

    public string ToWireJson() => JsonSerializer.Serialize(Normalize(), WireJsonOptions);
}
