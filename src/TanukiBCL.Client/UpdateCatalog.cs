using System.Net;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TanukiBCL.Client;

internal sealed record UpdateCandidate(string Version, Uri DownloadUrl, string Sha256, long Size, Uri ReleasePage);

// The Electron 3.2.7 updater installs Electron/NSIS artifacts. The .NET client
// must use its own release feed and reject source archives or unverified assets.
internal static partial class UpdateCatalog
{
    internal static readonly Uri ReleasesApi = new(
        "https://api.github.com/repos/kuretoshi/BetterCrewLink_Net/releases?per_page=20");
    private const string AssetName = "TanukiBCL.Net-win-x64.zip";
    private const string DownloadPrefix =
        "https://github.com/kuretoshi/BetterCrewLink_Net/releases/download/";

    internal static string CurrentVersion => typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?.Split('+', 2)[0] ?? "0.0.0";

    internal static async Task<UpdateCandidate?> CheckAsync(HttpClient client, string currentVersion,
        Uri? feed = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feed ?? ReleasesApi);
        request.Headers.UserAgent.ParseAdd("TanukiBCL.Net-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub Release一覧の形式が不正です。");
        UpdateCandidate? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.GetString() is not { } tag || CompareVersions(tag, currentVersion) <= 0)
                continue;
            if (!release.TryGetProperty("html_url", out var pageElement) ||
                !Uri.TryCreate(pageElement.GetString(), UriKind.Absolute, out var page) ||
                page.Scheme != Uri.UriSchemeHttps || page.Host != "github.com" ||
                !page.AbsolutePath.StartsWith("/kuretoshi/BetterCrewLink_Net/releases/tag/", StringComparison.Ordinal))
                continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name) || name.GetString() != AssetName ||
                    !asset.TryGetProperty("state", out var state) || state.GetString() != "uploaded" ||
                    !asset.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) ||
                    size <= 0 || !asset.TryGetProperty("digest", out var digestElement) ||
                    digestElement.GetString() is not { } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !HexSha256().IsMatch(digest[7..]) ||
                    !asset.TryGetProperty("browser_download_url", out var urlElement) ||
                    !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url) ||
                    !url.AbsoluteUri.StartsWith(DownloadPrefix, StringComparison.Ordinal) ||
                    !url.AbsolutePath.EndsWith('/' + AssetName, StringComparison.Ordinal))
                    continue;
                var candidate = new UpdateCandidate(tag, url, digest[7..].ToLowerInvariant(), size, page);
                if (newest is null || CompareVersions(candidate.Version, newest.Version) > 0)
                    newest = candidate;
            }
        }
        return newest;
    }

    internal static int CompareVersions(string left, string right)
    {
        static int[] Parts(string value)
        {
            var match = VersionPattern().Match(value.Trim());
            if (!match.Success) return [0, 0, 0, 0];
            var suffix = match.Groups[4].Value;
            var numericSuffix = Regex.Match(suffix, @"\d+");
            return [int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value), numericSuffix.Success ? int.Parse(numericSuffix.Value) : 0];
        }
        var a = Parts(left);
        var b = Parts(right);
        for (var index = 0; index < a.Length; index++)
        {
            var comparison = a[index].CompareTo(b[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    [GeneratedRegex("^[vV]?(\\d+)\\.(\\d+)\\.(\\d+)(?:-([^+]+))?(?:\\+.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexSha256();

    internal static async Task VerifyAsync()
    {
        var json = """
            [{"tag_name":"v3.2.7-net.2","draft":false,
              "html_url":"https://github.com/kuretoshi/BetterCrewLink_Net/releases/tag/v3.2.7-net.2",
              "assets":[{"name":"TanukiBCL.Net-win-x64.zip","state":"uploaded","size":123,
                "digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "browser_download_url":"https://github.com/kuretoshi/BetterCrewLink_Net/releases/download/v3.2.7-net.2/TanukiBCL.Net-win-x64.zip"}]}]
            """;
        using var handler = new CatalogHandler(json);
        using var client = new HttpClient(handler);
        var candidate = await CheckAsync(client, "3.2.7-net.1", new Uri("https://example.invalid/releases"));
        if (candidate?.Version != "v3.2.7-net.2" || candidate.Size != 123 ||
            candidate.Sha256.Length != 64 || handler.Requests != 1 || !handler.HasUserAgent)
            throw new InvalidOperationException(".NET update release candidate was not recognized");
        if (await CheckAsync(client, "3.2.7-net.2", new Uri("https://example.invalid/releases")) is not null)
            throw new InvalidOperationException("Same version was offered as an update");
        handler.Body = json.Replace("sha256:aaaaaaaa", "sha256:zzzzzzzz");
        if (await CheckAsync(client, "3.2.7-net.1", new Uri("https://example.invalid/releases")) is not null)
            throw new InvalidOperationException("Unverifiable release asset was accepted");
        handler.Status = HttpStatusCode.NotFound;
        if (await CheckAsync(client, "3.2.7-net.1", new Uri("https://example.invalid/releases")) is not null)
            throw new InvalidOperationException("Missing release was reported as an update");
        Console.WriteLine("[PASS] .NET release updater selects a newer verified asset and rejects invalid/missing releases");
    }

    private sealed class CatalogHandler(string body) : HttpMessageHandler
    {
        internal string Body = body;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal int Requests;
        internal bool HasUserAgent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            HasUserAgent = request.Headers.UserAgent.Count > 0;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
}
