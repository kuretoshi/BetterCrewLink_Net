using System.Diagnostics;
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
    private readonly DispatcherTimer popupCloseTimer;
    private IReadOnlyDictionary<int, PlayerAudioConfig> playerConfigs = new Dictionary<int, PlayerAudioConfig>();
    private int? popupClientId;
    private bool updatingPopup;
    private bool popupDirty;

    public VoiceView()
    {
        InitializeComponent();
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
    public event EventHandler? ReloadRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? MuteRequested;
    public event EventHandler? DeafenRequested;
    public event EventHandler? HelpRequested;
    public event Action<int, PlayerAudioConfig, bool>? PlayerConfigChanged;

    public void Update(AmongUsState? game, bool connected, bool localTalking, bool muted,
        bool deafened, IReadOnlyDictionary<int, VoicePlayerStatus> peers, bool hideCode = false,
        bool localUsingRadio = false, IReadOnlyDictionary<int, PlayerAudioConfig>? playerConfigs = null,
        ConnectionQuality? serverQuality = null)
    {
        this.playerConfigs = playerConfigs ?? new Dictionary<int, PlayerAudioConfig>();
        var local = game?.Players.FirstOrDefault(player => player.IsLocal);
        var inLobby = local is not null && game is not null && !string.IsNullOrWhiteSpace(game.LobbyCode) &&
                      game.GameState is not (GameState.Menu or GameState.Unknown);
        WaitingPanel.Visibility = inLobby ? Visibility.Collapsed : Visibility.Visible;
        LobbyHeader.Visibility = inLobby ? Visibility.Visible : Visibility.Collapsed;
        if (!inLobby || local is null || game is null)
        {
            ClosePlayerConfigPopup();
            OtherPlayersPanel.Children.Clear();
            remoteAvatars.Clear();
            displayedPlayers.Clear();
            return;
        }

        LocalName.Text = local.Name;
        LobbyCode.Text = hideCode ? "LOBBY" : game.LobbyCode;
        LocalAvatar.SetPlayer(local, game.PlayerColors);
        LocalAvatar.SetVisualState(localTalking, muted, deafened,
            connected ? "connected" : "disconnected", localUsingRadio, serverQuality);
        MuteIcon.Data = muted || deafened ? MicOff : Mic;
        DeafenIcon.Data = deafened ? VolumeOff : VolumeUp;
        MuteButton.ToolTip = muted || deafened ? "マイクミュート解除" : "マイクをミュート";
        DeafenButton.ToolTip = deafened ? "スピーカーミュート解除" : "スピーカーをミュート";

        var others = game.Players.Where(player => !player.IsLocal).ToArray();
        if (popupClientId is int activeClientId && others.All(player => player.ClientId != activeClientId))
            ClosePlayerConfigPopup();
        displayedPlayers.Clear();
        foreach (var player in others) displayedPlayers[player.ClientId] = player;
        var perRow = others.Length <= 9 ? 3 : Math.Min(12, (int)Math.Ceiling(Math.Sqrt(others.Length)));
        var avatarSize = 225d / perRow - 8d;
        foreach (var stale in remoteAvatars.Keys.Where(id => others.All(player => player.ClientId != id)).ToArray())
        {
            OtherPlayersPanel.Children.Remove(remoteAvatars[stale]);
            remoteAvatars.Remove(stale);
        }

        foreach (var player in others)
        {
            if (!remoteAvatars.TryGetValue(player.ClientId, out var avatar))
            {
                avatar = new PlayerAvatar { Margin = new Thickness(4) };
                avatar.Tag = player.ClientId;
                avatar.MouseEnter += PlayerAvatar_MouseEnter;
                avatar.MouseLeave += PlayerAvatar_MouseLeave;
                remoteAvatars.Add(player.ClientId, avatar);
                OtherPlayersPanel.Children.Add(avatar);
            }

            avatar.Width = avatarSize;
            avatar.Height = avatarSize;
            avatar.SetPlayer(player, game.PlayerColors);
            var status = peers.TryGetValue(player.ClientId, out var snapshot)
                ? snapshot
                : VoicePlayerStatus.Disconnected;
            var config = PlayerAudioConfig.For(player, this.playerConfigs);
            avatar.SetVisualState(status.Talking && !player.InVent, false,
                config.IsMuted || config.Volume == 0d, status.ConnectionState, status.UsingRadio, status.Quality);
        }
    }

    public void SetWarning(string? warning)
    {
        WarningText.Text = warning ?? string.Empty;
        WarningText.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetDetectedMod(string? mod)
    {
        DetectedMod.Text = string.IsNullOrWhiteSpace(mod) ? string.Empty : $"MOD: {mod}";
        DetectedMod.Visibility = string.IsNullOrWhiteSpace(mod) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void MuteButton_Click(object sender, RoutedEventArgs e) => MuteRequested?.Invoke(this, EventArgs.Empty);
    private void DeafenButton_Click(object sender, RoutedEventArgs e) => DeafenRequested?.Invoke(this, EventArgs.Empty);
    private void HelpButton_Click(object sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, EventArgs.Empty);

    private void PlayerAvatar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not PlayerAvatar { Tag: int clientId } avatar ||
            !displayedPlayers.TryGetValue(clientId, out var player)) return;
        popupCloseTimer.Stop();
        if (popupDirty && popupClientId != clientId) PersistPlayerVolume();
        popupClientId = clientId;
        PlayerConfigPopup.PlacementTarget = avatar;
        PlayerConfigName.Text = player.Name;
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
        popupClientId = null;
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
        if (popupClientId is not int clientId || !displayedPlayers.TryGetValue(clientId, out var player)) return;
        var current = PlayerAudioConfig.For(player, playerConfigs);
        var config = current with { IsMuted = !current.IsMuted };
        popupDirty = false;
        PlayerConfigChanged?.Invoke(player.PlayerConfigId, config, true);
        SetPopupVisual(config);
    }

    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPopup || popupClientId is not int clientId ||
            !displayedPlayers.TryGetValue(clientId, out var player)) return;
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
        if (popupClientId is not int clientId || !displayedPlayers.TryGetValue(clientId, out var player)) return;
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
        OpenLink("https://github.com/kuretoshi/TanukiBCL");

    private void DiscordButton_Click(object sender, RoutedEventArgs e) =>
        OpenLink("https://discord.gg/jEyDrpBsmJ");
}

public sealed record VoicePlayerStatus(
    string ConnectionState,
    bool Talking,
    bool UsingRadio,
    ConnectionQuality? Quality = null)
{
    public static readonly VoicePlayerStatus Disconnected = new("disconnected", false, false);
}
