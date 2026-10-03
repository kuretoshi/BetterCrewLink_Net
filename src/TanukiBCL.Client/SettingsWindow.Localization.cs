namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private readonly LocalizedStaticText staticText = new();

    private void ApplyLanguage(string language)
    {
        Title = UiLocalization.Translate(language, "settings.title");
        staticText.Apply(this, language, PlayerRows);
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
            !Equals(window.NatFixCheck.Content, UiLocalization.Translate("en", "settings.advanced.nat_fix")))
            throw new InvalidOperationException("English settings language did not apply");
        window.LanguageCombo.SelectedItem = UiLocalization.Languages.First(language => language.Code == "ja");
        if (settings.Language != "ja" || window.Title != "設定" || window.LanguageLabel.Text != "言語")
            throw new InvalidOperationException("Japanese settings language was not restored");
        window.Close();
        Console.WriteLine("[PASS] Settings language switches English/Japanese and persists through transaction");
    }
}
