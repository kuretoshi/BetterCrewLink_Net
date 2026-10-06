using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TanukiBCL.Client;

internal enum DebugAuthResult
{
    Authorized,
    Denied,
    Unavailable
}

internal static partial class DeveloperDebugAuth
{
    private const int Iterations = 100_000;
    internal const int MaxDebugPasswords = 16;
    private const int MaxConfigurationBytes = 16 * 1_024;
    internal const string DefaultEndpoint = "https://debug-auth.kuretoshi.work/v1/debug-auth/verify";

    private static readonly HttpClient RemoteClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// 3.2.9 OPEN_DEBUG: a configured HTTPS endpoint is authoritative and never falls back to
    /// bundled hashes; otherwise any of up to 16 local PBKDF2 records may match.
    /// </summary>
    internal static async Task<DebugAuthResult> VerifyAsync(string? password, string? configuration = null,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? send = null)
    {
        configuration ??= ResolveConfiguration();
        if (TryReadEndpoint(configuration, out var endpoint))
            return endpoint is null
                ? DebugAuthResult.Unavailable
                : await VerifyRemoteAsync(password, endpoint, send ?? (request => RemoteClient.SendAsync(request)));
        return Verify(password, configuration) ? DebugAuthResult.Authorized : DebugAuthResult.Denied;
    }

    internal static bool Verify(string? password, string? configuration = null)
    {
        configuration ??= ResolveConfiguration();
        if (string.IsNullOrEmpty(password) || password.Length > 1_024 || string.IsNullOrEmpty(configuration))
            return false;

        try
        {
            using var document = JsonDocument.Parse(configuration);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var records = root.TryGetProperty("passwords", out var list)
                ? list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : null
                : [root];
            if (records is null || records.Length is 0 or > MaxDebugPasswords) return false;
            var parsed = new List<(byte[] Salt, byte[] Hash)>(records.Length);
            foreach (var record in records)
            {
                if (record.ValueKind != JsonValueKind.Object ||
                    !record.TryGetProperty("salt", out var saltValue) ||
                    !record.TryGetProperty("hash", out var hashValue) ||
                    saltValue.ValueKind != JsonValueKind.String ||
                    hashValue.ValueKind != JsonValueKind.String ||
                    saltValue.GetString() is not { } saltHex || hashValue.GetString() is not { } hashHex ||
                    !SaltPattern().IsMatch(saltHex) || !HashPattern().IsMatch(hashHex))
                    return false;
                parsed.Add((Convert.FromHexString(saltHex), Convert.FromHexString(hashHex)));
            }
            var matched = false;
            foreach (var (salt, hash) in parsed)
                matched |= CryptographicOperations.FixedTimeEquals(
                    Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32), hash);
            return matched;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    // Returns true when the configuration selects remote mode; endpoint is null when it is unusable.
    private static bool TryReadEndpoint(string? configuration, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrEmpty(configuration)) return false;
        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("url", out var url)) return false;
            endpoint = url.ValueKind == JsonValueKind.String ? ValidateEndpoint(url.GetString()) : null;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static Uri? ValidateEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;

