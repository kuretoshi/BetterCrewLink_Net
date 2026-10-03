using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

internal sealed class ClientSettings
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static readonly PropertyInfo[] SettingsProperties = typeof(ClientSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true)
        .ToArray();

    public string ServerUrl { get; set; } = "https://bettercrewl.ink";
    public string Language { get; set; } = "ja";
    public List<string> ServerUrls { get; set; } = ["https://bettercrewl.ink"];
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
    public bool MobileHost { get; set; } = true;
    public bool EnableSpatialAudio { get; set; } = true;
    public bool EchoCancellation { get; set; } = true;
    public bool NoiseSuppression { get; set; } = true;
    public bool AutoGainControl { get; set; }
    public bool OldSampleDebug { get; set; }
    public bool HardwareAcceleration { get; set; } = true;
    public string LaunchPlatform { get; set; } = "STEAM";
    public Dictionary<string, GameLaunchPlatform> CustomPlatforms { get; set; } = [];
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

    // Use the same serializable property model as the settings file. Newly added
    // properties participate without another hand-maintained clone/copy list.
    public ClientSettings Clone() => JsonSerializer.Deserialize<ClientSettings>(
        JsonSerializer.SerializeToUtf8Bytes(this, SnapshotJsonOptions), SnapshotJsonOptions)!;

    public void CopyFrom(ClientSettings source, params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(propertyNames);
        var properties = propertyNames.Length == 0 ? SettingsProperties : propertyNames
            .Distinct(StringComparer.Ordinal)
            .Select(name => SettingsProperties.SingleOrDefault(property => property.Name == name)
                ?? throw new ArgumentException($"Unknown settings property: {name}", nameof(propertyNames)))
            .ToArray();
        var copy = source.Clone();
        foreach (var property in properties) property.SetValue(this, property.GetValue(copy));
    }

    public bool ContentEquals(ClientSettings other) => JsonNode.DeepEquals(
        JsonSerializer.SerializeToNode(this, SnapshotJsonOptions),
        JsonSerializer.SerializeToNode(other, SnapshotJsonOptions));

    public void Normalize()
    {
        ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? "https://bettercrewl.ink" : ServerUrl.Trim();
        Language = UiLocalization.Normalize(Language);
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            ServerUrl = "https://bettercrewl.ink";
        }
        ServerUrls = (ServerUrls ?? [])
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                          uri.Scheme is "http" or "https")
            .Append(ServerUrl)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        MasterVolume = Math.Clamp(MasterVolume, 0, 200);
        VoiceEffectStrength = Math.Clamp(VoiceEffectStrength, 0, 100);
        CrewVolumeAsGhost = Math.Clamp(CrewVolumeAsGhost, 0, 100);
        GhostVolumeAsImpostor = Math.Clamp(GhostVolumeAsImpostor, 0, 100);
        MicrophoneGain = Math.Clamp(MicrophoneGain, 0, 300);
        MicSensitivity = double.IsFinite(MicSensitivity) ? Math.Clamp(MicSensitivity, 0d, 1d) : 0.15d;
        if (!Enum.IsDefined(PushToTalkMode)) PushToTalkMode = MicrophoneActivationMode.Voice;
        if (OverlayPosition is not ("hidden" or "top" or "bottom_left" or "right" or
            "right1" or "left" or "left1")) OverlayPosition = "right";
        // 3.2.8 creates a secret only when it is absent; existing values are
        // opaque credentials and must survive imported settings unchanged.
        if (string.IsNullOrEmpty(ObsSecret))
            ObsSecret = ObsOverlay ? StreamingSettings.CreateSecret() : null;
        LaunchPlatform = string.IsNullOrWhiteSpace(LaunchPlatform) ? "STEAM" : LaunchPlatform;
        CustomPlatforms = (CustomPlatforms ?? [])
            .Where(pair => pair.Value is not null && pair.Key == pair.Value.Key && pair.Value.IsValid &&
                !new[] { "STEAM", "EPIC", "MICROSOFT" }.Contains(pair.Key,
                    StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
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
