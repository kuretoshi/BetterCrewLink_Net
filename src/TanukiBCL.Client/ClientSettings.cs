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
    public int MasterVolume { get; set; } = 100;
    public int MicrophoneGain { get; set; } = 100;
    public bool MicrophoneGainEnabled { get; set; }
    public double MicSensitivity { get; set; } = 0.15d;
    public bool MicSensitivityEnabled { get; set; }
    public LobbySettings MyLobbySettings { get; set; } = new();

    public void Normalize()
    {
        ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? "https://bettercrewl.ink" : ServerUrl.Trim();
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            ServerUrl = "https://bettercrewl.ink";
        }
        MasterVolume = Math.Clamp(MasterVolume, 0, 200);
        MicrophoneGain = Math.Clamp(MicrophoneGain, 0, 300);
        MicSensitivity = Math.Clamp(MicSensitivity, 0d, 1d);
        MyLobbySettings = (MyLobbySettings ?? new LobbySettings()).Normalize();
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