    private static async Task<DebugAuthResult> VerifyRemoteAsync(string? password, Uri endpoint,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1_024) return DebugAuthResult.Denied;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { password }), Encoding.UTF8, "application/json")
            };
            using var response = await send(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return DebugAuthResult.Denied;
            if (response.StatusCode != HttpStatusCode.OK) return DebugAuthResult.Unavailable;
            var text = await response.Content.ReadAsStringAsync();
            if (text.Length > 1_024) return DebugAuthResult.Unavailable;
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("authorized", out var authorized) &&
                authorized.ValueKind == JsonValueKind.True
                    ? DebugAuthResult.Authorized : DebugAuthResult.Denied;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or
            JsonException or IOException or InvalidOperationException)
        {
            return DebugAuthResult.Unavailable;
        }
    }

    private static string? ReadBundledConfiguration(string? baseDirectory = null)
    {
        try
        {
            var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "debug-auth.json");
            var file = new FileInfo(path);
            return file.Exists && file.Length is > 0 and <= MaxConfigurationBytes
                ? File.ReadAllText(path) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? ResolveConfiguration(string? baseDirectory = null,
        string? environmentConfiguration = null) =>
        ReadBundledConfiguration(baseDirectory) ?? environmentConfiguration ??
        (Environment.GetEnvironmentVariable("TANUKI_DEBUG_AUTH_URL") is { Length: > 0 } url
            ? JsonSerializer.Serialize(new { url }) : null) ??
        Environment.GetEnvironmentVariable("TANUKI_DEBUG_AUTH");

    internal static void VerifyParity()
    {
        // Node.js pbkdf2Sync vector from the released implementation's parameters.
        const string config = "{\"salt\":\"000102030405060708090a0b0c0d0e0f\",\"hash\":\"fc870efa2ecf82f10ed215e1fa0b2eeb1a49150cd539ca1d9dfc0e82faea2047\"}";
        if (!Verify("test-pass", config) || Verify("wrong-pass", config) ||
            Verify("test-pass", "{\"salt\":\"00\",\"hash\":\"ff\"}") ||
            Verify(new string('x', 1_025), config) || Verify("test-pass", "{}"))
            throw new InvalidOperationException("Developer debug authentication differs from TanukiBCL 3.2.8");
        const string other = "{\"name\":\"tester\",\"salt\":\"ffffffffffffffffffffffffffffffff\",\"hash\":\"" +
            "0000000000000000000000000000000000000000000000000000000000000000\"}";
        var multiple = $"{{\"passwords\":[{other},{config}]}}";
        if (!Verify("test-pass", multiple) || Verify("wrong-pass", multiple) ||
            Verify("test-pass", "{\"passwords\":[]}") ||
            Verify("test-pass", $"{{\"passwords\":[{config},{{\"salt\":\"00\"}}]}}") ||
            Verify("test-pass", $"{{\"passwords\":[{string.Join(",", Enumerable.Repeat(config, 17))}]}}"))
            throw new InvalidOperationException("3.2.9 multi-password debug authentication differs");
        var directory = Path.Combine(Path.GetTempPath(), $"tanuki-debug-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "debug-auth.json");
            File.WriteAllText(path, config);
            if (!Verify("test-pass", ResolveConfiguration(directory, "{}")))
                throw new InvalidOperationException("Bundled debug authentication did not override the environment");
            File.WriteAllText(path, new string('x', MaxConfigurationBytes + 1));
            if (ReadBundledConfiguration(directory) is not null)
                throw new InvalidOperationException("Oversized debug authentication file was accepted");
            if (!Verify("test-pass", ResolveConfiguration(directory, config)))
                throw new InvalidOperationException("Development environment authentication did not fall back");
        }
        finally { Directory.Delete(directory, recursive: true); }
        VerifyRemoteAsync().GetAwaiter().GetResult();
        Console.WriteLine("[PASS] Developer debug PBKDF2 authentication matches released parameters");
        Console.WriteLine("[PASS] 3.2.9 multiple debug passwords and HTTPS invitation authentication");
    }

    private static async Task VerifyRemoteAsync()
    {
        static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        var remote = JsonSerializer.Serialize(new { url = DefaultEndpoint });
        string? sentBody = null;
        Uri? sentUri = null;
        var authorized = await VerifyAsync("secret", remote, async request =>
        {
            sentUri = request.RequestUri;
            sentBody = await request.Content!.ReadAsStringAsync();
            return await Reply(HttpStatusCode.OK, "{\"authorized\":true}");
        });
        if (authorized != DebugAuthResult.Authorized || sentUri?.AbsoluteUri != DefaultEndpoint ||
            sentBody != "{\"password\":\"secret\"}")
            throw new InvalidOperationException("Remote debug authentication request differs from 3.2.9");
        if (await VerifyAsync("secret", remote, _ => Reply(HttpStatusCode.Unauthorized, "")) != DebugAuthResult.Denied ||
            await VerifyAsync("secret", remote, _ => Reply(HttpStatusCode.OK, "{\"authorized\":false}")) != DebugAuthResult.Denied ||
            await VerifyAsync("secret", remote, _ => Reply(HttpStatusCode.TooManyRequests, "")) != DebugAuthResult.Unavailable ||
            await VerifyAsync("secret", remote, _ => Reply(HttpStatusCode.OK, new string(' ', 1_025))) != DebugAuthResult.Unavailable ||
            await VerifyAsync("secret", remote, _ => throw new HttpRequestException("offline")) != DebugAuthResult.Unavailable ||
            await VerifyAsync("", remote, _ => throw new InvalidOperationException("must not send")) != DebugAuthResult.Denied)
            throw new InvalidOperationException("Remote debug authentication result mapping differs from 3.2.9");
        foreach (var invalid in new[] { "http://example.com/verify", "https://user:pass@example.com/verify",
                     "https://example.com/verify?x=1", "https://example.com/verify#x" })
        {
            if (await VerifyAsync("secret", JsonSerializer.Serialize(new { url = invalid }),
                    _ => throw new InvalidOperationException("must not send")) != DebugAuthResult.Unavailable)
                throw new InvalidOperationException($"Unsafe debug authentication endpoint accepted: {invalid}");
        }
    }

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaltPattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
