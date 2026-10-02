using System.Diagnostics;
using System.IO;
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
    private TaskCompletionSource? sessionStopped;
    private VoiceServerProbe? probe;
    private SettingsWindow? settingsWindow;
    private OverlayWindow? overlayWindow;
    private int? activeGamePid;
    private GlobalHotkeyMonitor? hotkeys;
    private Task? hotkeyTask;
    private volatile bool hotkeysSuspended;
    private readonly ObservableCollection<PeerRow> peers = [];
    private readonly ClientSettings settings;
    private AmongUsState? currentState;
    private bool microphoneMuted;
    private bool deafened;
    private bool radioTransmitting;
    private bool localTalking;
    private bool voiceServerConnected;
    private bool reloadInProgress;
    private ConnectionQuality? serverQuality;
    private long sentAudioFrames;

    public MainWindow()
    {
        settings = ClientSettingsStore.Load();
        InitializeComponent();
        Topmost = settings.AlwaysOnTop;
        PeerGrid.ItemsSource = peers;
        InputCombo.ItemsSource = AudioDeviceSession.GetInputDevices();
        OutputCombo.ItemsSource = AudioDeviceSession.GetOutputDevices();
        SelectConfiguredDevices();
        RefreshProcesses();
        var explicitProcess = SelectProcessFromCommandLine();
        CompactVoiceView.SettingsRequested += (_, _) => SettingsButton_Click(this, new RoutedEventArgs());
        CompactVoiceView.ReloadRequested += CompactVoiceView_ReloadRequested;
        CompactVoiceView.CloseRequested += (_, _) => Close();
        CompactVoiceView.MuteRequested += (_, _) => ToggleMicrophoneMute();
        CompactVoiceView.DeafenRequested += (_, _) => ToggleDeafen();
        CompactVoiceView.HelpRequested += (_, _) => ShowDiagnostics();
        CompactVoiceView.PlayerConfigChanged += ApplyPlayerConfig;
        UpdateCompactView();
        Loaded += (_, _) =>
        {
            if (explicitProcess || ProcessCombo.Items.Count == 1)
            {
                StartButton_Click(this, new RoutedEventArgs());
            }
            else if (ProcessCombo.Items.Count > 1)
            {
                ShowDiagnostics();
            }
        };
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CompactVoiceView.DismissPlayerConfigPopup();
        var previousServerUrl = settings.ServerUrl;
        var previousMicrophone = settings.MicrophoneName;
        var previousSpeaker = settings.SpeakerName;
        var previousEchoCancellation = settings.EchoCancellation;
        var previousNoiseSuppression = settings.NoiseSuppression;
        var previousAutoGainControl = settings.AutoGainControl;
        var hostInGame = currentState is { IsHost: true, GameState: GameState.Tasks or GameState.Discussion };
        var window = new SettingsWindow(settings, !hostInGame,
            probe?.CurrentLobbySettings, currentState?.IsHost != true, currentState,
            ApplyPlayerConfig) { Owner = this };
        settingsWindow = window;
        hotkeysSuspended = true;
        bool? saved;
        try
        {
            saved = window.ShowDialog();
        }
        finally
        {
            settingsWindow = null;
            hotkeysSuspended = false;
        }
        if (saved != true) return;

        Topmost = settings.AlwaysOnTop;
        var requiresRestart = !string.Equals(previousServerUrl, settings.ServerUrl, StringComparison.Ordinal) ||
            !string.Equals(previousMicrophone, settings.MicrophoneName, StringComparison.Ordinal) ||
            !string.Equals(previousSpeaker, settings.SpeakerName, StringComparison.Ordinal) ||
            previousEchoCancellation != settings.EchoCancellation ||
            previousNoiseSuppression != settings.NoiseSuppression ||
            previousAutoGainControl != settings.AutoGainControl;
        if (requiresRestart)
        {
            InputCombo.ItemsSource = AudioDeviceSession.GetInputDevices();
            OutputCombo.ItemsSource = AudioDeviceSession.GetOutputDevices();
        }
        SelectConfiguredDevices();
        if (requiresRestart && runCancellation is { IsCancellationRequested: false } cancellation &&
            sessionStopped is { } stopped)
        {
            // The server URL and audio device IDs are fixed when the probe starts.
            // Keep the user's mute/deafen choice across the controlled restart.
            var wasMuted = microphoneMuted;
            var wasDeafened = deafened;
            cancellation.Cancel();
            await stopped.Task;
            if (!IsVisible || Dispatcher.HasShutdownStarted) return;
            if (runCancellation is not null)
            {
                ShowDiagnostics();
                return;
            }
            if (wasMuted) ToggleMicrophoneMute();
            if (wasDeafened) ToggleDeafen();
            StartButton_Click(this, new RoutedEventArgs());
            return;
        }
        probe?.SetMasterVolume(settings.MasterVolume);
        probe?.SetVoiceEffectStrength(settings.VoiceEffectStrength);
        probe?.SetListenerVolumes(settings.CrewVolumeAsGhost, settings.GhostVolumeAsImpostor);
        probe?.SetSpatialAudio(settings.EnableSpatialAudio);
        probe?.SetMicrophoneGain(settings.MicrophoneGainEnabled ? settings.MicrophoneGain : 100d);
        probe?.SetMicrophoneSensitivity(settings.MicSensitivityEnabled, settings.MicSensitivity);
        probe?.SetNatFix(settings.NatFix);
        probe?.SetMicrophoneActivationMode(settings.PushToTalkMode);
        hotkeys?.UpdateBindings(settings);
        probe?.SetOwnLobbySettings(settings.MyLobbySettings);
        UpdateCompactView();
    }

    private void SelectConfiguredDevices()
    {
        InputCombo.SelectedItem = InputCombo.Items.Cast<AudioDeviceInfo>()
            .FirstOrDefault(device => device.Name == settings.MicrophoneName)
            ?? InputCombo.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
        OutputCombo.SelectedItem = OutputCombo.Items.Cast<AudioDeviceInfo>()
            .FirstOrDefault(device => device.Name == settings.SpeakerName)
            ?? OutputCombo.Items.Cast<AudioDeviceInfo>().FirstOrDefault();
    }

    private void ApplyPlayerConfig(int configId, PlayerAudioConfig config, bool persist)
    {
        settings.PlayerConfigMap[configId] = config.Normalize();
        probe?.SetPlayerConfig(configId, config);
        UpdateCompactView();
        if (!persist) return;
        try
        {
            ClientSettingsStore.Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"プレイヤー別音量を保存できませんでした: {exception.Message}", "TanukiBCL");
        }
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

        settings.MicrophoneName = input.Name;
        settings.SpeakerName = output.Name;
        try
        {
            ClientSettingsStore.Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"デバイス設定を保存できませんでした: {exception.Message}", "TanukiBCL");
        }
        activeGamePid = process.Id;
        SetRunning(true);
        ShowCompactView();
        runCancellation = new CancellationTokenSource();
        var optionArgs = new List<string>
        {
            "--server", settings.ServerUrl,
            "--game-process-id", process.Id.ToString(),
            "--live-audio",
            "--input-device", input.Id.ToString(),
            "--output-device", output.Id.ToString()
        };
        if (settings.NatFix) optionArgs.Add("--nat-fix");
        var options = ProbeOptions.Parse([.. optionArgs]);
        probe = new VoiceServerProbe(options, "client");
        probe.SetMasterVolume(settings.MasterVolume);
        probe.SetVoiceEffectStrength(settings.VoiceEffectStrength);
        probe.SetPlayerConfigs(settings.PlayerConfigMap);
        probe.SetListenerVolumes(settings.CrewVolumeAsGhost, settings.GhostVolumeAsImpostor);
        probe.SetSpatialAudio(settings.EnableSpatialAudio);
        probe.SetInputProcessing(settings.EchoCancellation, settings.NoiseSuppression, settings.AutoGainControl);
        probe.SetMicrophoneGain(settings.MicrophoneGainEnabled ? settings.MicrophoneGain : 100d);
        probe.SetMicrophoneSensitivity(settings.MicSensitivityEnabled, settings.MicSensitivity);
        probe.SetMicrophoneActivationMode(settings.PushToTalkMode);
        probe.SetOwnLobbySettings(settings.MyLobbySettings);
        probe.SetMicrophoneMuted(microphoneMuted);
        probe.SetDeafened(deafened);
        probe.LobbySettingsChanged += _ => Dispatch(() =>
            settingsWindow?.UpdateCurrentLobbySettings(probe?.CurrentLobbySettings));
        probe.ConnectionStatusChanged += status => Dispatch(() =>
        {
            StatusText.Text = status;
            voiceServerConnected = status == "ボイスサーバー接続済み";
            UpdateCompactView();
        });
        probe.ServerQualityChanged += quality => Dispatch(() =>
        {
            serverQuality = quality;
            UpdateCompactView();
        });
        probe.GameStateApplied += state => Dispatch(() => ShowGameState(state));
        probe.PeerMixChanged += (clientId, mix) => Dispatch(() => UpdatePeerMix(clientId, mix));
        probe.PeerConnectionStatusChanged += (clientId, status) => Dispatch(() => UpdatePeerConnection(clientId, status));
        probe.PeerVadChanged += (clientId, active) => Dispatch(() =>
        {
            var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
            var row = FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}");
            row.VadActive = active;
            row.Talking = active && row.Audible && player?.InVent != true;
            UpdateCompactView();
        });
        probe.PeerPcmReceived += (clientId, _) => Dispatch(() =>
        {
            var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
            var row = FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}");
            var firstFrame = !row.HasReceivedFrames;
            row.IncrementReceived();
            if (firstFrame) UpdateCompactView();
        });
        probe.LocalAudioFrameSent += peerCount => Dispatch(() =>
        {
            sentAudioFrames++;
            SentText.Text = $"Opus送信: {sentAudioFrames} frame / {peerCount} peer";
        });
        probe.LocalVadChanged += talking => Dispatch(() =>
        {
            localTalking = talking;
            VadText.Text = microphoneMuted ? "マイク: ミュート中" : talking ? "マイク: 発話中" : "マイク: 待機中";
            VadText.Foreground = talking && !microphoneMuted
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.LightGray;
            UpdateCompactView();
        });
        probe.ImpostorRadioAvailabilityChanged += available => Dispatch(() => RadioButton.IsEnabled = available);
        probe.ImpostorRadioTransmitChanged += active => Dispatch(() =>
        {
            radioTransmitting = active;
            RadioButton.Content = active ? "インポスターラジオ: ON" : "インポスターラジオ: OFF";
            RadioButton.Background = active ? System.Windows.Media.Brushes.DarkOrange : null;
            UpdateCompactView();
        });
        hotkeys = new GlobalHotkeyMonitor(
            pressed => probe?.SetPushToTalkPressed(pressed),
            () => Dispatch(() =>
            {
                if (runCancellation?.IsCancellationRequested == false && probe?.CanUseImpostorRadio == true)
                    probe.SetImpostorRadioTransmitting(!radioTransmitting);
            }),
            () => Dispatch(() =>
            {
                if (runCancellation?.IsCancellationRequested == false) ToggleMicrophoneMute();
            }),
            () => Dispatch(() =>
            {
                if (runCancellation?.IsCancellationRequested == false) ToggleDeafen();
            }),
            () => hotkeysSuspended);
        hotkeys.UpdateBindings(settings);
        var runningHotkeys = hotkeys;
        var hotkeyCancellationToken = runCancellation.Token;
        hotkeyTask = Task.Run(() => runningHotkeys.RunAsync(hotkeyCancellationToken));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionStopped = stopped;

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
            ShowDiagnostics();
        }
        finally
        {
            try
            {
                var wasStopped = runCancellation?.IsCancellationRequested == true;
                runCancellation?.Cancel();
                if (hotkeyTask is not null) await hotkeyTask;
                hotkeyTask = null;
                hotkeys = null;
                if (probe is not null)
                {
                    await probe.DisposeAsync();
                    probe = null;
                }
                runCancellation?.Dispose();
                runCancellation = null;
                overlayWindow?.Close();
                overlayWindow = null;
                activeGamePid = null;
                SetRunning(false);
                if (wasStopped)
                {
                    StatusText.Text = "停止しました";
                }
            }
            finally
            {
                if (ReferenceEquals(sessionStopped, stopped)) sessionStopped = null;
                stopped.TrySetResult();
            }
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => runCancellation?.Cancel();

    private async void CompactVoiceView_ReloadRequested(object? sender, EventArgs e)
    {
        if (reloadInProgress) return;
        var activeProbe = probe;
        var cancellation = runCancellation;
        if (activeProbe is null || cancellation is null || cancellation.IsCancellationRequested)
        {
            ShowDiagnostics();
            return;
        }

        reloadInProgress = true;
        try
        {
            StatusText.Text = "音声接続を再読み込み中...";
            await activeProbe.RestartServerConnectionAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"再読み込み失敗: {exception.Message}";
            ShowDiagnostics();
        }
        finally
        {
            reloadInProgress = false;
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
        => ToggleMicrophoneMute();

    private void ToggleMicrophoneMute()
    {
        microphoneMuted = !microphoneMuted;
        probe?.SetMicrophoneMuted(microphoneMuted);
        MuteButton.Content = microphoneMuted ? "マイクミュート解除" : "マイクをミュート";
        VadText.Text = microphoneMuted ? "マイク: ミュート中" : "マイク: 待機中";
        UpdateCompactView();
    }

    private void DeafenButton_Click(object sender, RoutedEventArgs e)
        => ToggleDeafen();

    private void ToggleDeafen()
    {
        deafened = !deafened;
        probe?.SetDeafened(deafened);
        DeafenButton.Content = deafened ? "スピーカーミュート解除" : "スピーカーをミュート";
        UpdateCompactView();
    }

    private void RadioButton_Click(object sender, RoutedEventArgs e) =>
        probe?.SetImpostorRadioTransmitting(!radioTransmitting);

    private void ShowGameState(AmongUsState state)
    {
        currentState = state;
        settingsWindow?.UpdateCurrentGameState(state);
        GameText.Text = $"ゲーム状態: {state.GameState}";
        LobbyText.Text = $"ロビー: {(state.GameState == GameState.Menu ? "—" : state.LobbyCode)}";
        PlayersText.Text = $"参加者: {state.Players.Count(player => !player.Disconnected)}人";
        var remotePlayers = state.Players.Where(player => !player.IsLocal).OrderBy(player => player.ClientId).ToArray();
        foreach (var player in remotePlayers)
        {
            var row = FindOrCreatePeer(player.ClientId, player.Name);
            row.Name = player.Name;
            row.Talking = row.VadActive && row.Audible && !player.InVent;
        }
        foreach (var stale in peers.Where(row => remotePlayers.All(player => player.ClientId != row.ClientId)).ToArray())
        {
            peers.Remove(stale);
        }
        UpdateCompactView();
    }

    private void UpdatePeerMix(int clientId, PeerVoiceMix mix)
    {
        var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
        var row = FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}");
        row.Audible = mix.Audible;
        row.Talking = row.VadActive && mix.Audible && player?.InVent != true;
        row.Voice = mix.Audible ? "聞こえる" : LocalizeReason(mix.Reason);
        row.Radio = mix.Audible && mix.Reason == "impostor-radio" ? "送信中" : "—";
        row.Gain = mix.Audible ? $"{mix.Gain * 100:0}%" : "0%";
        UpdateCompactView();
    }

    private void UpdatePeerConnection(int clientId, string status)
    {
        var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
        var row = FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}");
        if (status is "connecting" or "failed" or "closed")
        {
            row.ResetReceived();
            row.VadActive = false;
            row.Talking = false;
        }
        row.Connection = status switch
        {
            "connected" => "接続済み",
            "connecting" => "接続中",
            "failed" => "再接続中",
            "closed" => "切断",
            _ => status
        };
        UpdateCompactView();
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
        "living-cannot-hear-ghost" or "peer-in-vent" or "dead-only" or "meeting-ghost-only" or "radio-private" => "ルールで遮断",
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
        RadioButton.IsEnabled = running && probe?.CanUseImpostorRadio == true;
        ProcessCombo.IsEnabled = !running;
        InputCombo.IsEnabled = !running;
        OutputCombo.IsEnabled = !running;
        RefreshButton.IsEnabled = !running;
        if (!running)
        {
            sentAudioFrames = 0;
            microphoneMuted = false;
            deafened = false;
            radioTransmitting = false;
            localTalking = false;
            voiceServerConnected = false;
            serverQuality = null;
            RadioButton.Content = "インポスターラジオ: OFF";
            RadioButton.Background = null;
            MuteButton.Content = "マイクをミュート";
            DeafenButton.Content = "スピーカーをミュート";
            VadText.Text = "マイク: 待機中";
            SentText.Text = "Opus送信: 待機中";
            foreach (var row in peers)
            {
                row.Connection = "待機中";
                row.ResetReceived();
                row.VadActive = false;
                row.Audible = false;
                row.Talking = false;
            }
        }
        UpdateCompactView();
    }

    private void Dispatch(Action action) => Dispatcher.BeginInvoke(action);

    private bool SelectProcessFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var optionIndex = Array.IndexOf(args, "--game-process-id");
        if (optionIndex < 0 || optionIndex + 1 >= args.Length || !int.TryParse(args[optionIndex + 1], out var processId))
        {
            return false;
        }

        if (ProcessCombo.ItemsSource is IEnumerable<ProcessChoice> choices &&
            choices.SingleOrDefault(choice => choice.Id == processId) is { } choice)
        {
            ProcessCombo.SelectedItem = choice;
            StatusText.Text = $"Among Us PID {processId}を選択しました";
            return true;
        }
        return false;
    }

    private void UpdateCompactView()
    {
        var statuses = peers.ToDictionary(row => row.ClientId, row => new VoicePlayerStatus(
            row.Connection is "data-ready" or "接続済み"
                ? row.HasReceivedFrames ? "connected" : "novoice"
                : "disconnected",
            row.Talking, row.Radio == "送信中"));
        CompactVoiceView.Update(currentState, voiceServerConnected, localTalking && !microphoneMuted,
            microphoneMuted, deafened, statuses, hideCode: settings.HideCode, localUsingRadio: radioTransmitting,
            playerConfigs: settings.PlayerConfigMap, serverQuality: serverQuality);
        var mod = currentState?.Mod ?? AmongUsModType.None;
        CompactVoiceView.SetDetectedMod(mod == AmongUsModType.None ? null : AmongUsMod.For(mod).Label);
        var active = probe?.CurrentLobbySettings;
        CompactVoiceView.SetWarning(active?.DeadOnly == true
            ? "幽霊のみのボイス設定です"
            : active?.MeetingGhostOnly == true
                ? "会議中は幽霊のみ会話できます"
                : null);
        if (settings.ObsOverlay && currentState is { } state && probe is { } activeProbe)
        {
            var obsPeers = peers.ToDictionary(row => row.ClientId, row => new ObsPeerState(
                activeProbe.IsPeerPresent(row.ClientId), row.VadActive, row.Radio == "送信中"));
            activeProbe.PublishObsOverlay(settings.ObsSecret, state, obsPeers,
                localTalking && !microphoneMuted, radioTransmitting);
        }
        UpdateOverlayWindow();
    }

    private void UpdateOverlayWindow()
    {
        if (!settings.EnableOverlay || activeGamePid is not { } pid || probe is null)
        {
            overlayWindow?.Close();
            overlayWindow = null;
            return;
        }
        overlayWindow ??= new OverlayWindow(pid, settings);
        var peerStatuses = peers.ToDictionary(row => row.ClientId, row => new OverlayPeerStatus(
            row.Connection is "data-ready" or "接続済み",
            row.VadActive, row.Radio == "送信中"));
        overlayWindow.Update(currentState, peerStatuses, localTalking, microphoneMuted, deafened);
    }

    private void ShowDiagnostics()
    {
        CompactVoiceView.DismissPlayerConfigPopup();
        CompactVoiceView.Visibility = Visibility.Collapsed;
        DiagnosticsGrid.Visibility = Visibility.Visible;
        MinWidth = 620;
        MinHeight = 540;
        Width = 720;
        Height = 650;
    }

    private void ShowCompactView()
    {
        DiagnosticsGrid.Visibility = Visibility.Collapsed;
        CompactVoiceView.Visibility = Visibility.Visible;
        MinWidth = 280;
        MinHeight = 390;
        Width = 280;
        Height = 390;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => ShowCompactView();

    private void DiagnosticsCloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        runCancellation?.Cancel();
        overlayWindow?.Close();
        overlayWindow = null;
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
        private string radio = "—";
        private long receivedFrames;
        private string gain = "—";
        private bool vadActive;
        private bool audible;
        private bool talking;

        public int ClientId { get; } = clientId;
        public string Name { get => currentName; set => Set(ref currentName, value); }
        public string Connection { get => connection; set => Set(ref connection, value); }
        public string Voice { get => voice; set => Set(ref voice, value); }
        public string Radio { get => radio; set => Set(ref radio, value); }
        public string Received => receivedFrames == 0 ? "待機中" : $"{receivedFrames} frame";
        public bool HasReceivedFrames => receivedFrames > 0;
        public string Gain { get => gain; set => Set(ref gain, value); }
        public bool VadActive { get => vadActive; set => Set(ref vadActive, value); }
        public bool Audible { get => audible; set => Set(ref audible, value); }
        public bool Talking { get => talking; set => Set(ref talking, value); }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void IncrementReceived()
        {
            receivedFrames++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Received)));
        }

        public void ResetReceived()
        {
            if (receivedFrames == 0) return;
            receivedFrames = 0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Received)));
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
