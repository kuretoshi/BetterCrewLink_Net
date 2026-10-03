using System.Windows;
using System.Windows.Input;

namespace TanukiBCL.Client;

public partial class DebugAuthWindow : Window
{
    private DateTimeOffset nextAttempt;

    internal DebugAuthWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        OpenButton.IsEnabled = PasswordInput.Password.Length > 0;
        ErrorText.Text = string.Empty;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || !OpenButton.IsEnabled) return;
        OpenButton_Click(sender, e);
        e.Handled = true;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.UtcNow < nextAttempt) return;
        nextAttempt = DateTimeOffset.UtcNow.AddSeconds(1);
        var authenticated = DeveloperDebugAuth.Verify(PasswordInput.Password);
        PasswordInput.Clear();
        if (authenticated)
        {
            DialogResult = true;
        }
        else
        {
            ErrorText.Text = "認証できませんでした。パスワードと開発者用の設定を確認してください。";
        }
    }

    internal static void VerifyUi()
    {
        var window = new DebugAuthWindow();
        try
        {
            if (window.OpenButton.IsEnabled || window.PasswordInput.Password.Length != 0)
                throw new InvalidOperationException("Developer authentication should start without a password");
            window.PasswordInput.Password = "test";
            if (!window.OpenButton.IsEnabled)
                throw new InvalidOperationException("Developer authentication cannot submit an entered password");
            window.PasswordInput.Clear();
            if (window.OpenButton.IsEnabled)
                throw new InvalidOperationException("Developer authentication retained a cleared password");
        }
        finally { window.Close(); }
    }
}
