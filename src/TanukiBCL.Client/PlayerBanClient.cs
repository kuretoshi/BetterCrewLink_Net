using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanukiBCL.Client;

internal sealed record PlayerBanRecord(
    [property: JsonPropertyName("uid_digest")] string UidDigest,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("friend_code")] string FriendCode,
    [property: JsonPropertyName("reason")] string Reason);

internal static class PlayerBanClient
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(7) };
    private const string BaseUrl = "https://debug-auth.kuretoshi.work/v1/player-bans/";

    internal static async Task<string> CheckAsync(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 256) return "unavailable";
        try
        {
            using var response = await Client.PostAsJsonAsync(BaseUrl + "check", new { playerUid = uid });
            if (!response.IsSuccessStatusCode) return "unavailable";
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (!body.RootElement.TryGetProperty("banned", out var banned)) return "unavailable";
            return banned.ValueKind == JsonValueKind.True ? "banned" :
                banned.ValueKind == JsonValueKind.False ? "clear" : "unavailable";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "unavailable";
        }
    }

    internal static async Task<(PlayerBanRecord[]? Bans, string? Error)> ManageAsync(object payload)
    {
        try
        {
            using var response = await Client.PostAsJsonAsync(BaseUrl + "manage", payload);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (!response.IsSuccessStatusCode)
                return (null, body.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString() : "BAN管理サーバーに接続できませんでした。");
            if (!body.RootElement.TryGetProperty("bans", out var bans) || bans.ValueKind != JsonValueKind.Array)
                return (null, "BAN一覧を取得できませんでした。");
            return (bans.Deserialize<PlayerBanRecord[]>() ?? [], null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, "BAN管理サーバーに接続できませんでした。");
        }
    }
}
