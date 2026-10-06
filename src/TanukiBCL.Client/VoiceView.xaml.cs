using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

/// <summary>
/// Compact voice screen structured after TanukiBCL v3.2.7 VoiceView.tsx.
/// The host supplies current game and voice snapshots; connection policy stays
/// in VoiceServerProbe rather than in this visual component.
/// </summary>
public partial class VoiceView : UserControl
{
    private static readonly Regex HtmlWhitespace = new("[ \\t\\r\\n\\f]+", RegexOptions.Compiled);
    private static readonly Geometry MicOff = Geometry.Parse(
        "M19 11h-1.7c0 .74-.16 1.43-.43 2.05l1.23 1.23c.56-.98.9-2.09.9-3.28m-4.02.17c0-.06.02-.11.02-.17V5c0-1.66-1.34-3-3-3S9 3.34 9 5v.18zM4.27 3 3 4.27l6.01 6.01V11c0 1.66 1.33 3 2.99 3 .22 0 .44-.03.65-.08l1.66 1.66c-.71.33-1.5.52-2.31.52-2.76 0-5.3-2.1-5.3-5.1H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c.91-.13 1.77-.45 2.54-.9L19.73 21 21 19.73z");
    private static readonly Geometry VolumeOff = Geometry.Parse(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63m2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71M4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9zM12 4 9.91 6.09 12 8.18z");
    private static readonly Geometry Mic = Geometry.Parse(
        "M12 14c1.66 0 2.99-1.34 2.99-3L15 5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3m5.3-3c0 3-2.54 5.1-5.3 5.1S6.7 14 6.7 11H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c3.28-.48 6-3.3 6-6.72z");
    private static readonly Geometry VolumeUp = Geometry.Parse(
        "M3 9v6h4l5 5V4L7 9zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02M14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77");
    private readonly Dictionary<int, PlayerAvatar> remoteAvatars = [];
    private readonly Dictionary<int, Player> displayedPlayers = [];
    private readonly Dictionary<int, bool> remoteDeadForDisplay = [];
    internal IReadOnlyDictionary<int, bool> RemoteDeadForDisplay => remoteDeadForDisplay;
    private GameState previousDeathDisplayState = GameState.Unknown;
    private readonly DispatcherTimer popupCloseTimer;
    private IReadOnlyDictionary<int, PlayerAudioConfig> playerConfigs = new Dictionary<int, PlayerAudioConfig>();
    private int? popupPlayerId;
    private bool updatingPopup;
    private bool popupDirty;
    private bool configuringLaunchPlatforms;
    private string uiLanguage = "ja";

    public VoiceView()
    {
        InitializeComponent();
        VersionText.Text = FormatVersionLabel(UpdateCatalog.CurrentVersion);
        VersionText.ToolTip = $"v{UpdateCatalog.CurrentVersion}";
        popupCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        popupCloseTimer.Tick += (_, _) =>
        {
            popupCloseTimer.Stop();
            if (PlayerConfigPopupContent.IsMouseOver ||
                PlayerConfigPopup.PlacementTarget is UIElement { IsMouseOver: true }) return;
            ClosePlayerConfigPopup();
        };
    }

    public event EventHandler? SettingsRequested;

    private static string CollapseNoWrapWhitespace(string value) =>
        HtmlWhitespace.Replace(value, " ").Trim(' ', '\t', '\r', '\n', '\f');

    internal static string FormatVersionLabel(string version)
    {
        var beta = Regex.Match(version, @"^(\d+\.\d+\.\d+)-net-beta\.(\d+)$");
        if (beta.Success) return $"v{beta.Groups[1].Value} β{beta.Groups[2].Value}";
        var development = Regex.Match(version, @"^(\d+\.\d+\.\d+)-netdev\.\d+$");
        if (development.Success) return $"v{development.Groups[1].Value} DEV";
        var stable = Regex.Match(version, @"^(\d+\.\d+\.\d+)-net\.\d+$");
        if (stable.Success) return $"v{stable.Groups[1].Value} NET";
        return $"v{version}";
    }

    internal static void VerifyNameLayout()
    {
        var view = new VoiceView();
        var codeTypeface = new Typeface(view.LobbyCode.FontFamily, FontStyles.Normal,
            FontWeights.Medium, FontStretches.Normal);
        if (!codeTypeface.TryGetGlyphTypeface(out var codeGlyphs) ||
            !codeGlyphs.FontUri.ToString().Contains("SourceCodePro-Medium.otf", StringComparison.OrdinalIgnoreCase) ||
            System.Windows.Application.GetResourceStream(new Uri(
                "/TanukiBCL.Net;component/Assets/Fonts/SourceCodePro-Medium.otf", UriKind.Relative)) is null)
            throw new InvalidOperationException(
                $"Released lobby-code font was not embedded or resolved: {codeGlyphs?.FontUri}");
        if (view.VersionText.Text != FormatVersionLabel(UpdateCatalog.CurrentVersion) ||
            (string?)view.VersionText.ToolTip != $"v{UpdateCatalog.CurrentVersion}" ||
            FormatVersionLabel("3.2.8-netdev.0") != "v3.2.8 DEV" ||
            FormatVersionLabel("3.2.8-net-beta.1") != "v3.2.8 β1" ||
            FormatVersionLabel("3.2.8-net.0") != "v3.2.8 NET")
            throw new InvalidOperationException("Voice window version/channel label is incorrect");
        if (CollapseNoWrapWhitespace("コヨーテ \r\nさぁ、狩りの時間だ") !=
            "コヨーテ さぁ、狩りの時間だ")
            throw new InvalidOperationException("TOH role-name line breaks were not collapsed like HTML nowrap");
        view.LocalName.Text = "開発者くれとし 3";
        view.Measure(new Size(280, 390));
        view.Arrange(new Rect(0, 0, 280, 390));
        view.UpdateLayout();
        if (view.LocalAvatarSlot.Width != 100 || view.LocalAvatarSlot.Height != 100 ||
            view.LocalAvatar.Width != 90 || view.LocalAvatar.Height != 90 ||
            view.HeaderPanel.Margin != new Thickness(0, -6, 0, 6) ||
            view.LocalName.FontWeight != FontWeights.SemiBold ||
            view.CodeBackground.Padding != new Thickness(9, 5, 9, 5))
            throw new InvalidOperationException("Compact header no longer matches the released avatar/name/code geometry");
        var nameTop = view.LocalName.TranslatePoint(new Point(), view).Y;
        var codeTop = view.CodeBackground.TranslatePoint(new Point(), view).Y;
        var dividerTop = view.HeaderDivider.TranslatePoint(new Point(), view).Y;
        if (nameTop is < 25 or > 34 || codeTop is < 54 or > 63 || dividerTop is < 120 or > 129)
            throw new InvalidOperationException($"Compact header shifted: name={nameTop:0.0}, code={codeTop:0.0}, divider={dividerTop:0.0}");
        if (view.SettingsButton.Content is not Canvas settingsIcon ||
            view.ReloadButton.Content is not Canvas reloadIcon ||
            view.CloseButton.Content is not Canvas closeIcon ||
            settingsIcon.Children.Count != 1 || reloadIcon.Children.Count != 1 || closeIcon.Children.Count != 1 ||
            view.SettingsButton.TranslatePoint(new Point(), view).X != 0 ||
            view.ReloadButton.TranslatePoint(new Point(), view).X != 24 ||
            view.CloseButton.TranslatePoint(new Point(), view).X != 256)
            throw new InvalidOperationException("Released compact title-bar icons or positions changed");
        Console.WriteLine($"VoiceView header geometry: name={nameTop:0.0}, code={codeTop:0.0}, divider={dividerTop:0.0}");
        if (view.LocalName.TextTrimming != TextTrimming.None || view.LocalName.ActualWidth <= 115 ||
            view.LocalName.FontSize != 20 || view.LocalName.TextWrapping != TextWrapping.NoWrap)
            throw new InvalidOperationException("Long player name was clipped, shrunk or ellipsized");
        // A Viewbox can measure its child at full width while applying a layout clip
        // to its own 115px slot. Check the ancestors, not just the text's width.
        for (DependencyObject? parent = view.LocalName; parent is not null && parent != view;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is FrameworkElement element &&
                System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element) is { } clip &&
                !clip.IsEmpty())
                throw new InvalidOperationException("Long player name has a clipped layout ancestor");
        }
        view.popupCloseTimer.Stop();
        Console.WriteLine("[PASS] VoiceView long name retains 20px nowrap text beyond 115px box");
    }
    internal static void VerifyModAndVersionNotices()
    {
        var view = new VoiceView();
        view.SetDetectedMod("Nebula on the Ship", new NosReadStatus(true, "NoS未取得: test", null));
        if (view.DetectedMod.Text != "MOD: Nebula on the Ship" || view.DetectedModStatus.Text != "NoS未取得: test" ||
            view.DetectedModStatus.Visibility != Visibility.Visible ||
            view.DetectedModBadge.Background is not SolidColorBrush { Color: { R: 0x58, G: 0x1e, B: 0x24 } })
            throw new InvalidOperationException("NoS read failure MOD badge differs from 3.2.9");
        view.SetDetectedMod("Nebula on the Ship", new NosReadStatus(false, "NoSスナップショットを自動更新中", 20261005));
        if (view.DetectedModStatus.Visibility != Visibility.Collapsed ||
            view.DetectedModBadge.ToolTip as string != "NoSスナップショットを自動更新中")
            throw new InvalidOperationException("Recovered NoS MOD badge kept the failure style");
        view.SetVersionWarning("ホストはv3.2.10です。");
        if (view.VersionWarningText.Visibility != Visibility.Visible) throw new InvalidOperationException("Version warning hidden");
        view.SetVersionWarning("");
        if (view.VersionWarningText.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Version warning stayed visible");
        Console.WriteLine("[PASS] 3.2.9 NoS failure MOD badge and app-version notice");
    }

    internal static void VerifyLaunchControls()
    {
        var view = new VoiceView();
        var platforms = new[]
        {
            new GameLaunchPlatform("STEAM", "Steam", "URI", "steam://rungameid/945360", [""]),
            new GameLaunchPlatform("custom", "NoS", "EXE", @"C:\Games\NoS", ["Among Us.exe"]),
            new GameLaunchPlatform("EPIC", "Epic Games", "URI", "com.epicgames.launcher://apps/test", [""])
        };
        string? selected = null;
        GameLaunchPlatform? requested = null;
        var browserRequested = false;
        view.LaunchPlatformChanged += key => selected = key;
        view.LaunchGameRequested += platform => requested = platform;
        view.PublicLobbyRequested += (_, _) => browserRequested = true;
        view.SetLaunchPlatforms(platforms, "custom");
        if (!Equals(view.LaunchPlatformCombo.SelectedItem, platforms[1]) || !view.LaunchGameButton.IsEnabled ||
            !Equals(view.LaunchGameButton.Content, "NoS") || view.LaunchDropdownItems.Children.Count != 4 ||
            selected is not null)
            throw new InvalidOperationException("Launch platform selection was not restored");
        view.LaunchPlatformCombo.SelectedItem = platforms[0];
        view.LaunchGameButton_Click(view.LaunchGameButton, new RoutedEventArgs());
        if (selected != "STEAM" || requested != platforms[0])
            throw new InvalidOperationException("Launch platform change or launch request was not sent");
        view.LaunchDropdownButton_Click(view.LaunchDropdownButton, new RoutedEventArgs());
        if (view.LaunchDropdownPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Launcher dropdown did not open");
        view.LaunchDropdownButton_Click(view.LaunchDropdownButton, new RoutedEventArgs());
        view.WaitingPanel.Visibility = Visibility.Visible;
        view.Measure(new Size(280, 390));
        view.Arrange(new Rect(0, 0, 280, 390));
        view.UpdateLayout();
        if (view.LaunchButtonGroup.ActualWidth > 150)
            throw new InvalidOperationException("Short game platform name stretches the launch button");
        view.LaunchPlatformCombo.SelectedItem = platforms[2];
        view.UpdateLayout();
        if (view.WaitingPanel.Parent is not Grid waitingArea)
            throw new InvalidOperationException("Game launcher has no waiting area");
        if (view.WaitingPanel.ActualHeight + view.WaitingPanel.Margin.Top > waitingArea.ActualHeight)
            throw new InvalidOperationException($"Game launcher overflows the 280×390 voice window: waiting={view.WaitingPanel.ActualHeight:0} available={waitingArea.ActualHeight:0}");
        if (view.PublicLobbyButton.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("The released waiting view does not show a public-lobby button");
        if (view.LaunchButtonGroup.ActualWidth <= 142 ||
            view.LaunchButtonGroup.ActualWidth > view.ActualWidth)
            throw new InvalidOperationException("Long game platform name is clipped or overflows the voice window");
        view.PublicLobbyButton_Click(view.PublicLobbyButton, new RoutedEventArgs());
        view.SetLanguage("en");
        if (!browserRequested || !Equals(view.PublicLobbyButton.Content,
                UiLocalization.Translate("en", "buttons.public_lobby")))
            throw new InvalidOperationException("Public lobby entry or localized label is missing");
        view.popupCloseTimer.Stop();
        Console.WriteLine("[PASS] VoiceView restores game launcher choice and emits launch request");
    }
    public event EventHandler? ReloadRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? MuteRequested;
    public event EventHandler? DeafenRequested;
    public event EventHandler? HelpRequested;
    public event EventHandler? PublicLobbyRequested;
    internal event Action<string>? LaunchPlatformChanged;
    internal event Action<GameLaunchPlatform>? LaunchGameRequested;
    internal event EventHandler? AddCustomGameRequested;
    internal event Action<GameLaunchPlatform>? EditCustomGameRequested;
    public event Action<int, PlayerAudioConfig, bool>? PlayerConfigChanged;

    internal void SetLanguage(string language)
    {
        uiLanguage = language;
        WaitingTitle.Text = UiLocalization.Translate(language, "game.waiting");
        LaunchPresetLabel.Text = UiLocalization.Translate(language, "game.open");
        PublicLobbyButton.Content = UiLocalization.Translate(language, "buttons.public_lobby");
        RebuildLaunchDropdown();
    }

    internal void SetLaunchPlatforms(IReadOnlyList<GameLaunchPlatform> platforms, string selectedKey)
    {
        configuringLaunchPlatforms = true;
        try
        {
            LaunchPlatformCombo.ItemsSource = platforms;
            LaunchPlatformCombo.SelectedItem = platforms.FirstOrDefault(platform => platform.Key == selectedKey)
                ?? platforms.FirstOrDefault();
            LaunchGameButton.IsEnabled = LaunchPlatformCombo.SelectedItem is not null;
            LaunchGameButton.Content = (LaunchPlatformCombo.SelectedItem as GameLaunchPlatform)?.Name ?? "?";
            RebuildLaunchDropdown();
        }
        finally { configuringLaunchPlatforms = false; }
    }

    private void LaunchPlatformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (configuringLaunchPlatforms) return;
        LaunchGameButton.IsEnabled = LaunchPlatformCombo.SelectedItem is not null;
        LaunchGameButton.Content = (LaunchPlatformCombo.SelectedItem as GameLaunchPlatform)?.Name ?? "?";
        if (LaunchPlatformCombo.SelectedItem is GameLaunchPlatform platform)
            LaunchPlatformChanged?.Invoke(platform.Key);
    }

    private void RebuildLaunchDropdown()
    {
        LaunchDropdownItems.Children.Clear();
        if (LaunchPlatformCombo.ItemsSource is IEnumerable<GameLaunchPlatform> platforms)
        {
            foreach (var platform in platforms)
            {
                var item = CreateLaunchMenuItem(platform.Name);
                item.Tag = platform;
                item.Click += LaunchMenuItem_Click;
                if (!platform.IsDefault)
                {
                    item.ToolTip = "右クリックで編集／削除";
                    item.MouseRightButtonUp += LaunchMenuItem_MouseRightButtonUp;
                }
                LaunchDropdownItems.Children.Add(item);
            }
        }
        var addItem = CreateLaunchMenuItem(UiLocalization.Translate(uiLanguage, "platform.custom"));
        addItem.Click += (_, _) =>
        {
            LaunchDropdownPanel.Visibility = Visibility.Collapsed;
            AddCustomGameRequested?.Invoke(this, EventArgs.Empty);
        };
        LaunchDropdownItems.Children.Add(addItem);
    }

    private static Button CreateLaunchMenuItem(string content) => new()
    {
        Content = content,
        Height = 32,
        MinWidth = 140,
        Padding = new Thickness(8, 0, 8, 0),
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x27)),
        Foreground = Brushes.White,
        HorizontalContentAlignment = HorizontalAlignment.Left
    };

    private void LaunchMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameLaunchPlatform platform })
            LaunchPlatformCombo.SelectedItem = platform;
        LaunchDropdownPanel.Visibility = Visibility.Collapsed;
    }

    private void LaunchMenuItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: GameLaunchPlatform { IsDefault: false } platform })
        {
            LaunchDropdownPanel.Visibility = Visibility.Collapsed;
            EditCustomGameRequested?.Invoke(platform);
            e.Handled = true;
        }
    }

    private void LaunchDropdownButton_Click(object sender, RoutedEventArgs e) =>
        LaunchDropdownPanel.Visibility = LaunchDropdownPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void VoiceView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (LaunchDropdownPanel.Visibility == Visibility.Visible &&
            !LaunchDropdownPanel.IsMouseOver && !LaunchDropdownButton.IsMouseOver)
            LaunchDropdownPanel.Visibility = Visibility.Collapsed;
    }

    private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchPlatformCombo.SelectedItem is GameLaunchPlatform platform)
            LaunchGameRequested?.Invoke(platform);
    }

    public void Update(AmongUsState? game, bool connected, bool localTalking, bool muted,
        bool deafened, IReadOnlyDictionary<int, VoicePlayerStatus> peers, bool hideCode = false,
        bool localUsingRadio = false, IReadOnlyDictionary<int, PlayerAudioConfig>? playerConfigs = null,
        ConnectionQuality? serverQuality = null)
    {
        this.playerConfigs = playerConfigs ?? new Dictionary<int, PlayerAudioConfig>();
        var local = game?.Players.FirstOrDefault(player => player.IsLocal);
        // Released VoiceView keeps the lobby code and mute controls visible on
        // the end-game screen even while Among Us temporarily exposes no local
        // player. Only the avatar/name/remote roster depend on that player.
        var lobbyDetected = game is not null && !string.IsNullOrWhiteSpace(game.LobbyCode) &&
            game.LobbyCode != "MENU" && game.GameState is not (GameState.Menu or GameState.Unknown);
        var hasLocal = lobbyDetected && local is not null;
        WaitingPanel.Visibility = lobbyDetected ? Visibility.Collapsed : Visibility.Visible;
        OtherPlayersScroll.Visibility = hasLocal ? Visibility.Visible : Visibility.Collapsed;
        LobbyHeader.Visibility = lobbyDetected ? Visibility.Visible : Visibility.Collapsed;
        LocalAvatarSlot.Visibility = hasLocal ? Visibility.Visible : Visibility.Collapsed;
        LocalAvatarColumn.Width = new GridLength(hasLocal ? 96d : 0d);
        LocalNameSlot.Visibility = hasLocal ? Visibility.Visible : Visibility.Collapsed;
        CodeBackground.Margin = hasLocal ? new Thickness(0, 5, 0, 0) : new Thickness(0);
        HeaderPanel.Margin = hasLocal ? new Thickness(0, -6, 0, 6) :
            lobbyDetected ? new Thickness(0, 6, 0, 6) : new Thickness(0);
        if (lobbyDetected && game is not null)
        {
            LobbyCode.Text = hideCode ? "LOBBY" : game.LobbyCode;
            MuteIcon.Data = muted || deafened ? MicOff : Mic;
            DeafenIcon.Data = deafened ? VolumeOff : VolumeUp;
            MuteButton.ToolTip = muted || deafened ? "マイクミュート解除" : "マイクをミュート";
            DeafenButton.ToolTip = deafened ? "スピーカーミュート解除" : "スピーカーをミュート";
        }
        if (!hasLocal || local is null || game is null)
        {
            remoteDeadForDisplay.Clear();
            previousDeathDisplayState = GameState.Unknown;
            SetFooterVisible(true);
            ClosePlayerConfigPopup();
            OtherPlayersPanel.Children.Clear();
            remoteAvatars.Clear();
            displayedPlayers.Clear();
            return;
        }

        // Released VoiceController changes otherDead only on a game-state
        // transition: entering Lobby clears it; entering Discussion captures it.
        // Audio policy continues to use the live death bit.
        if (game.GameState != previousDeathDisplayState)
        {
            previousDeathDisplayState = game.GameState;
            if (game.GameState == GameState.Lobby)
                remoteDeadForDisplay.Clear();
            else if (game.GameState != GameState.Tasks)
                foreach (var player in game.Players)
                    remoteDeadForDisplay[player.ClientId] = player.IsDead || player.Disconnected;
        }

        var hideAppearance = game.GameState == GameState.Tasks;
        LocalName.Text = CollapseNoWrapWhitespace(
            string.IsNullOrWhiteSpace(local.AppearanceName) ? local.Name : local.AppearanceName);
        LocalAvatar.SetPlayer(local, game.PlayerColors, hideAppearance, game.Mod, game.GameExecutablePath);
        LocalAvatar.SetVisualState(localTalking && (local.ShiftedColor < 0 || game.GameState == GameState.Discussion), muted, deafened,
            connected ? "connected" : "disconnected", localUsingRadio, serverQuality);

        var others = game.Players.Where(player => !player.IsLocal).ToArray();
        SetFooterVisible(others.Length <= 6);
        if (popupPlayerId is int activePlayerId && others.All(player => player.Id != activePlayerId))
            ClosePlayerConfigPopup();
        displayedPlayers.Clear();
        foreach (var player in others) displayedPlayers[player.Id] = player;
        var perRow = others.Length <= 9 ? 3 : Math.Min(12, (int)Math.Ceiling(Math.Sqrt(others.Length)));
        var avatarSize = 225d / perRow - 8d;
        foreach (var stale in remoteAvatars.Keys.Where(id => others.All(player => player.Id != id)).ToArray())
        {
            OtherPlayersPanel.Children.Remove(remoteAvatars[stale]);
            remoteAvatars.Remove(stale);
        }

        foreach (var player in others)
        {
            if (!remoteAvatars.TryGetValue(player.Id, out var avatar))
            {
                avatar = new PlayerAvatar { Margin = new Thickness(4) };
                avatar.Tag = player.Id;
                avatar.MouseEnter += PlayerAvatar_MouseEnter;
                avatar.MouseLeave += PlayerAvatar_MouseLeave;
                remoteAvatars.Add(player.Id, avatar);
                OtherPlayersPanel.Children.Add(avatar);
            }

            avatar.Width = avatarSize;
            avatar.Height = avatarSize;
            avatar.SetPlayer(player, game.PlayerColors, hideAppearance, game.Mod, game.GameExecutablePath,
                remoteDeadForDisplay.TryGetValue(player.ClientId, out var displayDead) && displayDead);
            var status = !player.Disconnected && peers.TryGetValue(player.ClientId, out var snapshot)
                ? snapshot
                : VoicePlayerStatus.Disconnected;
            var config = PlayerAudioConfig.For(player, this.playerConfigs);
            avatar.SetVisualState(status.Talking && !player.InVent &&
                (player.ShiftedColor < 0 || game.GameState == GameState.Discussion), false,
                config.IsMuted || config.Volume == 0d, status.ConnectionState,
                status.UsingRadio && !player.Disconnected && !player.Bugged,
                ResolvePeerQuality(status.Quality, serverQuality), bugged: player.Bugged);
        }
    }

    private void SetFooterVisible(bool visible)
    {
        FooterBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FooterRow.Height = new GridLength(visible ? 52d : 0d);
    }

    internal static ConnectionQuality? ResolvePeerQuality(ConnectionQuality? peer, ConnectionQuality? server)
    {
        // The upstream peer sampler adds the current Engine.IO ping to each
        // peer's RTC sample. Keep measured peer RTT/jitter/loss if available.
        var serverPing = server?.ServerPingMs;
        return peer is not null
            ? serverPing is not null ? peer with { ServerPingMs = serverPing } : peer
            : serverPing is not null ? new ConnectionQuality(ServerPingMs: serverPing) : null;
    }

    internal static void VerifyPeerQualityFallback()
    {
        var server = new ConnectionQuality(ServerPingMs: 25d);
        var fallback = ResolvePeerQuality(null, server);
        if (fallback?.Bars != 3 || fallback.ServerPingMs != 25d)
            throw new InvalidOperationException("Connected peer did not inherit the measured server ping.");
        var measured = new ConnectionQuality(RttMs: 230d, JitterMs: 35d, LossPercent: 4d, Direct: true);
        var merged = ResolvePeerQuality(measured, server);
        if (merged?.RttMs != 230d || merged.JitterMs != 35d || merged.LossPercent != 4d ||
            !merged.Direct || merged.ServerPingMs != 25d || merged.Bars != 2)
            throw new InvalidOperationException("Server ping overwrote the peer's RTC measurements.");
        if (ResolvePeerQuality(null, null) is not null)
            throw new InvalidOperationException("Unmeasured quality was fabricated.");
    }

    internal static void VerifyDuplicateClientAvatars()
    {
        var view = new VoiceView();
        var game = new AmongUsState
        {
            GameState = GameState.Tasks,
            LobbyCode = "ABCDEF",
            Players =
            [
                new Player { Id = 1, ClientId = 3, IsLocal = true, Name = "Local" },
                new Player { Id = 0, ClientId = 2, Name = "Host" },
                new Player { Id = 2, ClientId = 2, Name = "Left 1", Disconnected = true, Bugged = true },
                new Player { Id = 3, ClientId = 2, Name = "Left 2", Disconnected = true, Bugged = true }
            ]
        };
        var peers = new Dictionary<int, VoicePlayerStatus>
        {
            [2] = new("connected", false, true, new ConnectionQuality(ServerPingMs: 25d))
        };
        view.Update(game, true, false, false, false, peers);
        if (view.OtherPlayersPanel.Children.Count != 3 || view.remoteAvatars.Count != 3 ||
            view.displayedPlayers.Count != 3 ||
            view.remoteAvatars[0].HasBuggedBadge || !view.remoteAvatars[0].HasGoodQualityBars ||
            !view.remoteAvatars[2].HasBuggedBadge || view.remoteAvatars[2].HasGoodQualityBars ||
            !view.remoteAvatars[3].HasBuggedBadge || view.remoteAvatars[3].HasGoodQualityBars)
            throw new InvalidOperationException("Disconnected players sharing a host client ID were merged or shown as connected");
        game.Players[2].Disconnected = false;
        view.Update(game, true, false, false, false, peers);
        if (!view.remoteAvatars[2].HasBuggedBadge || !view.remoteAvatars[2].HasGoodQualityBars ||
            view.remoteAvatars[2].IsRadioBadgeVisible)
            throw new InvalidOperationException("An active bugged player lost quality or received a radio badge");
        game.Players[2].Disconnected = true;
        game.Players.RemoveAll(player => player.Disconnected);
        view.Update(game, true, false, false, false, peers);
        if (view.OtherPlayersPanel.Children.Count != 1 || !view.remoteAvatars.ContainsKey(0))
            throw new InvalidOperationException("Stale disconnected avatars remained after roster cleanup");
        for (var id = 4; id <= 9; id++)
            game.Players.Add(new Player { Id = id, ClientId = id + 10, Name = $"Guest {id}" });
        view.Update(game, true, false, false, false, peers);
        if (view.OtherPlayersPanel.Children.Count != 7 ||
            view.FooterBar.Visibility != Visibility.Collapsed || view.FooterRow.Height.Value != 0d)
            throw new InvalidOperationException("Footer still uses space with seven remote players");
        game.Players.RemoveAt(game.Players.Count - 1);
        view.Update(game, true, false, false, false, peers);
        if (view.OtherPlayersPanel.Children.Count != 6 ||
            view.FooterBar.Visibility != Visibility.Visible || view.FooterRow.Height.Value != 52d)
            throw new InvalidOperationException("Footer did not return with six remote players");
        game.GameState = GameState.Lobby;
        game.Players.Clear();
        view.Update(game, true, false, true, false, peers);
        if (view.WaitingPanel.Visibility != Visibility.Collapsed ||
            view.LobbyHeader.Visibility != Visibility.Visible ||
            view.LocalAvatarSlot.Visibility != Visibility.Collapsed ||
            view.LocalNameSlot.Visibility != Visibility.Collapsed ||
            view.LocalAvatarColumn.Width.Value != 0d ||
            view.OtherPlayersScroll.Visibility != Visibility.Collapsed ||
            view.OtherPlayersPanel.Children.Count != 0 ||
            view.LobbyCode.Text != "ABCDEF" ||
            (string?)view.MuteButton.ToolTip != "マイクミュート解除")
            throw new InvalidOperationException("End-game roster gap showed the launcher instead of the released code-only lobby");
        game.Players.Add(new Player { Id = 1, ClientId = 3, IsLocal = true, Name = "Local" });
        view.Update(game, true, false, true, false, peers);
        if (view.LocalAvatarSlot.Visibility != Visibility.Visible ||
            view.LocalNameSlot.Visibility != Visibility.Visible ||
            view.LocalAvatarColumn.Width.Value != 96d ||
            view.LocalName.Text != "Local")
            throw new InvalidOperationException("Normal lobby header did not return after the roster recovered");
        game.GameState = GameState.Menu;
        view.Update(game, true, false, false, false, peers);
        if (view.FooterBar.Visibility != Visibility.Visible || view.FooterRow.Height.Value != 52d)
            throw new InvalidOperationException("Footer did not return on the game-waiting screen");
        view.SetError("Connection failed");
        if (view.ErrorPanel.Visibility != Visibility.Visible ||
            view.ErrorMessage.Text != "Connection failed" ||
            view.HeaderPanel.Visibility != Visibility.Collapsed ||
            view.HeaderDivider.Visibility != Visibility.Collapsed ||
            view.GameContent.Visibility != Visibility.Collapsed ||
            view.FooterBar.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Error view does not match the released layout");
        view.SetError(null);
        if (view.ErrorPanel.Visibility != Visibility.Collapsed ||
            view.HeaderPanel.Visibility != Visibility.Visible ||
            view.HeaderDivider.Visibility != Visibility.Visible ||
            view.GameContent.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Error view did not restore normal content");
        view.SetWarnings("dead only", "meeting ghost only");
        if (view.WarningText.Visibility != Visibility.Visible ||
            view.SecondWarningText.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Both independent lobby warnings must be visible");
        view.SetWarnings(null, null);
        if (view.WarningText.Visibility != Visibility.Collapsed ||
            view.SecondWarningText.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Lobby warnings did not clear");
        view.popupCloseTimer.Stop();
        Console.WriteLine("[PASS] VoiceView keeps duplicate-client avatars distinct and matches footer/error states");
    }

    internal static void VerifyRemoteDeathPresentation()
    {
        var view = new VoiceView();
        var local = new Player { Id = 0, ClientId = 10, IsLocal = true, Name = "Local" };
        var remote = new Player { Id = 1, ClientId = 11, Name = "Remote" };
        var game = new AmongUsState
        {
            GameState = GameState.Lobby,
            LobbyCode = "ABCDEF",
            Players = [local, remote]
        };
        var peers = new Dictionary<int, VoicePlayerStatus>();
        view.Update(game, true, false, false, false, peers);
        if (view.remoteAvatars[remote.Id].IsGhostVisual)
            throw new InvalidOperationException("Lobby avatar started as a ghost");

        game.GameState = GameState.Tasks;
        remote.IsDead = true;
        local.IsDead = true;
        view.Update(game, true, false, false, false, peers);
        if (view.remoteAvatars[remote.Id].IsGhostVisual || !view.LocalAvatar.IsGhostVisual)
            throw new InvalidOperationException("Tasks avatar death presentation differs from released VoiceController");

        game.GameState = GameState.Discussion;
        view.Update(game, true, false, false, false, peers);
        if (!view.remoteAvatars[remote.Id].IsGhostVisual)
            throw new InvalidOperationException("Discussion did not reveal the remote ghost avatar");

        remote.IsDead = false;
        view.Update(game, true, false, false, false, peers);
        if (!view.remoteAvatars[remote.Id].IsGhostVisual)
            throw new InvalidOperationException("Remote death snapshot changed without a state transition");
        remote.IsDead = true;

        game.GameState = GameState.Tasks;
        view.Update(game, true, false, false, false, peers);
        if (!view.remoteAvatars[remote.Id].IsGhostVisual)
            throw new InvalidOperationException("Remote ghost presentation was lost after discussion");

        game.GameState = GameState.Lobby;
        view.Update(game, true, false, false, false, peers);
        if (view.remoteAvatars[remote.Id].IsGhostVisual)
            throw new InvalidOperationException("New lobby did not clear the remote death snapshot");
        view.popupCloseTimer.Stop();
        Console.WriteLine("[PASS] Remote death avatar timing matches released Tasks/Discussion/Lobby transitions");
    }

    public void SetError(string? error)
    {
        var hasError = !string.IsNullOrWhiteSpace(error);
        ErrorMessage.Text = error ?? string.Empty;
        ErrorPanel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        HeaderPanel.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
        HeaderDivider.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
        GameContent.Visibility = hasError ? Visibility.Collapsed : Visibility.Visible;
        if (hasError) ClosePlayerConfigPopup();
    }

    public void SetWarnings(string? first, string? second)
    {
        WarningText.Text = first ?? string.Empty;
        WarningText.Visibility = string.IsNullOrWhiteSpace(first) ? Visibility.Collapsed : Visibility.Visible;
        SecondWarningText.Text = second ?? string.Empty;
        SecondWarningText.Visibility = string.IsNullOrWhiteSpace(second) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetDetectedMod(string? mod, NosReadStatus? nosStatus = null)
    {
        DetectedMod.Text = string.IsNullOrWhiteSpace(mod) ? string.Empty : $"MOD: {mod}";
        DetectedModBadge.Visibility = string.IsNullOrWhiteSpace(mod) ? Visibility.Collapsed : Visibility.Visible;
        // 3.2.9: a NoS match without player data turns the badge dark red and shows the reason.
        var failed = nosStatus?.Failed == true;
        DetectedModBadge.Background = failed
            ? new SolidColorBrush(Color.FromRgb(0x58, 0x1e, 0x24))
            : new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0));
        DetectedModBadge.MaxWidth = failed ? 360 : 268;
        DetectedModBadge.ToolTip = nosStatus?.Message;
        DetectedModStatus.Text = failed ? nosStatus!.Message : string.Empty;
        DetectedModStatus.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetVersionWarning(string? warning)
    {
        VersionWarningText.Text = warning ?? string.Empty;
        VersionWarningText.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void MuteButton_Click(object sender, RoutedEventArgs e) => MuteRequested?.Invoke(this, EventArgs.Empty);
    private void DeafenButton_Click(object sender, RoutedEventArgs e) => DeafenRequested?.Invoke(this, EventArgs.Empty);
    private void HelpButton_Click(object sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, EventArgs.Empty);
    private void PublicLobbyButton_Click(object sender, RoutedEventArgs e) => PublicLobbyRequested?.Invoke(this, EventArgs.Empty);

    private void PlayerAvatar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not PlayerAvatar { Tag: int playerId } avatar ||
            !displayedPlayers.TryGetValue(playerId, out var player)) return;
        popupCloseTimer.Stop();
        if (popupDirty && popupPlayerId != playerId) PersistPlayerVolume();
        popupPlayerId = playerId;
        PlayerConfigPopup.PlacementTarget = avatar;
        PlayerConfigName.Text = string.IsNullOrWhiteSpace(player.AppearanceName) ? player.Name : player.AppearanceName;
        SetPopupVisual(PlayerAudioConfig.For(player, playerConfigs));
        popupDirty = false;
        PlayerConfigPopup.IsOpen = true;
    }

    private void PlayerAvatar_MouseLeave(object sender, MouseEventArgs e) => popupCloseTimer.Start();
    private void PlayerConfigPopup_MouseEnter(object sender, MouseEventArgs e) => popupCloseTimer.Stop();
    private void PlayerConfigPopup_MouseLeave(object sender, MouseEventArgs e) => popupCloseTimer.Start();

    private void ClosePlayerConfigPopup()
    {
        popupCloseTimer.Stop();
        if (popupDirty) PersistPlayerVolume();
        PlayerConfigPopup.IsOpen = false;
        popupPlayerId = null;
    }

    public void DismissPlayerConfigPopup() => ClosePlayerConfigPopup();

    private void SetPopupVisual(PlayerAudioConfig config)
    {
        updatingPopup = true;
        PlayerVolumeSlider.Value = config.Volume;
        PlayerVolumeText.Text = $"{Math.Floor(config.Volume * 100d)}%";
        PlayerMuteIcon.Data = config.IsMuted || config.Volume == 0d ? VolumeOff : VolumeUp;
        PlayerMuteButton.ToolTip = config.IsMuted ? "このプレイヤーのミュートを解除" : "このプレイヤーをミュート";
        updatingPopup = false;
    }

    private void PlayerMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (popupPlayerId is not int playerId || !displayedPlayers.TryGetValue(playerId, out var player)) return;
        var current = PlayerAudioConfig.For(player, playerConfigs);
        var config = current with { IsMuted = !current.IsMuted };
        popupDirty = false;
        PlayerConfigChanged?.Invoke(player.PlayerConfigId, config, true);
        SetPopupVisual(config);
    }

    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPopup || popupPlayerId is not int playerId ||
            !displayedPlayers.TryGetValue(playerId, out var player)) return;
        var config = PlayerAudioConfig.For(player, playerConfigs) with { Volume = PlayerVolumeSlider.Value };
        PlayerVolumeText.Text = $"{Math.Floor(config.Volume * 100d)}%";
        PlayerMuteIcon.Data = config.IsMuted || config.Volume == 0d ? VolumeOff : VolumeUp;
        popupDirty = true;
        PlayerConfigChanged?.Invoke(player.PlayerConfigId, config, false);
    }

    private void PlayerVolumeSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => PersistPlayerVolume();
    private void PlayerVolumeSlider_KeyUp(object sender, KeyEventArgs e) => PersistPlayerVolume();

    private void PersistPlayerVolume()
    {
        if (popupPlayerId is not int playerId || !displayedPlayers.TryGetValue(playerId, out var player)) return;
        var config = PlayerAudioConfig.For(player, playerConfigs) with { Volume = PlayerVolumeSlider.Value };
        popupDirty = false;
        PlayerConfigChanged?.Invoke(player.PlayerConfigId, config, true);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button || e.ClickCount != 1) return;
        Window.GetWindow(this)?.DragMove();
    }

    private static void OpenLink(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void GithubButton_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://github.com/kuretoshi/BetterCrewLink/tree/voice_fixed");

    private void ErrorSupportButton_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://discord.gg/4cpvp3KyhF");

    private void DiscordButton_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://discord.gg/jEyDrpBsmJ");

    private void KofiButton_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://ko-fi.com/kuretoshi");

    private void XButton_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://x.com/tanukibcl?s=11");
}

public sealed record VoicePlayerStatus(
    string ConnectionState,
    bool Talking,
    bool UsingRadio,
    ConnectionQuality? Quality = null)
{
    public static readonly VoicePlayerStatus Disconnected = new("disconnected", false, false);
}
