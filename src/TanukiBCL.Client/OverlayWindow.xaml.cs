using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

/// <summary>Click-through, game-bound counterpart of 3.2.8 Overlay.tsx.</summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private readonly int gameProcessId;
    private readonly ClientSettings settings;
    private readonly DispatcherTimer placementTimer;
    private AmongUsState? game;
    private IReadOnlyDictionary<int, OverlayPeerStatus> peers =
        new Dictionary<int, OverlayPeerStatus>();
    private bool localTalking;
    private bool localUsingRadio;
    private bool microphoneMuted;
    private bool deafened;
    private IReadOnlyDictionary<int, bool>? remoteDeadForDisplay;
    private sealed record MeetingParticipant(int Id, int ClientId, int ColorId, bool IsLocal);
    private List<MeetingParticipant> meetingOrder = [];
    private readonly Dictionary<int, MeetingVoiceBorder> meetingSlots = [];
    private sealed class AvatarRow
    {
        internal readonly StackPanel Row = new();
        internal readonly PlayerAvatar Avatar = new();
        internal readonly TextBlock Name = new();
        internal bool? NameVisible;
        internal AnimationClock? NameFade;
        private double fadeStart;
        private double fadeEnd;
        private double reversingStart;
        private double shorteningFactor = 1;

        internal AvatarRow()
        {
            Row.Children.Add(Avatar);
            Avatar.FlowDirection = FlowDirection.LeftToRight;
            Name.FlowDirection = FlowDirection.LeftToRight;
            Name.Foreground = Brushes.White;
            Name.FontWeight = FontWeights.Bold;
            Name.TextTrimming = TextTrimming.CharacterEllipsis;
            Name.MaxWidth = 190;
            Name.VerticalAlignment = VerticalAlignment.Center;
            Name.Background = new SolidColorBrush(Color.FromArgb(0x52, 0, 0, 0));
            Name.Margin = new Thickness(5, 0, 5, 0);
        }

        internal void SetNameVisible(bool visible)
        {
            if (NameVisible == visible) return;
            if (NameVisible is null)
            {
                Name.ApplyAnimationClock(UIElement.OpacityProperty, null);
                Name.Opacity = visible ? 1 : 0;
                NameVisible = visible;
                return;
            }
            var from = Name.Opacity;
            var to = visible ? 1d : 0d;
            NameVisible = visible;
            if (from == to)
            {
                Name.ApplyAnimationClock(UIElement.OpacityProperty, null);
                NameFade = null;
                Name.Opacity = to;
                shorteningFactor = 1;
                return;
            }
            // Match CSS opacity transition's shortening of interrupted reversals.
            if (NameFade?.CurrentState == ClockState.Active && reversingStart == to && fadeEnd != fadeStart)
            {
                var easedProgress = (from - fadeStart) / (fadeEnd - fadeStart);
                shorteningFactor = Math.Clamp(Math.Abs(easedProgress * shorteningFactor + 1 - shorteningFactor), 0, 1);
                reversingStart = fadeEnd;
            }
            else
            {
                shorteningFactor = 1;
                reversingStart = from;
            }
            fadeStart = from;
            fadeEnd = to;
            var duration = TimeSpan.FromMilliseconds(400 * shorteningFactor);
            var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration),
                new KeySpline(0.25, 0.1, 0.25, 1)));
            NameFade = (AnimationClock)((Timeline)animation).CreateClock(true);
            Name.ApplyAnimationClock(UIElement.OpacityProperty, NameFade, HandoffBehavior.SnapshotAndReplace);
        }
    }
    private readonly Dictionary<int, AvatarRow> avatarRows = [];
    private GameState previousGameState = GameState.Unknown;

    internal OverlayWindow(int gameProcessId, ClientSettings settings)
    {
        InitializeComponent();
        this.gameProcessId = gameProcessId;
        this.settings = settings;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle,
                style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
        placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        placementTimer.Tick += (_, _) => RefreshPlacement();
        placementTimer.Start();
    }

    internal void Update(AmongUsState? gameState,
        IReadOnlyDictionary<int, OverlayPeerStatus> peerStatuses,
        bool isLocalTalking, bool isMicrophoneMuted, bool isDeafened, bool isLocalUsingRadio = false,
        IReadOnlyDictionary<int, bool>? remoteDeathDisplay = null)
    {
        game = gameState;
        peers = peerStatuses;
        localTalking = isLocalTalking;
        localUsingRadio = isLocalUsingRadio;
        microphoneMuted = isMicrophoneMuted;
        deafened = isDeafened;
        remoteDeadForDisplay = remoteDeathDisplay;
        if (gameState?.GameState == GameState.Discussion &&
            previousGameState != GameState.Discussion)
        {
            meetingSlots.Clear();
            MeetingCanvas.Children.Clear();
            meetingOrder = gameState.Players.OrderBy(player => player.Disconnected || player.IsDead)
                .ThenBy(player => player.Id)
                .Select(player => new MeetingParticipant(player.Id, player.ClientId, player.ColorId, player.IsLocal)).ToList();
        }
        else if (gameState?.GameState != GameState.Discussion) meetingOrder.Clear();
        previousGameState = gameState?.GameState ?? GameState.Unknown;
        Render();
        RefreshPlacement();
    }

    private void RefreshPlacement()
    {
        if (!settings.EnableOverlay || game is null ||
            game.GameState is GameState.Menu or GameState.Unknown)
        {
            if (IsVisible) Hide();
            return;
        }
        IntPtr gameWindow;
        try
        {
            using var process = Process.GetProcessById(gameProcessId);
            gameWindow = process.MainWindowHandle;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            if (IsVisible) Hide();
            return;
        }
        if (gameWindow == IntPtr.Zero || IsIconic(gameWindow) ||
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid) == 0 ||
            foregroundPid != gameProcessId || !GetClientRect(gameWindow, out var rect) ||
            rect.Right <= 0 || rect.Bottom <= 0)
        {
            if (IsVisible) Hide();
            return;
        }
        var point = new NativePoint();
        if (!ClientToScreen(gameWindow, ref point))
        {
            if (IsVisible) Hide();
            return;
        }
        var dpi = GetDpiForWindow(gameWindow);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var left = point.X / scale;
        var top = point.Y / scale;
        var width = (rect.Right - rect.Left) / scale;
        var height = (rect.Bottom - rect.Top) / scale;
        var resized = !double.IsFinite(Width) || !double.IsFinite(Height) ||
            Math.Abs(Left - left) > 0.5d || Math.Abs(Top - top) > 0.5d ||
            Math.Abs(Width - width) > 0.5d || Math.Abs(Height - height) > 0.5d;
        if (resized)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Render();
        }
        if (!IsVisible) Show();
    }

    private void Render()
    {
        var state = game;
        if (state is null || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            Width <= 0 || Height <= 0) return;
        var isTasks = state.GameState == GameState.Tasks;
        var mod = state.Mod switch
        {
            AmongUsModType.None => string.Empty,
            AmongUsModType.SuperNewRoles => "SuperNewRoles",
            AmongUsModType.TownOfHostForE => "TOH4E",
            AmongUsModType.NebulaOnTheShip => "NoS",
            _ => AmongUsMod.For(state.Mod).Label
        };
        WatermarkTitle.Text = $"TanukiBCL.Net {VoiceView.FormatVersionLabel(UpdateCatalog.CurrentVersion)}{(mod.Length > 0 ? $" [{mod}]" : "")}";
        WatermarkServer.Text = settings.ServerUrl;
        // 3.2.9 Overlay.tsx: dark red watermark with the NoS read failure reason.
        var nosFailed = state.Mod == AmongUsModType.NebulaOnTheShip && state.NosReadStatus?.Failed == true;
        Watermark.Background = nosFailed ? new SolidColorBrush(Color.FromRgb(0x58, 0x1e, 0x24)) : null;
        Watermark.MaxWidth = nosFailed ? 360 : double.PositiveInfinity;
        WatermarkNosStatus.Text = nosFailed ? state.NosReadStatus!.Message : string.Empty;
        WatermarkNosStatus.Visibility = nosFailed ? Visibility.Visible : Visibility.Collapsed;
        WatermarkTitle.TextWrapping = WatermarkServer.TextWrapping = nosFailed ? TextWrapping.Wrap : TextWrapping.NoWrap;
        WatermarkTitle.TextAlignment = isTasks ? TextAlignment.Center : TextAlignment.Left;
        WatermarkServer.TextAlignment = WatermarkNosStatus.TextAlignment = WatermarkTitle.TextAlignment;
        Watermark.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(Watermark, isTasks ? Math.Max(0, (Width - Watermark.DesiredSize.Width) / 2d) : 10d);
        Canvas.SetTop(Watermark, isTasks ? state.Mod == AmongUsModType.NebulaOnTheShip ? 58d : 10d
            : state.Mod == AmongUsModType.SuperNewRoles ? 96d
            : state.Mod == AmongUsModType.NebulaOnTheShip ? 58d : 8d);

        RenderAvatars(state);
        RenderMeeting(state);
    }

    private void RenderAvatars(AmongUsState state)
    {
        var position = settings.OverlayPosition;
        var selected = OverlaySelection.Select(state, peers, localTalking,
            microphoneMuted, settings.CompactOverlay, localUsingRadio, remoteDeadForDisplay);
        if (position == "hidden" || selected.Count == 0)
        {
            AvatarPanel.Children.Clear();
            avatarRows.Clear();
            AvatarBackground.Visibility = Visibility.Collapsed;
            AlternateSideBackground.Visibility = Visibility.Collapsed;
            return;
        }
        var currentIds = selected.Select(entry => entry.Player.Id).ToHashSet();
        foreach (var oldId in avatarRows.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            AvatarPanel.Children.Remove(avatarRows[oldId].Row);
            avatarRows.Remove(oldId);
        }
        var side = position is "left" or "left1" or "right" or "right1";
        var alternateSide = position is "left1" or "right1";
        var compact = settings.CompactOverlay || position is "left1" or "right1";
        var showName = !state.MixupSabotaged && side && (!settings.CompactOverlay || position is "left1" or "right1");
        // Overlay.tsx sets --size to 7.5 * (10 / rendered avatars) vh;
        // overlay.css caps only at 7.5vh, not at a fixed pixel dimension.
        var avatarSize = side ? 0.075d * Height * Math.Min(1d, 10d / selected.Count) : 60d;
        var sideRegionWidth = compact ? avatarSize + 24d : 300d;
        AvatarPanel.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
        AvatarPanel.MaxHeight = side ? Height : double.PositiveInfinity;
        AvatarPanel.MaxWidth = alternateSide ? Width : side ? sideRegionWidth : 800d;
        AvatarBackground.Background = compact || side ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(position == "bottom_left" ? (byte)0x59 : (byte)0x80,
                0, 0, 0));
        // The compact side background belongs to the inner player container,
        // not the outer overlay wrapper. The edge against the screen is square.
        AvatarPanelBackground.Background = side && compact && !alternateSide
            ? new SolidColorBrush(Color.FromArgb(0xc0, 0x25, 0x23, 0x2a)) : Brushes.Transparent;
        AvatarPanelBackground.CornerRadius = side && compact && !alternateSide
            ? position.StartsWith("left", StringComparison.Ordinal)
                ? new CornerRadius(0, 25, 25, 0) : new CornerRadius(25, 0, 0, 25)
            : new CornerRadius(0);
        AvatarBackground.Padding = alternateSide ? new Thickness(0)
            : compact ? new Thickness(3) : new Thickness(8);
        for (var index = 0; index < selected.Count; index++)
        {
            var entry = selected[index];
            var player = entry.Player;
            if (!avatarRows.TryGetValue(player.Id, out var item))
                avatarRows.Add(player.Id, item = new AvatarRow());
            var row = item.Row;
            row.Orientation = side ? Orientation.Horizontal : Orientation.Vertical;
            row.Margin = new Thickness(side ? 1d : 5d);
            row.FlowDirection = side && position.StartsWith("right", StringComparison.Ordinal)
                ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            row.Width = alternateSide ? Width - 2d
                : side ? sideRegionWidth - (compact ? 8d : 18d) : double.NaN;
            row.RenderTransform = position == "left1"
                ? new TranslateTransform(19d, 0d) : Transform.Identity;
            var avatar = item.Avatar;
            avatar.Width = avatarSize;
            avatar.Height = avatarSize;
            // Overlay.tsx displays the current outfit; only the main voice view
            // opts into hiding visibly changed avatars during Tasks.
            var displayDead = player.IsLocal ? player.IsDead
                : remoteDeadForDisplay is null ? player.IsDead
                : remoteDeadForDisplay.TryGetValue(player.ClientId, out var dead) && dead;
            avatar.SetPlayer(player, state.PlayerColors, false, state.Mod, state.GameExecutablePath, displayDead,
                state.GameState);
            avatar.SetOverlayMode(lookLeft: position is not ("left" or "left1" or "bottom_left"),
                showBorder: side && !settings.CompactOverlay);
            avatar.SetVisualState(entry.Talking,
                player.IsLocal && microphoneMuted,
                player.IsLocal && deafened, "connected", entry.UsingRadio,
                grayTalking: player.IsLocal && player.ShiftedColor != -1 && state.GameState != GameState.Discussion);
            if (showName)
            {
                item.Name.Text = string.IsNullOrWhiteSpace(player.AppearanceName)
                    ? player.Name : player.AppearanceName;
                if (!row.Children.Contains(item.Name)) row.Children.Add(item.Name);
                item.SetNameVisible(position is not ("left1" or "right1") || entry.Talking);
            }
            else if (row.Children.Contains(item.Name))
            {
                row.Children.Remove(item.Name);
                item.Name.ApplyAnimationClock(UIElement.OpacityProperty, null);
                item.NameFade = null;
                item.NameVisible = null;
            }
            if (AvatarPanel.Children.IndexOf(row) != index)
            {
                AvatarPanel.Children.Remove(row);
                AvatarPanel.Children.Insert(index, row);
            }
        }
        AvatarBackground.Visibility = Visibility.Visible;
        AvatarBackground.Width = double.NaN;
        AvatarBackground.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var regionWidth = alternateSide ? Width : side ? sideRegionWidth
            : position == "top" ? Math.Min(800d, Width) :
            Math.Min(AvatarBackground.DesiredSize.Width, Width);
        AvatarBackground.Width = regionWidth;
        AvatarBackground.Measure(new Size(regionWidth, double.PositiveInfinity));
        if (side)
        {
            Canvas.SetLeft(AvatarBackground, alternateSide ? 0d
                : position.StartsWith("right", StringComparison.Ordinal)
                    ? Math.Max(0, Width - regionWidth) : 0d);
            // Released overlay.css gives side .otherplayers a full viewport height
            // and centers its players_container with justify-content:center.
            // The WPF panel only occupies its content height, so center that
            // measured panel in the game client area instead of pinning it to top.
            Canvas.SetTop(AvatarBackground,
                Math.Max(0d, (Height - AvatarBackground.DesiredSize.Height) / 2d));
            if (alternateSide)
            {
                AlternateSideBackground.Width = sideRegionWidth;
                AlternateSideBackground.Height = AvatarBackground.DesiredSize.Height;
                AlternateSideBackground.CornerRadius = position == "left1"
                    ? new CornerRadius(0, 25, 25, 0) : new CornerRadius(25, 0, 0, 25);
                Canvas.SetLeft(AlternateSideBackground, position == "left1"
                    ? 0d : Math.Max(0d, Width - sideRegionWidth));
                Canvas.SetTop(AlternateSideBackground, Canvas.GetTop(AvatarBackground));
                AlternateSideBackground.Visibility = Visibility.Visible;
            }
            else AlternateSideBackground.Visibility = Visibility.Collapsed;
        }
        else if (position == "top")
        {
            AlternateSideBackground.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(AvatarBackground, Math.Max(0, (Width - regionWidth) / 2d));
            Canvas.SetTop(AvatarBackground, 0d);
        }
        else
        {
            AlternateSideBackground.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(AvatarBackground, 0d);
            Canvas.SetTop(AvatarBackground, Math.Max(0, Height - AvatarBackground.DesiredSize.Height));
        }
    }

    private void RenderMeeting(AmongUsState state)
    {
        if (!settings.MeetingOverlay || state.GameState != GameState.Discussion ||
            meetingOrder.Count == 0)
        {
            meetingSlots.Clear();
            MeetingCanvas.Children.Clear();
            MeetingCanvas.Visibility = Visibility.Collapsed;
            return;
        }
        var layout = MeetingOverlayLayout.Create(Width, Height, state.OldMeetingHud);
        var hudWidth = layout.Width;
        var hudHeight = layout.Height;
        MeetingCanvas.Width = hudWidth;
        MeetingCanvas.Height = hudHeight;
        Canvas.SetLeft(MeetingCanvas, (Width - hudWidth) / 2d);
        Canvas.SetTop(MeetingCanvas, (Height - hudHeight) / 2d);
        for (var index = 0; index < meetingOrder.Count; index++)
        {
            var player = meetingOrder[index];
            var livePlayer = state.Players.FirstOrDefault(candidate => candidate.Id == player.Id);
            peers.TryGetValue(player.ClientId, out var peer);
            var talking = player.IsLocal ? localTalking && !microphoneMuted : peer?.VoiceActive == true;
            var bounds = layout.Slot(index);
            var color = state.Mod == AmongUsModType.NebulaOnTheShip && livePlayer?.NosPlayer is { } nos &&
                double.IsFinite(nos.ColorR) && double.IsFinite(nos.ColorG) && double.IsFinite(nos.ColorB)
                ? Color.FromRgb(ToByte(nos.ColorR), ToByte(nos.ColorG), ToByte(nos.ColorB))
                : player.ColorId >= 0 && player.ColorId < state.PlayerColors.Count
                    ? AvatarImageFactory.GetSwatchColors(player.ColorId, state.PlayerColors).Main
                    : AvatarImageFactory.GetSwatchColors(0, null).Main;
            if (!meetingSlots.TryGetValue(player.Id, out var slot))
            {
                slot = new MeetingVoiceBorder
                {
                    BorderThickness = new Thickness(2d),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x37, 0, 0, 0)),
                    Background = Brushes.Transparent
                };
                meetingSlots.Add(player.Id, slot);
                MeetingCanvas.Children.Add(slot);
            }
            slot.Width = bounds.Width; slot.Height = bounds.Height;
            slot.CornerRadius = new CornerRadius(hudHeight / 100d);
            slot.SetShadow(color, hudHeight / 100d);
            slot.SetTalking(talking);
            Canvas.SetLeft(slot, bounds.X);
            Canvas.SetTop(slot, bounds.Y);
        }
        MeetingCanvas.Visibility = Visibility.Visible;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Floor(Math.Clamp(value, 0d, 1d) * 255d + 0.5d);

    internal static void VerifyRender()
    {
        MeetingOverlayLayout.Verify();
        MeetingVoiceBorder.Verify();
        VerifyMeetingSnapshot();
        VerifyAvatarSizingAndBackground();
        VerifyAvatarRowPersistence();
        VerifyAlternateSidePlacement();
        VerifySidePlacement();
        VerifyRemoteDeathDisplay();
        var settings = new ClientSettings { EnableOverlay = true, MeetingOverlay = true };
        var window = new OverlayWindow(0, settings) { Width = 1280, Height = 720 };
        try
        {
            var state = new AmongUsState
            {
                GameState = GameState.Discussion, LobbyCode = "TEST",
                Players =
                [
                    new Player { Id = 0, ClientId = 1, IsLocal = true, Name = "local" },
                    new Player { Id = 1, ClientId = 2, Name = "peer" }
                ]
            };
            window.Update(state, new Dictionary<int, OverlayPeerStatus>
                { [2] = new(true, true, false) }, false, false, false);
            if (window.AvatarPanel.Children.Count != 2 ||
                window.MeetingCanvas.Children.Count != 2 ||
                window.WatermarkTitle.Text != $"TanukiBCL.Net {VoiceView.FormatVersionLabel(UpdateCatalog.CurrentVersion)}")
                throw new InvalidOperationException("Overlay render smoke test failed");
            state.Mod = AmongUsModType.NebulaOnTheShip;
            state.NosReadStatus = new NosReadStatus(true, "NoS読み取り失敗: test", 20261005);
            window.Update(state, new Dictionary<int, OverlayPeerStatus>
                { [2] = new(true, true, false) }, false, false, false);
            if (window.WatermarkNosStatus.Visibility != Visibility.Visible ||
                window.WatermarkNosStatus.Text != "NoS読み取り失敗: test" ||
                window.Watermark.Background is not SolidColorBrush { Color: { R: 0x58, G: 0x1e, B: 0x24 } })
                throw new InvalidOperationException("Overlay NoS read failure watermark differs from 3.2.9");
            state.NosReadStatus = state.NosReadStatus with { Failed = false };
            state.Mod = AmongUsModType.None;
            window.Update(state, new Dictionary<int, OverlayPeerStatus>
                { [2] = new(true, true, false) }, false, false, false);
            if (window.WatermarkNosStatus.Visibility != Visibility.Collapsed || window.Watermark.Background is not null)
                throw new InvalidOperationException("Overlay NoS failure style remained after recovery");
            var retainedSlot = window.MeetingCanvas.Children[1];
            window.Update(state, new Dictionary<int, OverlayPeerStatus> { [2] = new(true, false, false) }, false, false, false);
            if (!ReferenceEquals(retainedSlot, window.MeetingCanvas.Children[1]))
                throw new InvalidOperationException("Meeting update replaced the animated slot");
            foreach (var oldHud in new[] { true, false })
            {
                state.OldMeetingHud = oldHud;
                window.Update(state, new Dictionary<int, OverlayPeerStatus> { [2] = new(true, true, false) }, false, false, false);
                var expected = MeetingOverlayLayout.Create(window.Width, window.Height, oldHud);
                var box = (Border)window.MeetingCanvas.Children[1];
                var expectedBox = expected.Slot(1);
                if (Math.Abs(window.MeetingCanvas.Width - expected.Width) > 0.000001 ||
                    Math.Abs(window.MeetingCanvas.Height - expected.Height) > 0.000001 ||
                    Math.Abs(Canvas.GetLeft(box) - expectedBox.X) > 0.000001 ||
                    Math.Abs(Canvas.GetTop(box) - expectedBox.Y) > 0.000001 ||
                    Math.Abs(box.Width - expectedBox.Width) > 0.000001 ||
                    Math.Abs(box.Height - expectedBox.Height) > 0.000001)
                    throw new InvalidOperationException("Meeting HUD layout was not applied to WPF slots");
            }
            foreach (var (position, mirrored, side, alternate) in new[] {
                ("left", false, true, false), ("left1", false, true, true),
                ("right", true, true, false), ("right1", true, true, true),
                ("top", true, false, false), ("bottom", true, false, false),
                ("bottom_left", false, false, false) })
            foreach (var compact in new[] { false, true })
            {
                settings.OverlayPosition = position;
                settings.CompactOverlay = compact;
                window.Update(state, new Dictionary<int, OverlayPeerStatus> { [2] = new(true, true, false) }, true, false, false);
                if (window.AvatarPanel.Children.Count != 2) throw new InvalidOperationException("Overlay avatar selection changed");
                foreach (StackPanel row in window.AvatarPanel.Children)
                    ((PlayerAvatar)row.Children[0]).VerifyOverlayAppearance(mirrored, side && !compact);
            }
            Console.WriteLine("[PASS] Overlay direction, equipment clipping and idle/active border across seven positions and compact modes");
            settings.OverlayPosition = "left";
            settings.CompactOverlay = false;
            state.GameState = GameState.Tasks;
            var local = state.Players[0];
            local.CurrentOutfit = 1;
            local.AppearanceColorId = local.ColorId + 1;
            local.ShiftedColor = local.AppearanceColorId;
            window.Update(state, new Dictionary<int, OverlayPeerStatus> { [2] = new(true, true, false) }, true, false, false);
            if (!state.MixupSabotaged || window.AvatarPanel.Children.Cast<StackPanel>().Any(row => row.Children.Count != 1))
                throw new InvalidOperationException("Disguised Tasks state exposed overlay names");
            ((PlayerAvatar)((StackPanel)window.AvatarPanel.Children[0]).Children[0]).VerifyDisguisedOverlay();
            state.GameState = GameState.Discussion;
            window.Update(state, new Dictionary<int, OverlayPeerStatus> { [2] = new(true, true, false) }, true, false, false);
            if (state.MixupSabotaged || window.AvatarPanel.Children.Cast<StackPanel>().Any(row => row.Children.Count != 2))
                throw new InvalidOperationException("Discussion did not restore overlay names");
            state.GameState = GameState.Tasks;
            local.Bugged = true;
            if (state.MixupSabotaged) throw new InvalidOperationException("Bugged player triggered mixup detection");
            local.Bugged = false;
            local.Disconnected = true;
            if (state.MixupSabotaged) throw new InvalidOperationException("Disconnected player triggered mixup detection");
            local.Disconnected = false;
            local.AppearanceColorId = local.ColorId;
            if (state.MixupSabotaged) throw new InvalidOperationException("Unchanged outfit triggered mixup detection");
            Console.WriteLine("[PASS] Overlay disguise names, gray local ring, visible outfit, discussion restore and invalid-player filtering");
            state.GameState = GameState.Discussion;
            foreach (var radio in new[] { true, false })
            {
                window.Update(state, new Dictionary<int, OverlayPeerStatus> { [2] = new(true, true, true) },
                    true, false, false, radio);
                var localAvatar = (PlayerAvatar)((StackPanel)window.AvatarPanel.Children[0]).Children[0];
                var remoteAvatar = (PlayerAvatar)((StackPanel)window.AvatarPanel.Children[1]).Children[0];
                if (localAvatar.IsRadioBadgeVisible != radio || !remoteAvatar.IsRadioBadgeVisible)
                    throw new InvalidOperationException("Local radio toggle was lost or changed remote radio badge");
            }
            Console.WriteLine("[PASS] Overlay local radio on/off reaches rendered badge independently of remote radio");
            var lastSlot = window.MeetingCanvas.Children[0];
            state.GameState = GameState.Tasks;
            window.Update(state, new Dictionary<int, OverlayPeerStatus>(), false, false, false);
            if (window.MeetingCanvas.Children.Count != 0 || window.meetingSlots.Count != 0)
                throw new InvalidOperationException("Meeting exit retained animated slots");
            state.GameState = GameState.Discussion;
            window.Update(state, new Dictionary<int, OverlayPeerStatus>(), false, false, false);
            if (ReferenceEquals(lastSlot, window.MeetingCanvas.Children[0]))
                throw new InvalidOperationException("New meeting reused previous meeting's fade state");
            Console.WriteLine("[PASS] Meeting slots persist across updates and reset between meetings");
        }
        finally
        {
            window.Close();
        }
    }

    private static void VerifySidePlacement()
    {
        var settings = new ClientSettings { EnableOverlay = true, MeetingOverlay = false };
        var window = new OverlayWindow(0, settings) { Width = 1280, Height = 720 };
        try
        {
            var state = new AmongUsState { GameState = GameState.Tasks,
                Players = [new Player { Id = 1, ClientId = 11, IsLocal = true, Name = "local" }] };
            foreach (var position in new[] { "left", "left1", "right", "right1" })
            {
                settings.OverlayPosition = position;
                window.Update(state, new Dictionary<int, OverlayPeerStatus>(), true, false, false);
                window.OverlayCanvas.Measure(new Size(1280, 720));
                window.OverlayCanvas.Arrange(new Rect(0, 0, 1280, 720));
                window.OverlayCanvas.UpdateLayout();
                var item = window.avatarRows[1];
                var avatarLeft = item.Avatar.TransformToAncestor(window.OverlayCanvas)
                    .Transform(new Point()).X;
                var nameLeft = item.Name.TransformToAncestor(window.OverlayCanvas)
                    .Transform(new Point()).X;
                var avatarTop = item.Avatar.TransformToAncestor(window.OverlayCanvas)
                    .Transform(new Point()).Y;
                var right = position.StartsWith("right", StringComparison.Ordinal);
                if (right ? avatarLeft <= nameLeft : avatarLeft >= nameLeft)
                    throw new InvalidOperationException($"{position} placed the name on the wrong side of the avatar");
                if (right && 1280 - (avatarLeft + item.Avatar.ActualWidth) > 40)
                    throw new InvalidOperationException($"{position} failed to anchor the avatar to the screen's right edge");
                if (!right && avatarLeft > 40)
                    throw new InvalidOperationException($"{position} failed to anchor the avatar to the screen's left edge");
                if (nameLeft < -0.5d || nameLeft + item.Name.ActualWidth > 1280.5d)
                    throw new InvalidOperationException($"{position} clipped the side name outside the game client area: {nameLeft}..{nameLeft + item.Name.ActualWidth}");
                if (Math.Abs(avatarTop + item.Avatar.ActualHeight / 2d - 360d) > 40d)
                    throw new InvalidOperationException($"{position} failed to center the avatar vertically");
            }
            Console.WriteLine("[PASS] Left/right side avatars, names and vertical centering at normal and alternate positions");
        }
        finally { window.Close(); }
    }

    private static void VerifyAlternateSidePlacement()
    {
        var settings = new ClientSettings { EnableOverlay = true, MeetingOverlay = false };
        var window = new OverlayWindow(0, settings) { Width = 720, Height = 576 };
        try
        {
            var state = new AmongUsState { GameState = GameState.Tasks,
                Players = [
                    new Player { Id = 1, ClientId = 11, IsLocal = true, Name = "開発者くれとし" },
                    new Player { Id = 2, ClientId = 22, Name = "開発者くれとし 1" }
                ] };
            var peers = new Dictionary<int, OverlayPeerStatus> { [22] = new(true, false, false) };
            foreach (var (width, height) in new[] { (720d, 576d), (1280d, 720d) })
            foreach (var position in new[] { "left1", "right1" })
            {
                window.Width = width;
                window.Height = height;
                settings.OverlayPosition = position;
                window.Update(state, peers, true, false, false);
                window.OverlayCanvas.Measure(new Size(width, height));
                window.OverlayCanvas.Arrange(new Rect(0, 0, width, height));
                window.OverlayCanvas.UpdateLayout();
                foreach (var row in window.avatarRows.Values)
                {
                    var avatar = row.Avatar.TransformToAncestor(window.OverlayCanvas).Transform(new Point());
                    var name = row.Name.TransformToAncestor(window.OverlayCanvas).Transform(new Point());
                    if (position == "left1" ? Math.Abs(avatar.X - 20d) > 2d
                        : Math.Abs(avatar.X + row.Avatar.ActualWidth - width) > 2d)
                        throw new InvalidOperationException($"{position} avatar was clipped at {width}x{height}: {avatar.X}");
                    if (name.X < 0d || name.X + row.Name.ActualWidth > width ||
                        (position == "left1" ? name.X <= avatar.X + row.Avatar.ActualWidth
                            : name.X + row.Name.ActualWidth >= avatar.X))
                        throw new InvalidOperationException($"{position} name was clipped or on the wrong side at {width}x{height}: {name.X}");
                }
                if (Math.Abs(Canvas.GetTop(window.AvatarBackground) + window.AvatarBackground.DesiredSize.Height / 2d
                    - height / 2d) > 1d)
                    throw new InvalidOperationException($"{position} background was not vertically centered");
                // Geometry alone missed a live regression: an avatar could have
                // in-bounds coordinates while its narrow ancestor clipped it.
                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window.OverlayCanvas);
                var pixels = new byte[(int)width * (int)height * 4];
                bitmap.CopyPixels(pixels, (int)width * 4, 0);
                var local = window.avatarRows[1].Avatar;
                var origin = local.TransformToAncestor(window.OverlayCanvas).Transform(new Point());
                var speechRingPixels = 0;
                for (var y = Math.Max(0, (int)origin.Y); y < Math.Min((int)height, (int)(origin.Y + local.ActualHeight)); y++)
                for (var x = Math.Max(0, (int)origin.X); x < Math.Min((int)width, (int)(origin.X + local.ActualWidth)); x++)
                {
                    var pixel = (y * (int)width + x) * 4;
                    if (pixels[pixel + 3] > 180 && pixels[pixel + 1] > 130 &&
                        pixels[pixel + 1] > pixels[pixel + 2] + 40 &&
                        pixels[pixel + 1] > pixels[pixel] + 30)
                        speechRingPixels++;
                }
                if (speechRingPixels < 8)
                    throw new InvalidOperationException($"{position} avatar was positioned but visually clipped at {width}x{height}");

                // The previous narrow ancestor also left the name's WPF bounds
                // inside the viewport while clipping most of the actual glyphs.
                // Compare a long label with that same label hidden, including
                // its trailing ellipsis at the far end of the measured box.
                foreach (var row in window.avatarRows.Values)
                {
                    row.Name.Text = new string('W', 40);
                    row.Name.Background = Brushes.Transparent;
                    row.Name.Opacity = 1d;
                }
                window.OverlayCanvas.UpdateLayout();
                var textVisible = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                textVisible.Render(window.OverlayCanvas);
                var visiblePixels = new byte[(int)width * (int)height * 4];
                textVisible.CopyPixels(visiblePixels, (int)width * 4, 0);
                foreach (var row in window.avatarRows.Values) row.Name.Opacity = 0d;
                var textHidden = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                textHidden.Render(window.OverlayCanvas);
                var hiddenPixels = new byte[(int)width * (int)height * 4];
                textHidden.CopyPixels(hiddenPixels, (int)width * 4, 0);
                foreach (var row in window.avatarRows.Values)
                {
                    var label = row.Name;
                    var labelOrigin = label.TransformToAncestor(window.OverlayCanvas).Transform(new Point());
                    var trailingGlyphPixels = 0;
                    for (var y = Math.Max(0, (int)labelOrigin.Y); y < Math.Min((int)height, (int)(labelOrigin.Y + label.ActualHeight)); y++)
                    for (var x = Math.Max(0, (int)(labelOrigin.X + label.ActualWidth * 0.75d));
                        x < Math.Min((int)width, (int)(labelOrigin.X + label.ActualWidth)); x++)
                    {
                        var pixel = (y * (int)width + x) * 4;
                        if (visiblePixels[pixel + 3] > hiddenPixels[pixel + 3] + 20)
                            trailingGlyphPixels++;
                    }
                    if (trailingGlyphPixels < 4)
                        throw new InvalidOperationException($"{position} name bounds were valid but its trailing text was visually clipped at {width}x{height}");
                }
            }
            Console.WriteLine("[PASS] Alternate side avatars and full labels render inside 720x576/1280x720 viewport edges");
        }
        finally { window.Close(); }
    }

    private static void VerifyRemoteDeathDisplay()
    {
        var settings = new ClientSettings { EnableOverlay = true, OverlayPosition = "left" };
        var window = new OverlayWindow(0, settings) { Width = 1280, Height = 720 };
        try
        {
            var state = new AmongUsState { GameState = GameState.Tasks,
                Players = [
                    new Player { Id = 1, ClientId = 11, IsLocal = true },
                    new Player { Id = 2, ClientId = 22, IsDead = true }
                ] };
            var peers = new Dictionary<int, OverlayPeerStatus> { [22] = new(true, false, false) };
            var display = new Dictionary<int, bool> { [22] = false };
            window.Update(state, peers, false, false, false, false, display);
            if (window.AvatarPanel.Children.Count != 2 ||
                ((PlayerAvatar)((StackPanel)window.AvatarPanel.Children[1]).Children[0]).IsGhostVisual)
                throw new InvalidOperationException("Overlay revealed a task death before the official UI snapshot");
            display[22] = true;
            state.GameState = GameState.Discussion;
            window.Update(state, peers, false, false, false, false, display);
            if (window.AvatarPanel.Children.Count != 1)
                throw new InvalidOperationException("Overlay kept a discussion-revealed ghost visible to a living player");
            state.Players[0].IsDead = true;
            window.Update(state, peers, false, false, false, false, display);
            if (window.AvatarPanel.Children.Count != 2 ||
                !((PlayerAvatar)((StackPanel)window.AvatarPanel.Children[1]).Children[0]).IsGhostVisual)
                throw new InvalidOperationException("Dead listener did not see the revealed ghost in the overlay");
            Console.WriteLine("[PASS] Overlay uses the released remote death snapshot for filtering and ghost visuals");
        }
        finally { window.Close(); }
    }

    private static void VerifyAvatarRowPersistence()
    {
        var row = new AvatarRow();
        row.SetNameVisible(true);
        if (row.Name.Opacity != 1) throw new InvalidOperationException("New overlay name must start visible");
        row.SetNameVisible(false);
        var first = row.NameFade!;
        first.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(200), TimeSeekOrigin.BeginTime);
        var midpoint = row.Name.Opacity;
        if (midpoint <= 0 || midpoint >= 0.5)
            throw new InvalidOperationException("Overlay name 400ms CSS ease midpoint incorrect");
        row.SetNameVisible(false);
        if (!ReferenceEquals(first, row.NameFade))
            throw new InvalidOperationException("Repeated VAD restarted overlay name fade");
        row.SetNameVisible(true);
        if (Math.Abs(row.NameFade!.Timeline.Duration.TimeSpan.TotalMilliseconds - 400 * (1 - midpoint)) > 0.001)
            throw new InvalidOperationException("Overlay name reversal lost CSS shortening");
        row.NameFade.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(1), TimeSeekOrigin.BeginTime);
        if (Math.Abs(row.Name.Opacity - 1) > 0.00001)
            throw new InvalidOperationException("Overlay name reversal did not end visible");

        var settings = new ClientSettings { EnableOverlay = true, MeetingOverlay = false, OverlayPosition = "left1" };
        var window = new OverlayWindow(0, settings) { Width = 1280, Height = 720 };
        try
        {
            var state = new AmongUsState { GameState = GameState.Tasks,
                Players = [new Player { Id = 1, ClientId = 11, Name = "local", IsLocal = true },
                    new Player { Id = 2, ClientId = 22, Name = "peer" }] };
            var active = new Dictionary<int, OverlayPeerStatus> { [22] = new(true, true, false) };
            window.Update(state, active, true, false, false);
            var retainedRow = window.avatarRows[2];
            var retainedAvatar = retainedRow.Avatar;
            var retainedName = retainedRow.Name;
            var inactive = new Dictionary<int, OverlayPeerStatus> { [22] = new(true, false, false) };
            window.Update(state, inactive, false, false, false);
            if (window.AvatarPanel.Children.Count != 2 ||
                !ReferenceEquals(retainedRow, window.avatarRows[2]) ||
                !ReferenceEquals(retainedAvatar, ((StackPanel)window.AvatarPanel.Children[1]).Children[0]) ||
                !ReferenceEquals(retainedName, ((StackPanel)window.AvatarPanel.Children[1]).Children[1]) ||
                retainedRow.NameVisible != false || retainedRow.NameFade is null)
                throw new InvalidOperationException("Silent alternate side name or avatar was replaced instead of fading");
            window.Update(state, active, true, false, false);
            if (!ReferenceEquals(retainedName, window.avatarRows[2].Name) || retainedRow.NameVisible != true)
                throw new InvalidOperationException("Overlay name was recreated when speaking resumed");
            settings.OverlayPosition = "top";
            window.Update(state, active, true, false, false);
            if (retainedRow.Row.Children.Count != 1 || retainedRow.NameVisible is not null)
                throw new InvalidOperationException("Horizontal overlay retained a side-only name");
            Console.WriteLine("[PASS] Overlay alternate side name survives VAD updates with 400ms fade/reversal; avatar is retained");
        }
        finally { window.Close(); }
    }

    private static void VerifyAvatarSizingAndBackground()
    {
        var settings = new ClientSettings { EnableOverlay = true, OverlayPosition = "left", MeetingOverlay = false };
        var window = new OverlayWindow(0, settings) { Width = 1920, Height = 1080 };
        try
        {
            foreach (var (height, count, expectedSize) in new[]
                { (720d, 4, 54d), (1080d, 10, 81d), (1080d, 15, 54d), (2160d, 15, 108d) })
            {
                window.Height = height;
                var state = new AmongUsState { GameState = GameState.Tasks,
                    Players = Enumerable.Range(0, count).Select(id => new Player
                        { Id = id, ClientId = id, IsLocal = id == 0, Name = $"player {id}" }).ToList() };
                var peers = state.Players.ToDictionary(player => player.ClientId,
                    _ => new OverlayPeerStatus(true, true, false));
                window.Update(state, peers, true, false, false);
                if (window.AvatarPanel.Children.Count != count ||
                    window.AvatarPanel.Children.Cast<StackPanel>().Any(row =>
                        Math.Abs(((PlayerAvatar)row.Children[0]).Width - expectedSize) > 0.000001))
                    throw new InvalidOperationException("Side avatar size did not follow viewport height and displayed count");
            }
            window.Height = 1080;
            var all = new AmongUsState { GameState = GameState.Tasks,
                Players = Enumerable.Range(0, 15).Select(id => new Player
                    { Id = id, ClientId = id, IsLocal = id == 0 }).ToList() };
            var statuses = all.Players.ToDictionary(player => player.ClientId,
                player => new OverlayPeerStatus(true, player.Id < 5, false));
            settings.CompactOverlay = true;
            window.Update(all, statuses, true, false, false);
            if (window.AvatarPanel.Children.Count != 5 ||
                ((PlayerAvatar)((StackPanel)window.AvatarPanel.Children[0]).Children[0]).Width != 81d)
                throw new InvalidOperationException("Compact avatar sizing used lobby count instead of rendered VAD count");
            foreach (var position in new[] { "left", "left1", "right", "right1", "top", "bottom_left" })
            foreach (var compact in new[] { false, true })
            {
                settings.OverlayPosition = position; settings.CompactOverlay = compact;
                window.Update(all, statuses, true, false, false);
                var side = position is "left" or "left1" or "right" or "right1";
                var alternateSide = position is "left1" or "right1";
                var compactStyle = compact || position.EndsWith('1');
                var outer = ((SolidColorBrush)window.AvatarBackground.Background).Color;
                var inner = ((SolidColorBrush)window.AvatarPanelBackground.Background).Color;
                var expectedOuter = side || compactStyle ? Colors.Transparent
                    : Color.FromArgb(position == "bottom_left" ? (byte)0x59 : (byte)0x80, 0, 0, 0);
                if (outer != expectedOuter || inner != (side && compactStyle && !alternateSide
                    ? Color.FromArgb(0xc0, 0x25, 0x23, 0x2a) : Colors.Transparent))
                    throw new InvalidOperationException("Overlay wrapper and compact player background colors differ from released CSS");
                var corners = window.AvatarPanelBackground.CornerRadius;
                var expectedCorners = side && compactStyle && !alternateSide
                    ? position.StartsWith("left", StringComparison.Ordinal)
                        ? new CornerRadius(0, 25, 25, 0) : new CornerRadius(25, 0, 0, 25)
                    : new CornerRadius(0);
                if (corners != expectedCorners)
                    throw new InvalidOperationException("Compact side background rounded the screen-facing edge");
                if (alternateSide && (window.AlternateSideBackground.Visibility != Visibility.Visible ||
                    ((SolidColorBrush)window.AlternateSideBackground.Background).Color !=
                    Color.FromArgb(0xc0, 0x25, 0x23, 0x2a) ||
                    window.AlternateSideBackground.CornerRadius != (position == "left1"
                        ? new CornerRadius(0, 25, 25, 0) : new CornerRadius(25, 0, 0, 25))))
                    throw new InvalidOperationException("Alternate side background was not drawn behind the full-width player layer");
                if (!side && ((PlayerAvatar)((StackPanel)window.AvatarPanel.Children[0]).Children[0]).Width != 60)
                    throw new InvalidOperationException("Horizontal overlay avatar width must remain 60px");
            }
            Console.WriteLine("[PASS] Overlay 4/10/15-player viewport sizing, compact VAD count, horizontal size and six-position background layers");
        }
        finally { window.Close(); }
    }

    private static void VerifyMeetingSnapshot()
    {
        var window = new OverlayWindow(0, new ClientSettings { EnableOverlay = true, MeetingOverlay = true })
            { Width = 1280, Height = 720 };
        try
        {
            var player = new Player { Id = 2, ClientId = 22, ColorId = 1, IsLocal = true };
            var state = new AmongUsState { GameState = GameState.Discussion, Players = [player],
                PlayerColors = [new() { Main = 0x000000ff }, new() { Main = 0x00ff0000 }] };
            var peers = new Dictionary<int, OverlayPeerStatus>();
            window.Update(state, peers, false, false, false);
            var slot = (MeetingVoiceBorder)window.MeetingCanvas.Children[0];
            Color Tint() => slot.ShadowColor;
            player.ColorId = 0; player.ClientId = 23; player.IsDead = true; player.IsLocal = false;
            state.Players.Add(new Player { Id = 1, ClientId = 99 });
            window.Update(state, peers, true, false, false);
            if (window.MeetingCanvas.Children.Count != 1 || !ReferenceEquals(slot, window.MeetingCanvas.Children[0]) ||
                Tint() != Colors.Blue || window.meetingOrder[0] != new MeetingParticipant(2, 22, 1, true))
                throw new InvalidOperationException("Meeting participant snapshot changed with live player updates");
            state.Mod = AmongUsModType.NebulaOnTheShip;
            player.NosPlayer = new NosPlayerData { ColorR = 0.5 / 255, ColorG = 1, ColorB = 0 };
            window.Update(state, peers, false, false, false);
            if (Tint() != Color.FromRgb(1, 255, 0)) throw new InvalidOperationException("Meeting NoS color did not update with JS byte rounding");
            state.Players.Remove(player);
            window.Update(state, peers, false, false, false);
            if (Tint() != Colors.Blue || window.MeetingCanvas.Children.Count != 1)
                throw new InvalidOperationException("Missing live player must retain its frozen meeting slot/palette ID");
            state.PlayerColors.Clear();
            window.Update(state, peers, false, false, false);
            if (Tint() != Color.FromRgb(0xc5, 0x11, 0x11))
                throw new InvalidOperationException("Missing meeting palette entry must use released red fallback");
            Console.WriteLine("[PASS] Meeting snapshot identity/palette, live NoS RGB, missing participant and red fallback");
        }
        finally { window.Close(); }
    }

    protected override void OnClosed(EventArgs e)
    {
        placementTimer.Stop();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
