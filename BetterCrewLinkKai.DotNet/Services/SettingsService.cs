using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class SettingsService
{
    private const string ObsSecretAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterCrewLinkKai.DotNet",
        "settings.json");

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions);
        return Normalize(settings ?? new AppSettings());
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, Normalize(settings), SerializerOptions);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (settings.ServerUrls.Count == 0)
        {
            settings.ServerUrls.Add(settings.ServerUrl);
        }

        if (string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            settings.ServerUrl = settings.ServerUrls[0];
        }

        settings.LocalLobbySettings ??= new LobbySettings();
        settings.LocalLobbySettings.PublicLobbyOn = false;
        settings.LocalLobbySettings.PublicLobbyTitle = string.Empty;
        settings.CustomPlatforms ??= [];
        settings.PlayerConfigMap ??= [];
        if (!IsValidObsSecret(settings.ObsSecret))
        {
            settings.ObsSecret = GenerateObsSecret();
        }

        return settings;
    }

    public static string GenerateObsSecret()
    {
        Span<char> chars = stackalloc char[9];
        Span<byte> bytes = stackalloc byte[9];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = ObsSecretAlphabet[bytes[i] % ObsSecretAlphabet.Length];
        }

        return new string(chars);
    }

    private static bool IsValidObsSecret(string? value)
    {
        return value is { Length: 9 } &&
               value.All(static character => char.IsDigit(character) || character is >= 'A' and <= 'Z');
    }
}
