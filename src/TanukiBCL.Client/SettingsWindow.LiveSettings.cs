using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private readonly HashSet<string> failedSaveKeys = [];
    private void InitializeImmediateSettings()
    {
        BindToggle(AlwaysOnTopCheck, nameof(ClientSettings.AlwaysOnTop));
        BindToggle(EnableOverlayCheck, nameof(ClientSettings.EnableOverlay));
        BindToggle(CompactOverlayCheck, nameof(ClientSettings.CompactOverlay));
        BindToggle(MeetingOverlayCheck, nameof(ClientSettings.MeetingOverlay));
        EnableOverlayCheck.Click += (_, _) => UpdateOverlayControls();
        UpdateOverlayControls();
        BindToggle(MicrophoneGainCheck, nameof(ClientSettings.MicrophoneGainEnabled));
        BindToggle(MicSensitivityCheck, nameof(ClientSettings.MicSensitivityEnabled));
        BindToggle(MobileHostCheck, nameof(ClientSettings.MobileHost));
        BindToggle(SpatialAudioCheck, nameof(ClientSettings.EnableSpatialAudio));
        BindToggle(EchoCancellationCheck, nameof(ClientSettings.EchoCancellation));
        BindToggle(NoiseSuppressionCheck, nameof(ClientSettings.NoiseSuppression));
        BindToggle(AutoGainControlCheck, nameof(ClientSettings.AutoGainControl));
        BindToggle(ShowLobbyCodeCheck, nameof(ClientSettings.HideCode));
        BindToggle(ObsOverlayCheck, nameof(ClientSettings.ObsOverlay), nameof(ClientSettings.ObsSecret));
        foreach (var radio in new[] { VoiceModeRadio, PushToTalkModeRadio, PushToMuteModeRadio })
            radio.Click += (_, _) => ApplyControls(true, nameof(ClientSettings.PushToTalkMode));
        MicrophoneCombo.SelectionChanged += (_, _) => ApplyControls(true, nameof(ClientSettings.MicrophoneName));
        SpeakerCombo.SelectionChanged += (_, _) => ApplyControls(true, nameof(ClientSettings.SpeakerName));
        OverlayPositionCombo.SelectionChanged += (_, _) => ApplyControls(true, nameof(ClientSettings.OverlayPosition));
        ServerUrlBox.SelectionChanged += (_, _) =>
            ApplyControls(true, nameof(ClientSettings.ServerUrl), nameof(ClientSettings.ServerUrls));
        BindShortcut(PushToTalkShortcutBox, nameof(ClientSettings.PushToTalkShortcut));
        BindShortcut(ImpostorRadioShortcutBox, nameof(ClientSettings.ImpostorRadioShortcut));
        BindShortcut(MuteShortcutBox, nameof(ClientSettings.MuteShortcut));
        BindShortcut(DeafenShortcutBox, nameof(ClientSettings.DeafenShortcut));
        BindSlider(MasterVolumeSlider, nameof(ClientSettings.MasterVolume));
        BindSlider(VoiceEffectStrengthSlider, nameof(ClientSettings.VoiceEffectStrength));
        BindSlider(CrewVolumeAsGhostSlider, nameof(ClientSettings.CrewVolumeAsGhost));
        BindSlider(GhostVolumeAsImpostorSlider, nameof(ClientSettings.GhostVolumeAsImpostor));
        BindSlider(MicrophoneGainSlider, nameof(ClientSettings.MicrophoneGain));
        MicSensitivitySlider.PreviewMouseLeftButtonUp += (_, _) => CommitMicrophoneSensitivity();
        MicSensitivitySlider.LostMouseCapture += (_, _) => CommitMicrophoneSensitivity();
        MicSensitivitySlider.KeyUp += (_, _) => CommitMicrophoneSensitivity();

        foreach (var toggle in LogicalDescendants<CheckBox>(LobbyControlsPanel))
        {
            if (toggle == DeadOnlyCheck || toggle == MeetingGhostOnlyCheck) continue;
            toggle.Click += (_, _) => QueueLobbyCommit();
        }
        DistanceSlider.ValueChanged += (_, _) => QueueLobbyCommit();
        DeadOnlyCheck.Click += (_, _) => ChangeExclusiveLobbyMode(true);
        MeetingGhostOnlyCheck.Click += (_, _) => ChangeExclusiveLobbyMode(false);
        NatFixCheck.Click += (_, _) =>
        {
            var enabled = NatFixCheck.IsChecked == true;
            NatFixCheck.IsChecked = settings.NatFix;
            void Apply()
            {
                NatFixCheck.IsChecked = enabled;
                ApplyControls(true, nameof(ClientSettings.NatFix));
            }
            if (enabled) ConfirmChange("これによりNATの問題は解決されますが、Peer to Peer（P2P）ではなくサーバーを使用するため、遅延が発生します。", Apply);
            else Apply();
        };
        lobbyCommitTimer.Tick += (_, _) => FlushPendingLobby();
        ConfirmDialog.Completed += accepted =>
        {
            var action = pendingConfirmation;
            pendingConfirmation = null;
            ConfirmDialogBackdrop.Visibility = Visibility.Collapsed;
            SettingsNavigation.IsEnabled = true;
            SettingsContent.IsEnabled = true;
            if (accepted) action?.Invoke();
        };
    }

    private void BindToggle(CheckBox box, params string[] keys)
        => box.Click += (_, _) => ApplyControls(true, keys);

    private void BindShortcut(TextBox box, string key)
        => box.TextChanged += (_, _) => ApplyControls(true, key);

    private void BindSlider(Slider slider, string key)
    {
        slider.ValueChanged += (_, _) => ApplyControls(false, key);
        slider.PreviewMouseLeftButtonUp += (_, _) => ApplyControls(true, key);
        slider.LostMouseCapture += (_, _) => ApplyControls(true, key);
        slider.KeyUp += (_, _) => ApplyControls(true, key);
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var item in LogicalTreeHelper.GetChildren(parent))
        {
            if (item is not DependencyObject child) continue;
            if (child is T match) yield return match;
            foreach (var descendant in LogicalDescendants<T>(child)) yield return descendant;
        }
    }

    private bool ApplyControls(bool persist, params string[] keys)
    {
        if (!settingsReady || loadingLobbyControls) return true;
        try
        {
            settingsTransaction.Apply(ReadSettingsControls(), persist, keys);
            if (persist) failedSaveKeys.ExceptWith(keys);
            if (failedSaveKeys.Count > 0) return true;
            SaveStatusText.Text = persist ? "変更を保存しました。" : "変更を反映中…";
            SaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xb7, 0xaa, 0xbd));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (persist) failedSaveKeys.UnionWith(keys);
            ShowSaveError(error);
            return false;
        }
    }

    private void QueueLobbyCommit()
    {
        if (!settingsReady || loadingLobbyControls || showingCurrentLobby || !lobbySettingsEditable) return;
        lobbyDraft = ReadLobbyControls();
        lobbyPending = true;
        lobbyCommitTimer.Stop();
        lobbyCommitTimer.Start();
    }

    private bool FlushPendingLobby()
    {
        lobbyCommitTimer.Stop();
        if (!settingsReady || !lobbyPending) return true;
        if (!ApplyControls(true, nameof(ClientSettings.MyLobbySettings), nameof(ClientSettings.RadioOnlyBackup))) return false;
        lobbyPending = false;
        return true;
    }

    private void UpdateOverlayControls()
    {
        var enabled = EnableOverlayCheck.IsChecked == true;
        CompactOverlayCheck.IsEnabled = enabled;
        MeetingOverlayCheck.IsEnabled = enabled;
        OverlayPositionCombo.IsEnabled = enabled;
    }

    private void ChangeExclusiveLobbyMode(bool deadOnly)
    {
        if (showingCurrentLobby || !lobbySettingsEditable) return;
        var toggle = deadOnly ? DeadOnlyCheck : MeetingGhostOnlyCheck;
        var other = deadOnly ? MeetingGhostOnlyCheck : DeadOnlyCheck;
        var requested = toggle.IsChecked == true;
        toggle.IsChecked = deadOnly ? lobbyDraft.DeadOnly : lobbyDraft.MeetingGhostOnly;
        void Apply()
        {
            toggle.IsChecked = requested;
            other.IsChecked = false;
            QueueLobbyCommit();
        }
        if (requested) ConfirmChange(deadOnly ? "生きているプレーヤーのサウンドを無効にする。"
            : "会議以外での生存プレイヤーのサウンドを無効にします", Apply);
        else Apply();
    }

    private void CommitMicrophoneSensitivity()
    {
        if (!settingsReady || pendingConfirmation is not null) return;
        var requested = MicSensitivitySlider.Value;
        void Apply()
        {
            MicSensitivitySlider.Value = requested;
            ApplyControls(true, nameof(ClientSettings.MicSensitivity));
        }
        if (Math.Abs(requested - 0.7) < 0.001 && settings.MicSensitivity < 0.3)
        {
            MicSensitivitySlider.Value = 1d - settings.MicSensitivity;
            ConfirmChange("これ以上、感度を下げるとうまくいかないかもしれません。", Apply);
        }
        else Apply();
    }

    private void ConfirmChange(string message, Action confirmed)
    {
        pendingConfirmation = confirmed;
        SettingsNavigation.IsEnabled = false;
        SettingsContent.IsEnabled = false;
        ConfirmDialogBackdrop.Visibility = Visibility.Visible;
        ConfirmDialog.Open("よろしいですか？", message);
    }

    private void ConfirmDialogBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ConfirmDialogBackdrop))
            ConfirmDialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void ShowSaveError(Exception error)
    {
        SaveStatusText.Text = $"設定を保存できませんでした。閉じる操作で再試行します: {error.Message}";
        SaveStatusText.Foreground = Brushes.IndianRed;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        FlushPendingLobby();
        base.OnDeactivated(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !settingsReady) return;
        if (!FlushPendingLobby()) { e.Cancel = true; return; }
        if (failedSaveKeys.Count > 0 && !ApplyControls(true, failedSaveKeys.ToArray()))
        { e.Cancel = true; return; }
        try { settingsTransaction.Flush(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowSaveError(error);
            e.Cancel = true;
        }
    }

    private void ChangePlayerConfig(int id, TanukiBCL.VoiceProbe.PlayerAudioConfig config, bool persist)
    {
        var candidate = settings.Clone();
        candidate.PlayerConfigMap[id] = config.Normalize();
        try
        {
            settingsTransaction.Apply(candidate, persist, nameof(ClientSettings.PlayerConfigMap));
            onPlayerConfigChanged(id, config, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowSaveError(error);
        }
    }
}
