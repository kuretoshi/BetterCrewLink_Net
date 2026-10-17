using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

// Only assets named by the current OBS state can be returned to its browser source.
internal sealed class ObsSkinAssets
{
    private const int MaxImageBytes = 512 * 1024;
    private const int MaxCacheBytes = 8 * 1024 * 1024;
    private readonly NosCosmeticContents contents;
    private readonly object gate = new();
    private readonly Dictionary<string, Func<byte[]?>> active = [];
    private readonly Dictionary<string, string> cache = [];
    private readonly Dictionary<string, Task<string?>> pending = [];
    private readonly Dictionary<string, DateTimeOffset> retryAt = [];
    private string session = string.Empty;
    private int generation;
    private int cacheBytes;

    internal ObsSkinAssets(NosCosmeticContents? contents = null) =>
        this.contents = contents ?? NosCosmeticContents.Shared;

    internal string AssetSession { get { lock (gate) return session; } }

    internal IReadOnlyList<ObsPlayerAppearance> Update(AmongUsState state)
    {
        var nextSession = $"{state.LobbyCode}:{state.Mod}";
        var nextActive = new Dictionary<string, Func<byte[]?>>();
        if (state.Mod == AmongUsModType.NebulaOnTheShip &&
            Path.GetDirectoryName(state.GameExecutablePath) is { Length: > 0 } folder)
            contents.Update(folder);
        var appearances = state.Players.Select(player => Appearance(state, player, nextActive)).ToArray();
        lock (gate)
        {
            if (session != nextSession) ResetLocked();
            session = nextSession;
            active.Clear();
            foreach (var (source, loader) in nextActive) active[source] = loader;
        }
        return appearances;
    }

    internal Task<string?> GetAsync(string source)
    {
        lock (gate)
        {
            if (!active.TryGetValue(source, out var loader)) return Task.FromResult<string?>(null);
            if (cache.TryGetValue(source, out var image)) return Task.FromResult<string?>(image);
            if (pending.TryGetValue(source, out var existing)) return existing;
            if (pending.Count >= 4 || DateTimeOffset.UtcNow < retryAt.GetValueOrDefault(source))
                return Task.FromResult<string?>(null);
            var token = generation;
            var request = Task.Run(() => Load(source, loader, token));
            pending[source] = request;
            _ = request.ContinueWith(_ => Complete(source, token), TaskScheduler.Default);
            return request;
        }
    }

    internal void Reset()
    {
        lock (gate) ResetLocked();
    }

    private void ResetLocked()
    {
        generation++;
        active.Clear();
        cache.Clear();
        pending.Clear();
        retryAt.Clear();
        cacheBytes = 0;
        session = string.Empty;
    }

