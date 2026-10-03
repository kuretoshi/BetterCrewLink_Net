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
        updateCandidate = null;
        UpdateVersionText.Text = string.Empty;
        UpdateVersionText.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "アップデートを確認してください。";
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Collapsed;
        ManualUpdateDownloadButton.Visibility = Visibility.Collapsed;
        StartUpdateButton.IsEnabled = false;
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (updateBusy) return;
        updateBusy = true;
        updateCandidate = null;
        UpdateVersionText.Visibility = Visibility.Collapsed;
        UpdateStatusText.Visibility = Visibility.Visible;
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
            ShowUpdateCheckResult(updateCandidate, UpdateInstallationAvailable());
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
        ManualUpdateDownloadButton.Visibility = Visibility.Collapsed;
        ShowUpdateDownloadProgress(null);
        var acceptProgress = true;
        try
        {
            var progress = new Progress<double>(percent =>
            {
                if (acceptProgress) ShowUpdateDownloadProgress(percent);
            });
            staged = await UpdatePackage.StageAsync(updateClient, updateCandidate, progress,
                updateCancellation.Token);
            acceptProgress = false;
            if (updateCancellation.IsCancellationRequested) return;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "アップデートの準備ができました。";
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
            if (!updateCancellation.IsCancellationRequested) StartUpdateButton.IsEnabled = true;
        }
        finally
        {
            acceptProgress = false;
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
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = message;
        ManualUpdateDownloadButton.Visibility = Visibility.Visible;
    }

    private void ShowUpdateCheckResult(UpdateCandidate? candidate, bool installationAvailable)
    {
        if (candidate is null)
        {
            UpdateStatusText.Text = "最新バージョンです";
            return;
        }

        var displayVersion = candidate.Version.StartsWith('v') || candidate.Version.StartsWith('V')
            ? candidate.Version : $"v{candidate.Version}";
        UpdateVersionText.Text = $"最新バージョン{displayVersion}";
        UpdateVersionText.Visibility = Visibility.Visible;
        if (!installationAvailable)
        {
            UpdateStatusText.Text = $"新しいバージョン {candidate.Version} があります。" +
                "この起動場所には更新補助ツールがないため、配布版から起動してください。";
            return;
        }

        UpdateStatusText.Visibility = Visibility.Collapsed;
        StartUpdateButton.IsEnabled = true;
    }

    private void ShowUpdateDownloadProgress(double? percent)
    {
        UpdateStatusText.Text = "ダウンロード中…";
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = percent is null or <= 0;
        if (percent is > 0) UpdateProgress.Value = Math.Clamp(percent.Value, 0, 100);
        UpdateProgress.Visibility = Visibility.Visible;
    }

    private void ManualUpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(UpdateCatalog.ReleasesPage.AbsoluteUri)
        {
            UseShellExecute = true
        })?.Dispose();
    }

    private static bool UpdateInstallationAvailable() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "update-manifest.json")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Updater", "TanukiBCL.Updater.exe"));
}
