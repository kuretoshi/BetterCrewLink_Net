using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TanukiBCL.Client;

public partial class InquiryWindow : Window
{
    private readonly ObservableCollection<InquiryAttachment> attachments = [];
    private bool sending;
    private bool shuttingDown;

    public InquiryWindow()
    {
        InitializeComponent();
        AttachmentsList.ItemsSource = attachments;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => UpdateSendAvailability();

    private void UpdateSendAvailability()
    {
        if (SendButton is not null)
            SendButton.IsEnabled = !sending && !string.IsNullOrWhiteSpace(SubjectBox?.Text) &&
                !string.IsNullOrWhiteSpace(BodyBox?.Text);
    }

    private void SelectFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "添付ファイルを選択" };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames)
        {
            if (attachments.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var file = new FileInfo(path);
                attachments.Add(new InquiryAttachment(file.FullName, file.Name, file.Length));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowResult($"添付ファイルを読み取れません: {error.Message}", true);
            }
        }
    }

    private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InquiryAttachment attachment }) attachments.Remove(attachment);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeInquiryButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseInquiryButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null;
             node = VisualTreeHelper.GetParent(node))
            if (node is Button) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!shuttingDown)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    internal void CloseForShutdown()
    {
        shuttingDown = true;
        Close();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sending) return;
        sending = true;
        UpdateSendAvailability();
        TagCombo.IsEnabled = SubjectBox.IsEnabled = BodyBox.IsEnabled = SelectFilesButton.IsEnabled =
            AttachmentsList.IsEnabled = CancelButton.IsEnabled = false;
        ResultText.Visibility = Visibility.Collapsed;
        try
        {
            var tag = TagCombo.SelectedIndex switch
            {
                1 => InquiryTag.Bug,
                2 => InquiryTag.Request,
                _ => InquiryTag.Question
            };
            await InquirySubmission.SubmitAsync(SubjectBox.Text, BodyBox.Text, tag, attachments.ToArray());
            SubjectBox.Clear();
            BodyBox.Clear();
            TagCombo.SelectedIndex = 0;
            attachments.Clear();
            ShowResult("送信しました。お問い合わせありがとうございます。", false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or
            UnauthorizedAccessException or InvalidOperationException or UriFormatException)
        {
            ShowResult(error.Message, true);
        }
        finally
        {
            sending = false;
            TagCombo.IsEnabled = SubjectBox.IsEnabled = BodyBox.IsEnabled = SelectFilesButton.IsEnabled =
                AttachmentsList.IsEnabled = CancelButton.IsEnabled = true;
            UpdateSendAvailability();
        }
    }

    private void ShowResult(string message, bool isError)
    {
        ResultText.Text = message;
        ResultText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(0xF4, 0x43, 0x36) :
            Color.FromRgb(0x81, 0xC7, 0x84));
        ResultText.Visibility = Visibility.Visible;
    }

    internal static void VerifyForm()
    {
        var window = new InquiryWindow();
        try
        {
            if (window.WindowStyle != WindowStyle.None || window.MinimizeInquiryButton.Content?.ToString() != "−" ||
                window.CloseInquiryButton.Content?.ToString() != "×" ||
                window.Width != 620 || window.Height != 640)
                throw new InvalidOperationException("Inquiry window frame differs from v3.2.8");
            if (window.SendButton.IsEnabled || window.TagCombo.Items.Count != 3)
                throw new InvalidOperationException("Inquiry form initial state differs from v3.2.7");
            window.SubjectBox.Text = "件名";
            window.BodyBox.Text = "本文";
            if (!window.SendButton.IsEnabled)
                throw new InvalidOperationException("Valid inquiry did not enable Send");
            window.BodyBox.Text = " ";
            if (window.SendButton.IsEnabled)
                throw new InvalidOperationException("Blank inquiry body enabled Send");
            window.Show();
            window.Close();
            if (window.IsVisible || window.SubjectBox.Text != "件名")
                throw new InvalidOperationException("Inquiry close did not hide and preserve its form");
            window.Show();
            if (!window.IsVisible || window.SubjectBox.Text != "件名")
                throw new InvalidOperationException("Inquiry reopen did not restore its form");
        }
        finally { window.CloseForShutdown(); }
    }
}
