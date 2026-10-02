using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

/// <summary>Click-through, game-bound counterpart of 3.2.7 Overlay.tsx.</summary>
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
    private bool microphoneMuted;
    private bool deafened;
    private List<int> meetingOrder = [];
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
        bool isLocalTalking, bool isMicrophoneMuted, bool isDeafened)
    {
        game = gameState;
        peers = peerStatuses;
        localTalking = isLocalTalking;
        microphoneMuted = isMicrophoneMuted;
        deafened = isDeafened;
        if (gameState?.GameState == GameState.Discussion &&
            previousGameState != GameState.Discussion)
        {
            meetingOrder = gameState.Players.OrderBy(player => player.Disconnected || player.IsDead)
                .ThenBy(player => player.Id).Select(player => player.Id).ToList();
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
        WatermarkTitle.Text = $"TanukiBCL v3.2.7 .NET{(mod.Length > 0 ? $" [{mod}]" : "")}";
        WatermarkServer.Text = settings.ServerUrl;
        WatermarkTitle.TextAlignment = isTasks ? TextAlignment.Center : TextAlignment.Left;
        WatermarkServer.TextAlignment = WatermarkTitle.TextAlignment;
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
        AvatarPanel.Children.Clear();
        var position = settings.OverlayPosition;
        var selected = OverlaySelection.Select(state, peers, localTalking,
            microphoneMuted, settings.CompactOverlay);
        if (position == "hidden" || selected.Count == 0)
        {
            AvatarBackground.Visibility = Visibility.Collapsed;
            return;
        }
        var side = position is "left" or "left1" or "right" or "right1";
        var compact = settings.CompactOverlay || position is "left1" or "right1";
        var showName = !state.MixupSabotaged && side && (!settings.CompactOverlay || position is "left1" or "right1");
        var avatarSize = side ? Math.Min(0.075d * Height, 72d) : 60d;
        AvatarPanel.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
        AvatarPanel.MaxHeight = side ? Height : double.PositiveInfinity;
        AvatarPanel.MaxWidth = side ? (compact ? avatarSize + 16d : 300d) : 800d;
        AvatarBackground.Background = compact ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(position == "bottom_left" ? (byte)0x59 : (byte)0x80,
                0x25, 0x23, 0x2a));
        AvatarBackground.Padding = compact ? new Thickness(3) : new Thickness(8);
        foreach (var entry in selected)
        {
            var player = entry.Player;
            var row = new StackPanel
            {
                Orientation = side ? Orientation.Horizontal : Orientation.Vertical,
                Margin = new Thickness(side ? 1d : 5d)
            };
            var avatar = new PlayerAvatar { Width = avatarSize, Height = avatarSize };
            // Overlay.tsx displays the current outfit; only the main voice view
            // opts into hiding visibly changed avatars during Tasks.
            avatar.SetPlayer(player, state.PlayerColors, false, state.Mod, state.GameExecutablePath);
            avatar.SetOverlayMode(lookLeft: position is not ("left" or "left1" or "bottom_left"),
                clipEquipment: side && !showName, showBorder: side && !settings.CompactOverlay);
            avatar.SetVisualState(entry.Talking,
                player.IsLocal && microphoneMuted,
                player.IsLocal && deafened, "connected", entry.UsingRadio,
                grayTalking: player.IsLocal && player.ShiftedColor != -1 && state.GameState != GameState.Discussion);
            row.Children.Add(avatar);
            if (showName && (position is not ("left1" or "right1") || entry.Talking))
            {
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(player.AppearanceName)
                        ? player.Name : player.AppearanceName,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 190,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromArgb(0x52, 0, 0, 0)),
                    Margin = new Thickness(5, 0, 5, 0)
                });
            }
            AvatarPanel.Children.Add(row);
        }
        AvatarBackground.Visibility = Visibility.Visible;
        AvatarBackground.Width = double.NaN;
        AvatarBackground.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var regionWidth = side ? compact ? avatarSize + 24d : 300d
            : position == "top" ? Math.Min(800d, Width) :
            Math.Min(AvatarBackground.DesiredSize.Width, Width);
        AvatarBackground.Width = regionWidth;
        AvatarBackground.Measure(new Size(regionWidth, double.PositiveInfinity));
        if (side)
        {
            Canvas.SetLeft(AvatarBackground, position.StartsWith("right", StringComparison.Ordinal)
                ? Math.Max(0, Width - regionWidth) : 0d);
            Canvas.SetTop(AvatarBackground, 0d);
        }
        else if (position == "top")
        {
            Canvas.SetLeft(AvatarBackground, Math.Max(0, (Width - regionWidth) / 2d));
            Canvas.SetTop(AvatarBackground, 0d);
        }
        else
        {
            Canvas.SetLeft(AvatarBackground, 0d);
            Canvas.SetTop(AvatarBackground, Math.Max(0, Height - AvatarBackground.DesiredSize.Height));
        }
    }

    private void RenderMeeting(AmongUsState state)
    {
        MeetingCanvas.Children.Clear();
        if (!settings.MeetingOverlay || state.GameState != GameState.Discussion ||
            meetingOrder.Count == 0)
        {
            MeetingCanvas.Visibility = Visibility.Collapsed;
            return;
        }
        var ratioDifference = Math.Abs(Width / Height - 1.7d);
        var hudWidth = Width / (ratioDifference < 0.25d ? 1.192d :
            ratioDifference < 0.5d ? 1.146d : 1.591d);
        var hudHeight = hudWidth / 1.72d;
        MeetingCanvas.Width = hudWidth;
        MeetingCanvas.Height = hudHeight;
        Canvas.SetLeft(MeetingCanvas, (Width - hudWidth) / 2d);
        Canvas.SetTop(MeetingCanvas, (Height - hudHeight) / 2d);
        var slotWidth = hudWidth * 0.3d;
        var slotHeight = hudHeight * 0.109d;
        for (var index = 0; index < meetingOrder.Count; index++)
        {
            var player = state.Players.FirstOrDefault(candidate => candidate.Id == meetingOrder[index]);
            if (player is null) continue;
            peers.TryGetValue(player.ClientId, out var peer);
            var talking = player.IsLocal ? localTalking && !microphoneMuted : peer?.VoiceActive == true;
            var color = state.Mod == AmongUsModType.NebulaOnTheShip && player.NosPlayer is { } nos &&
                double.IsFinite(nos.ColorR) && double.IsFinite(nos.ColorG) && double.IsFinite(nos.ColorB)
                ? Color.FromRgb(ToByte(nos.ColorR), ToByte(nos.ColorG), ToByte(nos.ColorB))
                : AvatarImageFactory.GetSwatchColors(player.ColorId, state.PlayerColors).Main;
            var slot = new Border
            {
                Width = slotWidth, Height = slotHeight,
                CornerRadius = new CornerRadius(Math.Max(3d, hudHeight / 100d)),
                BorderThickness = new Thickness(2d),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x37, 0, 0, 0)),
                Background = Brushes.Transparent,
                Opacity = talking ? 1d : 0d,
                Effect = new DropShadowEffect
                {
                    Color = color, BlurRadius = Math.Max(5d, hudHeight / 50d),
                    ShadowDepth = 0d, Opacity = 0.95d
                }
            };
            Canvas.SetLeft(slot, hudWidth * (0.004d + index % 3 * 0.3263d));
            Canvas.SetTop(slot, hudHeight * (0.15d + index / 3 * 0.128d));
            MeetingCanvas.Children.Add(slot);
        }
        MeetingCanvas.Visibility = Visibility.Visible;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0d, 1d) * 255d);

    internal static void VerifyRender()
    {
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
                window.WatermarkTitle.Text.Length == 0)
                throw new InvalidOperationException("Overlay render smoke test failed");
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
                    ((PlayerAvatar)row.Children[0]).VerifyOverlayAppearance(mirrored, side && compact && !alternate, side && !compact);
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
        }
        finally
        {
            window.Close();
        }
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
