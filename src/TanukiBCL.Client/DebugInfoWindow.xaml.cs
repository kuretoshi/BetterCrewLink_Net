using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TanukiBCL.Client;

internal sealed record DebugInfoSnapshot(string ModName, string Live, string SnrRoles,
    string GameState, string VoiceConnection);

public partial class DebugInfoWindow : Window
{
    private readonly Func<DebugInfoSnapshot> capture;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal DebugInfoWindow(Func<DebugInfoSnapshot> capture)
    {
        InitializeComponent();
        this.capture = capture;
        refreshTimer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            refreshTimer.Start();
        };
        Closed += (_, _) => refreshTimer.Stop();
    }

    private void DebugTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DebugTabs) && IsLoaded) Refresh();
    }

    private void Refresh()
    {
        try
        {
            var snapshot = capture();
            ModNameText.Text = $"起動中のMOD: {snapshot.ModName}";
            var tab = DebugTabs.SelectedIndex;
            HelpText.Text = tab == 4
                ? "最新のログ（最大64KB）を1秒ごとに更新します。"
                : "ゲーム・音声の状態を自動更新します。";
            DebugText.Text = tab switch
            {
                0 => snapshot.Live,
                1 => snapshot.SnrRoles,
                2 => snapshot.GameState,
                3 => snapshot.VoiceConnection,
                4 => ReadLogTail(),
                _ => string.Empty
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DebugText.Text = $"診断情報を取得できませんでした: {exception.Message}";
        }
    }

    private static string ReadLogTail()
    {
        var path = SupportLog.DefaultPath;
        if (!File.Exists(path)) return "ログはありません。";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-Math.Min(stream.Length, 64 * 1_024), SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void SaveLogButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SupportLog.DefaultPath;
        if (!File.Exists(source))
        {
            MessageBox.Show(this, "保存するログはありません。", "デバッグ情報");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "デバッグログを保存",
            FileName = $"TanukiBCL-debug-{DateTimeOffset.Now:yyyy-MM-dd-HH-mm-ss}.log",
            DefaultExt = ".log",
            Filter = "ログファイル (*.log)|*.log"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
            MessageBox.Show(this, "ログを保存しました。", "デバッグ情報");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"ログを保存できませんでした: {exception.Message}", "デバッグ情報");
        }
    }

    internal static void VerifyUi()
    {
        var window = new DebugInfoWindow(() => new DebugInfoSnapshot(
            "SuperNewRoles", "live-state", "snr-roles", "game-json", "voice-json"));
        try
        {
            if (window.DebugTabs.Items.Count != 5 || window.SaveLogButton is null)
                throw new InvalidOperationException("Released debug tabs or log save control are missing");
            var expected = new[] { "live-state", "snr-roles", "game-json", "voice-json" };
            for (var index = 0; index < expected.Length; index++)
            {
                window.DebugTabs.SelectedIndex = index;
                window.Refresh();
                if (window.DebugText.Text != expected[index] ||
                    window.ModNameText.Text != "起動中のMOD: SuperNewRoles")
                    throw new InvalidOperationException("Debug tab did not display its selected snapshot");
            }
        }
        finally { window.Close(); }
        Console.WriteLine("[PASS] Developer debug window switches live, SNR, game and voice snapshots");
    }
}
