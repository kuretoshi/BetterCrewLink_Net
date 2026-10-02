using System.IO;
using System.Windows;
using System.Windows.Controls;
using TanukiBCL.VoiceProbe;

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

    internal SettingsWindow(ClientSettings settings, bool lobbySettingsEditable,
        LobbySettings? currentLobbySettings, bool preferCurrentLobby)
    {
        InitializeComponent();
        this.settings = settings;
        this.lobbySettingsEditable = lobbySettingsEditable;
        this.currentLobbySettings = currentLobbySettings;
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
        MicrophoneGainSlider.Value = settings.MicrophoneGain;
        MicrophoneGainCheck.IsChecked = settings.MicrophoneGainEnabled;
        MicSensitivitySlider.Value = 1d - settings.MicSensitivity;
        MicSensitivityCheck.IsChecked = settings.MicSensitivityEnabled;
        ServerUrlBox.Text = settings.ServerUrl;
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
        CategoryList.SelectedIndex = 0;
        UpdateVolumeLabels();
    }

    internal void UpdateCurrentLobbySettings(LobbySettings? settings)
    {
        currentLobbySettings = settings;
        if (showingCurrentLobby) ShowSelectedLobbySettings();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralPanel is null) return;
        GeneralPanel.Visibility = CategoryList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LobbyPanel.Visibility = CategoryList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = CategoryList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPanel.Visibility = CategoryList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
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

    private LobbySettings ReadLobbyControls() => lobbyDraft with
    {
        MaxDistance = DistanceSlider.Value,
        Haunting = HauntingCheck.IsChecked == true,
        HearImpostorsInVents = HearVentsCheck.IsChecked == true,
        ImpostersHearImpostersInvent = ImpostorVentCheck.IsChecked == true,
        ImpostorRadioEnabled = ImpostorRadioCheck.IsChecked == true,
        CommsSabotage = CommsSabotageCheck.IsChecked == true,
        ImpostorRadioOnlyMode = RadioOnlyCheck.IsChecked == true,
        DeadOnly = DeadOnlyCheck.IsChecked == true,
        MeetingGhostOnly = MeetingGhostOnlyCheck.IsChecked == true
    };

    private void LoadLobbyControls(LobbySettings value)
    {
        loadingLobbyControls = true;
        DistanceSlider.Value = value.MaxDistance;
        HauntingCheck.IsChecked = value.Haunting;
        HearVentsCheck.IsChecked = value.HearImpostorsInVents;
        ImpostorVentCheck.IsChecked = value.ImpostersHearImpostersInvent;
        ImpostorRadioCheck.IsChecked = value.ImpostorRadioEnabled;
        CommsSabotageCheck.IsChecked = value.CommsSabotage;
        DeadOnlyCheck.IsChecked = value.DeadOnly;
        MeetingGhostOnlyCheck.IsChecked = value.MeetingGhostOnly;
        RadioOnlyCheck.IsChecked = value.ImpostorRadioOnlyMode;
        loadingLobbyControls = false;

        var regularSettingsEnabled = !value.ImpostorRadioOnlyMode;
        DistanceSlider.IsEnabled = regularSettingsEnabled;
        HearVentsCheck.IsEnabled = regularSettingsEnabled;
        ImpostorVentCheck.IsEnabled = regularSettingsEnabled;
        ImpostorRadioCheck.IsEnabled = regularSettingsEnabled;
        CommsSabotageCheck.IsEnabled = regularSettingsEnabled;
        DeadOnlyCheck.IsEnabled = regularSettingsEnabled;
        MeetingGhostOnlyCheck.IsEnabled = regularSettingsEnabled;
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
        if (MasterVolumeValue is null || MicrophoneGainValue is null || MicSensitivityValue is null || DistanceValue is null) return;
        MasterVolumeValue.Text = $"{MasterVolumeSlider.Value:0}%";
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
            MicrophoneName = (MicrophoneCombo.SelectedItem as AudioDeviceInfo)?.Name,
            SpeakerName = (SpeakerCombo.SelectedItem as AudioDeviceInfo)?.Name,
            AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true,
            MasterVolume = (int)MasterVolumeSlider.Value,
            MicrophoneGain = (int)MicrophoneGainSlider.Value,
            MicrophoneGainEnabled = MicrophoneGainCheck.IsChecked == true,
            MicSensitivity = Math.Round(1d - MicSensitivitySlider.Value, 2),
            MicSensitivityEnabled = MicSensitivityCheck.IsChecked == true,
            MyLobbySettings = showingCurrentLobby ? lobbyDraft : ReadLobbyControls(),
            RadioOnlyBackup = radioOnlyBackup
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
        settings.MicrophoneName = candidate.MicrophoneName;
        settings.SpeakerName = candidate.SpeakerName;
        settings.AlwaysOnTop = candidate.AlwaysOnTop;
        settings.MasterVolume = candidate.MasterVolume;
        settings.MicrophoneGain = candidate.MicrophoneGain;
        settings.MicrophoneGainEnabled = candidate.MicrophoneGainEnabled;
        settings.MicSensitivity = candidate.MicSensitivity;
        settings.MicSensitivityEnabled = candidate.MicSensitivityEnabled;
        settings.MyLobbySettings = candidate.MyLobbySettings;
        settings.RadioOnlyBackup = candidate.RadioOnlyBackup;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
