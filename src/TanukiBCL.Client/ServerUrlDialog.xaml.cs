using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;

namespace TanukiBCL.Client;

/// <summary>ServerURLInput.tsx from release 9861ccc, hosted inside the settings window.</summary>
public partial class ServerUrlDialog : UserControl
{
    internal const string DefaultUrl = "https://bettercrewl.ink";
    private string? initialUrl;
    internal event Action<string>? Confirmed;
    internal event Action? Dismissed;

    public ServerUrlDialog() => InitializeComponent();

    internal void Open(string currentUrl)
    {
        SynchronizeSelection(currentUrl);
        UrlInput.Focus();
    }

    internal void SynchronizeSelection(string currentUrl)
    {
        // Like upstream's initialURL effect, a changed selection resets the editor.
        // Backdrop/Escape dismissal deliberately keeps the draft until Cancel or a new selection.
        if (initialUrl != currentUrl)
        {
            initialUrl = currentUrl;
            UrlInput.Text = currentUrl;
            // Upstream trusts an already-saved initial URL, including legacy
            // entries. Validation starts when the user edits the new-URL field.
            ShowValidation(true);
        }
    }

    internal void Dismiss() => Dismissed?.Invoke();

    internal static bool IsValidUrl(string candidate) =>
        // Match release 3.2.7's valid-url 1.0.9 prefilter before parsing the URL.
        // In particular, a lone '%' in a query is accepted there; Uri's
        // IsWellFormedOriginalString would incorrectly reject that saved input.
        Regex.IsMatch(candidate, "^https?://[^/?#]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(candidate, "[^a-z0-9:/?#[\\]@!$&'()*+,;=.\\-_~%]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(candidate, "%[^0-9a-f]|%[0-9a-f](:?[^0-9a-f]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
        parsed.Scheme is "http" or "https" &&
        parsed.AbsolutePath == "/" &&
        !string.Equals(parsed.Host, "discord.gg", StringComparison.OrdinalIgnoreCase);

    private void UrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmButton is not null) UpdateValidation();
    }

    private void UpdateValidation()
    {
        ShowValidation(IsValidUrl(UrlInput.Text.Trim()));
    }

    private void ShowValidation(bool valid)
    {
        ConfirmButton.IsEnabled = valid;
        InputError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        var brush = valid ? new SolidColorBrush(Color.FromArgb(102, 255, 255, 255)) : Brushes.IndianRed;
        InputOutline.BorderBrush = brush;
        InputLabel.Foreground = valid ? new SolidColorBrush(Color.FromArgb(179, 255, 255, 255)) : Brushes.IndianRed;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlInput.Text.Trim();
        if (!ConfirmButton.IsEnabled) return;
        if (url.EndsWith('/')) url = url[..^1];
        Confirmed?.Invoke(url);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => Confirmed?.Invoke(DefaultUrl);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        UrlInput.Text = initialUrl ?? DefaultUrl;
        ShowValidation(true);
        Dismiss();
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    internal static void VerifyBehavior()
    {
        // Operate the actual controls/handlers, not a duplicate state machine.
        var dialog = new ServerUrlDialog();
        var confirmed = new List<string>();
        var dismissed = 0;
        dialog.Confirmed += confirmed.Add;
        dialog.Dismissed += () => dismissed++;
        dialog.Open(DefaultUrl);
        foreach (var invalid in new[] { "", "ftp://voice.test", "https://voice.test/api",
                     "https://discord.gg", "https://voice.test/bad path", "https:\\voice.test",
                     "https://voice.test/?q=%2", "https://voice.test/?q=%2G", "https:////voice.test", "https://音声.test" })
        {
            dialog.UrlInput.Text = invalid;
            if (dialog.ConfirmButton.IsEnabled || dialog.InputError.Visibility != Visibility.Visible)
                throw new InvalidOperationException($"Server dialog accepted {invalid}");
            dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        if (confirmed.Count != 0) throw new InvalidOperationException("Invalid URL escaped validation");
        foreach (var valid in new[] { "https://voice.test/", "http://localhost:3000", "https://voice.test/?room=one",
                     "https://voice.test/?q=%", "http://[::1]:3000/" })
        {
            dialog.UrlInput.Text = valid;
            if (!dialog.ConfirmButton.IsEnabled || dialog.InputError.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException($"Server dialog rejected {valid}");
        }
        dialog.UrlInput.Text = " https://voice.test/ ";
        dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!confirmed.SequenceEqual(new[] { "https://voice.test" }))
            throw new InvalidOperationException("Server confirmation did not normalize URL");
        dialog.UrlInput.Text = "https://draft.test";
        dialog.Dismiss();
        dialog.Open(DefaultUrl);
        if (dialog.UrlInput.Text != "https://draft.test" || dismissed != 1)
            throw new InvalidOperationException("Backdrop dismissal did not preserve URL draft");
        dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.Open(DefaultUrl);
        if (dialog.UrlInput.Text != DefaultUrl || confirmed.Count != 1 || dismissed != 2)
            throw new InvalidOperationException("Cancel changed server or failed to restore the editor");
        dialog.UrlInput.Text = "invalid";
        dialog.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (confirmed.Count != 2 || confirmed[1] != DefaultUrl)
            throw new InvalidOperationException("Default action did not confirm the default server");
        dialog.Open("https://selected.test");
        if (dialog.UrlInput.Text != "https://selected.test")
            throw new InvalidOperationException("Changed server selection did not reset editor");
        dialog.UrlInput.Text = "https://draft.test";
        dialog.Dismiss();
        dialog.SynchronizeSelection(DefaultUrl);
        dialog.SynchronizeSelection("https://selected.test");
        dialog.Open("https://selected.test");
        if (dialog.UrlInput.Text != "https://selected.test")
            throw new InvalidOperationException("History changes while closed resurrected an old draft");
        dialog.Open("https://legacy.test/api");
        if (!dialog.ConfirmButton.IsEnabled)
            throw new InvalidOperationException("An existing history URL was rejected without editing");
        dialog.UrlInput.Text = "https://legacy.test/other";
        if (dialog.ConfirmButton.IsEnabled)
            throw new InvalidOperationException("Edited history URL bypassed validation");
    }
}
