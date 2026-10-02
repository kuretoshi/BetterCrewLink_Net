using System.IO;
using System.Text.Json;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

internal sealed class ClientSettings
{
    public string ServerUrl { get; set; } = "https://bettercrewl.ink";
    public string? MicrophoneName { get; set; }
    public string? SpeakerName { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool EnableOverlay { get; set; }
    public bool CompactOverlay { get; set; }
    public bool MeetingOverlay { get; set; } = true;
    public string OverlayPosition { get; set; } = "right";
    public bool HideCode { get; set; }
    public bool ObsOverlay { get; set; }
    public string? ObsSecret { get; set; }
    public bool NatFix { get; set; }
    public int MasterVolume { get; set; } = 100;
    public int VoiceEffectStrength { get; set; } = 100;
    public int CrewVolumeAsGhost { get; set; } = 100;
    public int GhostVolumeAsImpostor { get; set; } = 10;
    public int MicrophoneGain { get; set; } = 100;
    public bool MicrophoneGainEnabled { get; set; }
    public double MicSensitivity { get; set; } = 0.15d;
    public bool MicSensitivityEnabled { get; set; }
    public MicrophoneActivationMode PushToTalkMode { get; set; } = MicrophoneActivationMode.Voice;
    public string PushToTalkShortcut { get; set; } = "V";
    public string ImpostorRadioShortcut { get; set; } = "F";
    public string MuteShortcut { get; set; } = "RAlt";
    public string DeafenShortcut { get; set; } = "RControl";
    public LobbySettings MyLobbySettings { get; set; } = new();
    public LobbySettings? RadioOnlyBackup { get; set; }
    public Dictionary<int, PlayerAudioConfig> PlayerConfigMap { get; set; } = [];

    public void Normalize()
    {
        ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? "https://bettercrewl.ink" : ServerUrl.Trim();
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            ServerUrl = "https://bettercrewl.ink";
        }
        MasterVolume = Math.Clamp(MasterVolume, 0, 200);
        VoiceEffectStrength = Math.Clamp(VoiceEffectStrength, 0, 100);
        CrewVolumeAsGhost = Math.Clamp(CrewVolumeAsGhost, 0, 100);
        GhostVolumeAsImpostor = Math.Clamp(GhostVolumeAsImpostor, 0, 100);
        MicrophoneGain = Math.Clamp(MicrophoneGain, 0, 300);
        MicSensitivity = Math.Clamp(MicSensitivity, 0d, 1d);
        if (!Enum.IsDefined(PushToTalkMode)) PushToTalkMode = MicrophoneActivationMode.Voice;
        if (OverlayPosition is not ("hidden" or "top" or "bottom_left" or "right" or
            "right1" or "left" or "left1")) OverlayPosition = "right";
        if (!StreamingSettings.IsValidSecret(ObsSecret))
            ObsSecret = ObsOverlay ? StreamingSettings.CreateSecret() : null;
        PushToTalkShortcut = GlobalHotkeyMonitor.NormalizeShortcut(PushToTalkShortcut, "V");
        ImpostorRadioShortcut = GlobalHotkeyMonitor.NormalizeShortcut(ImpostorRadioShortcut, "F");
        MuteShortcut = GlobalHotkeyMonitor.NormalizeShortcut(MuteShortcut, "RAlt");
        DeafenShortcut = GlobalHotkeyMonitor.NormalizeShortcut(DeafenShortcut, "RControl");
        MyLobbySettings = (MyLobbySettings ?? new LobbySettings()).Normalize();
        RadioOnlyBackup = RadioOnlyBackup?.Normalize();
        PlayerConfigMap = (PlayerConfigMap ?? []).ToDictionary(
            pair => pair.Key,
            pair => (pair.Value ?? PlayerAudioConfig.Default).Normalize());
    }
}

internal static class ClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TanukiBCL.Net", "settings.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new ClientSettings();
    }

    public static void Save(ClientSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
