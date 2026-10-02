using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private static void VerifyImmediateSettings()
    {
        var settings = new ClientSettings();
        var writes = new List<ClientSettings>();
        var changes = new List<ClientSettingsChange>();
        var failSave = false;
        var window = new SettingsWindow(settings, true, new LobbySettings { MaxDistance = 3 }, false,
            null, (_, _, _) => { }, saved =>
            {
                if (failSave) throw new IOException("injected write failure");
                writes.Add(saved.Clone());
            });
        window.SettingsApplied += changes.Add;
        try
        {
            RequireLive(writes.Count == 0 && changes.Count == 0, "Opening settings wrote values");
            window.MasterVolumeSlider.Value = 61;
            RequireLive(settings.MasterVolume == 61 && changes.Count == 1 && writes.Count == 0,
                "Volume preview was not live-only");
            window.AlwaysOnTopCheck.IsChecked = true;
            Click(window.AlwaysOnTopCheck);
            RequireLive(settings.AlwaysOnTop && writes.Last().AlwaysOnTop && writes.Last().MasterVolume == 100,
                "Checkbox save included another control's uncommitted slider");
            window.MasterVolumeSlider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                { RoutedEvent = Mouse.LostMouseCaptureEvent });
            RequireLive(writes.Last().MasterVolume == 61 && settings.MasterVolume == 61,
                "Slider capture completion did not persist its final value");

            window.CategoryList.SelectedIndex = 1;
            var oldDistance = settings.MyLobbySettings.MaxDistance;
            var count = writes.Count;
            var watch = Stopwatch.StartNew();
            window.DistanceSlider.Value = 8;
            RequireLive(settings.MyLobbySettings.MaxDistance == oldDistance && writes.Count == count,
                "Lobby changes bypassed the 750 ms draft period");
            PumpFor(TimeSpan.FromMilliseconds(850));
            RequireLive(watch.ElapsedMilliseconds >= 740 && settings.MyLobbySettings.MaxDistance == 8 &&
                writes.Count == count + 1, $"Debounced lobby change was not persisted exactly once (elapsed={watch.ElapsedMilliseconds}, distance={settings.MyLobbySettings.MaxDistance}, writes={writes.Count}, previous={count}, pending={window.lobbyPending})");
            window.DistanceSlider.Value = 7;
            window.CategoryList.SelectedIndex = 6;
            RequireLive(settings.MyLobbySettings.MaxDistance == 7, "Leaving lobby category did not flush its draft");

            count = writes.Count;
            window.UpdateCurrentGameState(new AmongUsState { IsHost = true, GameState = GameState.Tasks });
            window.DistanceSlider.Value = 9;
            RequireLive(!window.LobbyControlsPanel.IsEnabled && !window.lobbyPending && writes.Count == count,
                "Host could edit own lobby settings after game started");
            window.UpdateCurrentGameState(new AmongUsState { IsHost = false, GameState = GameState.Tasks });
            window.CurrentLobbyTab.IsChecked = true;
            RequireLive(!window.LobbyControlsPanel.IsEnabled && writes.Count == count &&
                settings.MyLobbySettings.MaxDistance == 7, "Received lobby controls changed own settings");
            window.MyLobbyTab.IsChecked = true;

            window.NatFixCheck.IsChecked = true;
            Click(window.NatFixCheck);
            RequireLive(!settings.NatFix && window.ConfirmDialogBackdrop.Visibility == Visibility.Visible,
                "NAT fix applied before warning confirmation");
            Click(window.ConfirmDialog.CancelButton);
            RequireLive(!settings.NatFix && window.NatFixCheck.IsChecked == false,
                "Canceling NAT warning changed state");
            window.NatFixCheck.IsChecked = true;
            Click(window.NatFixCheck);
            Click(window.ConfirmDialog.ConfirmButton);
            RequireLive(settings.NatFix, "Confirmed NAT setting was not applied");

            window.MicSensitivitySlider.Value = 0.7;
            RequireLive(settings.MicSensitivity == 0.15, "Sensitivity was applied before commit");
            window.CommitMicrophoneSensitivity();
            RequireLive(window.ConfirmDialogBackdrop.Visibility == Visibility.Visible && settings.MicSensitivity == 0.15,
                "Sensitivity boundary did not wait for confirmation");
            Click(window.ConfirmDialog.CancelButton);
            window.MicSensitivitySlider.Value = 0.5;
            window.CommitMicrophoneSensitivity();
            RequireLive(settings.MicSensitivity == 0.5 && window.ConfirmDialogBackdrop.Visibility == Visibility.Collapsed,
                "Sensitivity warned outside the upstream exact boundary");

            window.CategoryList.SelectedIndex = 1;
            window.DeadOnlyCheck.IsChecked = true;
            Click(window.DeadOnlyCheck);
            RequireLive(!settings.MyLobbySettings.DeadOnly, "Ghost-only mode applied before confirmation");
            Click(window.ConfirmDialog.ConfirmButton);
            window.CategoryList.SelectedIndex = 6;
            RequireLive(settings.MyLobbySettings.DeadOnly && !settings.MyLobbySettings.MeetingGhostOnly,
                "Exclusive lobby mode was not committed");

            failSave = true;
            window.AlwaysOnTopCheck.IsChecked = false;
            Click(window.AlwaysOnTopCheck);
            RequireLive(settings.AlwaysOnTop, "Failed save changed the shared setting");
            var closing = new CancelEventArgs();
            window.OnClosing(closing);
            RequireLive(closing.Cancel, "Closing discarded a failed setting write");
            failSave = false;
            closing = new CancelEventArgs();
            window.OnClosing(closing);
            RequireLive(!closing.Cancel && !settings.AlwaysOnTop && !writes.Last().AlwaysOnTop,
                "Closing did not retry the failed setting write");
        }
        finally
        {
            failSave = false;
            window.Close();
        }
    }

    private static void Click(System.Windows.Controls.Primitives.ButtonBase button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RequireLive(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
