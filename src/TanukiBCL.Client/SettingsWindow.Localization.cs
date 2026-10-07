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
        PlayersSectionTitle.Text = UiLocalization.Translate(language, "settings.players.title");
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
        var nosRadio = currentGameState?.Mod == AmongUsModType.NebulaOnTheShip;
        var jackalRadio = currentGameState?.Mod == AmongUsModType.SuperNewRoles;
        ImpostorRadioShortcutLabel.Text = UiLocalization.Translate(language,
            nosRadio ? "settings.keyboard.impostor_radio" :
            jackalRadio ? "settings.keyboard.impostor_jackal_radio" : "settings.keyboard.impostor_radio");
        JackalRadioShortcutRow.Visibility = nosRadio ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        JackalRadioShortcutLabel.Text = UiLocalization.Translate(language, "settings.keyboard.nos_jackal_radio");
        MuteShortcutLabel.Text = UiLocalization.Translate(language, "settings.keyboard.mute");
        DeafenShortcutLabel.Text = UiLocalization.Translate(language, "settings.keyboard.deafen");
        var prompt = UiLocalization.Translate(language, "settings.keyboard.press_key");
        PushToTalkShortcutBox.ToolTip = prompt;
        ImpostorRadioShortcutBox.ToolTip = prompt;
        JackalRadioShortcutBox.ToolTip = prompt;
        MuteShortcutBox.ToolTip = prompt;
        DeafenShortcutBox.ToolTip = prompt;
    }

    internal static void VerifyLocalization()
    {
        UiLocalization.Verify();
        var settings = new ClientSettings();
        var window = new SettingsWindow(settings, true, null, false, null, (_, _, _) => { }, _ => { });
        window.ApplyTemplate();
        bool OverlayLabelsMatch(string language)
        {
            var locations = window.OverlayPositionCombo.Items.OfType<ComboBoxItem>().ToArray();
            return Equals(window.OverlayAlwaysOnTopCheck.Content,
                       UiLocalization.Translate(language, "settings.overlay.always_on_top")) &&
                   Equals(window.EnableOverlayCheck.Content,
                       UiLocalization.Translate(language, "settings.overlay.enabled")) &&
                   Equals(window.CompactOverlayCheck.Content,
                       UiLocalization.Translate(language, "settings.overlay.compact")) &&
                   Equals(window.MeetingOverlayCheck.Content,
                       UiLocalization.Translate(language, "settings.overlay.meeting")) &&
                   window.OverlayPositionLabel.Text == UiLocalization.Translate(language, "settings.overlay.pos") &&
                   locations.Length == 7 && locations.All(location =>
                       location.Tag is string key &&
                       Equals(location.Content,
                           UiLocalization.Translate(language,
                               $"settings.overlay.locations.{(key == "bottom_left" ? "bottom" : key)}")));
        }
        if (!OverlayLabelsMatch("ja"))
            throw new InvalidOperationException("Released Japanese overlay settings labels were not applied: " +
                $"top={window.OverlayAlwaysOnTopCheck.Content}, enabled={window.EnableOverlayCheck.Content}, " +
                $"compact={window.CompactOverlayCheck.Content}, meeting={window.MeetingOverlayCheck.Content}, " +
                $"position={window.OverlayPositionLabel.Text}, locations=" +
                string.Join("|", window.OverlayPositionCombo.Items.OfType<ComboBoxItem>()
                    .Select(item => $"{item.Tag}:{item.Content}")));
        if (window.WindowStyle != System.Windows.WindowStyle.None ||
            window.MinimizeSettingsButton is null || window.CloseSettingsButton is null ||
            window.SaveStatusText.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Released settings title bar or hidden save status was not applied");
        if (window.SettingsPageScrollViewer.Resources["ThinSettingsScrollBarStyle"]
                is not System.Windows.Style scrollBarStyle ||
            !scrollBarStyle.Setters.OfType<System.Windows.Setter>().Any(setter =>
                setter.Property == System.Windows.FrameworkElement.WidthProperty &&
                Equals(setter.Value, 8d)))
            throw new InvalidOperationException("Released settings scrollbar width was not applied");
        if (window.SettingsPageScrollViewer.Margin.Right != -24d ||
            window.SettingsPageScrollViewer.Content is not Grid scrollContent ||
            scrollContent.Margin.Right != 24d)
            throw new InvalidOperationException("Released settings scrollbar did not reach the window edge");
        window.CategoryList.SelectedIndex = 6;
        window.SettingsPageScrollViewer.Measure(new System.Windows.Size(550, 500));
        window.SettingsPageScrollViewer.Arrange(new System.Windows.Rect(0, 0, 550, 500));
        window.ApplySettingsPageScrollBarStyle();
        window.SettingsPageScrollViewer.InvalidateMeasure();
        window.SettingsPageScrollViewer.Measure(new System.Windows.Size(550, 500));
        window.SettingsPageScrollViewer.Arrange(new System.Windows.Rect(0, 0, 550, 500));
        window.SettingsPageScrollViewer.UpdateLayout();
        var pageScrollBar = window.SettingsPageScrollViewer.Template.FindName(
            "PART_VerticalScrollBar", window.SettingsPageScrollViewer)
            as System.Windows.Controls.Primitives.ScrollBar;
        if (pageScrollBar is null || !ReferenceEquals(pageScrollBar.Style, scrollBarStyle) ||
            Math.Abs(pageScrollBar.ActualWidth - 8d) > 0.01d ||
            pageScrollBar.Template.FindName("PART_Track", pageScrollBar)
                is not System.Windows.Controls.Primitives.Track)
            throw new InvalidOperationException(
                $"Released settings scrollbar did not render at 8px (actual={pageScrollBar?.ActualWidth})");
        if (window.SettingsPageScrollViewer.ExtentHeight <=
            window.SettingsPageScrollViewer.ViewportHeight)
            throw new InvalidOperationException($"Advanced settings page did not expose a scrollable range: " +
                $"extent={window.SettingsPageScrollViewer.ExtentHeight}, " +
                $"viewport={window.SettingsPageScrollViewer.ViewportHeight}");
        if (window.AudioPanel.Children.OfType<System.Windows.Controls.Border>().Count() != 5 ||
            window.MicrophoneLevelStatus.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Released audio settings card layout was not applied");
        if (window.OverlayPositionCombo.Style != window.MicrophoneCombo.Style ||
            window.ServerUrlBox.Style != window.MicrophoneCombo.Style ||
            (window.OverlayPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string != "right" ||
            window.ServerUrlBox.SelectedItem as string != settings.ServerUrl)
            throw new InvalidOperationException("Overlay or server selector lost its released dark style or saved selection");
        if (new[] { window.MobileHostCheck, window.HardwareAccelerationCheck,
                window.EchoCancellationCheck, window.AutoGainControlCheck,
                window.SpatialAudioCheck, window.NoiseSuppressionCheck,
                window.OldSampleDebugCheck }
            .Any(toggle => toggle.Style != window.EnableOverlayCheck.Style))
            throw new InvalidOperationException("Advanced beta controls did not use the released right-side switch style");
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
        var keyboardHint = window.KeybindsPanel.Children.OfType<Border>().First();
        if (keyboardHint.MinHeight != 68 ||
            keyboardHint.Background is not System.Windows.Media.SolidColorBrush hintBackground ||
            hintBackground.Color != System.Windows.Media.Color.FromRgb(7, 27, 37) ||
            keyboardHint.Child is not DockPanel hintContent ||
            hintContent.Children.OfType<Viewbox>().Count() != 1 ||
            hintContent.Children.OfType<System.Windows.Controls.TextBlock>().Count() != 1)
            throw new InvalidOperationException("Released keyboard shortcut information banner was not applied");
        window.CategoryList.SelectedIndex = 4;
        if (window.SettingsContent.Margin.Top != 16)
            throw new InvalidOperationException("Keyboard shortcut alert did not align to the released top inset");
        window.CategoryList.SelectedIndex = 0;
        if (window.SettingsContent.Margin.Top != 24)
            throw new InvalidOperationException("General settings top inset was not restored");
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
            window.OpenDebugButton.Style != window.Resources["DebugOutlinedButtonStyle"] ||
            window.OpenDebugButton.Width != 145 || window.OpenDebugButton.Height != 60 ||
            window.OpenDebugButton.ContentTemplate?.LoadContent() is not TextBlock
                { MaxWidth: 90, TextWrapping: System.Windows.TextWrapping.Wrap } ||
            window.DebugInfoDescription.Text != "ゲーム状態・音声接続・ログを別ウィンドウで表示します。" ||
            window.OldSampleDebugDescription.Text != "依頼された場合のみ有効にするテスト機能です。")
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
        window.debugAuthenticator = _ => Task.FromResult(DebugAuthResult.Unavailable);
        window.OpenDebugButton_Click(window.OpenDebugButton, new System.Windows.RoutedEventArgs());
        window.DebugPasswordInput.Password = "test";
        window.SubmitDebugAuthButton_Click(window.SubmitDebugAuthButton, new System.Windows.RoutedEventArgs());
        if (window.DebugAuthBackdrop.Visibility != System.Windows.Visibility.Visible ||
            window.DebugPasswordInput.Password.Length != 0 ||
            !window.DebugPasswordMessage.Text.StartsWith("認証サーバーに接続できないか", StringComparison.Ordinal))
            throw new InvalidOperationException("Unavailable debug authentication message differs from 3.2.9");
        window.debugAuthenticator = _ => Task.FromResult(DebugAuthResult.Denied);
        window.nextDebugAuthAttempt = default;
        window.DebugPasswordInput.Password = "test";
        window.SubmitDebugAuthButton_Click(window.SubmitDebugAuthButton, new System.Windows.RoutedEventArgs());
        if (!window.DebugPasswordMessage.Text.StartsWith("認証できませんでした。配布された", StringComparison.Ordinal))
            throw new InvalidOperationException("Denied debug authentication message differs from 3.2.9");
        window.CancelDebugAuthButton_Click(window.CancelDebugAuthButton, new System.Windows.RoutedEventArgs());
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
            window.NatFixDescription.Text != UiLocalization.Translate("en", "settings.advanced.nat_fix_warning") ||
            !Equals(window.HauntingCheck.Content, UiLocalization.Translate("en", "settings.lobbysettings.impostorshearsghost")) ||
            !Equals(window.ImpostorVentCheck.Content, UiLocalization.Translate("en", "settings.lobbysettings.private_talk_invents")) ||
            !Equals(window.PublicLobbyOnCheck.Content, UiLocalization.Translate("en", "settings.lobbysettings.public_lobby.enabled")) ||
            window.NoLobbyText.Text != UiLocalization.Translate("en", "settings.lobbysettings.no_lobby") ||
            window.RadioOnlyDescription.Text != UiLocalization.Translate("en", "settings.lobbysettings.impostor_radio_only_description") ||
            window.DeadOnlyDescription.Text != UiLocalization.Translate("en", "settings.lobbysettings.ghost_only_warning") ||
            window.MeetingGhostOnlyDescription.Text != UiLocalization.Translate("en", "settings.lobbysettings.meetings_only_warning") ||
            window.LobbyNotice.Text != UiLocalization.Translate("en", "settings.lobbysettings.mine_notice") ||
            !Equals(window.CopyObsUrlButton.ToolTip, "Copy URL") ||
            !OverlayLabelsMatch("en"))
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
        if (window.PlayersEmptyText.Text != "No other players right now. Join a lobby to adjust their volume." ||
            window.PlayersEmptyAlert.Visibility != System.Windows.Visibility.Visible ||
            window.PlayersCard.Visibility != System.Windows.Visibility.Collapsed)
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
        window.UpdateCurrentGameState(new AmongUsState
        {
            Mod = AmongUsModType.SuperNewRoles,
            Players = [
                new Player { ClientId = 30, Name = "First", PlayerConfigId = 30 },
                new Player { ClientId = 10, Name = "Second", PlayerConfigId = 10 },
                new Player { ClientId = 20, Name = "Dummy", IsDummy = true }
            ]
        });
        if (window.PlayerRows.Children.Count != 2 ||
            window.PlayerRows.Children[0] is not Border { Child: Grid firstRow } ||
            firstRow.Children[0] is not StackPanel firstLabels ||
            firstLabels.Children[0] is not StackPanel firstHeading ||
            firstHeading.Children[1] is not TextBlock { Text: "First" } ||
            window.PlayerRows.Children[1] is not Border { Child: Grid secondRow } lastRow ||
            secondRow.Children[0] is not StackPanel secondLabels ||
            secondLabels.Children[0] is not StackPanel secondHeading ||
            secondHeading.Children[1] is not TextBlock { Text: "Second" } ||
            firstRow.ColumnDefinitions[1].MaxWidth != 300 ||
            !firstRow.ColumnDefinitions[1].Width.IsStar ||
            lastRow.BorderThickness.Bottom != 0)
            throw new InvalidOperationException("Player settings rows did not preserve released roster order or card layout");
        var firstControls = (Grid)firstRow.Children[1];
        var firstMute = (Button)firstControls.Children[0];
        var firstSlider = (Slider)firstControls.Children[1];
        firstMute.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        if (settings.PlayerConfigMap[30].IsMuted != true || firstSlider.IsEnabled)
            throw new InvalidOperationException("Player mute control did not apply immediately");
        firstMute.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        firstSlider.Value = 1.5;
        if (settings.PlayerConfigMap[30].IsMuted ||
            Math.Abs(settings.PlayerConfigMap[30].Volume - 1.5) > 0.000001)
            throw new InvalidOperationException("Player volume control did not apply the preview value");
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
            if (window.PlayersCard.Visibility != System.Windows.Visibility.Visible ||
                window.PlayersSectionTitle.Text != UiLocalization.Translate(settings.Language, "settings.players.title") ||
                window.PlayerRows.Children[0] is not Border { Child: Grid content } ||
                content.Children[0] is not StackPanel labels ||
                labels.Children[1] is not TextBlock disconnectedText ||
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
