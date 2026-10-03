using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        RefreshFieldLabels();
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateSendAvailability();
        RefreshFieldLabels();
    }

    private void Field_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => RefreshFieldLabels();

    private void RefreshFieldLabels()
    {
        if (SubjectBox is null || BodyBox is null || SubjectLabel is null || BodyLabel is null) return;
        PositionFieldLabel(SubjectLabel, SubjectBox, multiline: false);
        PositionFieldLabel(BodyLabel, BodyBox, multiline: true);
    }

    private static void PositionFieldLabel(TextBlock label, TextBox box, bool multiline)
    {
        var focused = box.IsKeyboardFocusWithin;
        var floating = focused || !string.IsNullOrEmpty(box.Text);
        label.FontSize = floating ? 12 : 16;
        label.Margin = floating ? new Thickness(12, -7, 0, 0) :
            multiline ? new Thickness(14, 16, 0, 0) : new Thickness(14, 0, 0, 0);
        label.VerticalAlignment = floating || multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        label.Background = floating ? new SolidColorBrush(Color.FromRgb(0x25, 0x23, 0x2A)) : Brushes.Transparent;
        label.Padding = floating ? new Thickness(4, 0, 4, 0) : new Thickness(0);
        label.Foreground = focused ? new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8)) :
            new SolidColorBrush(Color.FromRgb(0xB7, 0xAA, 0xBD));
    }

    private void UpdateSendAvailability()
    {
        if (SendButton is not null)
            SendButton.IsEnabled = !sending && !string.IsNullOrWhiteSpace(SubjectBox?.Text) &&
                !string.IsNullOrWhiteSpace(BodyBox?.Text);
    }

    private void SelectFilesButton_Click(object sender, RoutedEventArgs e)
    {
        ResultBanner.Visibility = Visibility.Collapsed;
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
        ResultBanner.Visibility = Visibility.Collapsed;
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
        ResultText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(0xFF, 0xAB, 0x91) :
            Color.FromRgb(0xA5, 0xD6, 0xA7));
        ResultBanner.Background = new SolidColorBrush(isError ? Color.FromRgb(0x3C, 0x21, 0x20) :
            Color.FromRgb(0x1B, 0x35, 0x23));
        ResultBanner.BorderBrush = new SolidColorBrush(isError ? Color.FromRgb(0x6D, 0x38, 0x34) :
            Color.FromRgb(0x36, 0x5A, 0x3B));
        ResultBanner.Visibility = Visibility.Visible;
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
            if (window.TagCombo.Height != 56 || window.SubjectBox.Height != 56 ||
                window.SubjectBox.Foreground is not SolidColorBrush fieldForeground ||
                fieldForeground.Color != Color.FromRgb(0xF5, 0xF1, 0xF7) ||
                window.SelectFilesButton.BorderBrush is not SolidColorBrush attachBorder ||
                attachBorder.Color != Color.FromRgb(0xF4, 0x43, 0x36))
                throw new InvalidOperationException("Inquiry form retained native-theme controls");
            if (window.SendButton.IsEnabled || window.TagCombo.Items.Count != 3)
                throw new InvalidOperationException("Inquiry form initial state differs from v3.2.7");
            if (window.SubjectLabel.FontSize != 16 || window.BodyLabel.FontSize != 16)
                throw new InvalidOperationException("Empty inquiry field labels did not rest inside their fields");
            window.SubjectBox.Text = "件名";
            window.BodyBox.Text = "本文";
            if (window.SubjectLabel.FontSize != 12 || window.BodyLabel.FontSize != 12 ||
                window.SubjectLabel.VerticalAlignment != VerticalAlignment.Top)
                throw new InvalidOperationException("Filled inquiry field labels did not float above their borders");
            if (!window.SendButton.IsEnabled)
                throw new InvalidOperationException("Valid inquiry did not enable Send");
            window.BodyBox.Text = " ";
            if (window.SendButton.IsEnabled)
                throw new InvalidOperationException("Blank inquiry body enabled Send");
            window.Show();
            window.ShowResult("Failure", true);
            if (window.ResultBanner.Visibility != Visibility.Visible ||
                window.ResultText.Text != "Failure")
                throw new InvalidOperationException("Inquiry error alert is missing");
            window.Close();
            if (window.IsVisible || window.SubjectBox.Text != "件名")
                throw new InvalidOperationException("Inquiry close did not hide and preserve its form");
            window.Show();
            if (!window.IsVisible || window.SubjectBox.Text != "件名")
                throw new InvalidOperationException("Inquiry reopen did not restore its form");
        }
        finally { window.CloseForShutdown(); }
        Console.WriteLine("[PASS] 3.2.8 inquiry frame, dark form controls, alerts and hide/reopen state");
    }

    internal static void RenderPreview(string outputPath)
    {
        var window = new InquiryWindow();
        try
        {
            if (window.Content is not FrameworkElement root)
                throw new InvalidOperationException("Inquiry root visual is missing");
            var size = new Size(620, 640);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(outputPath);
            encoder.Save(output);
        }
        finally { window.CloseForShutdown(); }
    }
}
