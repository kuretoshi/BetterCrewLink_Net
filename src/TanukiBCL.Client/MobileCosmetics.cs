using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

/// <summary>TanukiBCL 3.2.14 Web protocol: a small processed PNG on first use, IDs thereafter.</summary>
internal sealed class MobileCosmetics
{
    private const int MaxImageBytes = 192 * 1024;
    // The previous 8 MiB character budget held about 16 MiB of UTF-16 payloads.
    // Preserve that capacity so crowded lobbies do not evict unsent costumes sooner.
    private const int MaxCacheBytes = 16 * 1024 * 1024;
    private readonly object gate = new();
    private readonly NosCosmeticContents contents;
    private readonly Dictionary<string, (string Id, string Png)> cache = [];
    private readonly HashSet<string> pending = [];
    private readonly Dictionary<string, DateTimeOffset> retryAt = [];
    private readonly HashSet<string> sent = [];
    private string session = string.Empty;
    private int generation;
    private int bytes;
    private DateTimeOffset lastSentAt;

    internal MobileCosmetics(NosCosmeticContents? contents = null) =>
        this.contents = contents ?? NosCosmeticContents.Shared;

    internal void Reset()
    {
        lock (gate) ResetCore();
    }

    private void ResetCore()
    {
        generation++;
        session = string.Empty;
        cache.Clear();
        pending.Clear();
        retryAt.Clear();
        sent.Clear();
        bytes = 0;
        lastSentAt = default;
    }

    internal void Resend(IReadOnlyList<string>? ids)
    {
        lock (gate)
        {
            if (ids is null) sent.Clear();
            else foreach (var id in ids) sent.Remove(id);
        }
    }

    internal MobileCosmeticFrame Frame(AmongUsState state)
    {
        var nextSession = $"{state.LobbyCode}|{state.Mod}|{state.GameExecutablePath}";
        lock (gate)
            if (session != nextSession)
            {
                ResetCore();
                session = nextSession;
            }
        var partsByPlayer = new Dictionary<int, IReadOnlyDictionary<string, string>>();
        var active = new HashSet<string>();
        if (state.Mod == AmongUsModType.NebulaOnTheShip &&
            state.GameState is not (GameState.Menu or GameState.Unknown) &&
            Path.GetDirectoryName(state.GameExecutablePath) is { Length: > 0 } gameDirectory)
        {
            contents.Update(gameDirectory);
            foreach (var player in state.Players)
            {
                if (player.Disconnected) continue;
                var fallback = state.PlayerColors.Count > player.AppearanceColorId && player.AppearanceColorId >= 0
                    ? AvatarImageFactory.GetSwatchColors(player.AppearanceColorId, state.PlayerColors).Main
                    : Colors.White;
                var nos = state.GameState == GameState.Lobby
                    ? NosCosmeticContents.LobbyPlayer(player, fallback) : player.NosPlayer;
                if (nos is null || contents.Cosmetics(nos) is not { } sourceParts) continue;
                var parts = new Dictionary<string, string>();
                foreach (var (part, key) in sourceParts)
                {
                    var source = $"{key}:{nos.ColorR:R},{nos.ColorG:R},{nos.ColorB:R}";
                    active.Add(source);
                    Load(source, key, nos.ColorR, nos.ColorG, nos.ColorB);
                    string? id;
                    lock (gate) id = cache.GetValueOrDefault(source).Id;
                    parts[part switch
                    {
                        NosCosmeticPart.HatBack => "hatBack",
                        NosCosmeticPart.BodyMask => "bodyMask",
                        _ => part.ToString().ToLowerInvariant()
                    }] = $"nos-web://{id ?? "pending"}";
                }
                if (parts.Count > 0) partsByPlayer[player.Id] = parts;
            }
        }
        Dictionary<string, string>? assets = null;
        lock (gate)
        {
            PruneRetries(active);
            if (DateTimeOffset.UtcNow - lastSentAt >= TimeSpan.FromMilliseconds(100))
                foreach (var source in active)
                {
                    if (!cache.TryGetValue(source, out var asset) || sent.Contains(asset.Id)) continue;
                    assets = new Dictionary<string, string> { [asset.Id] = asset.Png };
                    sent.Add(asset.Id);
                    lastSentAt = DateTimeOffset.UtcNow;
                    break;
                }
        }
        return new MobileCosmeticFrame(partsByPlayer, assets);
    }

    // Caller holds gate; keep only failures for costumes still on screen.
    private void PruneRetries(HashSet<string> active)
    {
        if (retryAt.Count == 0) return;
        foreach (var source in retryAt.Keys.Where(source => !active.Contains(source)).ToArray())
            retryAt.Remove(source);
    }

