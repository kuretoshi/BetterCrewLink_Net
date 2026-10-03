using System.IO;
using System.Windows;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private bool CanResetSettings => currentGameState is null || !currentGameState.IsHost ||
        currentGameState.GameState is GameState.Menu or GameState.Lobby;

    private void UpdateResetAvailability()
    {
        ResetDefaultsButton.IsEnabled = CanResetSettings;
        ResetDisabledReason.Visibility = CanResetSettings ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanResetSettings) return;
        ConfirmChange("すべての設定がリセットされます。", RestoreDefaults);
    }

    private void RestoreDefaults()
    {
        // The game may start while the confirmation is open.
        if (!CanResetSettings) return;
        try
        {
            settingsTransaction.Apply(new ClientSettings(), true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the existing controls and pending draft intact on failure.
            SaveStatusText.Text = $"設定をリセットできませんでした。もう一度お試しください: {error.Message}";
            SaveStatusText.Foreground = System.Windows.Media.Brushes.IndianRed;
            SaveStatusText.Visibility = Visibility.Visible;
            return;
        }
        // Never flush a pre-reset lobby draft or failed slider after resetting.
        lobbyCommitTimer.Stop();
        lobbyPending = false;
        failedSaveKeys.Clear();
        settingsReady = false;
        SettingsReset?.Invoke();
        Close();
    }
}
