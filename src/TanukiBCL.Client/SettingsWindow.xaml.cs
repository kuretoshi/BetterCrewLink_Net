using System.IO;
using System.Windows;
using System.Windows.Controls;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

public partial class SettingsWindow : Window
{
    private readonly ClientSettings settings;

    internal SettingsWindow(ClientSettings settings, bool lobbySettingsEditable)
    {
        InitializeComponent();
        this.settings = settings;
        LobbyPanel.IsEnabled = lobbySettingsEditable;
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
        ServerUrlBox.Text = settings.ServerUrl;
        DistanceSlider.Value = settings.MyLobbySettings.MaxDistance;
        HauntingCheck.IsChecked = settings.MyLobbySettings.Haunting;
        HearVentsCheck.IsChecked = settings.MyLobbySettings.HearImpostorsInVents;
        ImpostorVentCheck.IsChecked = settings.MyLobbySettings.ImpostersHearImpostersInvent;
        ImpostorRadioCheck.IsChecked = settings.MyLobbySettings.ImpostorRadioEnabled;
        DeadOnlyCheck.IsChecked = settings.MyLobbySettings.DeadOnly;
        MeetingGhostOnlyCheck.IsChecked = settings.MyLobbySettings.MeetingGhostOnly;
        CategoryList.SelectedIndex = 0;
        UpdateVolumeLabels();
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
        if (MasterVolumeValue is null || MicrophoneGainValue is null || DistanceValue is null) return;
        MasterVolumeValue.Text = $"{MasterVolumeSlider.Value:0}%";
        MicrophoneGainValue.Text = $"{MicrophoneGainSlider.Value:0}%";
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
            MicSensitivity = settings.MicSensitivity,
            MicSensitivityEnabled = settings.MicSensitivityEnabled,
            MyLobbySettings = settings.MyLobbySettings with
            {
                MaxDistance = DistanceSlider.Value,
                Haunting = HauntingCheck.IsChecked == true,
                HearImpostorsInVents = HearVentsCheck.IsChecked == true,
                ImpostersHearImpostersInvent = ImpostorVentCheck.IsChecked == true,
                ImpostorRadioEnabled = ImpostorRadioCheck.IsChecked == true,
                DeadOnly = DeadOnlyCheck.IsChecked == true,
                MeetingGhostOnly = MeetingGhostOnlyCheck.IsChecked == true
            }
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
        settings.MyLobbySettings = candidate.MyLobbySettings;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
