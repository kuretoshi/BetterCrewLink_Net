using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private readonly HttpClient updateClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource updateCancellation = new();
    private UpdateCandidate? updateCandidate;
    private bool updateBusy;
    internal event Action<StagedUpdate>? UpdateInstallRequested;

    private void InitializeUpdatePanel()
    {
        UpdateVersionText.Text = $"現在のバージョン v{UpdateCatalog.CurrentVersion}";
        UpdateStatusText.Text = "アップデートを確認してください。";
        UpdateProgress.Visibility = Visibility.Collapsed;
        ManualUpdateDownloadButton.Visibility = Visibility.Collapsed;
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (updateBusy) return;
        updateBusy = true;
        updateCandidate = null;
        CheckUpdateButton.IsEnabled = false;
        StartUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        ManualUpdateDownloadButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "アップデートを確認中…";
        try
        {
            updateCandidate = await UpdateCatalog.CheckAsync(updateClient, UpdateCatalog.CurrentVersion,
                cancellationToken: updateCancellation.Token);
            if (updateCancellation.IsCancellationRequested) return;
            if (updateCandidate is null)
            {
                UpdateStatusText.Text = "利用できる.NET版の更新はありません。";
            }
            else if (!UpdateInstallationAvailable())
            {
                UpdateStatusText.Text = $"新しいバージョン {updateCandidate.Version} があります。" +
                    "この起動場所には更新補助ツールがないため、配布版から起動してください。";
            }
            else
            {
                UpdateStatusText.Text = $"最新バージョン {updateCandidate.Version} を利用できます。";
                StartUpdateButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            System.Text.Json.JsonException or TaskCanceledException or InvalidOperationException)
        {
            ShowUpdateError($"アップデートを確認できませんでした。{error.Message}");
        }
        finally
        {
            updateBusy = false;
            if (!updateCancellation.IsCancellationRequested) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void StartUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (updateBusy || updateCandidate is null || !UpdateInstallationAvailable()) return;
        StagedUpdate? staged = null;
        var handedOff = false;
        updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        StartUpdateButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        ManualUpdateDownloadButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "ダウンロード中…";
        try
        {
            var progress = new Progress<double>(percent =>
            {
                UpdateProgress.Value = percent;
                UpdateStatusText.Text = $"ダウンロード中… {percent:0}%";
            });
            staged = await UpdatePackage.StageAsync(updateClient, updateCandidate, progress,
                updateCancellation.Token);
            if (updateCancellation.IsCancellationRequested) return;
            UpdateStatusText.Text = "アップデートの準備ができました。アプリを終了して更新します。";
            if (UpdateInstallRequested is null)
                throw new InvalidOperationException("更新処理を開始できません。");
            UpdateInstallRequested.Invoke(staged);
            handedOff = true;
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
            System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException)
        {
            ShowUpdateError($"アップデートを取得できませんでした。{error.Message}");
            UpdateProgress.Visibility = Visibility.Collapsed;
            if (!updateCancellation.IsCancellationRequested) StartUpdateButton.IsEnabled = true;
        }
        finally
        {
            // StageAsync owns cleanup until it returns; after that this window owns
            // the stage unless the updater has accepted it.
            if (staged is not null && !handedOff)
            {
                try { Directory.Delete(staged.Root, recursive: true); }
                catch (IOException) { /* Leave the stage for inspection if it is in use. */ }
                catch (UnauthorizedAccessException) { /* Same as above. */ }
            }
            updateBusy = false;
            if (!updateCancellation.IsCancellationRequested) CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ShowUpdateError(string message)
    {
        UpdateStatusText.Text = message;
        ManualUpdateDownloadButton.Visibility = Visibility.Visible;
    }

    private void ManualUpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/kuretoshi/BetterCrewLink_Net/releases/latest")
        {
            UseShellExecute = true
        })?.Dispose();
    }

    private static bool UpdateInstallationAvailable() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "update-manifest.json")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Updater", "TanukiBCL.Updater.exe"));
}