    private string? Load(string source, Func<byte[]?> loader, int token)
    {
        try
        {
            var bytes = loader();
            if (bytes is null || !ValidPng(bytes)) return null;
            var image = "data:image/png;base64," + Convert.ToBase64String(bytes);
            lock (gate)
            {
                if (token != generation || !active.ContainsKey(source)) return null;
                while (cacheBytes + image.Length > MaxCacheBytes && cache.Count > 0)
                {
                    var oldest = cache.First();
                    cacheBytes -= oldest.Value.Length;
                    cache.Remove(oldest.Key);
                }
                cache[source] = image;
                cacheBytes += image.Length;
            }
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or NotSupportedException or FileFormatException or
            System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void Complete(string source, int token)
    {
        lock (gate)
        {
            if (token == generation)
            {
                pending.Remove(source);
                if (!cache.ContainsKey(source)) retryAt[source] = DateTimeOffset.UtcNow.AddMilliseconds(1500);
            }
        }
    }

    private static bool ValidPng(byte[] bytes) => bytes.Length is >= 24 and <= MaxImageBytes &&
        bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private ObsPlayerAppearance Appearance(AmongUsState state, Player player,
        Dictionary<string, Func<byte[]?>> assets)
    {
        var changed = player.CurrentOutfit is > 0 and <= 10;
        static string Select(bool changed, string appearance, string normal) =>
            changed || appearance.StartsWith("Modded_", StringComparison.Ordinal) ? appearance : normal;
        var hat = Select(changed, player.AppearanceHatId, player.HatId);
        var skin = Select(changed, player.AppearanceSkinId, player.SkinId);
        var visor = Select(changed, player.AppearanceVisorId, player.VisorId);
        if (state.Mod == AmongUsModType.NebulaOnTheShip)
            return NosAppearance(state, player, assets, hat, skin, visor);
        string? skinSource = null;
        if (state.Mod == AmongUsModType.SuperNewRoles && skin.StartsWith("Modded_", StringComparison.Ordinal))
        {
            skinSource = "snr-cosmetic://skin/" + Uri.EscapeDataString(skin);
            if (skinSource.Length <= 512)
                assets[skinSource] = () => ReadSnrSkin(state.GameExecutablePath, skin);
            else skinSource = null;
        }
        return new ObsPlayerAppearance(hat, skin, visor, skinSource);
    }

    private ObsPlayerAppearance NosAppearance(AmongUsState state, Player player,
        Dictionary<string, Func<byte[]?>> assets, string hat, string skin, string visor)
    {
        var swatch = AvatarImageFactory.GetSwatchColors(player.AppearanceColorId >= 0
            ? player.AppearanceColorId : player.ColorId, state.PlayerColors).Main;
        var nos = player.Disconnected ? null : state.GameState == GameState.Lobby
            ? NosCosmeticContents.LobbyPlayer(player, swatch) : player.NosPlayer;
        if (nos is null) return new ObsPlayerAppearance(hat, skin, visor);
        hat = nos.Hat?.Name ?? hat;
        skin = nos.Skin?.Name ?? skin;
        visor = nos.Visor?.Name ?? visor;
        var parts = contents.Cosmetics(nos);
        var skinSource = Source(NosCosmeticPart.Skin);
        var bodyMaskSource = Source(NosCosmeticPart.BodyMask);
        return new ObsPlayerAppearance(hat, skin, visor, skinSource, bodyMaskSource);

        string? Source(NosCosmeticPart part)
        {
            if (parts is null || !parts.TryGetValue(part, out var key)) return null;
            var color = string.Join(",", new[] { nos.ColorR, nos.ColorG, nos.ColorB }
                .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
            var source = $"nos-cosmetic://image/{key}/{color}";
            if (source.Length > 512) return null;
            assets[source] = () => EncodeNosSkin(key, nos.ColorR, nos.ColorG, nos.ColorB);
            return source;
        }
    }

    private static byte[]? ReadSnrSkin(string executable, string skin)
    {
        var path = SnrLocalCosmetics.Resolve(executable, skin, CosmeticPart.Skin)?.Url.LocalPath;
        if (path is null) return null;
        var file = new FileInfo(path);
        return file.Exists && file.Length is >= 24 and <= MaxImageBytes ? File.ReadAllBytes(path) : null;
    }

    private byte[]? EncodeNosSkin(string key, double red, double green, double blue)
    {
        var bitmap = contents.Image(key, red, green, blue);
        if (bitmap is null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.Length <= MaxImageBytes ? output.ToArray() : null;
    }

    internal static void VerifySnrTransfer()
    {
        var fixture = Directory.CreateTempSubdirectory("tanuki-obs-snr-");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(fixture.FullName,
                "SuperNewRolesNext", "CustomCosmetics", "Pack"));
            var file = Path.Combine(folder.FullName, "Name_front.png");
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32,
                null, new byte[] { 0, 0, 255, 255 }, 4);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(file)) encoder.Save(output);
            var assets = new ObsSkinAssets();
            var appearance = assets.Update(new AmongUsState
            {
                LobbyCode = "ABCDEF", Mod = AmongUsModType.SuperNewRoles, GameState = GameState.Tasks,
                GameExecutablePath = Path.Combine(fixture.FullName, "Among Us.exe"),
                Players = [new Player { SkinId = "Modded_Pack_Name" }]
            })[0];
            var image = assets.GetAsync(appearance.SkinSource!).GetAwaiter().GetResult();
            if (appearance.SkinSource != "snr-cosmetic://skin/Modded_Pack_Name" ||
                image != "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(file)) ||
                assets.GetAsync("snr-cosmetic://skin/unknown").GetAwaiter().GetResult() is not null)
                throw new InvalidOperationException("OBS SNR skin source was missing or an inactive asset was exposed.");
            assets.Reset();
            if (assets.GetAsync(appearance.SkinSource).GetAwaiter().GetResult() is not null ||
                ValidPng(new byte[MaxImageBytes + 1]))
                throw new InvalidOperationException("OBS skin transfer retained a former lobby or accepted oversized data.");
            Console.WriteLine("[PASS] OBS SNR skin transfer, active-source admission and reset");
        }
        finally { fixture.Delete(recursive: true); }
    }
}
