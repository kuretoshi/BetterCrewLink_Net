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

    private void UpdateCosmetics(Player player, IReadOnlyList<PlayerColorPair>? palette, AmongUsModType mod, int colorId)
    {
        var outfit = player.CurrentOutfit is > 0 and <= 10;
        static string Select(bool outfit, string appearance, string normal, string empty) =>
            (outfit || appearance.StartsWith("Modded_", StringComparison.Ordinal) ? appearance : normal) is var id && id != empty ? id : "";
        var hat = Select(outfit, player.AppearanceHatId, player.HatId, "hat_NoHat");
        var skin = Select(outfit, player.AppearanceSkinId, player.SkinId, "skin_None");
        var visor = Select(outfit, player.AppearanceVisorId, player.VisorId, "visor_EmptyVisor");
        var colors = AvatarImageFactory.GetSwatchColors(colorId, palette);
        var key = $"{player.IsDead}|{mod}|{hat}|{skin}|{visor}|{colors}";
        if (key == cosmeticKey) return;
        if (cosmeticKey is null && DateTimeOffset.UtcNow < cosmeticRetryAt) return;
        cosmeticKey = key;
        var generation = ++cosmeticGeneration;
        CosmeticBack.Children.Clear(); CosmeticSkin.Children.Clear(); CosmeticFront.Children.Clear();
        if (player.IsDead || string.IsNullOrEmpty(hat + skin + visor)) return;
        _ = LoadCosmeticsAsync(generation, hat, skin, visor, mod, colors);
    }

    private async Task LoadCosmeticsAsync(long generation, string hat, string skin, string visor,
        AmongUsModType mod, (Color Main, Color Shadow) colors)
    {
        try
        {
            var catalog = await catalogLoader();
            var snr = mod == AmongUsModType.SuperNewRoles && new[] { hat, skin, visor }.Any(id => id.StartsWith("Modded_", StringComparison.Ordinal))
                ? await snrCatalogLoader() : null;
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
            foreach (var (id, part, target) in new[] {
                (hat, CosmeticPart.HatBack, CosmeticBack), (skin, CosmeticPart.Skin, CosmeticSkin),
                (hat, CosmeticPart.Hat, CosmeticFront), (visor, CosmeticPart.Visor, CosmeticFront) })
            {
                if (generation != cosmeticGeneration) return;
                var customSnr = snr is not null && id.StartsWith("Modded_", StringComparison.Ordinal);
                var asset = customSnr ? snr!.Resolve(id, part) : catalog.Resolve(id, modName, part);
                // Image selection prioritizes SNR remote definitions, but dimensions
                // still use a shared catalog entry when one exists in 3.2.7.
                if (customSnr && asset is not null && catalog.Dimensions(id, modName) is { } shared)
                    asset = asset with { Top = shared.Top, Left = shared.Left, Width = shared.Width, SnrVisorLayout = false };
                if (asset is null) continue;
                try
                {
                    var bitmap = await imageLoader(asset.Url);
                    if (generation != cosmeticGeneration) return;
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
            }
        }
        catch (Exception error)
        {
            if (generation == cosmeticGeneration)
            {
                cosmeticKey = null;
                cosmeticRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            System.Diagnostics.Trace.TraceWarning($"Cosmetic catalog unavailable: {error.Message}");
        }
    }

    private void LayoutCosmetics()
    {
        var size = ActualWidth > 0 ? ActualWidth : Width;
        if (!double.IsFinite(size) || size <= 0) return;
        BodyCanvas.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        AvatarBody.Width = size * 1.05;
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
        avatar.snrCatalogLoader = () => Task.FromResult(SnrCosmeticCatalog.Parse("""
            {"hats":[{"name":"H","resource":"h.png","backresource":"back.png"}]}
            """, """
            {"Visors":[{"name":"V","resource":"v.png","IsSNR":true}]}
            """));
        avatar.SetPlayer(new Player { HatId = "Modded_NONE_PACKAGE_H", VisorId = "Modded_NONE_PACKAGE_V" },
            null, mod: AmongUsModType.SuperNewRoles);
        Require(avatar.CosmeticBack.Children.Count == 1 && avatar.CosmeticFront.Children.Count == 2,
            "SNR remote layers did not reach rendered avatar");
        var snrVisor = (Image)avatar.CosmeticFront.Children[1];
        var expectedVisor = SnrCosmeticCatalog.WithImageSize(new CosmeticAsset(new Uri("https://example.com/v.png"),
            false, "-52%", "-18px", "140%", true), 1, 1);
        Require(Math.Abs(snrVisor.Width - CosmeticCatalog.ResolveLength(expectedVisor.Width, 80)) < 0.0001,
            "SNR natural image size was not applied to rendering");
        Console.WriteLine("[PASS] Cosmetic layers, placement, base clip, death and stale asynchronous results");
        Console.WriteLine("[PASS] NoS adaptive cosmetic rendered pixels and published color changes");
        Console.WriteLine("[PASS] SNR remote layers and visor image-size layout in PlayerAvatar");
    }
}
