namespace BetterCrewLinkKai.DotNet.Models;

public sealed class LobbySettings
{
    public double MaxDistance { get; set; } = 5.32;

    public bool VisionHearing { get; set; }

    public bool Haunting { get; set; }

    public bool ThirdPartyHaunting { get; set; }

    public bool HearImpostorsInVents { get; set; }

    public bool ImpostorsHearImpostorsInVent { get; set; }

    public bool ImpostorRadioEnabled { get; set; }

    public bool CommsSabotage { get; set; }

    public bool VoiceEffectEnabled { get; set; } = true;

    public bool DeadOnly { get; set; }

    public bool MeetingGhostOnly { get; set; }

    public bool HearThroughCameras { get; set; }

    public bool WallsBlockAudio { get; set; }

    public bool PublicLobbyOn { get; set; }

    public string PublicLobbyTitle { get; set; } = string.Empty;

    public string PublicLobbyLanguage { get; set; } = "ja";

    public string PublicLobbyMods { get; set; } = "NONE";
}
