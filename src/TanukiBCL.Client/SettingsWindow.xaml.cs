using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class SettingsWindow : Window
{
    private readonly ClientSettings settings;
    private readonly bool lobbySettingsEditable;
    private LobbySettings? currentLobbySettings;
    private LobbySettings lobbyDraft;
    private LobbySettings? radioOnlyBackup;
    private bool loadingLobbyControls;
    private bool showingCurrentLobby;
    private AmongUsState? currentGameState;
    private readonly Action<int, PlayerAudioConfig, bool> onPlayerConfigChanged;
    private string playersSignature = string.Empty;
    private static readonly Geometry VolumeUp = Geometry.Parse(
        "M3 9v6h4l5 5V4L7 9zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02M14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77");
    private static readonly Geometry VolumeOff = Geometry.Parse(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63m2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71M4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9zM12 4 9.91 6.09 12 8.18z");

    internal SettingsWindow(ClientSettings settings, bool lobbySettingsEditable,
        LobbySettings? currentLobbySettings, bool preferCurrentLobby, AmongUsState? gameState,
        Action<int, PlayerAudioConfig, bool> onPlayerConfigChanged)
    {
        InitializeComponent();
        this.settings = settings;
        this.lobbySettingsEditable = lobbySettingsEditable;
        this.currentLobbySettings = currentLobbySettings;
        currentGameState = gameState;
        this.onPlayerConfigChanged = onPlayerConfigChanged;
        lobbyDraft = settings.MyLobbySettings;
        radioOnlyBackup = settings.RadioOnlyBackup;
        MicrophoneCombo.ItemsSource = AudioDeviceSession.GetInputDevices();
        SpeakerCombo.ItemsSource = AudioDeviceSession.GetOutputDevices();
        MicrophoneCombo.SelectedItem = ((IEnumerable<AudioDeviceInfo>)MicrophoneCombo.ItemsSource)
            .FirstOrDefault(device => device.Name == settings.MicrophoneName) ?? MicrophoneCombo.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
        SpeakerCombo.SelectedItem = ((IEnumerable<AudioDeviceInfo>)SpeakerCombo.ItemsSource)
            .FirstOrDefault(device => device.Name == settings.SpeakerName) ?? SpeakerCombo.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
        AlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
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
        ServerUrlBox.Text = settings.ServerUrl;
        NatFixCheck.IsChecked = settings.NatFix;
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
    }

    internal void UpdateCurrentLobbySettings(LobbySettings? settings)
    {
        currentLobbySettings = settings;
        if (showingCurrentLobby) ShowSelectedLobbySettings();
    }

    internal void UpdateCurrentGameState(AmongUsState? state)
    {
        currentGameState = state;
        UpdateModSettingsVisibility();
        var signature = string.Join(';', (state?.Players ?? []).Where(player => !player.IsLocal && !player.IsDummy)
            .OrderBy(player => player.ClientId)
            .Select(player => $"{player.ClientId}:{player.PlayerConfigId}:{player.Name}:{player.ColorId}:{player.Disconnected}"));
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
            $"{player.ClientId}:{player.PlayerConfigId}:{player.Name}:{player.ColorId}:{player.Disconnected}"));
        if (players.Length == 0)
        {
            PlayerRows.Children.Add(new TextBlock
            {
                Text = "調整できるプレイヤーがいません。",
                Foreground = new SolidColorBrush(Color.FromRgb(0xb7, 0xaa, 0xbd))
            });
            return;
        }

        foreach (var player in players) PlayerRows.Children.Add(CreatePlayerRow(player));
    }

    private Border CreatePlayerRow(Player player)
    {
        var (main, shadow) = AvatarImageFactory.GetSwatchColors(player.ColorId, currentGameState?.PlayerColors);
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
            heading.Children.Add(new TextBlock { Text = "  切断済み", Foreground = Brushes.Gray, FontSize = 11 });

        var muteIcon = new System.Windows.Shapes.Path
        {
            Width = 21, Height = 21, Fill = Brushes.White, Stretch = Stretch.Uniform
        };
        var mute = new Button
        {
            Width = 30, Height = 30, Padding = new Thickness(4), BorderThickness = new Thickness(0),
            Content = muteIcon, ToolTip = "このプレイヤーをミュート", IsEnabled = !player.Disconnected,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var slider = new Slider
        {
            Minimum = 0, Maximum = 2, TickFrequency = 0.02, IsSnapToTickEnabled = true,
            SmallChange = 0.02, LargeChange = 0.1, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Value = PlayerAudioConfig.For(player, settings.PlayerConfigMap).Volume
        };
        AutomationProperties.SetName(slider, $"{player.Name}の音量");
        var value = new TextBlock { Width = 62, TextAlignment = TextAlignment.Right };
        void UpdateControls()
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            muteIcon.Data = config.IsMuted ? VolumeOff : VolumeUp;
            mute.Background = new SolidColorBrush(config.IsMuted
                ? Color.FromArgb(0x24, 0xf4, 0x43, 0x36) : Color.FromArgb(0x10, 0xff, 0xff, 0xff));
            mute.ToolTip = config.IsMuted ? "このプレイヤーのミュートを解除" : "このプレイヤーをミュート";
            AutomationProperties.SetName(mute, $"{player.Name}: {mute.ToolTip}");
            slider.IsEnabled = !player.Disconnected && !config.IsMuted;
            value.Text = config.IsMuted ? "ミュート" : $"{Math.Floor(config.Volume * 100d)}%";
        }
        UpdateControls();
        mute.Click += (_, _) =>
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            onPlayerConfigChanged(player.PlayerConfigId, config with { IsMuted = !config.IsMuted }, true);
            UpdateControls();
        };
        slider.ValueChanged += (_, _) =>
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            onPlayerConfigChanged(player.PlayerConfigId, config with { Volume = slider.Value }, false);
            UpdateControls();
        };
        void PersistVolume()
        {
            var config = PlayerAudioConfig.For(player, settings.PlayerConfigMap);
            onPlayerConfigChanged(player.PlayerConfigId, config with { Volume = slider.Value }, true);
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
        GeneralPanel.Visibility = CategoryList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LobbyPanel.Visibility = CategoryList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PlayersPanel.Visibility = CategoryList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = CategoryList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        KeybindsPanel.Visibility = CategoryList.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanel.Visibility = CategoryList.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shortcut = GlobalHotkeyMonitor.FromKey(key);
        if (shortcut is not null) box.Text = shortcut;
        e.Handled = true;
    }

    private void ShortcutBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.ChangedButton == MouseButton.XButton1) box.Text = "MouseButton4";
        else if (e.ChangedButton == MouseButton.XButton2) box.Text = "MouseButton5";
        else return;
        e.Handled = true;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateVolumeLabels();

    private void LobbyTab_Changed(object sender, RoutedEventArgs e)
    {
        if (LobbyControlsPanel is null || loadingLobbyControls) return;
        var nextCurrent = CurrentLobbyTab.IsChecked == true;
        if (nextCurrent == showingCurrentLobby) return;
        if (!showingCurrentLobby)
        {
            lobbyDraft = ReadLobbyControls();
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
        if (NosControlsPanel is null) return;
        NosControlsPanel.Visibility = currentGameState?.Mod == AmongUsModType.NebulaOnTheShip
            ? Visibility.Visible : Visibility.Collapsed;
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
        JackalRadioEnabled = JackalRadioCheck.IsChecked == true,
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
        JackalRadioCheck.IsChecked = value.JackalRadioEnabled;
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

    private void DeadOnlyCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (MeetingGhostOnlyCheck is not null) MeetingGhostOnlyCheck.IsChecked = false;
    }

    private void MeetingGhostOnlyCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (DeadOnlyCheck is not null) DeadOnlyCheck.IsChecked = false;
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var serverUrl = ServerUrlBox.Text.Trim();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "http または https のボイスサーバーURLを指定してください。", "設定");
            return;
        }

        var candidate = new ClientSettings
        {
            ServerUrl = serverUrl,
            NatFix = NatFixCheck.IsChecked == true,
            MicrophoneName = (MicrophoneCombo.SelectedItem as AudioDeviceInfo)?.Name,
            SpeakerName = (SpeakerCombo.SelectedItem as AudioDeviceInfo)?.Name,
            AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true,
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
            MyLobbySettings = showingCurrentLobby ? lobbyDraft : ReadLobbyControls(),
            RadioOnlyBackup = radioOnlyBackup,
            PlayerConfigMap = settings.PlayerConfigMap.ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        try
        {
            ClientSettingsStore.Save(candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"設定を保存できませんでした: {exception.Message}", "設定");
            return;
        }
        settings.ServerUrl = candidate.ServerUrl;
        settings.NatFix = candidate.NatFix;
        settings.MicrophoneName = candidate.MicrophoneName;
        settings.SpeakerName = candidate.SpeakerName;
        settings.AlwaysOnTop = candidate.AlwaysOnTop;
        settings.MasterVolume = candidate.MasterVolume;
        settings.VoiceEffectStrength = candidate.VoiceEffectStrength;
        settings.CrewVolumeAsGhost = candidate.CrewVolumeAsGhost;
        settings.GhostVolumeAsImpostor = candidate.GhostVolumeAsImpostor;
        settings.MicrophoneGain = candidate.MicrophoneGain;
        settings.MicrophoneGainEnabled = candidate.MicrophoneGainEnabled;
        settings.MicSensitivity = candidate.MicSensitivity;
        settings.MicSensitivityEnabled = candidate.MicSensitivityEnabled;
        settings.PushToTalkMode = candidate.PushToTalkMode;
        settings.PushToTalkShortcut = candidate.PushToTalkShortcut;
        settings.ImpostorRadioShortcut = candidate.ImpostorRadioShortcut;
        settings.MuteShortcut = candidate.MuteShortcut;
        settings.DeafenShortcut = candidate.DeafenShortcut;
        settings.MyLobbySettings = candidate.MyLobbySettings;
        settings.RadioOnlyBackup = candidate.RadioOnlyBackup;
        settings.PlayerConfigMap = candidate.PlayerConfigMap;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
