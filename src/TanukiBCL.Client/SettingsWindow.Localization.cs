using System.Windows.Controls;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private readonly LocalizedStaticText staticText = new();

    private void ApplyLanguage(string language)
    {
        Title = UiLocalization.Translate(language, "settings.title");
        staticText.Apply(this, language, PlayerRows);
        UpdateLobbyNotice();
        UpdateDistanceTitle();
        CopyObsUrlButton.ToolTip = UiLocalization.Translate(language, "settings.streaming.copy_url");
        UpdateShortcutLabels();
        RenderPlayers();
    }

    private void UpdateShortcutLabels()
    {
        var language = settings.Language;
        KeybindHintText.Text = UiLocalization.Translate(language, "settings.keyboard.hint");
        KeybindsTitleText.Text = UiLocalization.Translate(language, "settings.keyboard.title");
        PushToTalkShortcutLabel.Text = UiLocalization.Translate(language, "settings.keyboard.push_to_talk");
        var jackalRadio = currentGameState?.Mod is AmongUsModType.SuperNewRoles or AmongUsModType.NebulaOnTheShip;
        ImpostorRadioShortcutLabel.Text = UiLocalization.Translate(language,
            jackalRadio ? "settings.keyboard.impostor_jackal_radio" : "settings.keyboard.impostor_radio");
        MuteShortcutLabel.Text = UiLocalization.Translate(language, "settings.keyboard.mute");
        DeafenShortcutLabel.Text = UiLocalization.Translate(language, "settings.keyboard.deafen");
        var prompt = UiLocalization.Translate(language, "settings.keyboard.press_key");
        PushToTalkShortcutBox.ToolTip = prompt;
        ImpostorRadioShortcutBox.ToolTip = prompt;
        MuteShortcutBox.ToolTip = prompt;
        DeafenShortcutBox.ToolTip = prompt;
    }

    internal static void VerifyLocalization()
    {
        UiLocalization.Verify();
        var settings = new ClientSettings();
        var window = new SettingsWindow(settings, true, null, false, null, (_, _, _) => { }, _ => { });
        window.ApplyTemplate();
        if (window.WindowStyle != System.Windows.WindowStyle.None ||
            window.MinimizeSettingsButton is null || window.CloseSettingsButton is null ||
            window.SaveStatusText.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Released settings title bar or hidden save status was not applied");
        if (window.AudioPanel.Children.OfType<System.Windows.Controls.Border>().Count() != 5 ||
            window.MicrophoneLevelStatus.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Released audio settings card layout was not applied");
        if (window.StreamingPanel.Children.OfType<Border>().Count() != 1 ||
            window.ShowLobbyCodeCheck.Style != window.ObsOverlayCheck.Style ||
            window.CopyObsUrlButton.Content is not System.Windows.Shapes.Path ||
            window.ObsUrlBox.FontSize != 14 ||
            window.ObsUrlBox.Foreground is not System.Windows.Media.SolidColorBrush urlForeground ||
            urlForeground.Color != System.Windows.Media.Color.FromRgb(245, 241, 247))
            throw new InvalidOperationException("Released streaming settings card layout was not applied");
        if (window.KeybindsPanel.Children.OfType<Border>().Count() != 2 ||
            window.PushToTalkShortcutBox.Style != window.ImpostorRadioShortcutBox.Style ||
            window.PushToTalkShortcutBox.Style != window.MuteShortcutBox.Style ||
            window.PushToTalkShortcutBox.Style != window.DeafenShortcutBox.Style)
            throw new InvalidOperationException("Released keyboard shortcut card layout was not applied");
        window.PushToTalkShortcutBox.ApplyTemplate();
        var prompt = window.PushToTalkShortcutBox.Template.FindName(
            "RecordingPrompt", window.PushToTalkShortcutBox) as StackPanel;
        if (prompt is null) throw new InvalidOperationException("Shortcut recording prompt template was not applied");
        var originalShortcut = window.PushToTalkShortcutBox.Text;
        SetShortcutRecording(window.PushToTalkShortcutBox, true);
        if (!IsShortcutRecording(window.PushToTalkShortcutBox) ||
            prompt.Visibility != System.Windows.Visibility.Visible ||
            window.PushToTalkShortcutBox.Text != originalShortcut)
            throw new InvalidOperationException("Shortcut recording changed the saved shortcut or hid its prompt");
        SetShortcutRecording(window.PushToTalkShortcutBox, false);
        if (prompt.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Shortcut recording prompt did not close");
        if (window.AdvancedPanel.Children.OfType<Border>().Count() != 3 ||
            window.NatFixCheck.Style != window.OverlayAlwaysOnTopCheck.Style ||
            window.OpenDebugButton is null)
            throw new InvalidOperationException("Advanced settings section card layout was not applied");
        window.OpenDebugButton_Click(window.OpenDebugButton, new System.Windows.RoutedEventArgs());
        if (window.DebugAuthBackdrop.Visibility != System.Windows.Visibility.Visible ||
            window.SubmitDebugAuthButton.IsEnabled ||
            window.SettingsContent.IsEnabled || window.SettingsNavigation.IsEnabled)
            throw new InvalidOperationException("Developer authentication did not open as a settings dialog");
        window.DebugPasswordInput.Password = "test";
        if (!window.SubmitDebugAuthButton.IsEnabled)
            throw new InvalidOperationException("Developer authentication did not accept entered text");
        window.CancelDebugAuthButton_Click(window.CancelDebugAuthButton, new System.Windows.RoutedEventArgs());
        if (window.DebugAuthBackdrop.Visibility != System.Windows.Visibility.Collapsed ||
            window.DebugPasswordInput.Password.Length != 0 ||
            !window.SettingsContent.IsEnabled || !window.SettingsNavigation.IsEnabled)
            throw new InvalidOperationException("Developer authentication did not clear on cancel");
        var debugOpened = false;
        var settingsClosed = false;
        window.DebugOpenRequested += () => debugOpened = true;
        window.Closed += (_, _) => settingsClosed = true;
        window.OpenAuthenticatedDebugInfo();
        if (!debugOpened || settingsClosed)
            throw new InvalidOperationException("Opening debug info must leave settings open");
        var generalItem = (System.Windows.Controls.ListBoxItem)window.CategoryList.Items[0];
        generalItem.ApplyTemplate();
        if (generalItem.Template.FindName("NavIcon", generalItem) is not System.Windows.Shapes.Path
            { Data: not null })
            throw new InvalidOperationException("Settings navigation icon template was not applied");
        window.OverlayAlwaysOnTopCheck.ApplyTemplate();
        if (window.OverlayAlwaysOnTopCheck.Template.FindName("SwitchTrack",
                window.OverlayAlwaysOnTopCheck) is not System.Windows.Controls.Border)
            throw new InvalidOperationException("Overlay switch template was not applied");
        window.LanguageCombo.SelectedItem = UiLocalization.Languages.First(language => language.Code == "en");
        if (settings.Language != "en" || window.Title != "Settings" ||
            window.LanguageLabel.Text != "Language" ||
            window.CategoryList.Items[0] is not System.Windows.Controls.ListBoxItem { Content: "General" } ||
            window.CategoryList.Items[4] is not System.Windows.Controls.ListBoxItem
                { Content: "Keyboard Shortcuts" } ||
            !Equals(window.VoiceModeRadio.Content, UiLocalization.Translate("en", "settings.audio.voice_activity")) ||
            window.NatFixLabel.Text != UiLocalization.Translate("en", "settings.advanced.nat_fix") ||
            !Equals(window.HauntingCheck.Content, UiLocalization.Translate("en", "settings.lobbysettings.impostorshearsghost")) ||
            !Equals(window.ImpostorVentCheck.Content, UiLocalization.Translate("en", "settings.lobbysettings.private_talk_invents")) ||
            !Equals(window.PublicLobbyOnCheck.Content, UiLocalization.Translate("en", "settings.lobbysettings.public_lobby.enabled")) ||
            window.NoLobbyText.Text != UiLocalization.Translate("en", "settings.lobbysettings.no_lobby") ||
            window.RadioOnlyDescription.Text != UiLocalization.Translate("en", "settings.lobbysettings.impostor_radio_only_description") ||
            window.DeadOnlyDescription.Text != UiLocalization.Translate("en", "settings.lobbysettings.ghost_only_warning") ||
            window.MeetingGhostOnlyDescription.Text != UiLocalization.Translate("en", "settings.lobbysettings.meetings_only_warning") ||
            window.LobbyNotice.Text != UiLocalization.Translate("en", "settings.lobbysettings.mine_notice") ||
            !Equals(window.CopyObsUrlButton.ToolTip, "Copy URL"))
            throw new InvalidOperationException("English settings language did not apply");
        window.VisionHearingCheck.IsChecked = true;
        if (window.DistanceTitleText.Text != UiLocalization.Translate("en", "settings.lobbysettings.voicedistance_impostor"))
            throw new InvalidOperationException("Vision-hearing distance title did not follow English language");
        if (window.HearVentsCheck.Content?.ToString() != UiLocalization.Translate("en", "settings.lobbysettings.hear_imposters_invents") ||
            window.CommsSabotageCheck.Content?.ToString() != UiLocalization.Translate("en", "settings.lobbysettings.comms_sabotage_audio") ||
            window.VoiceEffectEnabledCheck.Content?.ToString() != UiLocalization.Translate("en", "settings.lobbysettings.voice_effect_enabled") ||
            window.DeadOnlyCheck.Content?.ToString() != UiLocalization.Translate("en", "settings.lobbysettings.ghost_only") ||
            window.MeetingGhostOnlyCheck.Content?.ToString() != UiLocalization.Translate("en", "settings.lobbysettings.meetings_only") ||
            window.NosVoicePositionsCheck.Content is not TextBlock positionLabel ||
            positionLabel.Text != UiLocalization.Translate("en", "settings.lobbysettings.nos_voice_positions"))
            throw new InvalidOperationException("Lobby switches did not follow English language");
        window.UpdateCurrentGameState(new AmongUsState { IsHost = true, GameState = GameState.Tasks });
        if (window.LobbyNotice.Text != UiLocalization.Translate("en", "settings.lobbysettings.inlobbyonly"))
            throw new InvalidOperationException("Active host lobby notice did not follow English language");
        window.UpdateCurrentLobbySettings(new LobbySettings());
        window.UpdateCurrentGameState(new AmongUsState
        {
            IsHost = true, GameState = GameState.Lobby, LobbyCode = "ABCDEF"
        });
        window.CurrentLobbyTab.IsChecked = true;
        if (window.LobbyHostPanel.Visibility != System.Windows.Visibility.Visible ||
            window.LobbyInfoAlert.Visibility != System.Windows.Visibility.Collapsed ||
            window.LobbyHostNotice.Text != UiLocalization.Translate("en", "settings.lobbysettings.host_notice_you") ||
            window.LobbyHostName.Text != UiLocalization.Translate("en", "settings.lobbysettings.host_you") ||
            window.LobbyHostEditButton.Visibility != System.Windows.Visibility.Visible)
            throw new InvalidOperationException("Current host lobby card did not follow released behavior");
        window.LobbyHostEditButton.RaiseEvent(new System.Windows.RoutedEventArgs(
            System.Windows.Controls.Button.ClickEvent));
        if (window.MyLobbyTab.IsChecked != true)
            throw new InvalidOperationException("Host lobby edit button did not open own settings");
        window.UpdateCurrentGameState(new AmongUsState
        {
            GameState = GameState.Lobby, LobbyCode = "ABCDEF", HostId = 8,
            Players = [new Player { ClientId = 8, Name = "Alice" }]
        });
        window.CurrentLobbyTab.IsChecked = true;
        if (window.LobbyHostName.Text != "Alice" ||
            window.LobbyHostNotice.Text != UiLocalization.Translate("en", "settings.lobbysettings.host_notice") ||
            window.LobbyHostEditButton.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Guest lobby host card did not show the game host");
        window.UpdateCurrentGameState(new AmongUsState { GameState = GameState.Menu });
        if (window.LobbyInfoAlert.Visibility != System.Windows.Visibility.Visible ||
            window.NoLobbyText.Visibility != System.Windows.Visibility.Visible ||
            window.LobbyHostPanel.Visibility != System.Windows.Visibility.Collapsed ||
            window.LobbyControlsPanel.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Current lobby remained visible after leaving the game");
        window.MyLobbyTab.IsChecked = true;
        window.VisionHearingCheck.IsChecked = true;
        VerifyShortcutLabels("en", false);
        if (window.PlayerRows.Children[0] is not TextBlock { Text: "No other players right now. Join a lobby to adjust their volume." })
            throw new InvalidOperationException("Empty player state was not translated");
        settings.PlayerConfigMap[51] = new PlayerAudioConfig(IsMuted: true);
        window.UpdateCurrentGameState(new AmongUsState
        {
            Mod = AmongUsModType.SuperNewRoles,
            Players = [new Player { Name = "Remote", PlayerConfigId = 51, Disconnected = true }]
        });
        VerifyShortcutLabels("en", true);
        VerifyPlayerRow("Disconnected", "Unmute", "Muted");
        window.LanguageCombo.SelectedItem = UiLocalization.Languages.First(language => language.Code == "ja");
        if (settings.Language != "ja" || window.Title != "設定" || window.LanguageLabel.Text != "言語" ||
            window.DistanceTitleText.Text != UiLocalization.Translate("ja", "settings.lobbysettings.voicedistance_impostor") ||
            window.LobbyNotice.Text != UiLocalization.Translate("ja", "settings.lobbysettings.mine_notice") ||
            !Equals(window.CopyObsUrlButton.ToolTip, "URLをコピー"))
            throw new InvalidOperationException("Japanese settings language was not restored");
        VerifyPlayerRow("切断済み", "ミュート解除", "ミュート中");
        VerifyShortcutLabels("ja", true);
        window.Close();
        Console.WriteLine("[PASS] Settings language switches English/Japanese and persists through transaction");

        void VerifyShortcutLabels(string language, bool jackalRadio)
        {
            if (window.KeybindHintText.Text != UiLocalization.Translate(language, "settings.keyboard.hint") ||
                window.KeybindsTitleText.Text != UiLocalization.Translate(language, "settings.keyboard.title") ||
                window.PushToTalkShortcutLabel.Text != UiLocalization.Translate(language, "settings.keyboard.push_to_talk") ||
                window.ImpostorRadioShortcutLabel.Text != UiLocalization.Translate(language,
                    jackalRadio ? "settings.keyboard.impostor_jackal_radio" : "settings.keyboard.impostor_radio") ||
                window.MuteShortcutLabel.Text != UiLocalization.Translate(language, "settings.keyboard.mute") ||
                window.DeafenShortcutLabel.Text != UiLocalization.Translate(language, "settings.keyboard.deafen") ||
                !Equals(window.PushToTalkShortcutBox.ToolTip, UiLocalization.Translate(language, "settings.keyboard.press_key")))
                throw new InvalidOperationException("Keyboard shortcut labels did not follow game MOD and language");
        }

        void VerifyPlayerRow(string disconnected, string unmute, string muted)
        {
            if (window.PlayerRows.Children[0] is not Border { Child: StackPanel content } ||
                content.Children[0] is not StackPanel heading ||
                heading.Children[2] is not TextBlock disconnectedText ||
                !disconnectedText.Text.Contains(disconnected, StringComparison.Ordinal) ||
                content.Children[1] is not Grid controls ||
                controls.Children[0] is not Button muteButton ||
                !Equals(muteButton.ToolTip, unmute) ||
                controls.Children[2] is not TextBlock { Text: var volumeText } ||
                volumeText != muted)
                throw new InvalidOperationException("Player settings row did not follow the selected language");
        }
    }
}
