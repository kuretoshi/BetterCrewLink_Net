using System.Security.Cryptography;

namespace TanukiBCL.Client;

internal static class StreamingSettings
{
    private const string SecretAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string ObsOverlayUrl = "https://kuretoshi.github.io/BetterCrewlink-obs_fix/";

    public static bool IsValidSecret(string? secret) => secret is { Length: 9 } &&
        secret.All(character => SecretAlphabet.Contains(character));

    public static string CreateSecret() => new string(Enumerable.Range(0, 9)
        .Select(_ => SecretAlphabet[RandomNumberGenerator.GetInt32(SecretAlphabet.Length)]).ToArray());

    public static string BuildObsUrl(ClientSettings settings) =>
        $"{ObsOverlayUrl}?version={UpdateCatalog.CurrentVersion.Split('-', 2)[0]}" +
        $"&compact={(settings.CompactOverlay ? '1' : '0')}" +
        $"&position={settings.OverlayPosition}" +
        $"&meeting={(settings.MeetingOverlay ? '1' : '0')}" +
        $"&secret={Uri.EscapeDataString(settings.ObsSecret ?? string.Empty)}" +
        $"&server={Uri.EscapeDataString(settings.ServerUrl)}";
}
