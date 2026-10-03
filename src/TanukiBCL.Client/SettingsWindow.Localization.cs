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
            window.CopyObsUrlButton.Content is not System.Windows.Shapes.Path)
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
            !Equals(window.CopyObsUrlButton.ToolTip, "Copy URL"))
            throw new InvalidOperationException("English settings language did not apply");
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
