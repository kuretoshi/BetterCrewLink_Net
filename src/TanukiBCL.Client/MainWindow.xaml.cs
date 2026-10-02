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
    private readonly ClientSessionCoordinator sessions = new();
    private readonly CoalescedSessionRestart settingsRestarts = new();
    private VoiceServerProbe? probe;
    private SettingsWindow? settingsWindow;
    private OverlayWindow? overlayWindow;
    private int? activeGamePid;
    private GlobalHotkeyMonitor? hotkeys;
    private Task? reloadTask;
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
    private bool isClosing;
    private long connectionIntentVersion;
    private ConnectionQuality? serverQuality;
    private long sentAudioFrames;
    private ProcessStartInfo? relaunch;
    private bool relaunchRequested;

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

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (isClosing || settingsWindow is not null) return;
        CompactVoiceView.DismissPlayerConfigPopup();
        var hostInGame = currentState is { IsHost: true, GameState: GameState.Tasks or GameState.Discussion };
        var window = new SettingsWindow(settings, !hostInGame,
            probe?.CurrentLobbySettings, currentState?.IsHost != true, currentState,
            ApplyPlayerConfig) { Owner = this };
        settingsWindow = window;
        hotkeysSuspended = true;
        window.SettingsApplied += SettingsWindow_SettingsApplied;
        window.SettingsReset += SettingsWindow_SettingsReset;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            window.SettingsApplied -= SettingsWindow_SettingsApplied;
            window.SettingsReset -= SettingsWindow_SettingsReset;
            settingsWindow = null;
            hotkeysSuspended = false;
        }
    }

    private void SettingsWindow_SettingsReset()
    {
        hotkeys?.UpdateBindings(settings);
        if (!isClosing && sessions.Current is { AcceptsCallbacks: true } session)
            RequestSettingsRestart(session);
    }

    private void SettingsWindow_SettingsApplied(ClientSettingsChange change)
    {
        if (isClosing) return;
        try
        {
            var previous = change.Previous;
            var current = change.Current;
            if (previous.HardwareAcceleration != current.HardwareAcceleration)
                QueueApplicationRelaunch();
            Topmost = current.AlwaysOnTop;
            if (previous.MicrophoneName != current.MicrophoneName || previous.SpeakerName != current.SpeakerName)
            {
                InputCombo.ItemsSource = AudioDeviceSession.GetInputDevices();
                OutputCombo.ItemsSource = AudioDeviceSession.GetOutputDevices();
                SelectConfiguredDevices();
            }
            if (sessions.Current is { AcceptsCallbacks: true } session && probe is { } activeProbe)
            {
                if (ClientSessionSettings.From(previous) != ClientSessionSettings.From(current))
                    RequestSettingsRestart(session);
                else
                    ApplyLiveSettings(activeProbe, previous, current);
            }
            // The shared settings object has already been updated. Visual-only
            // changes also apply while stopped or while a restart is awaiting.
            UpdateCompactView();
        }
        catch (Exception error)
        {
            ReportSettingsApplicationFailure(error);
        }
    }

    private void ApplyLiveSettings(VoiceServerProbe activeProbe, ClientSettings previous, ClientSettings current)
    {
        if (previous.MasterVolume != current.MasterVolume) activeProbe.SetMasterVolume(current.MasterVolume);
        if (previous.VoiceEffectStrength != current.VoiceEffectStrength)
            activeProbe.SetVoiceEffectStrength(current.VoiceEffectStrength);
        if (previous.CrewVolumeAsGhost != current.CrewVolumeAsGhost ||
            previous.GhostVolumeAsImpostor != current.GhostVolumeAsImpostor)
            activeProbe.SetListenerVolumes(current.CrewVolumeAsGhost, current.GhostVolumeAsImpostor);
        if (previous.EnableSpatialAudio != current.EnableSpatialAudio)
            activeProbe.SetSpatialAudio(current.EnableSpatialAudio);
        if (previous.MicrophoneGainEnabled != current.MicrophoneGainEnabled ||
            previous.MicrophoneGain != current.MicrophoneGain)
            activeProbe.SetMicrophoneGain(current.MicrophoneGainEnabled ? current.MicrophoneGain : 100d);
        if (previous.MicSensitivityEnabled != current.MicSensitivityEnabled ||
            previous.MicSensitivity != current.MicSensitivity)
            activeProbe.SetMicrophoneSensitivity(current.MicSensitivityEnabled, current.MicSensitivity);
        if (previous.NatFix != current.NatFix) activeProbe.SetNatFix(current.NatFix);
        if (previous.MobileHost != current.MobileHost) activeProbe.SetMobileHost(current.MobileHost);
        // Resetting activation mode/bindings on unrelated slider events would
        // release an already-held PTT key, so update only the changed controls.
        if (previous.PushToTalkMode != current.PushToTalkMode)
            activeProbe.SetMicrophoneActivationMode(current.PushToTalkMode);
        if (previous.PushToTalkShortcut != current.PushToTalkShortcut ||
            previous.ImpostorRadioShortcut != current.ImpostorRadioShortcut ||
            previous.MuteShortcut != current.MuteShortcut || previous.DeafenShortcut != current.DeafenShortcut)
            hotkeys?.UpdateBindings(current);
        if (previous.MyLobbySettings != current.MyLobbySettings)
            activeProbe.SetOwnLobbySettings(current.MyLobbySettings);
        if (!previous.PlayerConfigMap.OrderBy(pair => pair.Key).SequenceEqual(
            current.PlayerConfigMap.OrderBy(pair => pair.Key)))
            activeProbe.SetPlayerConfigs(current.PlayerConfigMap);
    }

    private void RequestSettingsRestart(ClientSessionCoordinator.Session session)
    {
        var wasMuted = microphoneMuted;
        var wasDeafened = deafened;
        var restartIntent = connectionIntentVersion;
        _ = settingsRestarts.Request(async () =>
        {
            session.RequestStop();
            await session.Completion;
        }, () => !isClosing && IsVisible && !Dispatcher.HasShutdownStarted &&
            restartIntent == connectionIntentVersion && sessions.Current is null,
        () =>
        {
            // Changes made while teardown awaited are already in settings.
            // Refresh selection and start once from that latest configuration.
            SelectConfiguredDevices();
            if (microphoneMuted != wasMuted) ToggleMicrophoneMute();
            if (deafened != wasDeafened) ToggleDeafen();
            StartButton_Click(this, new RoutedEventArgs());
        }, ReportSettingsApplicationFailure);
    }

    private void ReportSettingsApplicationFailure(Exception error)
    {
        Trace.TraceError($"Settings could not be applied to the active session: {error}");
        if (isClosing || Dispatcher.HasShutdownStarted) return;
        StatusText.Text = $"設定の反映に失敗しました: {error.Message}";
        ShowDiagnostics();
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
        if (sessions.Current?.AcceptsCallbacks == true) probe?.SetPlayerConfig(configId, config);
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
        if (isClosing || sessions.Current is not null) return;
        if (ProcessCombo.SelectedItem is not ProcessChoice process ||
            InputCombo.SelectedItem is not AudioDeviceInfo input ||
            OutputCombo.SelectedItem is not AudioDeviceInfo output)
        {
            MessageBox.Show(this, "Among Us、マイク、スピーカーを選択してください。", "TanukiBCL");
            return;
        }
        if (!sessions.TryStart(out var session)) return;
        connectionIntentVersion++;
        await session.RunAsync(() => RunSessionAsync(session, process, input, output), result =>
        {
            if (result.Error is { } error)
            {
                Trace.TraceError($"Client session failed: {error}");
                if (!isClosing && !Dispatcher.HasShutdownStarted)
                {
                    StatusText.Text = $"接続失敗: {error.Message}";
                    ShowDiagnostics();
                }
            }
            else if (!isClosing && !Dispatcher.HasShutdownStarted) StatusText.Text = "停止しました";
        });
    }

    private async Task RunSessionAsync(ClientSessionCoordinator.Session session, ProcessChoice process,
        AudioDeviceInfo input, AudioDeviceInfo output)
    {
        // Register the UI reset first so it runs last, including partial startup.
        session.AddCleanup(() =>
        {
            activeGamePid = null;
            hotkeys = null;
            if (!isClosing && !Dispatcher.HasShutdownStarted) SetRunning(false);
            return ValueTask.CompletedTask;
        });
        session.AddCleanup(() =>
        {
            var previousOverlay = overlayWindow;
            overlayWindow = null;
            previousOverlay?.Close();
            return ValueTask.CompletedTask;
        });
        // During a live settings restart, the settings transaction owns disk
        // persistence. Do not commit other sliders' temporary runtime values.
        if (settingsWindow is null)
        {
            settings.MicrophoneName = input.Name;
            settings.SpeakerName = output.Name;
            try { ClientSettingsStore.Save(settings); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"デバイス設定を保存できませんでした: {exception.Message}", "TanukiBCL");
            }
        }
        session.Token.ThrowIfCancellationRequested();
        activeGamePid = process.Id;
        SetRunning(true);
        ShowCompactView();
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
        var activeProbe = new VoiceServerProbe(options, "client");
        probe = activeProbe;
        session.AddCleanup(async () =>
        {
            try { await activeProbe.DisposeAsync(); }
            finally { if (ReferenceEquals(probe, activeProbe)) probe = null; }
        });
        session.AddCleanup(async () =>
        {
            try
            {
                if (reloadTask is { } pendingReload) await pendingReload;
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
            finally
            {
                reloadTask = null;
                reloadInProgress = false;
            }
        });
        activeProbe.SetMasterVolume(settings.MasterVolume);
        activeProbe.SetVoiceEffectStrength(settings.VoiceEffectStrength);
        activeProbe.SetPlayerConfigs(settings.PlayerConfigMap);
        activeProbe.SetListenerVolumes(settings.CrewVolumeAsGhost, settings.GhostVolumeAsImpostor);
        activeProbe.SetSpatialAudio(settings.EnableSpatialAudio);
        activeProbe.SetMobileHost(settings.MobileHost);
        activeProbe.SetInputProcessing(settings.EchoCancellation, settings.NoiseSuppression, settings.AutoGainControl);
        activeProbe.SetMicrophoneGain(settings.MicrophoneGainEnabled ? settings.MicrophoneGain : 100d);
        activeProbe.SetMicrophoneSensitivity(settings.MicSensitivityEnabled, settings.MicSensitivity);
        activeProbe.SetMicrophoneActivationMode(settings.PushToTalkMode);
        activeProbe.SetOwnLobbySettings(settings.MyLobbySettings);
        activeProbe.SetMicrophoneMuted(microphoneMuted);
        activeProbe.SetDeafened(deafened);
        activeProbe.LobbySettingsChanged += _ => Dispatch(session, () =>
            settingsWindow?.UpdateCurrentLobbySettings(activeProbe.CurrentLobbySettings));
        activeProbe.ConnectionStatusChanged += status => Dispatch(session, () =>
        {
            StatusText.Text = status;
            voiceServerConnected = status == "ボイスサーバー接続済み";
            UpdateCompactView();
        });
        activeProbe.ServerQualityChanged += quality => Dispatch(session, () =>
        {
            serverQuality = quality;
            UpdateCompactView();
        });
        activeProbe.GameStateApplied += state => Dispatch(session, () => ShowGameState(state));
        activeProbe.PeerMixChanged += (clientId, mix) => Dispatch(session, () => UpdatePeerMix(clientId, mix));
        activeProbe.PeerConnectionStatusChanged += (clientId, status) => Dispatch(session, () => UpdatePeerConnection(clientId, status));
        activeProbe.PeerVadChanged += (clientId, active) => Dispatch(session, () =>
        {
            var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
            var row = FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}");
            row.VadActive = active;
            row.Talking = active && row.Audible && player?.InVent != true;
            UpdateCompactView();
        });
        activeProbe.PeerPcmReceived += (clientId, _) => Dispatch(session, () =>
        {
            var player = currentState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
            var row = FindOrCreatePeer(clientId, player?.Name ?? $"client {clientId}");
            var firstFrame = !row.HasReceivedFrames;
            row.IncrementReceived();
            if (firstFrame) UpdateCompactView();
        });
        activeProbe.LocalAudioFrameSent += peerCount => Dispatch(session, () =>
        {
            sentAudioFrames++;
            SentText.Text = $"Opus送信: {sentAudioFrames} frame / {peerCount} peer";
        });
        activeProbe.LocalVadChanged += talking => Dispatch(session, () =>
        {
            localTalking = talking;
            VadText.Text = microphoneMuted ? "マイク: ミュート中" : talking ? "マイク: 発話中" : "マイク: 待機中";
            VadText.Foreground = talking && !microphoneMuted
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.LightGray;
            UpdateCompactView();
        });
        activeProbe.ImpostorRadioAvailabilityChanged += available => Dispatch(session, () => RadioButton.IsEnabled = available);
        activeProbe.ImpostorRadioTransmitChanged += active => Dispatch(session, () =>
        {
            radioTransmitting = active;
            RadioButton.Content = active ? "インポスターラジオ: ON" : "インポスターラジオ: OFF";
            RadioButton.Background = active ? System.Windows.Media.Brushes.DarkOrange : null;
            UpdateCompactView();
        });
        hotkeys = new GlobalHotkeyMonitor(
            pressed => { if (session.AcceptsCallbacks) activeProbe.SetPushToTalkPressed(pressed); },
            () => Dispatch(session, () =>
            {
                if (activeProbe.CanUseImpostorRadio)
                    activeProbe.SetImpostorRadioTransmitting(!radioTransmitting);
            }),
            () => Dispatch(session, ToggleMicrophoneMute),
            () => Dispatch(session, ToggleDeafen),
            () => hotkeysSuspended);
        hotkeys.UpdateBindings(settings);
        var runningHotkeys = hotkeys;
        var hotkeyTask = Task.Run(() => runningHotkeys.RunAsync(session.Token));
        session.AddCleanup(async () =>
        {
            try
            {
                await hotkeyTask;
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        });
        var probeTask = activeProbe.RunAsync(session.Token);
        // A failed hotkey callback must not leave a seemingly live session with
        // controls no longer responding. Teardown observes the hotkey exception.
        if (await Task.WhenAny(probeTask, hotkeyTask) == hotkeyTask) session.RequestStop();
        await probeTask;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        // An explicit stop also cancels a settings-initiated pending restart.
        connectionIntentVersion++;
        sessions.Current?.RequestStop();
    }

    private async void CompactVoiceView_ReloadRequested(object? sender, EventArgs e)
    {
        if (reloadInProgress) return;
        var activeProbe = probe;
        var session = sessions.Current;
        if (activeProbe is null || session is null || !session.AcceptsCallbacks)
        {
            ShowDiagnostics();
            return;
        }

        reloadInProgress = true;
        Task? reload = null;
        try
        {
            StatusText.Text = "音声接続を再読み込み中...";
            reload = activeProbe.RestartServerConnectionAsync(session.Token);
            reloadTask = reload;
            await reload;
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (session.AcceptsCallbacks)
            {
                StatusText.Text = $"再読み込み失敗: {exception.Message}";
                ShowDiagnostics();
            }
        }
        finally
        {
            if (ReferenceEquals(reloadTask, reload))
            {
                reloadTask = null;
                reloadInProgress = false;
            }
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
        => ToggleMicrophoneMute();

    private void ToggleMicrophoneMute()
    {
        if (sessions.Current is { AcceptsCallbacks: false }) return;
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
        if (sessions.Current is { AcceptsCallbacks: false }) return;
        deafened = !deafened;
        probe?.SetDeafened(deafened);
        DeafenButton.Content = deafened ? "スピーカーミュート解除" : "スピーカーをミュート";
        UpdateCompactView();
    }

    private void RadioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sessions.Current?.AcceptsCallbacks == true)
            probe?.SetImpostorRadioTransmitting(!radioTransmitting);
    }

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

    private void Dispatch(ClientSessionCoordinator.Session session, Action action)
    {
        if (!session.AcceptsCallbacks || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(session.Guard(action));
    }

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
        if (settings.ObsOverlay && sessions.Current?.AcceptsCallbacks == true &&
            currentState is { } state && probe is { } activeProbe)
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
        if (!settings.EnableOverlay || sessions.Current?.AcceptsCallbacks != true ||
            activeGamePid is not { } pid || probe is null)
        {
            overlayWindow?.Close();
            overlayWindow = null;
            return;
        }
        overlayWindow ??= new OverlayWindow(pid, settings);
        var peerStatuses = peers.ToDictionary(row => row.ClientId, row => new OverlayPeerStatus(
            row.Connection is "data-ready" or "接続済み",
            row.VadActive, row.Radio == "送信中"));
        overlayWindow.Update(currentState, peerStatuses, localTalking, microphoneMuted, deafened, radioTransmitting);
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (sessions.Current is not { } session) return;
        // Keep the final window (and its Dispatcher) alive until native audio and
        // the socket have finished shutting down. OnClosed is too late for this.
        e.Cancel = true;
        if (isClosing) return;
        isClosing = true;
        connectionIntentVersion++;
        session.RequestStop();
        await session.Completion;
        if (!Dispatcher.HasShutdownStarted) Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        isClosing = true;
        sessions.Current?.RequestStop();
        overlayWindow?.Close();
        overlayWindow = null;
        if (relaunch is not null)
        {
            try { Process.Start(relaunch)?.Dispose(); }
            catch (Exception error)
            {
                MessageBox.Show($"再起動できませんでした。TanukiBCLを手動で起動してください: {error.Message}", "TanukiBCL");
            }
        }
        base.OnClosed(e);
    }

    private void QueueApplicationRelaunch()
    {
        if (relaunchRequested || isClosing) return;
        relaunchRequested = true;
        var resumeGamePid = sessions.Current is not null ? activeGamePid : null;
        // Finish the settings event before closing its modal window. This also
        // lets failed pending saves cancel the close instead of losing edits.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            relaunchRequested = false;
            if (isClosing) return;
            var dialog = settingsWindow;
            dialog?.Close();
            if (dialog?.IsVisible == true) return;
            relaunch = ApplicationRelaunch.Create(Environment.ProcessPath!,
                typeof(App).Assembly.Location, resumeGamePid);
            Close(); // OnClosing drains native audio/socket work before OnClosed launches.
        }));
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
