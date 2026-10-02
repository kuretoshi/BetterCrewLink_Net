using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TanukiBCL.Client;

/// <summary>SettingsControls.tsx useConfirmDialog from release 9861ccc.</summary>
public partial class SettingsConfirmDialog : UserControl
{
    private bool pending;
    internal event Action<bool>? Completed;

    public SettingsConfirmDialog() => InitializeComponent();

    internal void Open(string title, string message)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        pending = true;
        // Upstream autofocus is on Cancel, so Enter must not implicitly accept.
        FocusManager.SetFocusedElement(this, CancelButton);
        CancelButton.Focus();
    }

    private void Complete(bool confirmed)
    {
        if (!pending) return;
        pending = false;
        Completed?.Invoke(confirmed);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Complete(true);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Complete(false);

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (pending) CancelButton.Focus();
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Complete(false);
        e.Handled = true;
    }

    internal static void VerifyBehavior()
    {
        var dialog = new SettingsConfirmDialog();
        var completions = new List<bool>();
        dialog.Completed += completions.Add;
        dialog.Open("よろしいですか？", "確認後に設定を変更します。");
        if (dialog.TitleText.Text != "よろしいですか？" ||
            dialog.MessageText.Text != "確認後に設定を変更します。" || completions.Count != 0)
            throw new InvalidOperationException("Opening settings confirmation did not display the supplied text or completed early");
        dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!completions.SequenceEqual(new[] { true }))
            throw new InvalidOperationException("Confirmation did not complete exactly once with true");
        dialog.Open("確認の取り消し", "キャンセルでは変更しません。");
        dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!completions.SequenceEqual(new[] { true, false }) ||
            dialog.TitleText.Text != "確認の取り消し" || dialog.MessageText.Text != "キャンセルでは変更しません。")
            throw new InvalidOperationException("Reopened confirmation did not update text or complete with false on Cancel");
    }
}
