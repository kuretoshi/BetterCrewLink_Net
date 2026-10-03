using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

/// <summary>Visual portion of the v3.2.7 Avatar and ConnectionIndicator components.</summary>
public partial class PlayerAvatar : UserControl
{
    private System.Windows.Media.Imaging.BitmapSource? currentImage;
    private string? currentName;
    private bool hideAvatar;
    private bool renderedDead;
    private bool clipCosmetics;
    private Brush idleBorder = Brushes.Transparent;
    // Material icon paths used by the upstream Avatar.tsx status badges.
    private static readonly Geometry WifiOff = Geometry.Parse(
        "M22.99 9C19.15 5.16 13.8 3.76 8.84 4.78l2.52 2.52c3.47-.17 6.99 1.05 9.63 3.7zm-4 4c-1.29-1.29-2.84-2.13-4.49-2.56l3.53 3.53zM2 3.05 5.07 6.1C3.6 6.82 2.22 7.78 1 9l1.99 2c1.24-1.24 2.67-2.16 4.2-2.77l2.24 2.24C7.81 10.89 6.27 11.73 5 13v.01L6.99 15c1.36-1.36 3.14-2.04 4.92-2.06L18.98 20l1.27-1.26L3.29 1.79zM9 17l3 3 3-3c-1.65-1.66-4.34-1.66-6 0");
    private static readonly Geometry LinkOff = Geometry.Parse(
        "M17 7h-4v1.9h4c1.71 0 3.1 1.39 3.1 3.1 0 1.43-.98 2.63-2.31 2.98l1.46 1.46C20.88 15.61 22 13.95 22 12c0-2.76-2.24-5-5-5m-1 4h-2.19l2 2H16zM2 4.27l3.11 3.11C3.29 8.12 2 9.91 2 12c0 2.76 2.24 5 5 5h4v-1.9H7c-1.71 0-3.1-1.39-3.1-3.1 0-1.59 1.21-2.9 2.76-3.07L8.73 11H8v2h2.73L13 15.27V17h1.73l4.01 4L20 19.74 3.27 3z");
    private static readonly Geometry ErrorOutline = Geometry.Parse(
        "M11 15h2v2h-2zm0-8h2v6h-2zm1-5C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z");
    private static readonly Geometry MicOff = Geometry.Parse(
        "M19 11h-1.7c0 .74-.16 1.43-.43 2.05l1.23 1.23c.56-.98.9-2.09.9-3.28m-4.02.17c0-.06.02-.11.02-.17V5c0-1.66-1.34-3-3-3S9 3.34 9 5v.18zM4.27 3 3 4.27l6.01 6.01V11c0 1.66 1.33 3 2.99 3 .22 0 .44-.03.65-.08l1.66 1.66c-.71.33-1.5.52-2.31.52-2.76 0-5.3-2.1-5.3-5.1H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c.91-.13 1.77-.45 2.54-.9L19.73 21 21 19.73z");
    private static readonly Geometry VolumeOff = Geometry.Parse(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63m2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71M4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9zM12 4 9.91 6.09 12 8.18z");

    public PlayerAvatar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => LayoutCosmetics();
        Unloaded += (_, _) => { cosmeticGeneration++; cosmeticKey = null; };
    }

    public void SetOverlayMode(bool lookLeft = false, bool clipEquipment = false, bool showBorder = false)
    {
        QualityBadge.Visibility = Visibility.Collapsed;
        AvatarVisual.RenderTransform = new ScaleTransform(lookLeft ? -1 : 1, 1);
        clipCosmetics = clipEquipment;
        idleBorder = showBorder ? new SolidColorBrush(Color.FromArgb(0x86, 0xcc, 0xbd, 0xcc)) : Brushes.Transparent;
        LayoutCosmetics();
    }

