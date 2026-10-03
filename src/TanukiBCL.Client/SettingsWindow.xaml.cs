using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class SettingsWindow : Window
{
    private readonly ClientSettings settings;
    private readonly ObservableCollection<string> serverUrlDrafts = [];
    private bool lobbySettingsEditable;
    private readonly ClientSettingsTransaction settingsTransaction;
    private readonly DispatcherTimer lobbyCommitTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool settingsReady;
    internal event Action? DebugOpenRequested;
    private bool lobbyPending;
    private Action? pendingConfirmation;
    internal event Action<ClientSettingsChange>? SettingsApplied;
    internal event Action? SettingsReset;
    private LobbySettings? currentLobbySettings;
    private LobbySettings lobbyDraft;
    private LobbySettings? radioOnlyBackup;
    private bool loadingLobbyControls;
    private bool showingCurrentLobby;
    private string? obsSecretDraft;
    private Geometry? originalObsCopyIcon;
    private int obsCopyFeedbackVersion;
    private VoiceEffectPreviewSession? voiceEffectPreview;
    private MicrophoneLevelSession? microphoneLevelSession;
    private SpeakerTestSession? speakerTestSession;
    private AmongUsState? currentGameState;
    private readonly Action<int, PlayerAudioConfig, bool> onPlayerConfigChanged;
    private string playersSignature = string.Empty;
    private static readonly Geometry VolumeUp = Geometry.Parse(
        "M3 9v6h4l5 5V4L7 9zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02M14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77");
    private static readonly Geometry VolumeOff = Geometry.Parse(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63m2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71M4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9zM12 4 9.91 6.09 12 8.18z");

    internal SettingsWindow(ClientSettings settings, bool lobbySettingsEditable,
        LobbySettings? currentLobbySettings, bool preferCurrentLobby, AmongUsState? gameState,
        Action<int, PlayerAudioConfig, bool> onPlayerConfigChanged, Action<ClientSettings>? persistSettings = null)
    {
        InitializeComponent();
        InitializeUpdatePanel();
        this.settings = settings;
        settingsTransaction = new ClientSettingsTransaction(settings, persistSettings ?? ClientSettingsStore.Save);
        settingsTransaction.Changed += change => SettingsApplied?.Invoke(change);
        this.lobbySettingsEditable = lobbySettingsEditable;
        this.currentLobbySettings = currentLobbySettings;
        currentGameState = gameState;
        this.onPlayerConfigChanged = onPlayerConfigChanged;
        LanguageCombo.ItemsSource = UiLocalization.Languages;
        LanguageCombo.SelectedItem = UiLocalization.Languages.First(language => language.Code == settings.Language);
        lobbyDraft = settings.MyLobbySettings;
        radioOnlyBackup = settings.RadioOnlyBackup;
        MicrophoneCombo.ItemsSource = AudioDeviceSession.GetInputDevices();
        SpeakerCombo.ItemsSource = AudioDeviceSession.GetOutputDevices();
        MicrophoneCombo.SelectedItem = ((IEnumerable<AudioDeviceInfo>)MicrophoneCombo.ItemsSource)
            .FirstOrDefault(device => device.Name == settings.MicrophoneName) ?? MicrophoneCombo.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
        SpeakerCombo.SelectedItem = ((IEnumerable<AudioDeviceInfo>)SpeakerCombo.ItemsSource)
            .FirstOrDefault(device => device.Name == settings.SpeakerName) ?? SpeakerCombo.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
        OverlayAlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
        EnableOverlayCheck.IsChecked = settings.EnableOverlay;
        CompactOverlayCheck.IsChecked = settings.CompactOverlay;
        MeetingOverlayCheck.IsChecked = settings.MeetingOverlay;
        OverlayPositionCombo.SelectedItem = OverlayPositionCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => (string?)item.Tag == settings.OverlayPosition)
            ?? OverlayPositionCombo.Items[3];
        MasterVolumeSlider.Value = settings.MasterVolume;
        VoiceEffectStrengthSlider.Value = settings.VoiceEffectStrength;
        CrewVolumeAsGhostSlider.Value = settings.CrewVolumeAsGhost;
        GhostVolumeAsImpostorSlider.Value = settings.GhostVolumeAsImpostor;
        MicrophoneGainSlider.Value = settings.MicrophoneGain;
        MicrophoneGainCheck.IsChecked = settings.MicrophoneGainEnabled;
        MicSensitivitySlider.Value = 1d - settings.MicSensitivity;
        MicSensitivityCheck.IsChecked = settings.MicSensitivityEnabled;
        VoiceModeRadio.IsChecked = settings.PushToTalkMode == MicrophoneActivationMode.Voice;
        PushToTalkModeRadio.IsChecked = settings.PushToTalkMode == MicrophoneActivationMode.PushToTalk;
        PushToMuteModeRadio.IsChecked = settings.PushToTalkMode == MicrophoneActivationMode.PushToMute;
        PushToTalkShortcutBox.Text = settings.PushToTalkShortcut;
        ImpostorRadioShortcutBox.Text = settings.ImpostorRadioShortcut;
        MuteShortcutBox.Text = settings.MuteShortcut;
        DeafenShortcutBox.Text = settings.DeafenShortcut;
        foreach (var url in settings.ServerUrls.Append(settings.ServerUrl).Distinct(StringComparer.Ordinal))
            serverUrlDrafts.Add(url);
        ServerUrlBox.ItemsSource = serverUrlDrafts;
        ServerUrlBox.SelectedItem = settings.ServerUrl;
        ServerDialog.Confirmed += ConfirmServerUrl;
        ServerDialog.Dismissed += CloseServerDialog;
        NatFixCheck.IsChecked = settings.NatFix;
        MobileHostCheck.IsChecked = settings.MobileHost;
        SpatialAudioCheck.IsChecked = settings.EnableSpatialAudio;
        EchoCancellationCheck.IsChecked = settings.EchoCancellation;
        NoiseSuppressionCheck.IsChecked = settings.NoiseSuppression;
        AutoGainControlCheck.IsChecked = settings.AutoGainControl;
        OldSampleDebugCheck.IsChecked = settings.OldSampleDebug;
        HardwareAccelerationCheck.IsChecked = settings.HardwareAcceleration;
        ShowLobbyCodeCheck.IsChecked = !settings.HideCode;
        obsSecretDraft = settings.ObsSecret;
        ObsOverlayCheck.IsChecked = settings.ObsOverlay;
        UpdateObsUrl();
        LoadLobbyControls(lobbyDraft);
        if (currentLobbySettings is not null && preferCurrentLobby)
        {
            CurrentLobbyTab.IsChecked = true;
        }
        else
        {
            MyLobbyTab.IsChecked = true;
        }
        ShowSelectedLobbySettings();
        RenderPlayers();
        CategoryList.SelectedIndex = 0;
        UpdateVolumeLabels();
        InitializeImmediateSettings();
        UpdateResetAvailability();
        settingsReady = true;
        ApplyLanguage(settings.Language);
    }

    internal void UpdateCurrentLobbySettings(LobbySettings? settings)
    {
        currentLobbySettings = settings;
        if (showingCurrentLobby) ShowSelectedLobbySettings();
    }

    internal void UpdateCurrentGameState(AmongUsState? state)
    {
        currentGameState = state;
        UpdateResetAvailability();
        var wasEditable = lobbySettingsEditable;
        lobbySettingsEditable = state is not { IsHost: true, GameState: GameState.Tasks or GameState.Discussion };
        LobbyControlsPanel.IsEnabled = !showingCurrentLobby && lobbySettingsEditable;
        if (wasEditable != lobbySettingsEditable) ShowSelectedLobbySettings();
        UpdateModSettingsVisibility();
        UpdateShortcutLabels();
        var signature = string.Join(';', (state?.Players ?? []).Where(player => !player.IsLocal && !player.IsDummy)
            .OrderBy(player => player.ClientId)
            .Select(player => $"{player.ClientId}:{player.PlayerConfigId}:{player.Name}:{player.ColorId}:{NosColor.For(player)}:{player.Disconnected}"));
        if (signature == playersSignature) return;
        RenderPlayers();
    }

    private void RenderPlayers()
    {
        if (PlayerRows is null) return;
        PlayerRows.Children.Clear();
        var players = (currentGameState?.Players ?? []).Where(player => !player.IsLocal && !player.IsDummy)
            .OrderBy(player => player.ClientId).ToArray();
        playersSignature = string.Join(';', players.Select(player =>
            $"{player.ClientId}:{player.PlayerConfigId}:{player.Name}:{player.ColorId}:{NosColor.For(player)}:{player.Disconnected}"));
        if (players.Length == 0)
        {
            PlayerRows.Children.Add(new TextBlock
            {
                Text = PlayerText("empty"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xb7, 0xaa, 0xbd))
            });
            return;
        }

        foreach (var player in players) PlayerRows.Children.Add(CreatePlayerRow(player));
    }

    private string PlayerText(string key) => UiLocalization.Translate(settings.Language, $"settings.players.{key}");

    private Border CreatePlayerRow(Player player)
    {
        var (main, shadow) = AvatarImageFactory.GetSwatchColors(player.ColorId, currentGameState?.PlayerColors);
        if (AvatarImageFactory.GetNosColor(player) is { } nosColor) main = shadow = nosColor;
        var swatch = new Ellipse
        {
            Width = 12, Height = 12, Stroke = new SolidColorBrush(shadow), StrokeThickness = 1,
            Fill = player.ColorId == AvatarImageFactory.RainbowColorId
                ? new LinearGradientBrush([new GradientStop(Colors.Red, 0),
                    new GradientStop(Colors.Yellow, 0.25), new GradientStop(Colors.Lime, 0.5),
                    new GradientStop(Colors.DeepSkyBlue, 0.75), new GradientStop(Colors.Purple, 1)], 0)
                : new SolidColorBrush(main),
            Margin = new Thickness(0, 0, 7, 0)
        };
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(swatch);
        heading.Children.Add(new TextBlock { Text = player.Name, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 275 });
        if (player.Disconnected)
            heading.Children.Add(new TextBlock { Text = $"  {PlayerText("disconnected")}",
                Foreground = Brushes.Gray, FontSize = 11 });

        var muteIcon = new System.Windows.Shapes.Path
        {
            Width = 21, Height = 21, Fill = Brushes.White, Stretch = Stretch.Uniform
        };
        var mute = new Button
        {
            Width = 30, Height = 30, Padding = new Thickness(4), BorderThickness = new Thickness(0),
            Content = muteIcon, ToolTip = PlayerText("mute"), IsEnabled = !player.Disconnected,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var slider = new Slider
        {
            Minimum = 0, Maximum = 2, TickFrequency = 0.02, IsSnapToTickEnabled = true,
            SmallChange = 0.02, LargeChange = 0.1, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Value = PlayerAudioConfig.For(player, settings.PlayerConfigMap).Volume
        };
        AutomationProperties.SetName(slider, $"{player.Name} {PlayerText("volume")}");
        var value = new TextBlock { Width = 62, TextAlignment = TextAlignment.Right };
        void UpdateControls()
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            muteIcon.Data = config.IsMuted ? VolumeOff : VolumeUp;
            mute.Background = new SolidColorBrush(config.IsMuted
                ? Color.FromArgb(0x24, 0xf4, 0x43, 0x36) : Color.FromArgb(0x10, 0xff, 0xff, 0xff));
            mute.ToolTip = config.IsMuted ? PlayerText("unmute") : PlayerText("mute");
            AutomationProperties.SetName(mute, $"{player.Name}: {mute.ToolTip}");
            slider.IsEnabled = !player.Disconnected && !config.IsMuted;
            value.Text = config.IsMuted ? PlayerText("muted") : $"{Math.Floor(config.Volume * 100d)}%";
        }
        UpdateControls();
        mute.Click += (_, _) =>
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            ChangePlayerConfig(player.PlayerConfigId, config with { IsMuted = !config.IsMuted }, true);
            UpdateControls();
        };
        slider.ValueChanged += (_, _) =>
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            ChangePlayerConfig(player.PlayerConfigId, config with { Volume = slider.Value }, false);
            UpdateControls();
        };
        void PersistVolume()
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            ChangePlayerConfig(player.PlayerConfigId, config with { Volume = slider.Value }, true);
        }
        slider.PreviewMouseLeftButtonUp += (_, _) => PersistVolume();
        slider.KeyUp += (_, _) => PersistVolume();

        var controls = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Children.Add(mute);
        Grid.SetColumn(slider, 1);
        controls.Children.Add(slider);
        Grid.SetColumn(value, 2);
        controls.Children.Add(value);
        var content = new StackPanel();
        content.Children.Add(heading);
        content.Children.Add(controls);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x29, 0x25, 0x2f)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10), Child = content
        };
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralPanel is null) return;
        if (CategoryList.SelectedIndex != 1) FlushPendingLobby();
        GeneralPanel.Visibility = CategoryList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LobbyPanel.Visibility = CategoryList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PlayersPanel.Visibility = CategoryList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = CategoryList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        KeybindsPanel.Visibility = CategoryList.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        OverlayPanel.Visibility = CategoryList.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanel.Visibility = CategoryList.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanel.Visibility = CategoryList.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
        StreamingPanel.Visibility = CategoryList.SelectedIndex == 8 ? Visibility.Visible : Visibility.Collapsed;
        if (CategoryList.SelectedIndex == 8) UpdateObsUrl();
        if (IsLoaded)
        {
            UpdateMicrophoneLevelSession();
            if (CategoryList.SelectedIndex != 3)
            {
                StopSpeakerTest();
                StopVoiceEffectPreview();
            }
        }
    }

    private void ObsOverlayCheck_Changed(object sender, RoutedEventArgs e) => UpdateObsUrl();

    private void UpdateObsUrl()
    {
        if (ObsUrlPanel is null || ObsToggleBorder is null || ObsOverlayCheck is null || ObsUrlBox is null ||
            ServerUrlBox is null || OverlayPositionCombo is null) return;
        var enabled = ObsOverlayCheck.IsChecked == true;
        ObsUrlPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ObsToggleBorder.BorderThickness = enabled ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        if (!enabled) return;
        if (!StreamingSettings.IsValidSecret(obsSecretDraft))
            obsSecretDraft = StreamingSettings.CreateSecret();
        ObsUrlBox.Text = StreamingSettings.BuildObsUrl(new ClientSettings
        {
            ServerUrl = SelectedServerUrl,
            CompactOverlay = CompactOverlayCheck.IsChecked == true,
            MeetingOverlay = MeetingOverlayCheck.IsChecked == true,
            OverlayPosition = (OverlayPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "right",
            ObsSecret = obsSecretDraft
        });
    }

    private async void CopyObsUrlButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateObsUrl();
        try
        {
            Clipboard.SetText(ObsUrlBox.Text);
            originalObsCopyIcon ??= CopyObsUrlIcon.Data;
            CopyObsUrlIcon.Data = Geometry.Parse("M9 16.17l-3.88-3.88L3.71 13.7 9 19l12-12-1.41-1.41z");
            var version = ++obsCopyFeedbackVersion;
            await Task.Delay(1_500);
            if (version == obsCopyFeedbackVersion && !Dispatcher.HasShutdownStarted)
                CopyObsUrlIcon.Data = originalObsCopyIcon;
        }
        catch (ExternalException exception)
        {
            MessageBox.Show(this, $"URLをコピーできませんでした: {exception.Message}", "配信設定");
        }
    }

    private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Tab) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shortcut = GlobalHotkeyMonitor.FromKey(key);
        if (shortcut is not null)
        {
            box.Text = shortcut;
            SetShortcutRecording(box, false);
        }
        e.Handled = true;
    }

    private void ShortcutBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.ChangedButton == MouseButton.Left && box.IsKeyboardFocused)
        {
            SetShortcutRecording(box, !IsShortcutRecording(box));
            e.Handled = true;
        }
        else if (IsShortcutRecording(box) && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            box.Text = e.ChangedButton == MouseButton.XButton1 ? "MouseButton4" : "MouseButton5";
            SetShortcutRecording(box, false);
            e.Handled = true;
        }
    }

    private void ShortcutBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) SetShortcutRecording(box, true);
    }

    private void ShortcutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) SetShortcutRecording(box, false);
    }

    private static bool IsShortcutRecording(TextBox box) => Equals(box.Tag, "Recording");

    private static void SetShortcutRecording(TextBox box, bool recording) =>
        box.Tag = recording ? "Recording" : null;

    private void OpenDebugButton_Click(object sender, RoutedEventArgs e)
    {
        var authentication = new DebugAuthWindow { Owner = this };
        if (authentication.ShowDialog() != true) return;
        OpenAuthenticatedDebugInfo();
    }

    private void OpenAuthenticatedDebugInfo() => DebugOpenRequested?.Invoke();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateVolumeLabels();
        if (ReferenceEquals(sender, VoiceEffectStrengthSlider))
            voiceEffectPreview?.SetStrength((int)VoiceEffectStrengthSlider.Value);
    }

    private void MicrophoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateMicrophoneLevelSession();
        StopVoiceEffectPreview();
    }

    private void AutoGainControlCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateMicrophoneLevelSession();
    }

    private void UpdateMicrophoneLevelSession()
    {
        StopMicrophoneLevelSession();
        if (AudioPanel.Visibility != Visibility.Visible ||
            MicrophoneCombo.SelectedItem is not AudioDeviceInfo input) return;
        try
        {
            MicrophoneLevelSession? started = null;
            started = MicrophoneLevelSession.Start(input.Id, AutoGainControlCheck.IsChecked == true, level =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(microphoneLevelSession, started))
                        MicrophoneLevelBar.Value = level;
                })));
            microphoneLevelSession = started;
            MicrophoneLevelStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            MicrophoneLevelStatus.Text = $"マイクに接続できません: {error.Message}";
            MicrophoneLevelStatus.Visibility = Visibility.Visible;
        }
    }

    private void StopMicrophoneLevelSession()
    {
        var session = microphoneLevelSession;
        microphoneLevelSession = null;
        session?.Dispose();
        if (MicrophoneLevelBar is not null) MicrophoneLevelBar.Value = 0;
    }

    private void SpeakerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        StopSpeakerTest();
        StopVoiceEffectPreview();
    }

    private void TestSpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        if (speakerTestSession is not null)
        {
            StopSpeakerTest();
            return;
        }
        if (SpeakerCombo.SelectedItem is not AudioDeviceInfo speaker)
        {
            MessageBox.Show(this, "スピーカーを選択してください。", "スピーカーテスト");
            return;
        }
        try
        {
            var chimePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "chime.mp3");
            SpeakerTestSession? started = null;
            started = SpeakerTestSession.Start(speaker.Id, chimePath, () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(speakerTestSession, started)) StopSpeakerTest();
                })));
            speakerTestSession = started;
            TestSpeakerButton.Content = "スピーカーテスト停止";
        }
        catch (Exception error)
        {
            StopSpeakerTest();
            MessageBox.Show(this, $"スピーカーテストを開始できませんでした: {error.Message}",
                "スピーカーテスト");
        }
    }

    private void StopSpeakerTest()
    {
        var session = speakerTestSession;
        speakerTestSession = null;
        session?.Dispose();
        if (TestSpeakerButton is not null) TestSpeakerButton.Content = "スピーカーテスト";
    }

    private void TestVoiceEffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (voiceEffectPreview is not null)
        {
            StopVoiceEffectPreview();
            return;
        }

        if (MicrophoneCombo.SelectedItem is not AudioDeviceInfo input ||
            SpeakerCombo.SelectedItem is not AudioDeviceInfo output)
        {
            MessageBox.Show(this, "マイクとスピーカーを選択してください。", "ボイスエフェクトテスト");
            return;
        }

        try
        {
            voiceEffectPreview = VoiceEffectPreviewSession.Start(input.Id, output.Id,
                (int)VoiceEffectStrengthSlider.Value);
            TestVoiceEffectButton.Content = "エフェクトテスト停止";
        }
        catch (Exception error)
        {
            StopVoiceEffectPreview();
            MessageBox.Show(this, $"エフェクトテストを開始できませんでした: {error.Message}",
                "ボイスエフェクトテスト");
        }
    }

    private void StopVoiceEffectPreview()
    {
        voiceEffectPreview?.Dispose();
        voiceEffectPreview = null;
        TestVoiceEffectButton.Content = "ボイスエフェクトテスト";
    }

    protected override void OnClosed(EventArgs e)
    {
        lobbyCommitTimer.Stop();
        StopMicrophoneLevelSession();
        StopSpeakerTest();
        StopVoiceEffectPreview();
        updateCancellation.Cancel();
        updateClient.Dispose();
        base.OnClosed(e);
    }

    private void LobbyTab_Changed(object sender, RoutedEventArgs e)
    {
        if (LobbyControlsPanel is null || loadingLobbyControls) return;
        var nextCurrent = CurrentLobbyTab.IsChecked == true;
        if (nextCurrent == showingCurrentLobby) return;
        if (!showingCurrentLobby)
        {
            lobbyDraft = ReadLobbyControls();
            FlushPendingLobby();
        }
        showingCurrentLobby = nextCurrent;
        ShowSelectedLobbySettings();
    }

    private void ShowSelectedLobbySettings()
    {
        UpdateModSettingsVisibility();
        var value = showingCurrentLobby ? currentLobbySettings : lobbyDraft;
        LobbyNotice.Text = showingCurrentLobby
            ? "ホストから受け取った設定です。ここでは変更できません。"
            : lobbySettingsEditable
                ? "ホスト時に参加者へ送信する設定です。"
                : "ゲーム進行中のホストは自分のロビー設定を変更できません。";
        NoLobbyText.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;
        LobbyControlsPanel.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        LobbyControlsPanel.IsEnabled = !showingCurrentLobby && lobbySettingsEditable;
        if (value is not null) LoadLobbyControls(value);
    }

    private void UpdateModSettingsVisibility()
    {
        if (NosControlsPanel is null || SnrControlsPanel is null || TohControlsPanel is null) return;
        SnrControlsPanel.Visibility = currentGameState?.Mod == AmongUsModType.SuperNewRoles
            ? Visibility.Visible : Visibility.Collapsed;
        TohControlsPanel.Visibility = currentGameState?.Mod == AmongUsModType.TownOfHostForE
            ? Visibility.Visible : Visibility.Collapsed;
        NosControlsPanel.Visibility = currentGameState?.Mod == AmongUsModType.NebulaOnTheShip
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void JackalRadioCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingLobbyControls || SnrJackalRadioCheck is null || JackalRadioCheck is null) return;
        var source = (CheckBox)sender;
        var target = ReferenceEquals(source, SnrJackalRadioCheck) ? JackalRadioCheck : SnrJackalRadioCheck;
        if (target.IsChecked != source.IsChecked) target.IsChecked = source.IsChecked;
    }

    private LobbySettings ReadLobbyControls() => lobbyDraft with
    {
        MaxDistance = DistanceSlider.Value,
        WallsBlockAudio = WallsBlockAudioCheck.IsChecked == true,
        VisionHearing = VisionHearingCheck.IsChecked == true,
        Haunting = HauntingCheck.IsChecked == true,
        HearImpostorsInVents = HearVentsCheck.IsChecked == true,
        ImpostersHearImpostersInvent = ImpostorVentCheck.IsChecked == true,
        ImpostorRadioEnabled = ImpostorRadioCheck.IsChecked == true,
        CommsSabotage = CommsSabotageCheck.IsChecked == true,
        HearThroughCameras = HearThroughCamerasCheck.IsChecked == true,
        VoiceEffectEnabled = VoiceEffectEnabledCheck.IsChecked == true,
        ImpostorRadioOnlyMode = RadioOnlyCheck.IsChecked == true,
        DeadOnly = DeadOnlyCheck.IsChecked == true,
        MeetingGhostOnly = MeetingGhostOnlyCheck.IsChecked == true,
        SnrJumboVoice = SnrJumboVoiceCheck.IsChecked == true,
        JackalHaunting = SnrJackalHauntingCheck.IsChecked == true,
        JackalRadioEnabled = currentGameState?.Mod == AmongUsModType.SuperNewRoles
            ? SnrJackalRadioCheck.IsChecked == true : JackalRadioCheck.IsChecked == true,
        JackalHearOutsideVents = SnrJackalHearOutsideVentsCheck.IsChecked == true,
        JackalTalkInVents = SnrJackalTalkInVentsCheck.IsChecked == true,
        SidekickHaunting = SnrSidekickHauntingCheck.IsChecked == true,
        SidekickHearOutsideVents = SnrSidekickHearOutsideVentsCheck.IsChecked == true,
        SidekickTalkInVents = SnrSidekickTalkInVentsCheck.IsChecked == true,
        TohNeutralKillerHaunting = TohNeutralKillerHauntingCheck.IsChecked == true,
        NosNeutralKillerHaunting = NosNeutralKillerHauntingCheck.IsChecked == true,
        NosVoicePositions = NosVoicePositionsCheck.IsChecked == true,
        NosSizeVoiceEffect = NosSizeVoiceEffectCheck.IsChecked == true,
        NosFixerJammingVoiceBlock = NosFixerJammingVoiceBlockCheck.IsChecked == true
    };

    private void LoadLobbyControls(LobbySettings value)
    {
        loadingLobbyControls = true;
        DistanceSlider.Value = value.MaxDistance;
        WallsBlockAudioCheck.IsChecked = value.WallsBlockAudio;
        VisionHearingCheck.IsChecked = value.VisionHearing;
        HauntingCheck.IsChecked = value.Haunting;
        HearVentsCheck.IsChecked = value.HearImpostorsInVents;
        ImpostorVentCheck.IsChecked = value.ImpostersHearImpostersInvent;
        ImpostorRadioCheck.IsChecked = value.ImpostorRadioEnabled;
        CommsSabotageCheck.IsChecked = value.CommsSabotage;
        HearThroughCamerasCheck.IsChecked = value.HearThroughCameras;
        VoiceEffectEnabledCheck.IsChecked = value.VoiceEffectEnabled;
        DeadOnlyCheck.IsChecked = value.DeadOnly;
        MeetingGhostOnlyCheck.IsChecked = value.MeetingGhostOnly;
        SnrJumboVoiceCheck.IsChecked = value.SnrJumboVoice;
        SnrJackalHauntingCheck.IsChecked = value.JackalHaunting;
        SnrJackalRadioCheck.IsChecked = value.JackalRadioEnabled;
        SnrJackalHearOutsideVentsCheck.IsChecked = value.JackalHearOutsideVents;
        SnrJackalTalkInVentsCheck.IsChecked = value.JackalTalkInVents;
        SnrSidekickHauntingCheck.IsChecked = value.SidekickHaunting;
        SnrSidekickHearOutsideVentsCheck.IsChecked = value.SidekickHearOutsideVents;
        SnrSidekickTalkInVentsCheck.IsChecked = value.SidekickTalkInVents;
        TohNeutralKillerHauntingCheck.IsChecked = value.TohNeutralKillerHaunting;
        JackalRadioCheck.IsChecked = value.JackalRadioEnabled;
        NosNeutralKillerHauntingCheck.IsChecked = value.NosNeutralKillerHaunting;
        NosVoicePositionsCheck.IsChecked = value.NosVoicePositions;
        NosSizeVoiceEffectCheck.IsChecked = value.NosSizeVoiceEffect;
        NosFixerJammingVoiceBlockCheck.IsChecked = value.NosFixerJammingVoiceBlock;
        RadioOnlyCheck.IsChecked = value.ImpostorRadioOnlyMode;
        loadingLobbyControls = false;

        var regularSettingsEnabled = !value.ImpostorRadioOnlyMode;
        DistanceSlider.IsEnabled = regularSettingsEnabled;
        WallsBlockAudioCheck.IsEnabled = regularSettingsEnabled;
        VisionHearingCheck.IsEnabled = regularSettingsEnabled;
        HearVentsCheck.IsEnabled = regularSettingsEnabled;
        ImpostorVentCheck.IsEnabled = regularSettingsEnabled;
        ImpostorRadioCheck.IsEnabled = regularSettingsEnabled;
        CommsSabotageCheck.IsEnabled = regularSettingsEnabled;
        HearThroughCamerasCheck.IsEnabled = regularSettingsEnabled;
        VoiceEffectEnabledCheck.IsEnabled = regularSettingsEnabled;
        DeadOnlyCheck.IsEnabled = regularSettingsEnabled;
        MeetingGhostOnlyCheck.IsEnabled = regularSettingsEnabled;
        JackalRadioCheck.IsEnabled = regularSettingsEnabled;
        SnrJumboVoiceCheck.IsEnabled = regularSettingsEnabled;
        SnrJackalRadioCheck.IsEnabled = regularSettingsEnabled;
        SnrJackalHearOutsideVentsCheck.IsEnabled = regularSettingsEnabled;
        SnrJackalTalkInVentsCheck.IsEnabled = regularSettingsEnabled;
        SnrSidekickHearOutsideVentsCheck.IsEnabled = regularSettingsEnabled;
        SnrSidekickTalkInVentsCheck.IsEnabled = regularSettingsEnabled;
        NosVoicePositionsCheck.IsEnabled = regularSettingsEnabled;
    }

    private void RadioOnlyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingLobbyControls || showingCurrentLobby || DistanceSlider is null) return;

        var current = ReadLobbyControls();
        if (RadioOnlyCheck.IsChecked == true)
        {
            radioOnlyBackup = current;
            lobbyDraft = current.EnableImpostorRadioOnlyMode();
        }
        else
        {
            lobbyDraft = current.DisableImpostorRadioOnlyMode(radioOnlyBackup);
            radioOnlyBackup = null;
        }
        LoadLobbyControls(lobbyDraft);
    }

    private void VisionHearingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (DistanceTitleText is null) return;
        DistanceTitleText.Text = VisionHearingCheck.IsChecked == true
            ? "インポスターに届く音声距離" : "音声が届く距離";
    }

    private void UpdateVolumeLabels()
    {
        if (MasterVolumeValue is null || VoiceEffectStrengthValue is null || CrewVolumeAsGhostValue is null || GhostVolumeAsImpostorValue is null ||
            MicrophoneGainValue is null || MicSensitivityValue is null || DistanceValue is null) return;
        MasterVolumeValue.Text = $"{MasterVolumeSlider.Value:0}%";
        VoiceEffectStrengthValue.Text = $"{VoiceEffectStrengthSlider.Value:0}%";
        CrewVolumeAsGhostValue.Text = $"{CrewVolumeAsGhostSlider.Value:0}%";
        GhostVolumeAsImpostorValue.Text = $"{GhostVolumeAsImpostorSlider.Value:0}%";
        MicrophoneGainValue.Text = $"{MicrophoneGainSlider.Value:0}%";
        MicSensitivityValue.Text = $"{MicSensitivitySlider.Value:0.00}";
        DistanceValue.Text = $"{DistanceSlider.Value:0.0}";
    }

    private ClientSettings ReadSettingsControls()
    {
        var serverUrl = SelectedServerUrl;
        // The history selector preserves existing URLs verbatim, as in 3.2.7.
        // Only the new-URL dialog validates/normalizes newly entered addresses.

        return new ClientSettings
        {
            Language = (LanguageCombo.SelectedItem as UiLanguage)?.Code ?? "ja",
            ServerUrl = serverUrl,
            ServerUrls = serverUrlDrafts.Append(serverUrl)
                .Distinct(StringComparer.Ordinal).ToList(),
            NatFix = NatFixCheck.IsChecked == true,
            MobileHost = MobileHostCheck.IsChecked == true,
            EnableSpatialAudio = SpatialAudioCheck.IsChecked == true,
            EchoCancellation = EchoCancellationCheck.IsChecked == true,
            NoiseSuppression = NoiseSuppressionCheck.IsChecked == true,
            AutoGainControl = AutoGainControlCheck.IsChecked == true,
            OldSampleDebug = OldSampleDebugCheck.IsChecked == true,
            HardwareAcceleration = HardwareAccelerationCheck.IsChecked == true,
            MicrophoneName = (MicrophoneCombo.SelectedItem as AudioDeviceInfo)?.Name,
            SpeakerName = (SpeakerCombo.SelectedItem as AudioDeviceInfo)?.Name,
            AlwaysOnTop = OverlayAlwaysOnTopCheck.IsChecked == true,
            EnableOverlay = EnableOverlayCheck.IsChecked == true,
            CompactOverlay = CompactOverlayCheck.IsChecked == true,
            MeetingOverlay = MeetingOverlayCheck.IsChecked == true,
            OverlayPosition = (OverlayPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "right",
            HideCode = ShowLobbyCodeCheck.IsChecked != true,
            ObsOverlay = ObsOverlayCheck.IsChecked == true,
            ObsSecret = obsSecretDraft,
            MasterVolume = (int)MasterVolumeSlider.Value,
            VoiceEffectStrength = (int)VoiceEffectStrengthSlider.Value,
            CrewVolumeAsGhost = (int)CrewVolumeAsGhostSlider.Value,
            GhostVolumeAsImpostor = (int)GhostVolumeAsImpostorSlider.Value,
            MicrophoneGain = (int)MicrophoneGainSlider.Value,
            MicrophoneGainEnabled = MicrophoneGainCheck.IsChecked == true,
            MicSensitivity = Math.Round(1d - MicSensitivitySlider.Value, 2),
            MicSensitivityEnabled = MicSensitivityCheck.IsChecked == true,
            PushToTalkMode = PushToTalkModeRadio.IsChecked == true
                ? MicrophoneActivationMode.PushToTalk
                : PushToMuteModeRadio.IsChecked == true
                    ? MicrophoneActivationMode.PushToMute
                    : MicrophoneActivationMode.Voice,
            PushToTalkShortcut = PushToTalkShortcutBox.Text,
            ImpostorRadioShortcut = ImpostorRadioShortcutBox.Text,
            MuteShortcut = MuteShortcutBox.Text,
            DeafenShortcut = DeafenShortcutBox.Text,
            MyLobbySettings = lobbyDraft,
            RadioOnlyBackup = radioOnlyBackup,
            PlayerConfigMap = settings.PlayerConfigMap.ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }

    private string SelectedServerUrl => ServerUrlBox.SelectedItem as string ?? ServerUrlDialog.DefaultUrl;

    private void ServerUrlBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerUrlDescription is not null) ServerUrlDescription.Text = SelectedServerUrl;
        ServerDialog?.SynchronizeSelection(SelectedServerUrl);
        UpdateObsUrl();
    }

    private void ChangeServerButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsNavigation.IsEnabled = false;
        SettingsContent.IsEnabled = false;
        ServerDialogBackdrop.Visibility = Visibility.Visible;
        ServerDialog.Open(SelectedServerUrl);
    }

    private void ConfirmServerUrl(string url)
    {
        if (!serverUrlDrafts.Contains(url)) serverUrlDrafts.Add(url);
        ServerUrlBox.SelectedItem = url;
        CloseServerDialog();
    }

    private void CloseServerDialog()
    {
        ServerDialogBackdrop.Visibility = Visibility.Collapsed;
        SettingsNavigation.IsEnabled = true;
        SettingsContent.IsEnabled = true;
        ChangeServerButton.Focus();
    }

    private void ServerDialogBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ServerDialogBackdrop)) ServerDialog.Dismiss();
    }

    internal static bool IsValidServerUrl(string candidate) => ServerUrlDialog.IsValidUrl(candidate);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void MinimizeSettingsButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => Close();

    internal static void RenderServerDialogPreview(string directory)
    {
        // Render our own WPF visual tree offscreen; no desktop capture or live settings are used.
        Directory.CreateDirectory(directory);
        var window = new SettingsWindow(new ClientSettings(), true, null, false, null, (_, _, _) => { }, _ => { });
        try
        {
            window.CategoryList.SelectedIndex = 6;
            window.ChangeServerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var root = (FrameworkElement)window.Content;
            foreach (var size in new[] { new Size(760, 620), new Size(650, 500) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(System.IO.Path.Combine(directory,
                    $"server-dialog-{size.Width:0}x{size.Height:0}.png"));
                encoder.Save(output);
            }
        }
        finally { window.Close(); }
    }

    internal static void VerifyModControls()
    {
        ServerUrlDialog.VerifyBehavior();
        SettingsConfirmDialog.VerifyBehavior();
        VerifyImmediateSettings();
        VerifyResetDefaults();
        var lobby = new LobbySettings
        {
            SnrJumboVoice = true, JackalHaunting = true, JackalRadioEnabled = true,
            JackalHearOutsideVents = true, JackalTalkInVents = true,
            SidekickHaunting = true, SidekickHearOutsideVents = true,
            SidekickTalkInVents = true, TohNeutralKillerHaunting = true,
            NosNeutralKillerHaunting = true
        };
        var settings = new ClientSettings { MyLobbySettings = lobby };
        var window = new SettingsWindow(settings, true, null, false,
            new AmongUsState { Mod = AmongUsModType.SuperNewRoles }, (_, _, _) => { }, _ => { });
        try
        {
            var read = window.ReadLobbyControls();
            if (!read.SnrJumboVoice || !read.JackalHaunting || !read.JackalRadioEnabled ||
                !read.JackalHearOutsideVents || !read.JackalTalkInVents ||
                !read.SidekickHaunting || !read.SidekickHearOutsideVents ||
                !read.SidekickTalkInVents || !read.TohNeutralKillerHaunting ||
                !read.NosNeutralKillerHaunting ||
                window.SnrControlsPanel.Visibility != Visibility.Visible)
                throw new InvalidOperationException("MOD lobby controls did not round-trip");
            window.UpdateCurrentGameState(new AmongUsState { Mod = AmongUsModType.TownOfHostForE });
            if (window.TohControlsPanel.Visibility != Visibility.Visible ||
                window.SnrControlsPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("TOH lobby settings are not visible");
            window.UpdateCurrentGameState(new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip });
            if (window.NosControlsPanel.Visibility != Visibility.Visible)
                throw new InvalidOperationException("NoS lobby settings are not visible");
            window.JackalRadioCheck.IsChecked = false;
            window.UpdateCurrentGameState(new AmongUsState { Mod = AmongUsModType.SuperNewRoles });
            if (window.ReadLobbyControls().JackalRadioEnabled || window.SnrJackalRadioCheck.IsChecked != false)
                throw new InvalidOperationException("Shared Jackal radio setting did not follow MOD switch");
            window.CategoryList.SelectedIndex = 7;
            if (window.UpdatePanel.Visibility != Visibility.Visible || window.StartUpdateButton.IsEnabled ||
                window.UpdateVersionText.Visibility != Visibility.Collapsed ||
                window.UpdateStatusText.Visibility != Visibility.Visible ||
                window.UpdateStatusText.Text != "アップデートを確認してください。" ||
                window.CheckUpdateButton.Height != 40 || window.StartUpdateButton.Height != 40 ||
                window.ManualUpdateDownloadButton.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Update settings did not initialize");
            var updateCandidate = new UpdateCandidate("3.2.8-test",
                new Uri("https://example.invalid/update.zip"), new string('0', 64), 100,
                new Uri("https://example.invalid/release"));
            window.ShowUpdateCheckResult(updateCandidate, installationAvailable: true);
            if (window.UpdateVersionText.Text != "最新バージョンv3.2.8-test" ||
                window.UpdateVersionText.Visibility != Visibility.Visible ||
                window.UpdateStatusText.Visibility != Visibility.Collapsed ||
                !window.StartUpdateButton.IsEnabled)
                throw new InvalidOperationException("Available update state did not render");
            window.ShowUpdateDownloadProgress(null);
            if (!window.UpdateProgress.IsIndeterminate ||
                window.UpdateProgress.Visibility != Visibility.Visible ||
                window.UpdateStatusText.Text != "ダウンロード中…")
                throw new InvalidOperationException("Unknown update progress did not render");
            window.ShowUpdateDownloadProgress(42);
            if (window.UpdateProgress.IsIndeterminate || window.UpdateProgress.Value != 42 ||
                window.UpdateStatusText.Text != "ダウンロード中…")
                throw new InvalidOperationException("Known update progress did not render");
            window.ShowUpdateError("Test update failure");
            if (window.ManualUpdateDownloadButton.Visibility != Visibility.Visible ||
                window.UpdateStatusText.Text != "Test update failure" ||
                window.UpdateProgress.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Update failure did not offer the manual download fallback");
            window.InitializeUpdatePanel();
            if (window.ManualUpdateDownloadButton.Visibility != Visibility.Collapsed ||
                window.StartUpdateButton.IsEnabled || window.UpdateProgress.IsIndeterminate)
                throw new InvalidOperationException("Manual download fallback survived update reset");
            window.CategoryList.SelectedIndex = 8;
            window.ObsOverlayCheck.IsChecked = true;
            if (window.StreamingPanel.Visibility != Visibility.Visible ||
                !StreamingSettings.IsValidSecret(window.obsSecretDraft) ||
                window.ObsUrlPanel.Visibility != Visibility.Visible ||
                window.ObsToggleBorder.BorderThickness.Bottom != 1 ||
                !window.ObsUrlBox.Text.Contains("version=3.2.8&compact=0&position=right&meeting=1&secret=") ||
                !window.ObsUrlBox.Text.Contains("&server=https%3A%2F%2Fbettercrewl.ink"))
                throw new InvalidOperationException("OBS streaming settings did not initialize");
            window.ObsOverlayCheck.IsChecked = false;
            if (window.ObsUrlPanel.Visibility != Visibility.Collapsed ||
                window.ObsToggleBorder.BorderThickness.Bottom != 0)
                throw new InvalidOperationException("OBS URL row did not hide with its switch");
            window.ObsOverlayCheck.IsChecked = true;
            var streamSettings = new ClientSettings
            {
                HideCode = true, ObsOverlay = true, ObsSecret = window.obsSecretDraft
            };
            var restored = JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(streamSettings));
            if (restored is null || !restored.HideCode || !restored.ObsOverlay ||
                restored.ObsSecret != window.obsSecretDraft)
                throw new InvalidOperationException("Streaming settings did not persist");
            window.CategoryList.SelectedIndex = 6;
            window.ChangeServerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (window.ServerDialogBackdrop.Visibility != Visibility.Visible ||
                window.SettingsContent.IsEnabled || window.SettingsNavigation.IsEnabled ||
                window.ServerUrlBox.IsEditable)
                throw new InvalidOperationException("Server dialog did not block underlying settings controls");
            window.ServerDialog.Dismiss();
            if (window.ServerDialogBackdrop.Visibility != Visibility.Collapsed ||
                !window.SettingsContent.IsEnabled || !window.SettingsNavigation.IsEnabled)
                throw new InvalidOperationException("Dismissing server dialog did not restore settings controls");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                window.ChangeServerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.ServerDialog.UrlInput.Text = "https://voice.example.test/";
                window.ServerDialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            if (window.serverUrlDrafts.Count(url => url == "https://voice.example.test") != 1 ||
                window.SelectedServerUrl != "https://voice.example.test" ||
                window.ServerUrlDescription.Text != "https://voice.example.test" ||
                settings.ServerUrl != "https://voice.example.test" ||
                window.ServerDialogBackdrop.Visibility != Visibility.Collapsed ||
                !window.ObsUrlBox.Text.Contains("&server=https%3A%2F%2Fvoice.example.test"))
                throw new InvalidOperationException("Server confirmation did not update unique history, selection and OBS preview");
            if (!window.ServerUrlBox.Items.Cast<string>().Contains("https://bettercrewl.ink") ||
                !IsValidServerUrl("https://voice.example.test/") ||
                IsValidServerUrl("https://voice.example.test/api") ||
                IsValidServerUrl("https://discord.gg/abc"))
                throw new InvalidOperationException("Server URL selection or validation differs from 3.2.7");
            var serverHistory = new ClientSettings
            {
                ServerUrl = "https://voice.example.test",
                ServerUrls = ["https://bettercrewl.ink", "https://voice.example.test"]
            };
            var restoredServers = JsonSerializer.Deserialize<ClientSettings>(
                JsonSerializer.Serialize(serverHistory));
            restoredServers?.Normalize();
            if (restoredServers is null || restoredServers.ServerUrl != serverHistory.ServerUrl ||
                !restoredServers.ServerUrls.Contains("https://voice.example.test"))
                throw new InvalidOperationException("Server URL history did not persist");
            if (window.MobileHostCheck.IsChecked != true)
                throw new InvalidOperationException("Mobile host default was not loaded");
            window.MobileHostCheck.IsChecked = false;
            var mobileRestored = JsonSerializer.Deserialize<ClientSettings>(
                JsonSerializer.Serialize(new ClientSettings
                { MobileHost = window.MobileHostCheck.IsChecked == true }));
            if (mobileRestored?.MobileHost != false)
                throw new InvalidOperationException("Mobile host setting did not persist");
            if (window.SpatialAudioCheck.IsChecked != true)
                throw new InvalidOperationException("Spatial audio default was not loaded");
            window.SpatialAudioCheck.IsChecked = false;
            var spatialRestored = JsonSerializer.Deserialize<ClientSettings>(
                JsonSerializer.Serialize(new ClientSettings
                { EnableSpatialAudio = window.SpatialAudioCheck.IsChecked == true }));
            if (spatialRestored?.EnableSpatialAudio != false)
                throw new InvalidOperationException("Spatial audio setting did not persist");
            if (window.EchoCancellationCheck.IsChecked != true ||
                window.NoiseSuppressionCheck.IsChecked != true ||
                window.AutoGainControlCheck.IsChecked != false)
                throw new InvalidOperationException("Input processing defaults were not loaded");
            window.EchoCancellationCheck.IsChecked = false;
            window.NoiseSuppressionCheck.IsChecked = false;
            window.AutoGainControlCheck.IsChecked = true;
            var processingRestored = JsonSerializer.Deserialize<ClientSettings>(
                JsonSerializer.Serialize(new ClientSettings
                {
                    EchoCancellation = window.EchoCancellationCheck.IsChecked == true,
                    NoiseSuppression = window.NoiseSuppressionCheck.IsChecked == true,
                    AutoGainControl = window.AutoGainControlCheck.IsChecked == true
                }));
            if (processingRestored is null || processingRestored.EchoCancellation ||
                processingRestored.NoiseSuppression || !processingRestored.AutoGainControl)
                throw new InvalidOperationException("Input processing settings did not persist");
            var chimePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "chime.mp3");
            using var chime = new NAudio.Wave.AudioFileReader(chimePath);
            if (chime.TotalTime < TimeSpan.FromMilliseconds(100) ||
                MicrophoneLevelSession.CalculateLevel([0, 0, 0, 0]) != 0 ||
                MicrophoneLevelSession.CalculateLevel([0, 64, 0, 64]) < 90)
                throw new InvalidOperationException("Audio settings previews are not ready");
        }
        finally { window.Close(); }
    }
}
