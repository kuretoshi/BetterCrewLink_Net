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
        RenderPlayers();
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
            !Equals(window.NatFixCheck.Content, UiLocalization.Translate("en", "settings.advanced.nat_fix")) ||
            !Equals(window.CopyObsUrlButton.ToolTip, "Copy URL"))
            throw new InvalidOperationException("English settings language did not apply");
        if (window.PlayerRows.Children[0] is not TextBlock { Text: "No other players right now. Join a lobby to adjust their volume." })
            throw new InvalidOperationException("Empty player state was not translated");
        settings.PlayerConfigMap[51] = new PlayerAudioConfig(IsMuted: true);
        window.UpdateCurrentGameState(new AmongUsState
        {
            Players = [new Player { Name = "Remote", PlayerConfigId = 51, Disconnected = true }]
        });
        VerifyPlayerRow("Disconnected", "Unmute", "Muted");
        window.LanguageCombo.SelectedItem = UiLocalization.Languages.First(language => language.Code == "ja");
        if (settings.Language != "ja" || window.Title != "設定" || window.LanguageLabel.Text != "言語" ||
            !Equals(window.CopyObsUrlButton.ToolTip, "URLをコピー"))
            throw new InvalidOperationException("Japanese settings language was not restored");
        VerifyPlayerRow("切断済み", "ミュート解除", "ミュート中");
        window.Close();
        Console.WriteLine("[PASS] Settings language switches English/Japanese and persists through transaction");

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
