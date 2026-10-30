using System.Net;
using System.Text;
using System.Text.Json;
using TanukiBCL.Client;

internal static class Program
{
    private static readonly Uri Endpoint = new("https://example.invalid/player-bans/");
    private const string NewResponse = """
        {"bans":[{"uid_digest":"digest","player_uid":"self","display_name":"Self","friend_code":"",
          "reason":"test","ban_type":"ip","public_ip":"8.8.8.8"}],"pending":[],"history":[{
          "uid_digest":"digest","display_name":"Self","friend_code":"","first_seen_at":100,
          "last_seen_at":200,"is_banned":1,"uid_banned":0,"ip_banned":1,"public_ip":"8.8.8.8"}],
          "historyTotal":1,"ipBansAvailable":true}
        """;

    public static async Task Main(string[] args)
    {
        await VerifyCheckAsync();
        await VerifyManagementAsync();
        await VerifyOldServerAsync();
        await VerifyNoFallbackAsync();
        if (args.Length == 1) await VerifyLocalApiAsync(new Uri(args[0]));
        Console.WriteLine("PASS player BAN client: trusted-source request shape, UID/IP modes, legacy capability, explicit errors" +
            (args.Length == 1 ? ", actual local API integration" : ""));
    }

    private static async Task VerifyCheckAsync()
    {
        using var handler = new FixtureHandler("{\"banned\":false}");
        using var client = new HttpClient(handler);
        object[] roster = [new { playerUid = "observed", playerName = "Other" }];
        Require(await PlayerBanClient.CheckAsync(client, Endpoint, "self", "Self", roster) == "clear", "Clear check failed.");
        using var request = JsonDocument.Parse(handler.Requests.Single());
        var root = request.RootElement;
        Require(root.EnumerateObject().Select(value => value.Name).Order().SequenceEqual(
            new[] { "observedPlayers", "playerName", "playerUid" }), "Client sent unexpected IP attribution fields.");
        Require(root.GetProperty("playerUid").GetString() == "self" &&
            root.GetProperty("observedPlayers")[0].GetProperty("playerUid").GetString() == "observed",
            "Self and observed identities were mixed.");
        Require(await PlayerBanClient.CheckAsync(client, Endpoint, "", "Self", []) == "unavailable" &&
            handler.Requests.Count == 1, "Invalid UID was transmitted.");
    }

    private static async Task VerifyManagementAsync()
    {
        using var handler = new FixtureHandler(NewResponse);
        using var client = new HttpClient(handler);
        var result = await PlayerBanClient.ManageAsync(client, Endpoint, new { password = "fixture", action = "list" });
        Require(result.Error is null && result.IpBansAvailable && result.HistoryTotal == 1, "New capability was lost.");
        var ban = result.Bans!.Single();
        var history = result.History!.Single();
        Require(ban.BanType == "ip" && ban.PublicIp == "8.8.8.8" &&
            history.PublicIp == "8.8.8.8" && !history.IsUidBanned && history.IpBanned == 1,
            "Independent UID/IP fields were lost.");
        using var ipRequest = JsonDocument.Parse(JsonSerializer.Serialize(PlayerBanClient.RemovalRequest("fixture", ban)));
        Require(ipRequest.RootElement.GetProperty("action").GetString() == "remove-ip" &&
            ipRequest.RootElement.GetProperty("publicIp").GetString() == "8.8.8.8" &&
            !ipRequest.RootElement.TryGetProperty("uidDigest", out _), "IP removal became UID removal.");
        using var uidRequest = JsonDocument.Parse(JsonSerializer.Serialize(PlayerBanClient.RemovalRequest("fixture", ban with { BanType = "uid" })));
        Require(uidRequest.RootElement.GetProperty("action").GetString() == "remove" &&
            !uidRequest.RootElement.TryGetProperty("publicIp", out _), "UID removal became IP removal.");
        try { PlayerBanClient.RemovalRequest("fixture", ban with { BanType = "unknown" }); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Unknown BAN type silently became a UID removal.");
    }

    private static async Task VerifyOldServerAsync()
    {
        const string oldResponse = """
            {"bans":[{"uid_digest":"digest","player_uid":"self","display_name":"Self","friend_code":"","reason":""}],
            "pending":[],"history":[{"uid_digest":"digest","display_name":"Self","friend_code":"",
            "first_seen_at":100,"last_seen_at":200,"is_banned":1}],"historyTotal":1}
            """;
        using var handler = new FixtureHandler(oldResponse);
        using var client = new HttpClient(handler);
        var result = await PlayerBanClient.ManageAsync(client, Endpoint, new { password = "fixture", action = "list" });
        Require(result.Error is null && !result.IpBansAvailable && result.Bans!.Single().BanType == "uid" &&
            result.History!.Single().IsUidBanned && result.History!.Single().PublicIp == "", "Legacy UID compatibility changed.");
    }

    private static async Task VerifyNoFallbackAsync()
    {
        foreach (var action in new[] { "add-ip", "add-history-ip" })
        {
            using var handler = new FixtureHandler("{\"error\":\"No verified public IP is available for this player.\"}", HttpStatusCode.BadRequest);
            using var client = new HttpClient(handler);
            var result = await PlayerBanClient.ManageAsync(client, Endpoint,
                new { password = "fixture", action, playerUid = "self", uidDigest = "digest" });
            Require(result.Error == "No verified public IP is available for this player." && !result.IpBansAvailable &&
                handler.Requests.Count == 1, "Unsupported or unavailable IP action silently retried as UID BAN.");
            using var request = JsonDocument.Parse(handler.Requests.Single());
            Require(request.RootElement.GetProperty("action").GetString() == action, "IP action changed in transit.");
        }
    }

    private static async Task VerifyLocalApiAsync(Uri baseUri)
    {
        Require(baseUri.IsLoopback && baseUri.Scheme == "http", "Integration tests require an isolated loopback HTTP fixture.");
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Real-IP", "8.8.8.8");
        var check = new Uri(baseUri, "check");
        var manage = new Uri(baseUri, "manage");
        Require(await PlayerBanClient.CheckAsync(client, check, "net-self", "NET Self", []) == "clear", "Initial API check failed.");
        var listed = await PlayerBanClient.ManageAsync(client, manage, new { password = "fixture-admin", action = "list" });
        Require(listed.Error is null && listed.IpBansAvailable && listed.History!.Single().PublicIp == "8.8.8.8", "API history IP capture failed.");
        var digest = listed.History!.Single().UidDigest;
        var added = await PlayerBanClient.ManageAsync(client, manage,
            new { password = "fixture-admin", action = "add-history-ip", uidDigest = digest });
        Require(added.Error is null && added.Bans!.Single().BanType == "ip", "API IP BAN creation failed.");
        Require(await PlayerBanClient.CheckAsync(client, check, "net-other", "NET Other", []) == "banned", "IP BAN did not cover changed UID.");
        client.DefaultRequestHeaders.Remove("X-Real-IP");
        client.DefaultRequestHeaders.Add("X-Real-IP", "1.1.1.1");
        Require(await PlayerBanClient.CheckAsync(client, check, "net-self", "NET Self", []) == "clear", "IP BAN followed a UID to a different IP.");
        var removed = await PlayerBanClient.ManageAsync(client, manage, PlayerBanClient.RemovalRequest("fixture-admin", added.Bans!.Single()));
        Require(removed.Error is null && removed.Bans!.Length == 0, "API IP BAN removal failed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
