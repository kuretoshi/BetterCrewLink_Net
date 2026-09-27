using System.Diagnostics;
using System.Windows;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class MainWindow : Window
{
    private CancellationTokenSource? runCancellation;
    private VoiceServerProbe? probe;

    public MainWindow()
    {
        InitializeComponent();
        InputCombo.ItemsSource = AudioDeviceSession.GetInputDevices();
        OutputCombo.ItemsSource = AudioDeviceSession.GetOutputDevices();
        InputCombo.SelectedIndex = InputCombo.Items.Count > 0 ? 0 : -1;
        OutputCombo.SelectedIndex = OutputCombo.Items.Count > 0 ? 0 : -1;
        RefreshProcesses();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private void RefreshProcesses()
    {
        var selectedPid = (ProcessChoice?)ProcessCombo.SelectedItem is { } selected ? selected.Id : (int?)null;
        var processes = Process.GetProcessesByName("Among Us");
        var choices = processes.Select(process => new ProcessChoice(process.Id)).OrderBy(choice => choice.Id).ToArray();
        foreach (var process in processes)
        {
            process.Dispose();
        }
        ProcessCombo.ItemsSource = choices;
        ProcessCombo.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedPid) ?? choices.FirstOrDefault();
        StatusText.Text = choices.Length == 0
            ? "Among Usが見つかりません"
            : $"Among Usを{choices.Length}件検出しました";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessCombo.SelectedItem is not ProcessChoice process ||
            InputCombo.SelectedItem is not AudioDeviceInfo input ||
            OutputCombo.SelectedItem is not AudioDeviceInfo output)
        {
            MessageBox.Show(this, "Among Us、マイク、スピーカーを選択してください。", "TanukiBCL");
            return;
        }

        SetRunning(true);
        runCancellation = new CancellationTokenSource();
        var options = ProbeOptions.Parse([
            "--server", "https://bettercrewl.ink",
            "--game-process-id", process.Id.ToString(),
            "--live-audio",
            "--input-device", input.Id.ToString(),
            "--output-device", output.Id.ToString()
        ]);
        probe = new VoiceServerProbe(options, "client");
        probe.ConnectionStatusChanged += status => Dispatch(() => StatusText.Text = status);
        probe.GameStateApplied += state => Dispatch(() => ShowGameState(state));
        probe.LocalVadChanged += talking => Dispatch(() =>
        {
            VadText.Text = talking ? "マイク: 発話中" : "マイク: 待機中";
            VadText.Foreground = talking
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.LightGray;
        });

        try
        {
            await probe.RunAsync(runCancellation.Token);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"接続失敗: {exception.Message}";
        }
        finally
        {
            var wasStopped = runCancellation?.IsCancellationRequested == true;
            if (probe is not null)
            {
                await probe.DisposeAsync();
                probe = null;
            }
            runCancellation?.Dispose();
            runCancellation = null;
            SetRunning(false);
            if (wasStopped)
            {
                StatusText.Text = "停止しました";
            }
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => runCancellation?.Cancel();

    private void ShowGameState(AmongUsState state)
    {
        GameText.Text = $"ゲーム状態: {state.GameState}";
        LobbyText.Text = $"ロビー: {(state.GameState == GameState.Menu ? "—" : state.LobbyCode)}";
        PlayersText.Text = $"参加者: {state.Players.Count}人（生存 {state.Players.Count(p => !p.IsDead && !p.Disconnected)}人）";
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ProcessCombo.IsEnabled = !running;
        InputCombo.IsEnabled = !running;
        OutputCombo.IsEnabled = !running;
        RefreshButton.IsEnabled = !running;
    }

    private void Dispatch(Action action) => Dispatcher.BeginInvoke(action);

    protected override void OnClosed(EventArgs e)
    {
        runCancellation?.Cancel();
        base.OnClosed(e);
    }

    private sealed record ProcessChoice(int Id)
    {
        public string DisplayName => $"Among Us (PID {Id})";
    }
}
