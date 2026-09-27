using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class MainWindow : Window
{
    private CancellationTokenSource? runCancellation;
    private VoiceServerProbe? probe;
    private readonly ObservableCollection<PeerRow> peers = [];
    private AmongUsState? currentState;
    private bool microphoneMuted;
    private bool deafened;
    private long sentAudioFrames;

    public MainWindow()
    {
        InitializeComponent();
        PeerGrid.ItemsSource = peers;
        InputCombo.ItemsSource = AudioDeviceSession.GetInputDevices();
        OutputCombo.ItemsSource = AudioDeviceSession.GetOutputDevices();
        InputCombo.SelectedIndex = InputCombo.Items.Count > 0 ? 0 : -1;
        OutputCombo.SelectedIndex = OutputCombo.Items.Count > 0 ? 0 : -1;
        RefreshProcesses();
        SelectProcessFromCommandLine();
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
        probe.PeerMixChanged += (clientId, mix) => Dispatch(() => UpdatePeerMix(clientId, mix));
        probe.PeerConnectionStatusChanged += (clientId, status) => Dispatch(() => UpdatePeerConnection(clientId, status));
        probe.PeerPcmReceived += (clientId, _) => Dispatch(() =>
        {
            var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
            FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}").IncrementReceived();
        });
        probe.LocalAudioFrameSent += peerCount => Dispatch(() =>
        {
            sentAudioFrames++;
            SentText.Text = $"Opus送信: {sentAudioFrames} frame / {peerCount} peer";
        });
        probe.LocalVadChanged += talking => Dispatch(() =>
        {
            VadText.Text = microphoneMuted ? "マイク: ミュート中" : talking ? "マイク: 発話中" : "マイク: 待機中";
            VadText.Foreground = talking && !microphoneMuted
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

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        microphoneMuted = !microphoneMuted;
        probe?.SetMicrophoneMuted(microphoneMuted);
        MuteButton.Content = microphoneMuted ? "マイクミュート解除" : "マイクをミュート";
        VadText.Text = microphoneMuted ? "マイク: ミュート中" : "マイク: 待機中";
    }

    private void DeafenButton_Click(object sender, RoutedEventArgs e)
    {
        deafened = !deafened;
        probe?.SetDeafened(deafened);
        DeafenButton.Content = deafened ? "スピーカーミュート解除" : "スピーカーをミュート";
    }

    private void ShowGameState(AmongUsState state)
    {
        currentState = state;
        GameText.Text = $"ゲーム状態: {state.GameState}";
        LobbyText.Text = $"ロビー: {(state.GameState == GameState.Menu ? "—" : state.LobbyCode)}";
        PlayersText.Text = $"参加者: {state.Players.Count(player => !player.Disconnected)}人";
        var remotePlayers = state.Players.Where(player => !player.IsLocal).OrderBy(player => player.ClientId).ToArray();
        foreach (var player in remotePlayers)
        {
            FindOrCreatePeer(player.ClientId, player.Name).Name = player.Name;
        }
        foreach (var stale in peers.Where(row => remotePlayers.All(player => player.ClientId != row.ClientId)).ToArray())
        {
            peers.Remove(stale);
        }
    }

    private void UpdatePeerMix(int clientId, PeerVoiceMix mix)
    {
        var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
        var row = FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}");
        row.Voice = mix.Audible ? "聞こえる" : LocalizeReason(mix.Reason);
        row.Gain = mix.Audible ? $"{mix.Gain * 100:0}%" : "0%";
    }

    private void UpdatePeerConnection(int clientId, string status)
    {
        var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
        FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}").Connection = status switch
        {
            "connected" => "接続済み",
            "connecting" => "接続中",
            "failed" => "再接続中",
            "closed" => "切断",
            _ => status
        };
    }

    private PeerRow FindOrCreatePeer(int clientId, string name)
    {
        var row = peers.SingleOrDefault(candidate => candidate.ClientId == clientId);
        if (row is not null)
        {
            return row;
        }
        row = new PeerRow(clientId, name);
        peers.Add(row);
        return row;
    }

    private static string LocalizeReason(string reason) => reason switch
    {
        "out-of-range" => "距離外",
        "living-cannot-hear-ghost" or "peer-in-vent" or "dead-only" or "meeting-ghost-only" => "ルールで遮断",
        "disconnected" => "退出済み",
        "not-in-game" => "ゲーム外",
        _ => "ミュート"
    };

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        MuteButton.IsEnabled = running;
        DeafenButton.IsEnabled = running;
        ProcessCombo.IsEnabled = !running;
        InputCombo.IsEnabled = !running;
        OutputCombo.IsEnabled = !running;
        RefreshButton.IsEnabled = !running;
        if (!running)
        {
            sentAudioFrames = 0;
            microphoneMuted = false;
            deafened = false;
            MuteButton.Content = "マイクをミュート";
            DeafenButton.Content = "スピーカーをミュート";
            VadText.Text = "マイク: 待機中";
            SentText.Text = "Opus送信: 待機中";
        }
    }

    private void Dispatch(Action action) => Dispatcher.BeginInvoke(action);

    private void SelectProcessFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var optionIndex = Array.IndexOf(args, "--game-process-id");
        if (optionIndex < 0 || optionIndex + 1 >= args.Length || !int.TryParse(args[optionIndex + 1], out var processId))
        {
            return;
        }

        if (ProcessCombo.ItemsSource is IEnumerable<ProcessChoice> choices &&
            choices.SingleOrDefault(choice => choice.Id == processId) is { } choice)
        {
            ProcessCombo.SelectedItem = choice;
            StatusText.Text = $"Among Us PID {processId}を選択しました";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        runCancellation?.Cancel();
        base.OnClosed(e);
    }

    private sealed record ProcessChoice(int Id)
    {
        public string DisplayName => $"Among Us (PID {Id})";
    }

    private sealed class PeerRow(int clientId, string name) : INotifyPropertyChanged
    {
        private string currentName = name;
        private string connection = "待機中";
        private string voice = "待機中";
        private long receivedFrames;
        private string gain = "—";

        public int ClientId { get; } = clientId;
        public string Name { get => currentName; set => Set(ref currentName, value); }
        public string Connection { get => connection; set => Set(ref connection, value); }
        public string Voice { get => voice; set => Set(ref voice, value); }
        public string Received => receivedFrames == 0 ? "待機中" : $"{receivedFrames} frame";
        public string Gain { get => gain; set => Set(ref gain, value); }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void IncrementReceived()
        {
            receivedFrames++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Received)));
        }

        private void Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value)
            {
                return;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
