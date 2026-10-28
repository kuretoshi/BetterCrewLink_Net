using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanukiBCL.Client;

internal sealed record PlayerBanRecord(
    [property: JsonPropertyName("uid_digest")] string UidDigest,
    [property: JsonPropertyName("player_uid")] string PlayerUid,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("friend_code")] string FriendCode,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record PendingPlayerNameBan(
    [property: JsonPropertyName("player_name")] string PlayerName,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record PlayerHistoryRecord(
    [property: JsonPropertyName("uid_digest")] string UidDigest,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("friend_code")] string FriendCode,
    [property: JsonPropertyName("first_seen_at")] long FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] long LastSeenAt,
    [property: JsonPropertyName("is_banned")] int IsBanned);

internal static class PlayerBanClient
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(7) };
    private const string BaseUrl = "https://debug-auth.kuretoshi.work/v1/player-bans/";

    internal static async Task<string> CheckAsync(string uid, string name, object[] observedPlayers)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 256 || name.Length > 80) return "unavailable";
        try
        {
            using var response = await Client.PostAsJsonAsync(BaseUrl + "check", new { playerUid = uid, playerName = name, observedPlayers });
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

    internal static async Task<(PlayerBanRecord[]? Bans, PendingPlayerNameBan[]? Pending,
        PlayerHistoryRecord[]? History, int HistoryTotal, string? Error)> ManageAsync(object payload)
    {
        try
        {
            using var response = await Client.PostAsJsonAsync(BaseUrl + "manage", payload);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (!response.IsSuccessStatusCode)
                return (null, null, null, 0, body.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString() : "BAN管理サーバーに接続できませんでした。");
            if (!body.RootElement.TryGetProperty("bans", out var bans) || bans.ValueKind != JsonValueKind.Array)
                return (null, null, null, 0, "BAN一覧を取得できませんでした。");
            if (!body.RootElement.TryGetProperty("pending", out var pending) || pending.ValueKind != JsonValueKind.Array)
                return (null, null, null, 0, "UID待ち一覧を取得できませんでした。");
            if (!body.RootElement.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array ||
                !body.RootElement.TryGetProperty("historyTotal", out var total) || total.ValueKind != JsonValueKind.Number)
                return (bans.Deserialize<PlayerBanRecord[]>() ?? [], pending.Deserialize<PendingPlayerNameBan[]>() ?? [],
                    null, 0, null);
            return (bans.Deserialize<PlayerBanRecord[]>() ?? [], pending.Deserialize<PendingPlayerNameBan[]>() ?? [],
                history.Deserialize<PlayerHistoryRecord[]>() ?? [], total.GetInt32(), null);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, null, null, 0, "BAN管理サーバーに接続できませんでした。");
        }
    }
}