    private void Load(string source, string key, double red, double green, double blue)
    {
        int currentGeneration;
        lock (gate)
        {
            if (cache.ContainsKey(source) || pending.Contains(source) || pending.Count >= 3 ||
                DateTimeOffset.UtcNow < retryAt.GetValueOrDefault(source)) return;
            pending.Add(source);
            currentGeneration = generation;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var image = contents.Image(key, red, green, blue) ?? throw new InvalidDataException("NoS image missing");
                var asset = Encode(image);
                lock (gate)
                {
                    if (generation != currentGeneration) return;
                    CacheAsset(source, asset);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
                ArgumentException or NotSupportedException or OverflowException)
            {
                lock (gate)
                    if (generation == currentGeneration)
                        retryAt[source] = DateTimeOffset.UtcNow.AddSeconds(5);
            }
            finally
            {
                lock (gate)
                    if (generation == currentGeneration) pending.Remove(source);
            }
        });
    }

    // Caller holds gate; bytes tracks retained UTF-16 PNG text, not character count.
    private void CacheAsset(string source, (string Id, string Png) asset)
    {
        var assetBytes = checked(asset.Png.Length * sizeof(char));
        while (bytes + assetBytes > MaxCacheBytes && cache.Count > 0)
        {
            var oldest = cache.First();
            bytes -= checked(oldest.Value.Png.Length * sizeof(char));
            sent.Remove(oldest.Value.Id);
            cache.Remove(oldest.Key);
        }
        cache[source] = asset;
        bytes += assetBytes;
    }

    private static (string Id, string Png) Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var length = checked((int)stream.Length);
        var png = stream.GetBuffer();
        if (length is < 24 or > MaxImageBytes || png[0] != 137 || png[1] != 80)
            throw new InvalidDataException("Invalid processed NoS PNG");
        return (Convert.ToHexString(SHA256.HashData(png.AsSpan(0, length))).ToLowerInvariant(),
            "data:image/png;base64," + Convert.ToBase64String(png, 0, length));
    }

    internal static void VerifyEncoding()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 1, 2, 3, 255 }, 4);
        bitmap.Freeze();
        var (id, png) = Encode(bitmap);
        var bytes = Convert.FromBase64String(png["data:image/png;base64,".Length..]);
        if (id.Length != 64 || id != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() ||
            !png.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
            throw new InvalidOperationException("Mobile NoS PNG hash and data URL differ from 3.2.14");
        var cacheTest = new MobileCosmetics();
        for (var index = 0; index < 40; index++)
            cacheTest.CacheAsset($"source-{index}", ($"id-{index}", new string((char)('A' + index), 300_000)));
        if (cacheTest.bytes > MaxCacheBytes ||
            cacheTest.bytes != cacheTest.cache.Values.Sum(asset => asset.Png.Length * sizeof(char)) ||
            cacheTest.cache.ContainsKey("source-0") || !cacheTest.cache.ContainsKey("source-39"))
            throw new InvalidOperationException("Mobile NoS cache did not budget UTF-16 strings or evict oldest entries");
        cacheTest.retryAt["active"] = DateTimeOffset.UtcNow.AddSeconds(5);
        cacheTest.retryAt["stale"] = DateTimeOffset.UtcNow.AddSeconds(5);
        cacheTest.PruneRetries(new HashSet<string> { "active" });
        if (!cacheTest.retryAt.ContainsKey("active") || cacheTest.retryAt.ContainsKey("stale"))
            throw new InvalidOperationException("Inactive NoS image retry state was retained");
        Console.WriteLine("[PASS] 3.2.14 mobile NoS processed PNG hash and data URL");
    }

    internal static void VerifyTransfer(NosCosmeticContents contents, string gameDirectory, NosPlayerData costume)
    {
        var transfer = new MobileCosmetics(contents);
        var state = new AmongUsState
        {
            LobbyCode = "ABCDEF", Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Tasks,
            GameExecutablePath = Path.Combine(gameDirectory, "Among Us.exe"),
            Players = [new Player { Id = 4, NosPlayer = costume }]
        };
        var expected = contents.Cosmetics(costume)?.Count ?? 0;
        var received = new Dictionary<string, string>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (received.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            var frame = transfer.Frame(state);
            if (frame.PlayerParts.GetValueOrDefault(4)?.Count != expected)
                throw new InvalidOperationException("Web NoS costume layers were omitted");
            if (frame.Assets is not null)
                foreach (var (id, png) in frame.Assets)
                    received[id] = png;
            Thread.Sleep(25);
        }
        if (expected < 3 || received.Count != expected)
            throw new InvalidOperationException("Web NoS costume assets were not transferred");
        var first = received.First();
        transfer.Resend([first.Key]);
        Thread.Sleep(110);
        var resent = transfer.Frame(state).Assets;
        if (resent is null || resent.Count != 1 || resent.GetValueOrDefault(first.Key) != first.Value)
            throw new InvalidOperationException("Web NoS missing-image request was not fulfilled");
        transfer.Resend([]);
        Thread.Sleep(110);
        if (transfer.Frame(state).Assets is not null)
            throw new InvalidOperationException("Empty Web image request resent cached assets");
        transfer.Reset();
        Console.WriteLine("[PASS] 3.2.14 Web NoS layers, deduplicated transfer and targeted retry");
    }
}
