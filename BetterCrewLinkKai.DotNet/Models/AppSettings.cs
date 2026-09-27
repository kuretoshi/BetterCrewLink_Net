namespace BetterCrewLinkKai.DotNet.Models;

public enum PushToTalkMode
{
    Voice,
    PushToTalk,
    PushToMute
}

public sealed class SocketConfig
{
    public double Volume { get; set; } = 1;

    public bool IsMuted { get; set; }
}

public sealed class AppSettings
{
    public bool AlwaysOnTop { get; set; }

    public string Language { get; set; } = "ja";

    public string Microphone { get; set; } = "Default";

    public string Speaker { get; set; } = "Default";

    public PushToTalkMode PushToTalkMode { get; set; } = PushToTalkMode.Voice;

    public string ServerUrl { get; set; } = "https://bettercrewl.ink";

    public List<string> ServerUrls { get; set; } = ["https://bettercrewl.ink"];

    public string PushToTalkShortcut { get; set; } = "V";

    public string DeafenShortcut { get; set; } = "RControl";

    public string MuteShortcut { get; set; } = "RAlt";

    public string ImpostorRadioShortcut { get; set; } = "F";

    public bool HideCode { get; set; }

    public bool NatFix { get; set; }

    public bool CompactOverlay { get; set; }

    public string OverlayPosition { get; set; } = "right";

    public bool EnableOverlay { get; set; } = true;

    public bool MeetingOverlay { get; set; } = true;

    public LobbySettings LocalLobbySettings { get; set; } = new();

    public double GhostVolumeAsImpostor { get; set; } = 10;

    public double CrewVolumeAsGhost { get; set; } = 100;

    public double MasterVolume { get; set; } = 100;

    public double MicrophoneGain { get; set; } = 100;

    public bool MicrophoneGainEnabled { get; set; }

    public double VoiceEffectStrength { get; set; } = 100;

    public double MicSensitivity { get; set; } = 0.15;

    public bool MicSensitivityEnabled { get; set; }

    public bool MobileHost { get; set; } = true;

    public bool VadEnabled { get; set; } = true;

    public bool HardwareAcceleration { get; set; } = true;

    public bool EchoCancellation { get; set; } = true;

    public bool NoiseSuppression { get; set; } = true;

    public bool OldSampleDebug { get; set; }

    public bool EnableSpatialAudio { get; set; } = true;

    public Dictionary<string, SocketConfig> PlayerConfigMap { get; set; } = [];

    public bool ObsOverlay { get; set; }

    public string? ObsSecret { get; set; }

    public GamePlatform LaunchPlatform { get; set; } = GamePlatform.Steam;

    public Dictionary<string, GamePlatformInstance> CustomPlatforms { get; set; } = [];
}
