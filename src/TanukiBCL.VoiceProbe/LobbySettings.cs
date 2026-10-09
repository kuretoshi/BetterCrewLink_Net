using System.Text.Json;
using System.Text.Json.Serialization;
using TanukiBCL.VoiceProbe.GameMemory;

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
    public bool NosRokurokubiVoiceEffect { get; init; } = true;
    public bool NosBerserkerVoiceEffect { get; init; } = true;
    public bool NosCitrusVoiceEffect { get; init; } = true;
    public bool NosRainbowStarEcho { get; init; } = true;
    public bool NosFixerJammingVoiceBlock { get; init; } = true;
    public bool NosFixerJammingLowpass { get; init; }
    public bool TohNeutralKillerHaunting { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, bool>? TohGhostRoles { get; init; }
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

    [JsonPropertyName("publicLobby_mods")]
    public string PublicLobbyMods { get; init; } = "NONE";

    public LobbySettings Normalize() => this with
    {
        MaxDistance = double.IsFinite(MaxDistance) ? Math.Clamp(MaxDistance, 1d, 10d) : 5.32d,
        PublicLobbyTitle = PublicLobbyTitle ?? string.Empty,
        PublicLobbyLanguage = string.IsNullOrWhiteSpace(PublicLobbyLanguage) ? "ja" : PublicLobbyLanguage,
        PublicLobbyMods = PublicLobbyMods ?? "NONE"
    };

    // SettingsPanel.tsx RADIO_ONLY_FORCED_SETTINGS. Keep the original values so
    // switching the preset off restores only the fields the preset changed.
    public LobbySettings EnableImpostorRadioOnlyMode() => this with
    {
        ImpostorRadioOnlyMode = true,
        ImpostorRadioEnabled = true,
        MeetingGhostOnly = true,
        DeadOnly = false,
        WallsBlockAudio = false,
        VisionHearing = false,
        VoiceEffectEnabled = false,
        HearImpostorsInVents = false,
        ImpostersHearImpostersInvent = false,
        CommsSabotage = false,
        HearThroughCameras = false,
        SnrJumboVoice = false,
        JackalHearOutsideVents = false,
        JackalTalkInVents = false,
        JackalRadioEnabled = false,
        SidekickHearOutsideVents = false,
        SidekickTalkInVents = false,
        NosVoicePositions = false
    };

    public LobbySettings DisableImpostorRadioOnlyMode(LobbySettings? backup) => backup is null
        ? this with { ImpostorRadioOnlyMode = false }
        : this with
        {
            ImpostorRadioOnlyMode = false,
            ImpostorRadioEnabled = backup.ImpostorRadioEnabled,
            MeetingGhostOnly = backup.MeetingGhostOnly,
            DeadOnly = backup.DeadOnly,
            WallsBlockAudio = backup.WallsBlockAudio,
            VisionHearing = backup.VisionHearing,
            VoiceEffectEnabled = backup.VoiceEffectEnabled,
            HearImpostorsInVents = backup.HearImpostorsInVents,
            ImpostersHearImpostersInvent = backup.ImpostersHearImpostersInvent,
            CommsSabotage = backup.CommsSabotage,
            HearThroughCameras = backup.HearThroughCameras,
            SnrJumboVoice = backup.SnrJumboVoice,
            JackalHearOutsideVents = backup.JackalHearOutsideVents,
            JackalTalkInVents = backup.JackalTalkInVents,
            JackalRadioEnabled = backup.JackalRadioEnabled,
            SidekickHearOutsideVents = backup.SidekickHearOutsideVents,
            SidekickTalkInVents = backup.SidekickTalkInVents,
            NosVoicePositions = backup.NosVoicePositions
        };

    public string ToWireJson() => JsonSerializer.Serialize(Normalize(), WireJsonOptions);
}
