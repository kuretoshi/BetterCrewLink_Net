using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TanukiBCL.Client;

internal sealed record StagedUpdate(string Root, string PayloadDirectory);

internal static class UpdatePackage
{
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxUncompressedBytes = 3L * 1024 * 1024 * 1024;
    private const int MaxEntries = 2_000;
    internal static string StagingBase => Path.Combine(Path.GetTempPath(), "TanukiBCL.Net", "updates");

    internal static async Task<StagedUpdate> StageAsync(HttpClient client, UpdateCandidate candidate,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        string? stagingBase = null)
    {
        if (candidate.Size is <= 0 or > MaxArchiveBytes || candidate.Sha256.Length != 64 ||
            !candidate.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新ファイルのサイズまたはSHA-256が不正です。");
        var root = Path.GetFullPath(stagingBase ?? StagingBase);
        Directory.CreateDirectory(root);
        var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var archivePath = Path.Combine(stage, "package.zip");
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("TanukiBCL.Net-Updater/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != candidate.Size)
                throw new InvalidDataException("更新ファイルの公開サイズと応答サイズが一致しません。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    received = checked(received + count);
                    if (received > candidate.Size) throw new InvalidDataException("更新ファイルが公開サイズを超えました。");
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report((double)received / candidate.Size * 100d);
                }
                if (received != candidate.Size ||
                    !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),
                        Convert.FromHexString(candidate.Sha256)))
                    throw new InvalidDataException("更新ファイルのサイズまたはSHA-256が一致しません。");
            }

            var payload = Path.Combine(stage, "payload");
            Directory.CreateDirectory(payload);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                ValidateEntries(archive, payload);
                archive.ExtractToDirectory(payload);
            }
            foreach (var required in new[] { "TanukiBCL.Net.exe", "TanukiBCL.Net.deps.json",
                "update-manifest.json", Path.Combine("Updater", "TanukiBCL.Updater.exe"),
                Path.Combine("NoSReader", "TbclSnapshotReader.exe"),
                Path.Combine("RoleReaders", "SnrRoleReader.exe") })
            {
                if (!File.Exists(Path.Combine(payload, required)))
                    throw new InvalidDataException($"更新ファイルに必須の構成要素がありません: {required}");
            }
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "update-manifest.json"))))
                if (!manifest.RootElement.TryGetProperty("appId", out var appId) ||
                    appId.ValueKind != JsonValueKind.String || appId.GetString() != "TanukiBCL.Net" ||
                    !manifest.RootElement.TryGetProperty("formatVersion", out var format) ||
                    !format.TryGetInt32(out var formatVersion) || formatVersion != 1)
                    throw new InvalidDataException("更新ファイルの識別情報が不正です。");
            progress?.Report(100);
            return new StagedUpdate(stage, payload);
        }
        catch
        {
            if (Directory.Exists(stage) && Path.GetDirectoryName(stage) == root)
                Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static void ValidateEntries(ZipArchive archive, string payload)
    {
        if (archive.Entries.Count is 0 or > MaxEntries)
            throw new InvalidDataException("更新ZIPのファイル数が不正です。");
        var payloadPrefix = Path.GetFullPath(payload).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long unpacked = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (name.Length == 0 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
                parts.Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                    IsReservedWindowsName(part)) ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("更新ZIPに不正なパスが含まれています。");
            var destination = Path.GetFullPath(Path.Combine(payload, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase) ||
                !paths.Add(destination))
                throw new InvalidDataException("更新ZIPに重複または領域外のパスがあります。");
            unpacked = checked(unpacked + entry.Length);
            if (unpacked > MaxUncompressedBytes)
                throw new InvalidDataException("更新ZIPの展開サイズが上限を超えました。");
        }
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9';
    }

    internal static async Task VerifyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tanukibcl-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            static byte[] Zip(bool escape = false)
            {
                using var stream = new MemoryStream();
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var name in new[] { "TanukiBCL.Net.exe", "TanukiBCL.Net.deps.json",
                        "update-manifest.json", "Updater/TanukiBCL.Updater.exe",
                        "NoSReader/TbclSnapshotReader.exe", "RoleReaders/SnrRoleReader.exe" })
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                        writer.Write(name == "update-manifest.json"
                            ? "{\"appId\":\"TanukiBCL.Net\",\"formatVersion\":1}" : name);
                    }
                    if (escape)
                    {
                        using var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open());
                        writer.Write("escape");
                    }
                }
                return stream.ToArray();
            }
            var bytes = Zip();
            var candidate = new UpdateCandidate("v3.2.7-net.2",
                new Uri("https://example.invalid/package.zip"),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length,
                new Uri("https://example.invalid/release"));
            using var handler = new PackageHandler(bytes);
            using var client = new HttpClient(handler);
            var staged = await StageAsync(client, candidate, stagingBase: root);
            if (!File.Exists(Path.Combine(staged.PayloadDirectory, "TanukiBCL.Net.exe")) || handler.Requests != 1)
                throw new InvalidOperationException("Verified update ZIP was not staged");
            Directory.Delete(staged.Root, recursive: true);
            try
            {
                await StageAsync(client, candidate with { Sha256 = new string('0', 64) }, stagingBase: root);
                throw new InvalidOperationException("Bad update digest was accepted");
            }
            catch (InvalidDataException error) when (error.Message.Contains("SHA-256")) { }
            var badZip = Zip(escape: true);
            handler.Body = badZip;
            try
            {
                await StageAsync(client, candidate with
                {
                    Sha256 = Convert.ToHexString(SHA256.HashData(badZip)).ToLowerInvariant(),
                    Size = badZip.Length
                }, stagingBase: root);
                throw new InvalidOperationException("ZIP path traversal was accepted");
            }
            catch (InvalidDataException error) when (error.Message.Contains("パス")) { }
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("Failed update left staging files behind");
            Console.WriteLine("[PASS] Update download verifies size/SHA-256, validates ZIP paths and removes failed stages");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static async Task VerifyPublishedArchiveAsync(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Published update ZIP not found", path);
        string sha256;
        await using (var input = File.OpenRead(file.FullName))
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
        var candidate = new UpdateCandidate("local-test", new Uri("https://example.invalid/package.zip"),
            sha256, file.Length, new Uri("https://example.invalid/release"));
        var testRoot = Path.Combine(Path.GetTempPath(), $"tanukibcl-real-package-{Guid.NewGuid():N}");
        using var handler = new FilePackageHandler(file.FullName);
        using var client = new HttpClient(handler);
        try
        {
            var staged = await StageAsync(client, candidate, stagingBase: testRoot);
            if (!File.Exists(Path.Combine(staged.PayloadDirectory, "Updater", "TanukiBCL.Updater.exe")))
                throw new InvalidDataException("Published package lost updater executable");
            Directory.Delete(staged.Root, recursive: true);
            Console.WriteLine($"[PASS] Published ZIP size/SHA-256/entries staged safely: {file.Length} bytes");
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class PackageHandler(byte[] body) : HttpMessageHandler
    {
        internal byte[] Body = body;
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body)
            });
        }
    }

    private sealed class FilePackageHandler(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(File.OpenRead(path))
            });
    }
}