    internal void VerifyOverlayAppearance(bool left, bool clipped, bool bordered)
    {
        if (AvatarVisual.RenderTransform is not ScaleTransform transform || transform.ScaleX != (left ? -1 : 1) ||
            transform.ScaleY != 1 || (CosmeticBack.Clip is EllipseGeometry) != clipped ||
            (CosmeticFront.Clip is EllipseGeometry) != clipped || BodyCanvas.Clip is not EllipseGeometry ||
            QualityBadge.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Overlay avatar direction/clip differs from release");
        if (StateBadge.Parent == AvatarVisual || RadioBadge.Parent != AvatarVisual || AvatarVisual.Parent != StateBadge.Parent)
            throw new InvalidOperationException("Overlay mirror must include radio but exclude status badges");
        SetVisualState(false, false, false, "connected", false);
        if (SpeechRing.Stroke is not SolidColorBrush idle || idle.Color.A != (bordered ? 0x86 : 0))
            throw new InvalidOperationException("Overlay idle border differs from release");
        SetVisualState(true, false, false, "connected", false);
        if (SpeechRing.Stroke is not SolidColorBrush active || active.Color != Color.FromRgb(0x2e, 0xcc, 0x71))
            throw new InvalidOperationException("Overlay active border lost after idle border configuration");
    }

    internal void VerifyDisguisedOverlay()
    {
        if (AvatarBody.Visibility != Visibility.Visible || SpeechRing.Visibility != Visibility.Visible ||
            SpeechRing.Stroke is not SolidColorBrush stroke || stroke.Color != Colors.Gray ||
            CosmeticFront.Clip is not EllipseGeometry)
            throw new InvalidOperationException("Disguised local overlay must retain its outfit, gray ring and clipped equipment");
    }

    internal bool IsRadioBadgeVisible => RadioBadge.Visibility == Visibility.Visible;

    internal bool IsGhostVisual => renderedDead;

    internal bool HasBuggedBadge => StateBadge.Visibility == Visibility.Visible &&
        ReferenceEquals(StateIcon.Data, ErrorOutline);

    internal bool HasGoodQualityBars => QualityBar3.Background is SolidColorBrush brush &&
        brush.Color == Color.FromRgb(0x66, 0xbb, 0x6a);

    public void SetPlayer(Player player, IReadOnlyList<PlayerColorPair>? colors,
        bool hideWhenAppearanceChanged = false, AmongUsModType mod = AmongUsModType.None,
        string gameExecutable = "", bool? displayDead = null)
    {
        renderedDead = displayDead ?? player.IsDead;
        hideAvatar = hideWhenAppearanceChanged && player.HasVisibleAppearanceChanged();
        AvatarBody.Visibility = hideAvatar ? Visibility.Hidden : Visibility.Visible;
        CosmeticBack.Visibility = CosmeticSkin.Visibility = CosmeticFront.Visibility =
            hideAvatar || renderedDead ? Visibility.Hidden : Visibility.Visible;
        SpeechRing.Visibility = hideAvatar ? Visibility.Hidden : Visibility.Visible;
        var colorId = AvatarImageFactory.GetDisplayColorId(player, colors, mod);
        var image = (mod == AmongUsModType.NebulaOnTheShip ? AvatarImageFactory.GetNos(player, renderedDead) : null)
            ?? AvatarImageFactory.Get(colorId, renderedDead, colors);
        if (!ReferenceEquals(image, currentImage))
        {
            AvatarBody.Source = image;
            currentImage = image;
        }
        UpdateCosmetics(player, colors, mod, colorId, gameExecutable);
        LayoutCosmetics();
        var displayName = string.IsNullOrWhiteSpace(player.AppearanceName) ? player.Name : player.AppearanceName;
        if (displayName != currentName)
        {
            ToolTip = displayName;
            currentName = displayName;
        }
    }

    public void SetVisualState(bool talking, bool muted, bool deafened, string connectionState,
        bool usingRadio, ConnectionQuality? quality = null, bool grayTalking = false, bool bugged = false)
    {
        SpeechRing.Stroke = talking && !hideAvatar ?
            grayTalking ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71)) : idleBorder;
        RadioBadge.Visibility = usingRadio && !hideAvatar ? Visibility.Visible : Visibility.Collapsed;

        var (geometry, badgeColor, borderColor) = bugged
            ? (ErrorOutline, Color.FromRgb(0xff, 0x00, 0x00), Color.FromRgb(0x69, 0x0a, 0x00))
            : connectionState switch
        {
            "disconnected" => (WifiOff, Color.FromRgb(0xea, 0x3c, 0x2a), Color.FromRgb(0x69, 0x0a, 0x00)),
            "novoice" => (LinkOff, Color.FromRgb(0xe6, 0x7e, 0x22), Color.FromRgb(0x69, 0x49, 0x00)),
            _ when deafened => (VolumeOff, Color.FromRgb(0xea, 0x3c, 0x2a), Color.FromRgb(0x69, 0x0a, 0x00)),
            _ when muted => (MicOff, Color.FromRgb(0xea, 0x3c, 0x2a), Color.FromRgb(0x69, 0x0a, 0x00)),
            _ => (null, default, default)
        };
        StateBadge.Visibility = geometry is null ? Visibility.Collapsed : Visibility.Visible;
        if (geometry is not null)
        {
            StateIcon.Data = geometry;
            StateBadge.Background = new SolidColorBrush(badgeColor);
            StateBadge.BorderBrush = new SolidColorBrush(borderColor);
        }

        var connected = connectionState == "connected";
        var bars = connected ? quality?.Bars ?? 0 : 0;
        var active = bars switch
        {
            1 => Color.FromRgb(0xef, 0x53, 0x50),
            2 => Color.FromRgb(0xff, 0xca, 0x28),
            _ => Color.FromRgb(0x66, 0xbb, 0x6a)
        };
        var activeBrush = new SolidColorBrush(active);
        QualityBar1.Background = bars >= 1 ? activeBrush : new SolidColorBrush(Color.FromRgb(0x72, 0x77, 0x7d));
        QualityBar2.Background = bars >= 2 ? activeBrush : new SolidColorBrush(Color.FromRgb(0x72, 0x77, 0x7d));
        QualityBar3.Background = bars >= 3 ? activeBrush : new SolidColorBrush(Color.FromRgb(0x72, 0x77, 0x7d));
        var status = !connected ? "未接続" : bars switch
        {
            0 => "未計測",
            1 => "不安定",
            2 => "普通",
            _ => "良好"
        };
        var ping = connected ? quality?.ServerPingMs ?? quality?.RttMs : null;
        var tooltip = $"音声接続: {status}\nボイスサーバーとのping: {(ping is null ? "—" : $"{Math.Round(ping.Value)} ms")}";
        if (connected && quality?.JitterMs is double jitterMs)
            tooltip += $"\n受信の揺らぎ: {Math.Round(jitterMs)} ms";
        if (connected && quality?.LossPercent is double lossPercent)
            tooltip += $"\n受信ロス: {lossPercent:0.0}%";
        QualityBadge.ToolTip = tooltip;
    }
}
