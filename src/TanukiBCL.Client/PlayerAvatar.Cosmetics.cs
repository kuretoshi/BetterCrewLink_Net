using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class PlayerAvatar
{
    private string? cosmeticKey;
    private long cosmeticGeneration;
    private DateTimeOffset cosmeticRetryAt;
    private Func<Task<CosmeticCatalog>> catalogLoader = CosmeticImages.GetCatalogAsync;
    private Func<Task<SnrCosmeticCatalog>> snrCatalogLoader = CosmeticImages.GetSnrCatalogAsync;
    private Func<Uri, Task<System.Windows.Media.Imaging.BitmapSource>> imageLoader = CosmeticImages.GetImageAsync;
    private NosCosmeticContents nosContents = NosCosmeticContents.Shared;
    private ImageBrush? nosBodyMask;

    private void UpdateCosmetics(Player player, IReadOnlyList<PlayerColorPair>? palette, AmongUsModType mod, int colorId,
        string gameExecutable, GameState gameState)
    {
        var outfit = player.CurrentOutfit is > 0 and <= 10;
        static string Select(bool outfit, string appearance, string normal, string empty) =>
            (outfit || appearance.StartsWith("Modded_", StringComparison.Ordinal) ? appearance : normal) is var id && id != empty ? id : "";
        var hat = Select(outfit, player.AppearanceHatId, player.HatId, "hat_NoHat");
        var skin = Select(outfit, player.AppearanceSkinId, player.SkinId, "skin_None");
        var visor = Select(outfit, player.AppearanceVisorId, player.VisorId, "visor_EmptyVisor");
        // 3.2.9: NoS TBCLFields costume names replace the vanilla IDs, and LoadedContents.json
        // images take priority over the shared catalog for the same part.
        var colors = AvatarImageFactory.GetSwatchColors(colorId, palette);
        var lobbyFallback = palette is not null && player.AppearanceColorId >= 0 &&
            player.AppearanceColorId < palette.Count
            ? AvatarImageFactory.GetSwatchColors(player.AppearanceColorId, palette).Main : Colors.White;
        var nosPlayer = mod != AmongUsModType.NebulaOnTheShip || player.Disconnected ? null
            : gameState == GameState.Lobby ? NosCosmeticContents.LobbyPlayer(player, lobbyFallback) : player.NosPlayer;
        if (nosPlayer?.Hat is { } nosHat) hat = nosHat.Name == "hat_NoHat" ? "" : nosHat.Name;
        if (nosPlayer?.Skin is { } nosSkin) skin = nosSkin.Name == "skin_None" ? "" : nosSkin.Name;
        if (nosPlayer?.Visor is { } nosVisor) visor = nosVisor.Name == "visor_EmptyVisor" ? "" : nosVisor.Name;
        IReadOnlyDictionary<NosCosmeticPart, string>? nos = null;
        if (nosPlayer is not null && System.IO.Path.GetDirectoryName(gameExecutable) is { Length: > 0 } gameDirectory)
        {
            nosContents.Update(gameDirectory);
            nos = nosContents.Cosmetics(nosPlayer);
        }
        var nosColor = nosPlayer is null ? "" : $"{nosPlayer.ColorR},{nosPlayer.ColorG},{nosPlayer.ColorB}";
        var nosKeys = nos is null ? "" : string.Join(",", nos.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        var secondary = mod == AmongUsModType.SuperNewRoles && !player.Disconnected ? player.SnrRole : null;
        var hat2 = secondary?.Hat2Id is { } secondHat && secondHat != "hat_NoHat" ? secondHat : "";
        var visor2 = secondary?.Visor2Id is { } secondVisor && secondVisor != "visor_EmptyVisor" ? secondVisor : "";
        var key = $"{gameExecutable}|{player.IsDead}|{mod}|{gameState}|{hat}|{skin}|{visor}|{hat2}|{visor2}|{colors}|{nosKeys}|{nosColor}";
        if (key == cosmeticKey && (cosmeticRetryAt == default || DateTimeOffset.UtcNow < cosmeticRetryAt)) return;
        cosmeticKey = key;
        cosmeticRetryAt = default;
        var generation = ++cosmeticGeneration;
        CosmeticBack.Children.Clear(); CosmeticSkin.Children.Clear(); CosmeticFront.Children.Clear();
        BodyCanvas.OpacityMask = nosBodyMask = null;
        if (player.IsDead || string.IsNullOrEmpty(hat + skin + visor + hat2 + visor2) && nos is null) return;
        _ = LoadCosmeticsAsync(generation, hat, skin, visor, hat2, visor2, mod, colors, gameExecutable, nos, nosPlayer);
    }

    private async Task<System.Windows.Media.Imaging.BitmapSource?> LoadNosImageAsync(string key, NosPlayerData player)
    {
        var contents = nosContents;
        return await Task.Run(() => contents.Image(key, player.ColorR, player.ColorG, player.ColorB));
    }

    private async Task LoadCosmeticsAsync(long generation, string hat, string skin, string visor, string hat2, string visor2,
        AmongUsModType mod, (Color Main, Color Shadow) colors, string gameExecutable,
        IReadOnlyDictionary<NosCosmeticPart, string>? nos = null, NosPlayerData? nosPlayer = null)
    {
        try
        {
            var customSnrPresent = mod == AmongUsModType.SuperNewRoles &&
                new[] { hat, skin, visor, hat2, visor2 }.Any(id => id.StartsWith("Modded_", StringComparison.Ordinal));
            var catalogTask = catalogLoader();
            // 3.2.13 does not wait for the remote hat list before showing SNR's local PNGs.
            CosmeticCatalog? catalog = customSnrPresent && !catalogTask.IsCompletedSuccessfully
                ? null : await catalogTask;
            var (snr, retrySnr, retryDelaySeconds) = ResolveSnrCatalog(
                customSnrPresent, catalog is null, catalogTask.IsFaulted);
            if (catalogTask.IsFaulted) _ = catalogTask.Exception;
            var modName = mod switch
            {
                AmongUsModType.NebulaOnTheShip => "NoS",
                AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
                AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
                AmongUsModType.TownOfUs => "TOWN_OF_US",
                AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
                AmongUsModType.LasMonjas => "LAS_MONJAS",
                _ => "NONE"
            };
            foreach (var (id, part, target, nosPart) in new (string, CosmeticPart, Canvas, NosCosmeticPart?)[] {
                // Upstream z-order: backs 4/5, body 6, skin 7, secondary
                // hat 10, secondary visor 20, primary hat 30, primary visor 40.
                (hat, CosmeticPart.HatBack, CosmeticBack, NosCosmeticPart.HatBack), (hat2, CosmeticPart.HatBack, CosmeticBack, null),
                (skin, CosmeticPart.Skin, CosmeticSkin, NosCosmeticPart.Skin),
                (hat2, CosmeticPart.Hat, CosmeticFront, null), (visor2, CosmeticPart.Visor, CosmeticFront, null),
                (hat, CosmeticPart.Hat, CosmeticFront, NosCosmeticPart.Hat), (visor, CosmeticPart.Visor, CosmeticFront, NosCosmeticPart.Visor) })
                if (!await LoadCosmeticLayerAsync(generation, id, part, target, nosPart, mod, modName,
                        colors, gameExecutable, catalog, snr, nos, nosPlayer)) return;
            if (nos?.TryGetValue(NosCosmeticPart.BodyMask, out var maskKey) == true &&
                await LoadNosImageAsync(maskKey, nosPlayer!) is { } mask && generation == cosmeticGeneration)
            {
                // A missing mask must leave the normal crewmate visible.
                nosBodyMask = new ImageBrush(mask) { ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                LayoutCosmetics();
            }
            if (retrySnr && generation == cosmeticGeneration)
            {
                cosmeticRetryAt = DateTimeOffset.UtcNow.AddSeconds(retryDelaySeconds);
            }
        }
        catch (Exception error)
        {
            if (generation == cosmeticGeneration)
            {
                cosmeticRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            System.Diagnostics.Trace.TraceWarning($"Cosmetic catalog unavailable: {error.Message}");
        }
    }

    private (SnrCosmeticCatalog? Catalog, bool Retry, int DelaySeconds) ResolveSnrCatalog(
        bool customSnrPresent, bool catalogMissing, bool catalogFaulted)
    {
        var retry = customSnrPresent && catalogMissing;
        var delay = catalogFaulted ? 30 : 1;
        if (!customSnrPresent) return (null, retry, delay);
        try
        {
            var definitions = snrCatalogLoader();
            if (definitions.IsCompletedSuccessfully) return (definitions.Result, retry, delay);
            retry = true;
            if (!definitions.IsFaulted) return (null, retry, delay);
            _ = definitions.Exception;
            return (null, retry, 30);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"SNR definitions unavailable: {error.Message}");
            return (null, true, 30);
        }
    }

    private async Task<bool> LoadCosmeticLayerAsync(long generation, string id, CosmeticPart part, Canvas target,
        NosCosmeticPart? nosPart, AmongUsModType mod, string modName, (Color Main, Color Shadow) colors,
        string gameExecutable, CosmeticCatalog? catalog, SnrCosmeticCatalog? snr,
        IReadOnlyDictionary<NosCosmeticPart, string>? nos, NosPlayerData? nosPlayer)
    {
        if (generation != cosmeticGeneration) return false;
        if (nosPart is { } nosLayer && nos?.TryGetValue(nosLayer, out var nosKey) == true)
        {
            // Rendered on the upstream 300x375 canvas; a failed image leaves the layer empty.
            var nosImage = await LoadNosImageAsync(nosKey, nosPlayer!);
            if (generation != cosmeticGeneration) return false;
            if (nosImage is null) return true;
            target.Children.Add(new Image { Source = nosImage, Stretch = Stretch.Uniform, IsHitTestVisible = false,
                Tag = new CosmeticAsset(new Uri($"nos-cosmetic://image/{nosKey}"), false, "-52%", "-18px", "140%") });
            LayoutCosmetics();
            return true;
        }
        if (id.Length == 0) return true;
        var customSnr = mod == AmongUsModType.SuperNewRoles && id.StartsWith("Modded_", StringComparison.Ordinal);
        var asset = customSnr ? SnrLocalCosmetics.Resolve(gameExecutable, id, part)
            : catalog?.Resolve(id, modName, part);
        if (customSnr && asset is not null && snr is not null)
            asset = snr.ApplyLocalMetadata(id, part, asset);
        if (customSnr && asset is not null && catalog?.Dimensions(id, modName) is { } shared)
            asset = asset with { Top = shared.Top, Left = shared.Left, Width = shared.Width, SnrVisorLayout = false };
        if (asset is null) return true;
        try
        {
            var bitmap = await imageLoader(asset.Url);
            if (generation != cosmeticGeneration) return false;
            asset = SnrCosmeticCatalog.WithImageSize(asset, bitmap.PixelWidth, bitmap.PixelHeight);
            CosmeticCatalog.ResolveLength(asset.Top, 80);
            CosmeticCatalog.ResolveLength(asset.Left, 80);
            CosmeticCatalog.ResolveLength(asset.Width, 80);
            if (asset.Adaptive) bitmap = AvatarImageFactory.Recolor(bitmap, colors.Main, colors.Shadow);
            target.Children.Add(new Image { Source = bitmap, Tag = asset, Stretch = Stretch.Uniform,
                IsHitTestVisible = false });
            LayoutCosmetics();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Cosmetic image unavailable: {error.Message}");
        }
        return true;
    }

    private void LayoutCosmetics()
    {
        var size = ActualWidth > 0 ? ActualWidth : Width;
        if (!double.IsFinite(size) || size <= 0) return;
        BodyCanvas.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        // 3.2.12: hats and visors are cut by the same circle as the speech border in every view.
        CosmeticBack.Clip = CosmeticFront.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        AvatarBody.Width = size * 1.05;
        if (nosBodyMask is not null)
        {
            // Aligned with the NoS hat image: left -18px, top -52%, width 140% of the avatar.
            var maskWidth = size * 1.4;
            nosBodyMask.Viewport = new Rect(-18 + Math.Max(2, size / 40) / 2 - 7, size * 0.22 - size * 0.52,
                maskWidth, maskWidth * NosCosmeticContents.CanvasHeight / NosCosmeticContents.CanvasWidth);
            BodyCanvas.OpacityMask = nosBodyMask;
        }
        Canvas.SetTop(AvatarBody, size * 0.22);
        Canvas.SetLeft(AvatarBody, -7);
        foreach (var canvas in new[] { CosmeticBack, CosmeticSkin, CosmeticFront })
            foreach (Image image in canvas.Children)
            {
                var asset = (CosmeticAsset)image.Tag;
                image.Width = Math.Max(0, CosmeticCatalog.ResolveLength(asset.Width, size));
                Canvas.SetTop(image, size * 0.22 + CosmeticCatalog.ResolveLength(asset.Top, size));
                Canvas.SetLeft(image, CosmeticCatalog.ResolveLength(asset.Left, size) + Math.Max(2, size / 40) / 2 - 7);
            }
    }

    internal static void VerifyLocalCosmetics(string executable)
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32,
            null, new byte[] { 0, 0, 255, 255 }, 4);
        bitmap.Freeze();
        var pendingCatalog = new TaskCompletionSource<CosmeticCatalog>();
        var pendingSnr = new TaskCompletionSource<SnrCosmeticCatalog>();
        var requested = new List<Uri>();
        var avatar = new PlayerAvatar {
            catalogLoader = () => pendingCatalog.Task,
            snrCatalogLoader = () => pendingSnr.Task,
            imageLoader = uri => { requested.Add(uri); return Task.FromResult(bitmap); } };
        avatar.SetPlayer(new Player { HatId = "Modded_Pack_Name", VisorId = "Modded_Pack_Name" }, null,
            mod: AmongUsModType.SuperNewRoles, gameExecutable: executable);
        if (avatar.CosmeticBack.Children.Count != 1 || avatar.CosmeticFront.Children.Count != 2 ||
            requested.Any(uri => !uri.IsFile))
            throw new InvalidOperationException("SNR local images waited for remote definitions or used CDN images.");
        pendingCatalog.SetResult(CosmeticCatalog.Parse("{}"));
        pendingSnr.SetResult(SnrCosmeticCatalog.Parse("{}", """
            {"Visors":[{"name":"Name","package":"Pack","resource":"Name_idle.png","IsSNR":true}]}
            """));
        avatar.cosmeticRetryAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        avatar.SetPlayer(new Player { HatId = "Modded_Pack_Name", VisorId = "Modded_Pack_Name" }, null,
            mod: AmongUsModType.SuperNewRoles, gameExecutable: executable);
        var visor = (Image)avatar.CosmeticFront.Children[1];
        if (((CosmeticAsset)visor.Tag).Url.IsFile != true ||
            Math.Abs(visor.Width - CosmeticCatalog.ResolveLength(
                SnrCosmeticCatalog.WithImageSize(new CosmeticAsset(new Uri("https://example.invalid/test.png"),
                    false, "-52%", "-18px", "140%", true), 1, 1).Width, avatar.Width)) > 0.001)
            throw new InvalidOperationException($"SNR local visor lost definition-based image layout: " +
                $"file={((CosmeticAsset)visor.Tag).Url.IsFile} width={visor.Width} " +
                $"asset={((CosmeticAsset)visor.Tag).Width} avatar={avatar.Width}.");
        avatar.SetPlayer(new Player { HatId = "Modded_Pack_Name" }, null, mod: AmongUsModType.SuperNewRoles,
            gameExecutable: System.IO.Path.Combine(System.IO.Path.GetDirectoryName(executable)!, "other", "Among Us.exe"));
        if (avatar.CosmeticBack.Children.Count != 0 || avatar.CosmeticFront.Children.Count != 0)
            throw new InvalidOperationException("SNR images were loaded outside the selected game folder.");
        Console.WriteLine("[PASS] SNR local-only PNGs render before remote catalogs and preserve visor metadata");
    }

    internal static void VerifyCosmeticLayers()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var catalog = CosmeticCatalog.Parse("""
            {"NONE":{"defaultTop":"-10%","defaultLeft":"3px","defaultWidth":"120%","hats":{
                "hat":{"image":"hat.png","back_image":"back.png"},
                "skin":{"image":"skin.png"},"visor":{"image":"visor.png"}}}}
            """);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32,
            null, new byte[] { 0, 0, 255, 255 }, 4);
        bitmap.Freeze();
        var avatar = new PlayerAvatar { Width = 80, Height = 80,
            catalogLoader = () => Task.FromResult(catalog), imageLoader = _ => Task.FromResult(bitmap) };
        avatar.Measure(new Size(80, 80)); avatar.Arrange(new Rect(0, 0, 80, 80));
        var player = new Player { HatId = "hat", SkinId = "skin", VisorId = "visor" };
        avatar.SetPlayer(player, null);
        Require(avatar.CosmeticBack.Children.Count == 1 && avatar.CosmeticSkin.Children.Count == 1 &&
            avatar.CosmeticFront.Children.Count == 2, "Missing cosmetic layers");
        var front = (Image)avatar.CosmeticFront.Children[0];
        Require(((CosmeticAsset)front.Tag).Url.AbsolutePath.EndsWith("hat.png") &&
            ((CosmeticAsset)((Image)avatar.CosmeticFront.Children[1]).Tag).Url.AbsolutePath.EndsWith("visor.png"),
            "Hat/visor layer order changed");
        Require(front.Width == 96 && Math.Abs(Canvas.GetTop(front) - 9.6) < 0.001 && Canvas.GetLeft(front) == -3,
            "Cosmetic placement differs from upstream");
        Require(avatar.AvatarBody.Width == 84 && Canvas.GetTop(avatar.AvatarBody) == 17.6 &&
            avatar.BodyCanvas.Clip is EllipseGeometry, "Base mask layout/clip differs");
        player.IsDead = true;
        avatar.SetPlayer(player, null);
        Require(avatar.CosmeticFront.Children.Count == 0 && avatar.CosmeticFront.Visibility == Visibility.Hidden,
            "Dead avatar has cosmetics");
        var pending = new TaskCompletionSource<System.Windows.Media.Imaging.BitmapSource>();
        avatar.imageLoader = _ => pending.Task;
        player.IsDead = false;
        avatar.SetPlayer(player, null);
        // Before the previous image arrives, the same slot is assigned an empty outfit.
        avatar.SetPlayer(new Player(), null);
        pending.SetResult(bitmap);
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Require(avatar.CosmeticBack.Children.Count == 0 && avatar.CosmeticFront.Children.Count == 0,
            "Late cosmetic image replaced a newer outfit");
        // Exercise the real SetPlayer -> palette selection -> adaptive recolor path.
        avatar.catalogLoader = () => Task.FromResult(CosmeticCatalog.Parse("""
            {"NONE":{"defaultWidth":"100%","hats":{"adaptive":{"image":"test.png","multi_color":true}}}}
            """));
        avatar.imageLoader = _ => Task.FromResult(bitmap);
        var adaptivePlayer = new Player { HatId = "adaptive", ColorId = 0,
            NosPlayer = new() { ColorR = 20d / 255, ColorG = 100d / 255, ColorB = 200d / 255 } };
        PlayerColorPair[] palette = [new() { Main = 0xff0000ff }, new() { Main = 0xffc86414 }];
        avatar.SetPlayer(adaptivePlayer, palette, mod: AmongUsModType.NebulaOnTheShip);
        byte[] painted = new byte[4];
        ((System.Windows.Media.Imaging.BitmapSource)((Image)avatar.CosmeticFront.Children[0]).Source)
            .CopyPixels(painted, 4, 0);
        Require(painted.SequenceEqual(new byte[] { 200, 100, 20, 255 }), "Adaptive cosmetic ignored NoS palette match");
        adaptivePlayer.NosPlayer.ColorR = 1; adaptivePlayer.NosPlayer.ColorG = 0; adaptivePlayer.NosPlayer.ColorB = 0;
        avatar.SetPlayer(adaptivePlayer, palette, mod: AmongUsModType.NebulaOnTheShip);
        ((System.Windows.Media.Imaging.BitmapSource)((Image)avatar.CosmeticFront.Children[0]).Source)
            .CopyPixels(painted, 4, 0);
        Require(painted.SequenceEqual(new byte[] { 0, 0, 255, 255 }), "Published color change did not refresh cosmetic");
        avatar.catalogLoader = () => Task.FromResult(CosmeticCatalog.Parse("{}"));
        avatar.SetPlayer(new Player { HatId = "Modded_NONE_PACKAGE_H", VisorId = "Modded_NONE_PACKAGE_V" },
            null, mod: AmongUsModType.SuperNewRoles);
        Require(avatar.CosmeticBack.Children.Count == 0 && avatar.CosmeticFront.Children.Count == 0,
            "SNR images must not fall back to remote images without a local game file");
        Console.WriteLine("[PASS] Cosmetic layers, placement, base clip, death and stale asynchronous results");
        Console.WriteLine("[PASS] NoS adaptive cosmetic rendered pixels and published color changes");
        Console.WriteLine("[PASS] SNR custom layers never fall back to GitHub images");
        VerifySecondaryCosmetics();
    }

    internal static void VerifyNosCosmetics()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var game = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tanuki-nos-avatar-{Guid.NewGuid():N}");
        try
        {
            var folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(game, "BepInEx", "MoreCosmic", "Local"));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(
                2, 2, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 16).ToArray(), 8)));
            using (var stream = System.IO.File.Create(System.IO.Path.Combine(folder.FullName, "hat.png"))) encoder.Save(stream);
            System.IO.File.Copy(System.IO.Path.Combine(folder.FullName, "hat.png"), System.IO.Path.Combine(folder.FullName, "mask.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(game, "BepInEx", "MoreCosmic", "LoadedContents.json"), """
                {"Version":20261005,"Hats":{"H":{"ProductId":"nos_h","RelatedRawLocalPath":"BepInEx/MoreCosmic/Local",
                 "Images":[{"Layer":"Main","Address":"hat.png","MaskAddress":"mask.png","DivisionX":1,"DivisionY":1}]}},
                 "Visors":{"V":{"ProductId":"nos_v","RelatedRawLocalPath":"BepInEx/MoreCosmic/Local",
                 "Images":[{"Layer":"Main","Address":"hat.png","DivisionX":1,"DivisionY":1}]}},
                 "Skins":{"S":{"ProductId":"nos_s","RelatedRawLocalPath":"BepInEx/MoreCosmic/Local",
                 "Images":[{"Layer":"Main","Address":"hat.png","DivisionX":1,"DivisionY":1}]}}}
                """);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32,
                null, new byte[] { 0, 0, 255, 255 }, 4);
            bitmap.Freeze();
            var requested = new List<Uri>();
            var avatar = new PlayerAvatar { Width = 80, Height = 80, nosContents = new NosCosmeticContents(),
                catalogLoader = () => Task.FromResult(CosmeticCatalog.Parse("""
                    {"NoS":{"defaultWidth":"100%","hats":{"visor_x":{"image":"v.png"}}}}
                    """)),
                imageLoader = uri => { requested.Add(uri); return Task.FromResult(bitmap); } };
            avatar.Measure(new Size(80, 80)); avatar.Arrange(new Rect(0, 0, 80, 80));
            var player = new Player { HatId = "hat_vanilla", VisorId = "visor_vanilla",
                NosPlayer = new NosPlayerData { ColorR = 1, Hat = new NosCostumeData("nos_h"),
                    Visor = new NosCostumeData("visor_x"), Skin = new NosCostumeData("") } };
            avatar.SetPlayer(player, null, mod: AmongUsModType.NebulaOnTheShip,
                gameExecutable: System.IO.Path.Combine(game, "Among Us.exe"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((avatar.CosmeticFront.Children.Count < 2 || avatar.BodyCanvas.OpacityMask is null) && DateTime.UtcNow < deadline)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
            Require(avatar.CosmeticFront.Children.Count == 2 &&
                ((CosmeticAsset)((Image)avatar.CosmeticFront.Children[0]).Tag).Url.Scheme == "nos-cosmetic" &&
                requested.Count == 1 && requested[0].AbsolutePath.EndsWith("v.png"),
                "NoS LoadedContents hat or published visor name did not reach the avatar");
            Require(avatar.BodyCanvas.OpacityMask is ImageBrush { ViewportUnits: BrushMappingMode.Absolute } mask &&
                Math.Abs(mask.Viewport.Width - 112) < 0.001 && Math.Abs(mask.Viewport.Y - -24) < 0.001,
                "NoS body mask is not aligned with the hat");
            Require(avatar.CosmeticBack.Children.Count == 0 && avatar.CosmeticSkin.Children.Count == 0,
                "Empty NoS skin or missing hat back produced a layer");
            player.IsDead = true;
            avatar.SetPlayer(player, null, mod: AmongUsModType.NebulaOnTheShip,
                gameExecutable: System.IO.Path.Combine(game, "Among Us.exe"));
            Require(avatar.BodyCanvas.OpacityMask is null && avatar.CosmeticFront.Children.Count == 0,
                "Dead NoS avatar kept its body mask");
            var lobbyPlayer = new Player { AppearanceHatId = "nos_h", AppearanceVisorId = "nos_v",
                AppearanceSkinId = "nos_s", NosLobbyColor = "#ff8000" };
            avatar.SetPlayer(lobbyPlayer, null, mod: AmongUsModType.NebulaOnTheShip,
                gameExecutable: System.IO.Path.Combine(game, "Among Us.exe"), gameState: GameState.Lobby);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while ((avatar.CosmeticFront.Children.Count < 2 || avatar.CosmeticSkin.Children.Count < 1 ||
                    avatar.BodyCanvas.OpacityMask is null) &&
                DateTime.UtcNow < deadline)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
            Require(avatar.CosmeticFront.Children.Count == 2 && avatar.CosmeticSkin.Children.Count == 1 &&
                avatar.BodyCanvas.OpacityMask is not null,
                $"NoS lobby equipment was not rendered without round PlayerData: " +
                $"front={avatar.CosmeticFront.Children.Count} skin={avatar.CosmeticSkin.Children.Count} " +
                $"mask={avatar.BodyCanvas.OpacityMask is not null}.");
            lobbyPlayer.AppearanceHatId = lobbyPlayer.AppearanceSkinId = lobbyPlayer.AppearanceVisorId = "";
            avatar.SetPlayer(lobbyPlayer, null, mod: AmongUsModType.NebulaOnTheShip,
                gameExecutable: System.IO.Path.Combine(game, "Among Us.exe"), gameState: GameState.Lobby);
            Require(avatar.CosmeticBack.Children.Count == 0 && avatar.CosmeticFront.Children.Count == 0 &&
                avatar.CosmeticSkin.Children.Count == 0 && avatar.BodyCanvas.OpacityMask is null,
                "Unequipping in the NoS lobby left stale layers.");
        }
        finally
        {
            try { System.IO.Directory.Delete(game, recursive: true); } catch (System.IO.IOException) { }
        }
        Console.WriteLine("[PASS] NoS round/lobby costume layers, unequipping and body mask in PlayerAvatar");
    }

    private static void VerifySecondaryCosmetics()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32,
            null, new byte[] { 0, 0, 255, 255 }, 4);
        bitmap.Freeze();
        var catalog = CosmeticCatalog.Parse("""
            {"NONE":{"defaultWidth":"100%","hats":{
            "hat":{"image":"hat.png","back_image":"back.png"},"visor":{"image":"visor.png"},
            "hat2":{"image":"hat2.png","back_image":"back2.png"},"visor2":{"image":"visor2.png"}}}}
            """);
        var avatar = new PlayerAvatar { catalogLoader = () => Task.FromResult(catalog), imageLoader = _ => Task.FromResult(bitmap) };
        var player = new Player { HatId = "hat", VisorId = "visor",
            CurrentOutfit = 1, AppearanceHatId = "hat", AppearanceVisorId = "visor",
            SnrRole = new SnrRoleData(0, null, null, null, null, null, Hat2Id: "hat2", Visor2Id: "visor2") };
        static string[] Names(Canvas canvas) => canvas.Children.Cast<Image>()
            .Select(image => System.IO.Path.GetFileName(((CosmeticAsset)image.Tag).Url.AbsolutePath)).ToArray();
        avatar.SetPlayer(player, null, mod: AmongUsModType.SuperNewRoles);
        Require(Names(avatar.CosmeticBack).SequenceEqual(new[] { "back.png", "back2.png" }) &&
            Names(avatar.CosmeticFront).SequenceEqual(new[] { "hat2.png", "visor2.png", "hat.png", "visor.png" }),
            "SNR secondary layer order or disguise handling differs from 3.2.7");
        player.SnrRole = player.SnrRole with { Hat2Id = "hat_NoHat", Visor2Id = "visor_EmptyVisor" };
        avatar.SetPlayer(player, null, mod: AmongUsModType.SuperNewRoles);
        Require(avatar.CosmeticBack.Children.Count == 1 && avatar.CosmeticFront.Children.Count == 2, "Empty secondary IDs did not remove layers");
        player.SnrRole = player.SnrRole with { Hat2Id = "hat2", Visor2Id = "visor2" };
        avatar.SetPlayer(player, null, mod: AmongUsModType.None);
        Require(avatar.CosmeticFront.Children.Count == 2, "SNR secondary layers leaked into another MOD");
        player.IsDead = true;
        avatar.SetPlayer(player, null, mod: AmongUsModType.SuperNewRoles);
        Require(avatar.CosmeticBack.Children.Count == 0 && avatar.CosmeticFront.Children.Count == 0, "Dead player retained secondary layers");
        player.IsDead = false;
        player.Disconnected = true;
        avatar.SetPlayer(player, null, mod: AmongUsModType.SuperNewRoles);
        Require(avatar.CosmeticFront.Children.Count == 2, "Disconnected player retained secondary metadata");
        player.Disconnected = false;
        var pending = new TaskCompletionSource<System.Windows.Media.Imaging.BitmapSource>();
        avatar.imageLoader = _ => pending.Task;
        avatar.SetPlayer(player, null, mod: AmongUsModType.SuperNewRoles);
        player.SnrRole = null;
        avatar.imageLoader = _ => Task.FromResult(bitmap);
        avatar.SetPlayer(player, null, mod: AmongUsModType.SuperNewRoles);
        pending.SetResult(bitmap);
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Require(Names(avatar.CosmeticFront).SequenceEqual(new[] { "hat.png", "visor.png" }), "Late secondary image survived metadata removal");
        var fixture = System.IO.Directory.CreateTempSubdirectory("tanuki-snr-secondary-");
        try
        {
            var pack = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fixture.FullName,
                "SuperNewRolesNext", "CustomCosmetics", "NONE_PACKAGE"));
            foreach (var suffix in new[] { "front", "back", "idle" })
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(pack.FullName, $"Extra_{suffix}.png"), [1]);
            avatar.SetPlayer(new Player { SnrRole = new SnrRoleData(0, null, null, null, null, null,
                Hat2Id: "Modded_NONE_PACKAGE_Extra", Visor2Id: "Modded_NONE_PACKAGE_Extra") }, null,
                mod: AmongUsModType.SuperNewRoles,
                gameExecutable: System.IO.Path.Combine(fixture.FullName, "Among Us.exe"));
            Require(Names(avatar.CosmeticBack).SequenceEqual(new[] { "Extra_back.png" }) &&
                Names(avatar.CosmeticFront).SequenceEqual(new[] { "Extra_front.png", "Extra_idle.png" }),
                "Secondary-only SNR outfit did not use the selected game's local images");
        }
        finally { fixture.Delete(recursive: true); }
        Console.WriteLine("[PASS] SNR secondary layer order, disguise, empty IDs, MOD isolation, death, disconnect and stale response removal");
    }
}
