using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SocketIOClient;
using SocketIOClient.Transport;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed class VoiceServerProbe : IAsyncDisposable
{
    private readonly ProbeOptions options;
    private readonly string label;
    private readonly SocketIOClient.SocketIO socket;
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource peerVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AudioTestResult> audioVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WebRtcPeerManager peerManager;
    private readonly ConcurrentDictionary<string, int> peerClientIds = new();
    private readonly ConcurrentDictionary<string, int> stalledReconnectAttempts = new();
    private readonly ConcurrentDictionary<string, int> failedReconnectAttempts = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> peerOperationGates = new();
    private readonly ConcurrentDictionary<string, byte> pendingOfferFallbacks = new();
    private readonly ConcurrentDictionary<int, RadioStatus> impostorRadioStates = new();
    private readonly ConcurrentDictionary<int, NosRadioReport> nosRadioReports = new();
    private AmongUsState? currentGameState;
    private AmongUsMemoryReaderService? gameReader;
    private readonly SemaphoreSlim gameStateGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private long serverConnectionVersion;
    private long joinedConnectionVersion = -1;
    private volatile TaskCompletionSource currentConnectionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string currentJoinedLobby = "MENU";
    private string lastPublishedPublicLobbySignature = string.Empty;
    private string lastMixSignature = string.Empty;
    private SpatialVoiceSettings spatialVoiceSettings = new();
    private bool spatialAudioEnabled = true;
    private volatile bool mobileHostEnabled = true;
    private volatile bool mobileRunning;
    private IReadOnlyDictionary<int, PlayerAudioConfig> playerConfigs = new Dictionary<int, PlayerAudioConfig>();
    private LobbySettings ownLobbySettings = new();
    private LobbySettings activeLobbySettings = new();
    private volatile bool hasActiveLobbySettings;
    private int? hostClientId;
    private AudioDeviceSession? audioSession;
    private bool microphoneMuted;
    private bool deafened;
    private double masterVolume = 100d;
    private int voiceEffectStrength = 100;
    private double crewVolumeAsGhost = 1d;
    private double ghostVolumeAsImpostor = 0.1d;
    private double microphoneGain = 100d;
    private bool echoCancellation = true;
    private bool noiseSuppression = true;
    private bool autoGainControl;
    private bool microphoneSensitivityEnabled;
    private double microphoneSensitivity = 0.15d;
    private MicrophoneActivationMode microphoneActivationMode;
    private bool pushToTalkPressed;
    private readonly object radioTransmitGate = new();
    private volatile bool impostorRadioTransmitting;
    private volatile bool localVadTalking;
    private long impostorRadioVersion;
    private DateTimeOffset lastRadioStatusSentAt;
    private string nosRadioSession = string.Empty;
    private string lastNosRadioSignature = string.Empty;
    private DateTimeOffset lastNosRadioSentAt;
    private bool tohLobbyEnabled;
    private string tohLobbyCode = string.Empty;
    private TohRoleData? tohRoleOverride;
    private DateTimeOffset tohRoleReceivedAt;
    private DateTimeOffset lastTohLobbySentAt;
    private DateTimeOffset lastTohRosterSentAt;
    private readonly Dictionary<int, string> tohGameStartNames = [];
    private readonly Dictionary<int, string> tohLobbyNames = [];
    private readonly ConcurrentDictionary<string, (string Signature, DateTimeOffset SentAt)> tohRoleSent = new();
    private readonly object obsPayloadGate = new();
    private string lastObsPayload = string.Empty;
    private string lastObsSecret = string.Empty;

    public VoiceServerProbe(ProbeOptions options, string label = "probe")
    {
        this.options = options;
        this.label = label;
        lifetimeToken = lifetimeCancellation.Token;
        socket = new SocketIOClient.SocketIO(options.Server, new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = true,
            ReconnectionAttempts = 3,
            ReconnectionDelay = 500,
            ReconnectionDelayMax = 2_000,
            ConnectionTimeout = TimeSpan.FromSeconds(10)
        });
        peerManager = new WebRtcPeerManager(label, SendSignalAsync,
            sendTestTone: !options.LiveAudio && !options.AutoRadioTone, natFix: options.NatFix,
            testToneTarget: options.ExpectedPeerClientId is int expectedClientId
                ? socketId => peerClientIds.TryGetValue(socketId, out var clientId) && clientId == expectedClientId
                : null,
            traceDtlsRecords: options.DtlsTrace, turnTcp: options.TurnTcp);
        peerManager.PeerVerified += socketId =>
        {
            Log("OK", $"P2P双方向通信成功 peer={socketId}");
            peerVerified.TrySetResult();
            if (IsCurrentHost)
            {
                SendLobbySettingsToPeer(socketId);
            }
        };
        peerManager.AudioVerified += (socketId, result) =>
        {
            Log("OK", $"Opus音声検証成功 peer={socketId} rms={result.Rms:0.000} frequency={result.FrequencyHz:0.0}Hz");
            audioVerified.TrySetResult(result);
            if (peerClientIds.TryGetValue(socketId, out var clientId))
            {
                PeerAudioVerified?.Invoke(clientId, result);
            }
        };
        peerManager.PcmReceived += (socketId, pcm) =>
        {
            audioSession?.SubmitPlayback(socketId, pcm);
            if (peerClientIds.TryGetValue(socketId, out var clientId))
            {
                PeerPcmReceived?.Invoke(clientId, pcm);
            }
        };
        peerManager.PeerDataReceived += ApplyPeerData;
        peerManager.PeerDataChannelOpened += remoteSocketId =>
        {
            stalledReconnectAttempts.TryRemove(remoteSocketId, out _);
            failedReconnectAttempts.TryRemove(remoteSocketId, out _);
            SendNosRadioReportToPeer(remoteSocketId);
            if (peerClientIds.TryGetValue(remoteSocketId, out var clientId))
            {
                PeerConnectionStatusChanged?.Invoke(clientId, "data-ready");
            }
        };
        peerManager.PeerDataChannelStalled += (remoteSocketId, peerInstanceId) =>
        {
            _ = RunPeerOperationAsync(() => RecoverStalledPeerAsync(remoteSocketId, peerInstanceId));
        };
        peerManager.PeerConnectionFailed += (remoteSocketId, peerInstanceId) =>
        {
            if (string.CompareOrdinal(socket.Id, remoteSocketId) < 0)
            {
                _ = RunPeerOperationAsync(() => RecoverFailedPeerAsync(remoteSocketId, peerInstanceId));
            }
        };
        peerManager.PeerConnectionStateChanged += (remoteSocketId, state) =>
        {
            if (peerClientIds.TryGetValue(remoteSocketId, out var clientId))
            {
                PeerConnectionStatusChanged?.Invoke(clientId, state.ToString());
            }
        };
        peerManager.PeerQualityChanged += (remoteSocketId, quality) =>
        {
            if (peerClientIds.TryGetValue(remoteSocketId, out var clientId))
            {
                PeerQualityChanged?.Invoke(clientId, quality);
            }
        };
        peerManager.TestToneSent += remoteSocketId =>
        {
            if (peerClientIds.TryGetValue(remoteSocketId, out var clientId))
            {
                PeerTestToneSent?.Invoke(clientId);
            }
        };

        RegisterHandlers();
    }

    public Task Connected => connected.Task;

    public Task PeerVerified => peerVerified.Task;

    public Task<AudioTestResult> AudioVerified => audioVerified.Task;

    public event Action<AmongUsState>? GameStateApplied;

    public event Action<LobbySettings?>? LobbySettingsChanged;

    public event Action<int, PeerVoiceMix>? PeerMixChanged;

    public event Action<int, AudioTestResult>? PeerAudioVerified;

    public event Action<int, short[]>? PeerPcmReceived;

    public event Action<bool>? LocalVadChanged;

    public event Action<int, bool>? PeerVadChanged;

    public event Action<int>? LocalAudioFrameSent;

    public event Action<string>? ConnectionStatusChanged;

    public event Action<ConnectionQuality?>? ServerQualityChanged;

    public event Action<int, string>? PeerConnectionStatusChanged;

    public event Action<int, ConnectionQuality>? PeerQualityChanged;

    public event Action<int>? PeerTestToneSent;

    public event Action<bool>? ImpostorRadioTransmitChanged;

    public event Action<bool>? ImpostorRadioAvailabilityChanged;

    public bool CanUseImpostorRadio =>
        currentGameState is { GameState: GameState.Tasks or GameState.Discussion } state &&
        state.Players.Any(player => player.IsLocal && !player.IsDead && CanUseRadio(state, player));

    public LobbySettings? CurrentLobbySettings =>
        hasActiveLobbySettings && currentJoinedLobby != "MENU" &&
        Volatile.Read(ref joinedConnectionVersion) == Volatile.Read(ref serverConnectionVersion)
            ? activeLobbySettings : null;

    public bool IsPeerPresent(int clientId) =>
        peerClientIds.Any(peer => peer.Value == clientId);

    public void PublishObsOverlay(string? secret, AmongUsState state,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool localTalking, bool localUsingRadio)
    {
        if (!socket.Connected || secret is not { Length: 9 } || state.Players.Count == 0 ||
            ((state.GameState is GameState.Unknown or GameState.Menu) &&
             state.OldGameState == state.GameState))
            return;
        var data = ObsOverlayWire.Build(state, peers, localTalking, localUsingRadio);
        var payload = data.GetRawText();
        lock (obsPayloadGate)
        {
            if (payload == lastObsPayload && secret == lastObsSecret) return;
            lastObsPayload = payload;
            lastObsSecret = secret;
        }
        _ = RunPeerOperationAsync(() => socket.EmitAsync("signal", new { to = secret, data }).WaitAsync(lifetimeToken));
    }

    private bool IsCurrentHost => currentGameState?.IsHost ?? options.IsHost;

    public void SetOwnLobbySettings(LobbySettings settings)
    {
        ownLobbySettings = settings.Normalize();
        if (!IsCurrentHost)
        {
            return;
        }

        ApplyLobbySettings(ownLobbySettings);
        BroadcastLobbySettings();
        _ = RunPeerOperationAsync(PublishPublicLobbyForCurrentStateAsync);
    }

    private async Task PublishPublicLobbyForCurrentStateAsync()
    {
        await gameStateGate.WaitAsync(lifetimeToken);
        try
        {
            if (currentGameState is { } state)
                await PublishPublicLobbyIfChangedAsync(state);
        }
        finally
        {
            gameStateGate.Release();
        }
    }

    private async Task PublishPublicLobbyIfChangedAsync(AmongUsState state)
    {
        if (!state.IsHost || state.GameState is GameState.Menu or GameState.Unknown ||
            state.LobbyCode is "" or "MENU" || !socket.Connected ||
            !string.Equals(currentJoinedLobby, state.LobbyCode, StringComparison.Ordinal) ||
            Volatile.Read(ref joinedConnectionVersion) != Volatile.Read(ref serverConnectionVersion))
            return;

        var payload = PublicLobbyAnnouncement.Build(state, activeLobbySettings);
        var signature = $"{Volatile.Read(ref serverConnectionVersion)}|{state.LobbyCode}|{JsonSerializer.Serialize(payload)}";
        if (signature == lastPublishedPublicLobbySignature) return;
        await socket.EmitAsync("lobby", state.LobbyCode, payload).WaitAsync(lifetimeToken);
        lastPublishedPublicLobbySignature = signature;
    }

    public bool SetImpostorRadioTransmitting(bool active)
    {
        if (active && !CanUseImpostorRadio)
        {
            return false;
        }

        lock (radioTransmitGate)
        {
            if (impostorRadioTransmitting == active)
            {
                return true;
            }
            impostorRadioTransmitting = active;
            impostorRadioVersion = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), impostorRadioVersion + 1);
        }

        SendImpostorRadioStatus();
        if (socket.Connected)
        {
            _ = socket.EmitAsync("VAD", !active && localVadTalking);
        }
        ImpostorRadioTransmitChanged?.Invoke(active);
        return true;
    }

    public void ApplyGameState(AmongUsState state)
    {
        if (state.LobbyCode != tohLobbyCode || state.GameState == GameState.Menu)
        {
            tohLobbyCode = state.LobbyCode;
            tohLobbyEnabled = false;
            tohRoleOverride = null;
            tohRoleReceivedAt = default;
            lastTohLobbySentAt = default;
            lastTohRosterSentAt = default;
            tohGameStartNames.Clear();
            tohLobbyNames.Clear();
            tohRoleSent.Clear();
        }
        if (state.GameState == GameState.Lobby)
        {
            tohGameStartNames.Clear();
            tohLobbyNames.Clear();
            if (state.IsHost && state.Mod == AmongUsModType.TownOfHostForE)
            {
                foreach (var player in state.Players.Where(player => !player.Disconnected &&
                    player.ClientId >= 0 && player.Name.Length <= 100))
                    tohLobbyNames[player.ClientId] = player.Name;
            }
        }
        if (state.IsHost && state.Mod == AmongUsModType.TownOfHostForE &&
            state.GameState is GameState.Tasks or GameState.Discussion &&
            tohGameStartNames.Count == 0)
        {
            if (tohLobbyNames.Count > 0)
                foreach (var (id, name) in tohLobbyNames) tohGameStartNames[id] = name;
            else
                foreach (var player in state.Players.Where(player => !player.Disconnected &&
                    player.ClientId >= 0 && player.Name.Length <= 100))
                    tohGameStartNames[player.ClientId] = player.Name;
        }
        if (!state.IsHost && tohLobbyEnabled)
        {
            state.Mod = AmongUsModType.TownOfHostForE;
            if (state.GameState is GameState.Tasks or GameState.Discussion)
            {
                foreach (var player in state.Players)
                {
                    if (!tohGameStartNames.TryGetValue(player.ClientId, out var name)) continue;
                    player.Name = name;
                    player.AppearanceName = name;
                }
                var local = state.Players.SingleOrDefault(player => player.IsLocal);
                if (local is not null && DateTimeOffset.UtcNow - tohRoleReceivedAt < TimeSpan.FromSeconds(5))
                    local.TohRole = tohRoleOverride;
            }
        }
        if (currentGameState?.LobbyCode != state.LobbyCode ||
            state.GameState is GameState.Menu or GameState.Unknown)
        {
            mobileRunning = false;
        }
        currentGameState = state;
        SyncNosRadioReports(state);
        SyncTohReports(state);
        if (state.HostId > 0)
        {
            hostClientId = state.HostId;
        }
        if (options.AutoRadioTone && !impostorRadioTransmitting && CanUseImpostorRadio)
        {
            SetImpostorRadioTransmitting(true);
        }
        if (impostorRadioTransmitting && !CanUseImpostorRadio)
        {
            SetImpostorRadioTransmitting(false);
        }
        else if (impostorRadioTransmitting && DateTimeOffset.UtcNow - lastRadioStatusSentAt >= TimeSpan.FromSeconds(1))
        {
            SendImpostorRadioStatus();
        }
        GameStateApplied?.Invoke(state);
        ImpostorRadioAvailabilityChanged?.Invoke(CanUseImpostorRadio);
        RefreshPeerMixes();
    }

    public void SetExpectedHostClientId(int clientId)
    {
        if (clientId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientId));
        }
        hostClientId = clientId;
    }

    public void SetMicrophoneMuted(bool muted)
    {
        microphoneMuted = muted;
        audioSession?.SetMicrophoneMuted(muted);
    }

    public void SetMasterVolume(double volumePercent)
    {
        masterVolume = Math.Clamp(volumePercent, 0d, 200d);
        audioSession?.SetMasterVolume(masterVolume);
    }

    public void SetVoiceEffectStrength(int strengthPercent)
    {
        voiceEffectStrength = Math.Clamp(strengthPercent, 0, 100);
        RefreshPeerMixes();
    }

    public void SetPlayerConfigs(IReadOnlyDictionary<int, PlayerAudioConfig> configs)
    {
        Volatile.Write(ref playerConfigs, configs.ToDictionary(
            pair => pair.Key, pair => pair.Value.Normalize()));
        RefreshPeerMixes();
    }

    public void SetPlayerConfig(int configId, PlayerAudioConfig config)
    {
        var updated = new Dictionary<int, PlayerAudioConfig>(Volatile.Read(ref playerConfigs))
        {
            [configId] = config.Normalize()
        };
        Volatile.Write(ref playerConfigs, updated);
        RefreshPeerMixes();
    }

    public void SetListenerVolumes(double crewAsGhostPercent, double ghostAsImpostorPercent)
    {
        crewVolumeAsGhost = Math.Clamp(crewAsGhostPercent / 100d, 0d, 1d);
        ghostVolumeAsImpostor = Math.Clamp(ghostAsImpostorPercent / 100d, 0d, 1d);
        spatialVoiceSettings = spatialVoiceSettings with
        {
            CrewVolumeAsGhost = crewVolumeAsGhost,
            GhostVolumeAsImpostor = ghostVolumeAsImpostor
        };
        RefreshPeerMixes();
    }

    public void SetSpatialAudio(bool enabled)
    {
        spatialAudioEnabled = enabled;
        spatialVoiceSettings = spatialVoiceSettings with { SpatialAudio = enabled };
        RefreshPeerMixes();
    }

    public void SetMobileHost(bool enabled) => mobileHostEnabled = enabled;

    public bool IsMobileClientDetected => mobileRunning;

    public void SetMicrophoneGain(double gainPercent)
    {
        microphoneGain = Math.Clamp(gainPercent, 0d, 300d);
        audioSession?.SetMicrophoneGain(microphoneGain);
    }

    public void SetInputProcessing(bool echoCancellation, bool noiseSuppression, bool autoGainControl)
    {
        if (audioSession is not null)
        {
            throw new InvalidOperationException("Audio input processing changes require a session restart.");
        }
        this.echoCancellation = echoCancellation;
        this.noiseSuppression = noiseSuppression;
        this.autoGainControl = autoGainControl;
    }

    public void SetMicrophoneSensitivity(bool enabled, double minimumNoiseLevel)
    {
        microphoneSensitivityEnabled = enabled;
        microphoneSensitivity = Math.Clamp(minimumNoiseLevel, 0d, 1d);
        audioSession?.SetMicrophoneSensitivity(enabled, microphoneSensitivity);
    }

    public void SetNatFix(bool enabled) => peerManager.SetNatFix(enabled);

    public void SetMicrophoneActivationMode(MicrophoneActivationMode mode)
    {
        microphoneActivationMode = mode;
        pushToTalkPressed = false;
        audioSession?.SetMicrophoneActivationMode(mode);
    }

    public void SetPushToTalkPressed(bool pressed)
    {
        pushToTalkPressed = pressed;
        audioSession?.SetPushToTalkPressed(pressed);
    }

    public void SetDeafened(bool value)
    {
        deafened = value;
        audioSession?.SetDeafened(value);
    }

    public Task ReconnectClientAsync(int clientId)
    {
        var socketId = peerClientIds.SingleOrDefault(pair => pair.Value == clientId).Key;
        if (string.IsNullOrEmpty(socketId))
        {
            throw new InvalidOperationException($"再接続対象のclient IDが見つかりません: {clientId}");
        }

        Log("INFO", $"音声メディアを再接続 client={clientId}");
        return RunPeerOperationAsync(socketId, () => peerManager.ReconnectAsync(socketId));
    }

    public async Task RejoinCurrentGameLobbyAsync()
    {
        await gameStateGate.WaitAsync(lifetimeToken);
        try
        {
            var state = currentGameState ?? throw new InvalidOperationException("ゲーム状態をまだ取得していません。");
            var local = state.Players.SingleOrDefault(player => player.IsLocal)
                ?? throw new InvalidOperationException("ローカルプレイヤーを特定できません。");
            if (impostorRadioTransmitting)
            {
                SetImpostorRadioTransmitting(false);
            }
            await socket.EmitAsync("leave").WaitAsync(lifetimeToken);
            ResetPeerState();
            currentJoinedLobby = "MENU";
            var version = Volatile.Read(ref serverConnectionVersion);
            await Task.Delay(500, lifetimeToken);
            await socket.EmitAsync("id", local.Id, state.ClientId, string.Empty, string.Empty, state.ClientId.ToString()).WaitAsync(lifetimeToken);
            await socket.EmitAsync("join", state.LobbyCode, local.Id, state.ClientId, state.IsHost).WaitAsync(lifetimeToken);
            if (!socket.Connected || version != Volatile.Read(ref serverConnectionVersion)) return;
            Volatile.Write(ref joinedConnectionVersion, version);
            currentJoinedLobby = state.LobbyCode;
            await Task.Delay(500, lifetimeToken);
            foreach (var remoteSocketId in peerClientIds.Keys.ToArray())
            {
                await RunPeerOperationAsync(remoteSocketId, () => peerManager.InitiateAsync(remoteSocketId));
            }
            Log("INFO", $"復旧試験でロビー再参加 code={state.LobbyCode} client={state.ClientId}");
        }
        finally
        {
            gameStateGate.Release();
        }
    }

    public async Task RestartServerConnectionAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        cancellationToken = cancellation.Token;
        await gameStateGate.WaitAsync(cancellationToken);
        try
        {
            var state = currentGameState ?? throw new InvalidOperationException("ゲーム状態をまだ取得していません。");
            var local = state.Players.SingleOrDefault(player => player.IsLocal)
                ?? throw new InvalidOperationException("ローカルプレイヤーを特定できません。");

            ResetPeerState();
            currentJoinedLobby = "MENU";
            await socket.DisconnectAsync().WaitAsync(cancellationToken);
            await Task.Delay(500, cancellationToken);
            await socket.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await currentConnectionReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await SynchronizeGameStateCoreAsync(currentGameState ?? state);
            await Task.Delay(500, cancellationToken);
            foreach (var remoteSocketId in peerClientIds.Keys.ToArray())
            {
                await RunPeerOperationAsync(remoteSocketId, () => peerManager.InitiateAsync(remoteSocketId));
            }

            Log("INFO", $"復旧試験でサーバー再接続 code={state.LobbyCode} client={state.ClientId} socketId={socket.Id}");
        }
        finally
        {
            gameStateGate.Release();
        }
    }

    private void ResetPeerState()
    {
        var previousPeers = peerClientIds.ToArray();
        peerClientIds.Clear();
        peerManager.RemoveAllPeers();
        foreach (var (socketId, clientId) in previousPeers)
        {
            audioSession?.RemovePeer(socketId);
            PeerVadChanged?.Invoke(clientId, false);
            PeerConnectionStatusChanged?.Invoke(clientId, "closed");
        }
        stalledReconnectAttempts.Clear();
        failedReconnectAttempts.Clear();
        pendingOfferFallbacks.Clear();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(lifetimeCancellation.Cancel);
        try
        {
            await RunCoreAsync(lifetimeToken);
        }
        finally
        {
            lifetimeCancellation.Cancel();
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Log("INFO", $"接続開始: {options.Server}");
        // SocketIOClient 3.1.2 は、接続完了後に渡したトークンをキャンセルすると
        // 内部TaskCompletionSourceを再度完了させようとするため、待機側で制限する。
        await socket.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        if (options.GameProcessId is { } gameProcessId)
        {
            StartGameTracking(gameProcessId);
        }

        if (options.LiveAudio)
        {
            audioSession = new AudioDeviceSession(
                options.InputDevice,
                options.OutputDevice,
                pcm =>
                {
                    var peerCount = peerManager.BroadcastMonoPcm48k(
                        pcm.Span,
                        impostorRadioTransmitting ? CanReceiveRadioAudio : null);
                    if (peerCount > 0)
                    {
                        LocalAudioFrameSent?.Invoke(peerCount);
                    }
                },
                talking =>
                {
                    localVadTalking = talking;
                    LocalVadChanged?.Invoke(talking);
                    if (socket.Connected)
                    {
                        _ = socket.EmitAsync("VAD", talking && !impostorRadioTransmitting);
                    }
                },
                echoCancellation,
                noiseSuppression,
                autoGainControl,
                options.OldSampleDebug);
            audioSession.SetMicrophoneMuted(microphoneMuted);
            audioSession.SetDeafened(deafened);
            audioSession.SetMasterVolume(masterVolume);
            audioSession.SetMicrophoneGain(microphoneGain);
            audioSession.SetMicrophoneSensitivity(microphoneSensitivityEnabled, microphoneSensitivity);
            audioSession.SetMicrophoneActivationMode(microphoneActivationMode);
            audioSession.SetPushToTalkPressed(pushToTalkPressed);
            audioSession.Start();
        }

        if (options.AutoRadioTone)
        {
            _ = SendAutoRadioToneAsync(cancellationToken);
        }

        if (options.LobbyCode is not null)
        {
            await JoinLobbyAsync();
        }
        else
        {
            Log("INFO", options.GameProcessId is null
                ? "疎通確認モードです。ロビー参加は行いません。"
                : "ゲーム状態からロビー参加情報を待機しています。");
        }

        var mobileBeaconTask = MobileHostBeacon.RunAsync(
            () => socket.Connected ? MobileHostBeacon.Create(currentGameState, mobileHostEnabled) : null,
            beacon => socket.EmitAsync("signal", beacon),
            error => Log("WARN", $"Mobile host beacon failed: {error.Message}"),
            cancellationToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            await mobileBeaconTask;
        }
    }

    private void RegisterHandlers()
    {
        socket.OnConnected += (_, _) =>
        {
            if (lifetimeToken.IsCancellationRequested) return;
            lock (obsPayloadGate) { lastObsPayload = string.Empty; lastObsSecret = string.Empty; }
            Log("OK", $"Socket.IO接続成功 socketId={socket.Id}");
            ConnectionStatusChanged?.Invoke("ボイスサーバー接続済み");
            currentConnectionReady.TrySetResult();
            if (!connected.TrySetResult())
            {
                _ = RecoverServerSessionAsync();
            }
        };

        socket.OnPong += (_, duration) =>
        {
            if (!socket.Connected || duration < TimeSpan.Zero) return;
            var pingMs = duration.TotalMilliseconds;
            if (double.IsFinite(pingMs))
            {
                ServerQualityChanged?.Invoke(new ConnectionQuality(ServerPingMs: pingMs));
            }
        };

        socket.OnDisconnected += (_, reason) =>
        {
            if (lifetimeToken.IsCancellationRequested) return;
            // A new Socket.IO transport has a new server-side room membership.
            // Keep the game snapshot, but invalidate the old room and its peers.
            Interlocked.Increment(ref serverConnectionVersion);
            currentConnectionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            currentJoinedLobby = "MENU";
            ResetPeerState();
            hasActiveLobbySettings = false;
            impostorRadioStates.Clear();
            nosRadioReports.Clear();
            LobbySettingsChanged?.Invoke(null);
            mobileRunning = false;
            lock (obsPayloadGate) { lastObsPayload = string.Empty; lastObsSecret = string.Empty; }
            ServerQualityChanged?.Invoke(null);
            Log("WARN", $"切断: {reason}");
            ConnectionStatusChanged?.Invoke($"切断: {reason}");
        };
        socket.OnError += (_, error) =>
        {
            Log("ERROR", error);
            ConnectionStatusChanged?.Invoke($"接続エラー: {error}");
        };
        socket.OnReconnectAttempt += (_, attempt) =>
        {
            Log("INFO", $"再接続試行: {attempt}");
            ConnectionStatusChanged?.Invoke($"再接続中（{attempt}）");
        };

        socket.On("clientPeerConfig", response =>
        {
            var configuration = response.GetValue<JsonElement>();
            peerManager.Configure(configuration);
            Log("EVENT", "clientPeerConfig received (credentials redacted)");
        });
        socket.On("setHost", response =>
        {
            hostClientId = response.GetValue<int>();
            Log("EVENT", $"setHost client={hostClientId}");
        });
        socket.On("setClient", response =>
        {
            var remoteSocketId = response.GetValue<string>(0);
            var client = response.GetValue<JsonElement>(1);
            RegisterPeerClient(remoteSocketId, client);
            ScheduleOfferFallback(remoteSocketId);
        });
        socket.On("setClients", response =>
        {
            var clients = response.GetValue<JsonElement>();
            peerClientIds.Clear();
            stalledReconnectAttempts.Clear();
            failedReconnectAttempts.Clear();
            foreach (var client in clients.EnumerateObject())
            {
                RegisterPeerClient(client.Name, client.Value);
                ScheduleOfferFallback(client.Name);
            }
            Log("EVENT", $"setClients count={peerClientIds.Count}");
        });
        socket.On("join", response =>
        {
            var remoteSocketId = response.GetValue<string>(0);
            RegisterPeerClient(remoteSocketId, response.GetValue<JsonElement>(1));
            Log("EVENT", $"join peer={remoteSocketId}");
            // v3.2.7's existing client always offers to a newly joined peer.
            // Applying the glare tie-break here can leave both sides waiting:
            // the new Electron peer never offers from its setClients handler.
            _ = RunPeerOperationAsync(remoteSocketId, () => peerManager.InitiateAsync(remoteSocketId));
        });
        socket.On("leave", response =>
        {
            var remoteSocketId = response.GetValue<string>();
            Log("EVENT", $"leave peer={remoteSocketId}");
            if (peerClientIds.TryRemove(remoteSocketId, out var departedClientId))
            {
                stalledReconnectAttempts.TryRemove(remoteSocketId, out _);
                failedReconnectAttempts.TryRemove(remoteSocketId, out _);
                pendingOfferFallbacks.TryRemove(remoteSocketId, out _);
                impostorRadioStates.TryRemove(departedClientId, out _);
                nosRadioReports.TryRemove(departedClientId, out _);
                PeerVadChanged?.Invoke(departedClientId, false);
            }
            peerManager.RemovePeer(remoteSocketId);
            audioSession?.RemovePeer(remoteSocketId);
        });
        socket.On("VAD", response =>
        {
            var payload = response.GetValue<JsonElement>();
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("activity", out var activity) ||
                activity.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !payload.TryGetProperty("client", out var client) ||
                client.ValueKind != JsonValueKind.Object ||
                !client.TryGetProperty("clientId", out var id) ||
                !id.TryGetInt32(out var clientId) ||
                !payload.TryGetProperty("socketId", out var socketIdElement) ||
                socketIdElement.ValueKind != JsonValueKind.String ||
                socketIdElement.GetString() is not { Length: > 0 } socketId ||
                !peerClientIds.TryGetValue(socketId, out var knownClientId) ||
                knownClientId != clientId)
            {
                return;
            }

            PeerVadChanged?.Invoke(clientId, activity.GetBoolean());
        });
        socket.On("signal", response =>
        {
            var envelope = response.GetValue<JsonElement>();
            if (!envelope.TryGetProperty("from", out var fromElement) ||
                !envelope.TryGetProperty("data", out var data) ||
                fromElement.GetString() is not { Length: > 0 } remoteSocketId)
            {
                return;
            }

            if (MobileHostBeacon.IsResponseForLobby(data, currentGameState))
            {
                mobileRunning = true;
                return;
            }
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("mobilePlayerInfo", out _))
            {
                return;
            }

            var type = data.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "other";
            if (type == "bcl-control" && data.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.String)
            {
                ApplyPeerData(remoteSocketId, payload.GetString() ?? string.Empty);
                return;
            }
            if (type == "offer" && string.CompareOrdinal(socket.Id, remoteSocketId) < 0 &&
                peerManager.ShouldDeferIncomingOffer(remoteSocketId))
            {
                Log("EVENT", $"signal offer ignored by glare rule < {remoteSocketId}");
                return;
            }
            var detail = type == "candidate" ? DescribeCandidate(data) : string.Empty;
            Log("EVENT", $"signal {type}{detail} < {remoteSocketId}");
            _ = RunPeerOperationAsync(remoteSocketId, () => peerManager.ApplySignalAsync(remoteSocketId, data.Clone()));
        });
        Observe("error");
    }

    private void RegisterPeerClient(string socketId, JsonElement client)
    {
        if (client.TryGetProperty("clientId", out var clientIdElement) && clientIdElement.TryGetInt32(out var clientId))
        {
            foreach (var staleSocketId in peerClientIds
                         .Where(pair => pair.Value == clientId && pair.Key != socketId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                Log("INFO", $"同一clientの旧peerを除去 client={clientId} peer={staleSocketId}");
                peerClientIds.TryRemove(staleSocketId, out _);
                stalledReconnectAttempts.TryRemove(staleSocketId, out _);
                failedReconnectAttempts.TryRemove(staleSocketId, out _);
                peerManager.RemovePeer(staleSocketId);
                audioSession?.RemovePeer(staleSocketId);
            }
            peerClientIds[socketId] = clientId;
            RefreshPeerMix(socketId, clientId);
        }
    }

    private void ScheduleOfferFallback(string remoteSocketId)
    {
        if (!pendingOfferFallbacks.TryAdd(remoteSocketId, 0)) return;
        _ = OfferFallbackAsync(remoteSocketId);
    }

    private async Task RecoverStalledPeerAsync(string remoteSocketId, Guid peerInstanceId)
    {
        // Delay outside the peer operation gate: a new offer from the remote
        // peer must be processed while we wait for its own recovery attempt.
        if (string.CompareOrdinal(socket.Id, remoteSocketId) > 0)
        {
            await Task.Delay(4_000, lifetimeToken);
        }
        if (!socket.Connected || !peerClientIds.ContainsKey(remoteSocketId) ||
            !peerManager.IsCurrentPeer(remoteSocketId, peerInstanceId) ||
            peerManager.HasOpenDataChannel(remoteSocketId)) return;

        var attempt = stalledReconnectAttempts.AddOrUpdate(remoteSocketId, 1, (_, count) => count + 1);
        if (attempt > 1)
        {
            await Task.Delay(Math.Min(1_000 * (1 << Math.Min(attempt - 2, 4)), 15_000), lifetimeToken);
        }
        await RunPeerOperationAsync(remoteSocketId, async () =>
        {
            if (!socket.Connected || !peerClientIds.ContainsKey(remoteSocketId) ||
                !peerManager.IsCurrentPeer(remoteSocketId, peerInstanceId) ||
                peerManager.HasOpenDataChannel(remoteSocketId)) return;
            Log("INFO", $"データチャネル停滞を再接続 peer={remoteSocketId} attempt={attempt}");
            await peerManager.ReconnectAsync(remoteSocketId);
        });
    }

    private async Task RecoverFailedPeerAsync(string remoteSocketId, Guid peerInstanceId)
    {
        // A relay candidate can arrive after the first ICE checks fail. Do not
        // replace that peer immediately and discard candidates still in flight.
        if (!peerManager.IsFailedPeer(remoteSocketId, peerInstanceId)) return;
        var attempt = failedReconnectAttempts.AddOrUpdate(remoteSocketId, 1, (_, count) => count + 1);
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * (1 << Math.Min(attempt - 1, 3)), 16)), lifetimeToken);
        await RunPeerOperationAsync(remoteSocketId, async () =>
        {
            if (!socket.Connected || !peerClientIds.ContainsKey(remoteSocketId) ||
                !peerManager.IsFailedPeer(remoteSocketId, peerInstanceId)) return;
            Log("INFO", $"失敗したpeerを自動再接続 peer={remoteSocketId}");
            await peerManager.ReconnectAsync(remoteSocketId);
        });
    }

    private async Task OfferFallbackAsync(string remoteSocketId)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), lifetimeToken);
            if (!socket.Connected || currentJoinedLobby == "MENU" ||
                !peerClientIds.ContainsKey(remoteSocketId)) return;
            await RunPeerOperationAsync(remoteSocketId, async () =>
            {
                if (peerManager.HasPeer(remoteSocketId)) return;
                Log("INFO", $"offer未着のpeerへ接続を開始 peer={remoteSocketId}");
                await peerManager.InitiateAsync(remoteSocketId);
            });
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        finally
        {
            pendingOfferFallbacks.TryRemove(remoteSocketId, out _);
        }
    }

    private void ApplyPeerData(string remoteSocketId, string message)
    {
        if (!peerClientIds.TryGetValue(remoteSocketId, out var clientId))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() == "nos-radio-data")
            {
                ApplyNosRadioReport(clientId, data);
                return;
            }

            if (type.ValueKind == JsonValueKind.String &&
                type.GetString() is "toh4e-lobby" or "toh4e-roster" or "toh4e-role")
            {
                ApplyTohReport(clientId, data);
                return;
            }

            if (data.TryGetProperty("impostorRadio", out var radio) &&
                radio.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                ApplyImpostorRadioStatus(clientId, data, radio.GetBoolean());
                return;
            }

            if (IsCurrentHost || hostClientId != clientId ||
                !data.TryGetProperty("maxDistance", out var maxDistance) ||
                !maxDistance.TryGetDouble(out _))
            {
                return;
            }
            var received = JsonSerializer.Deserialize<LobbySettings>(message, LobbySettings.WireJsonOptions);
            if (received is not null)
            {
                ApplyLobbySettings(received);
            }
        }
        catch (JsonException)
        {
            // Other peer messages are not necessarily lobby settings.
        }
    }

    private void SyncTohReports(AmongUsState state)
    {
        if (!state.IsHost || state.GameState is GameState.Menu or GameState.Unknown ||
            state.LobbyCode is "" or "MENU") return;
        var now = DateTimeOffset.UtcNow;
        if (now - lastTohLobbySentAt >= TimeSpan.FromSeconds(1))
        {
            var lobby = JsonSerializer.Serialize(new
            {
                type = "toh4e-lobby", lobbyCode = state.LobbyCode,
                enabled = state.Mod == AmongUsModType.TownOfHostForE
            });
            foreach (var socketId in peerClientIds.Keys)
                peerManager.TrySendPeerData(socketId, lobby);
            lastTohLobbySentAt = now;
        }
        if (state.Mod != AmongUsModType.TownOfHostForE ||
            state.GameState is not (GameState.Tasks or GameState.Discussion)) return;

        if (tohGameStartNames.Count > 0 &&
            now - lastTohRosterSentAt >= TimeSpan.FromSeconds(1))
        {
            var roster = JsonSerializer.Serialize(new
            {
                type = "toh4e-roster", lobbyCode = state.LobbyCode,
                players = tohGameStartNames.Select(pair => new
                {
                    clientId = pair.Key, name = pair.Value
                }).ToArray()
            });
            foreach (var socketId in peerClientIds.Keys)
                peerManager.TrySendPeerData(socketId, roster);
            lastTohRosterSentAt = now;
        }

        foreach (var (socketId, clientId) in peerClientIds)
        {
            var player = state.Players.SingleOrDefault(candidate =>
                candidate.ClientId == clientId && !candidate.IsLocal && !candidate.Disconnected);
            if (player is null) continue;
            var role = player.TohRole;
            var signature = $"{state.LobbyCode}|{player.Id}|{JsonSerializer.Serialize(role)}";
            if (tohRoleSent.TryGetValue(socketId, out var sent) && sent.Signature == signature &&
                now - sent.SentAt < TimeSpan.FromSeconds(1)) continue;
            var payload = TohRoleWire.Serialize(state.LobbyCode, player);
            if (peerManager.TrySendPeerData(socketId, payload))
                tohRoleSent[socketId] = (signature, now);
        }
    }

    private void ApplyTohReport(int clientId, JsonElement data)
    {
        var state = currentGameState;
        if (state is null || state.IsHost || clientId != hostClientId ||
            state.GameState is GameState.Menu or GameState.Unknown ||
            !data.TryGetProperty("lobbyCode", out var code) ||
            code.ValueKind != JsonValueKind.String || code.GetString() != state.LobbyCode ||
            !data.TryGetProperty("type", out var type)) return;
        if (type.GetString() == "toh4e-lobby")
        {
            if (!data.TryGetProperty("enabled", out var enabled) ||
                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
            tohLobbyEnabled = enabled.GetBoolean();
            if (!tohLobbyEnabled)
            {
                tohRoleOverride = null;
                tohRoleReceivedAt = default;
                tohGameStartNames.Clear();
            }
            return;
        }
        if (type.GetString() == "toh4e-roster")
        {
            if (!data.TryGetProperty("players", out var players) ||
                players.ValueKind != JsonValueKind.Array || players.GetArrayLength() > 20) return;
            var names = new Dictionary<int, string>();
            foreach (var item in players.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("clientId", out var id) || !id.TryGetInt32(out var parsedId) ||
                    !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    name.GetString() is not { Length: <= 100 } parsedName) return;
                names[parsedId] = parsedName;
            }
            tohGameStartNames.Clear();
            foreach (var (id, name) in names) tohGameStartNames[id] = name;
            tohLobbyEnabled = true;
            return;
        }
        if (type.GetString() != "toh4e-role" ||
            state.GameState is not (GameState.Tasks or GameState.Discussion) ||
            !data.TryGetProperty("targetClientId", out var targetClientId) ||
            !targetClientId.TryGetInt32(out var targetClient) || targetClient != state.ClientId ||
            !data.TryGetProperty("targetPlayerId", out var targetPlayerId) ||
            !targetPlayerId.TryGetInt32(out var targetPlayer) ||
            state.Players.SingleOrDefault(player => player.IsLocal)?.Id != targetPlayer ||
            !TohRoleWire.TryRead(data, out var parsed)) return;
        tohRoleOverride = parsed;
        tohRoleReceivedAt = DateTimeOffset.UtcNow;
        tohLobbyEnabled = true;
        state.Mod = AmongUsModType.TownOfHostForE;
        var local = state.Players.SingleOrDefault(player => player.IsLocal);
        if (local is not null) local.TohRole = parsed;
        RefreshPeerMixes();
    }

    private void SyncNosRadioReports(AmongUsState state)
    {
        var local = state.Players.SingleOrDefault(player => player.IsLocal);
        var active = state.Mod == AmongUsModType.NebulaOnTheShip &&
            state.GameState is GameState.Tasks or GameState.Discussion && local is not null;
        var session = active ? $"{state.LobbyCode}|{state.ClientId}" : string.Empty;
        if (nosRadioSession != session)
        {
            nosRadioSession = session;
            lastNosRadioSignature = string.Empty;
            lastNosRadioSentAt = default;
            nosRadioReports.Clear();
        }
        if (!active || local is null) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var (playerId, report) in nosRadioReports)
        {
            if (now - report.ReceivedAt >= TimeSpan.FromSeconds(10) ||
                !state.Players.Any(player => player.Id == playerId && player.ClientId == report.ClientId &&
                    !player.Disconnected))
                nosRadioReports.TryRemove(playerId, out _);
        }

        if (state.NosLocalMicPosition is null) return;
        var signature = JsonSerializer.Serialize(state.NosRadios);
        if (signature == lastNosRadioSignature && now - lastNosRadioSentAt < TimeSpan.FromSeconds(3)) return;
        foreach (var socketId in peerClientIds.Keys)
            SendNosRadioReportToPeer(socketId);
        lastNosRadioSignature = signature;
        lastNosRadioSentAt = now;
    }

    private void SendNosRadioReportToPeer(string remoteSocketId)
    {
        var state = currentGameState;
        var local = state?.Players.SingleOrDefault(player => player.IsLocal);
        if (state is null || local is null || state.Mod != AmongUsModType.NebulaOnTheShip ||
            state.GameState is not (GameState.Tasks or GameState.Discussion) ||
            state.NosLocalMicPosition is null || !peerClientIds.ContainsKey(remoteSocketId))
            return;
        var payload = JsonSerializer.Serialize(new
        {
            type = "nos-radio-data",
            lobbyCode = state.LobbyCode,
            playerId = local.Id,
            radios = state.NosRadios.Select(radio => new
            {
                kind = radio.Kind,
                hearableMask = radio.HearableMask,
                nameLength = radio.Name.Length,
                name = radio.Name
            })
        });
        peerManager.TrySendPeerData(remoteSocketId, payload);
    }

    private void ApplyNosRadioReport(int clientId, JsonElement data)
    {
        var state = currentGameState;
        var sender = state?.Players.SingleOrDefault(player => player.ClientId == clientId);
        if (state is null || state.Mod != AmongUsModType.NebulaOnTheShip ||
            state.GameState is not (GameState.Tasks or GameState.Discussion) ||
            sender is null || sender.IsLocal || sender.Disconnected ||
            !data.TryGetProperty("lobbyCode", out var lobbyCode) ||
            lobbyCode.ValueKind != JsonValueKind.String || lobbyCode.GetString() != state.LobbyCode ||
            !data.TryGetProperty("playerId", out var playerId) ||
            !playerId.TryGetInt32(out var parsedPlayerId) || parsedPlayerId != sender.Id ||
            !data.TryGetProperty("radios", out var radiosElement) ||
            radiosElement.ValueKind != JsonValueKind.Array || radiosElement.GetArrayLength() > 8)
            return;

        var radios = new List<NosRadioData>();
        foreach (var element in radiosElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("kind", out var kind) || !kind.TryGetInt32(out var parsedKind) ||
                !element.TryGetProperty("hearableMask", out var mask) || !mask.TryGetInt32(out var parsedMask) ||
                !element.TryGetProperty("nameLength", out var nameLength) ||
                !nameLength.TryGetInt32(out var parsedLength) || parsedLength is < 0 or > 32 ||
                !element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                name.GetString() is not { } parsedName || parsedName.Length != parsedLength)
                return;
            radios.Add(new NosRadioData(parsedKind, parsedMask, parsedName));
        }
        var previous = nosRadioReports.TryGetValue(sender.Id, out var existing) ? existing : null;
        nosRadioReports[sender.Id] = new NosRadioReport(sender.ClientId, radios, DateTimeOffset.UtcNow);
        if (previous is null || !previous.Radios.SequenceEqual(radios))
        {
            Log("INFO", $"NoSラジオ定義: client={sender.ClientId} channels={radios.Count}");
            RefreshPeerMixes();
        }
    }

    private IReadOnlyList<NosRadioData>? GetNosRadios(AmongUsState state, Player player)
    {
        if (state.Mod != AmongUsModType.NebulaOnTheShip) return null;
        if (player.IsLocal) return state.NosLocalMicPosition is null ? null : state.NosRadios;
        if (!nosRadioReports.TryGetValue(player.Id, out var report) || report.ClientId != player.ClientId ||
            DateTimeOffset.UtcNow - report.ReceivedAt >= TimeSpan.FromSeconds(10)) return null;
        return report.Radios;
    }

    public IReadOnlyDictionary<int, NosRadioReport> GetNosRadioReportsSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return nosRadioReports
            .Where(entry => now - entry.Value.ReceivedAt < TimeSpan.FromSeconds(10))
            .ToDictionary(entry => entry.Key, entry => entry.Value with
            {
                Radios = entry.Value.Radios.ToArray()
            });
    }

    private bool HasNosJackalRadio(AmongUsState state, Player player) =>
        NosRadioRules.HasJackalChannel(GetNosRadios(state, player));

    private bool CanHearNosJackalRadio(AmongUsState state, Player sender, Player listener) =>
        NosRadioRules.CanHearJackalChannel(GetNosRadios(state, sender), listener.Id);

    private bool CanUseRadio(AmongUsState state, Player player)
    {
        if (HasNosJackalRadio(state, player))
            return spatialVoiceSettings.JackalRadioEnabled && !spatialVoiceSettings.ImpostorRadioOnlyMode;
        if (state.Mod == AmongUsModType.SuperNewRoles && player.SnrRole?.IsJackalTeam == true)
            return spatialVoiceSettings.JackalRadioEnabled && !spatialVoiceSettings.ImpostorRadioOnlyMode;
        return player.IsImpostor &&
            (spatialVoiceSettings.ImpostorRadioEnabled || spatialVoiceSettings.ImpostorRadioOnlyMode);
    }

    private void ApplyLobbySettings(LobbySettings settings)
    {
        var next = settings.Normalize();
        if (hasActiveLobbySettings && next == activeLobbySettings)
        {
            return;
        }

        activeLobbySettings = next;
        hasActiveLobbySettings = true;
        spatialVoiceSettings = spatialVoiceSettings with
        {
            MaxDistance = next.MaxDistance,
            VisionHearing = next.VisionHearing,
            HearImpostorsInVents = next.HearImpostorsInVents,
            ImpostorsHearImpostorsInVents = next.ImpostersHearImpostersInvent,
            ImpostorRadioEnabled = next.ImpostorRadioEnabled,
            ImpostorRadioOnlyMode = next.ImpostorRadioOnlyMode,
            CommsSabotage = next.CommsSabotage,
            HearThroughCameras = next.HearThroughCameras,
            WallsBlockAudio = next.WallsBlockAudio,
            Haunting = next.Haunting,
            DeadOnly = next.DeadOnly,
            MeetingGhostOnly = next.MeetingGhostOnly,
            NosVoicePositions = next.NosVoicePositions,
            NosFixerJammingVoiceBlock = next.NosFixerJammingVoiceBlock,
            JackalRadioEnabled = next.JackalRadioEnabled,
            JackalHaunting = next.JackalHaunting,
            JackalHearOutsideVents = next.JackalHearOutsideVents,
            JackalTalkInVents = next.JackalTalkInVents,
            SidekickHaunting = next.SidekickHaunting,
            SidekickHearOutsideVents = next.SidekickHearOutsideVents,
            SidekickTalkInVents = next.SidekickTalkInVents,
            TohNeutralKillerHaunting = next.TohNeutralKillerHaunting,
            NosNeutralKillerHaunting = next.NosNeutralKillerHaunting
        };
        if (options.AutoRadioTone && !impostorRadioTransmitting && CanUseImpostorRadio)
        {
            SetImpostorRadioTransmitting(true);
        }
        if (impostorRadioTransmitting && !CanUseImpostorRadio)
        {
            SetImpostorRadioTransmitting(false);
        }
        LobbySettingsChanged?.Invoke(next);
        ImpostorRadioAvailabilityChanged?.Invoke(CanUseImpostorRadio);
        Log("INFO", $"ロビー音声設定更新: distance={next.MaxDistance:0.##} vent={next.HearImpostorsInVents} impostorVent={next.ImpostersHearImpostersInvent}");
        RefreshPeerMixes();
    }

    private void BroadcastLobbySettings()
    {
        if (!IsCurrentHost || currentJoinedLobby == "MENU" || !socket.Connected)
        {
            return;
        }

        foreach (var socketId in peerClientIds.Keys)
        {
            SendLobbySettingsToPeer(socketId);
        }
    }

    private void SendLobbySettingsToPeer(string socketId)
    {
        if (!IsCurrentHost || currentJoinedLobby == "MENU" || !socket.Connected)
        {
            return;
        }

        var payload = ownLobbySettings.ToWireJson();
        peerManager.TrySendPeerData(socketId, payload);
        _ = RunPeerOperationAsync(() => SendSignalAsync(socketId, new { type = "bcl-control", payload }));
    }

    private void SendImpostorRadioStatus()
    {
        var state = currentGameState;
        if (state is null || !socket.Connected)
        {
            return;
        }

        bool active;
        long version;
        lock (radioTransmitGate)
        {
            active = impostorRadioTransmitting;
            version = impostorRadioVersion;
            lastRadioStatusSentAt = DateTimeOffset.UtcNow;
        }

        var payload = JsonSerializer.Serialize(new { impostorRadio = active, impostorRadioVersion = version });
        foreach (var socketId in peerClientIds.Keys)
        {
            if (active && !CanReceiveRadioAudio(socketId))
            {
                continue;
            }

            peerManager.TrySendPeerData(socketId, payload);
            _ = RunPeerOperationAsync(() => SendSignalAsync(socketId, new { type = "bcl-control", payload }));
        }
        Log("INFO", $"インポスターラジオ送信: active={active} version={version}");
    }

    private bool CanReceiveRadioAudio(string remoteSocketId)
    {
        if (!peerClientIds.TryGetValue(remoteSocketId, out var clientId))
        {
            return false;
        }

        var state = currentGameState;
        var sender = state?.Players.SingleOrDefault(candidate => candidate.IsLocal);
        var listener = state?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
        if (state is null || sender is null || listener is null) return false;
        if (HasNosJackalRadio(state, sender))
            return spatialVoiceSettings.JackalRadioEnabled && !spatialVoiceSettings.ImpostorRadioOnlyMode &&
                CanHearNosJackalRadio(state, sender, listener);
        if (state.Mod == AmongUsModType.SuperNewRoles && sender.SnrRole?.IsJackalTeam == true)
            return spatialVoiceSettings.JackalRadioEnabled && !spatialVoiceSettings.ImpostorRadioOnlyMode &&
                listener.SnrRole?.IsJackalTeam == true && !listener.IsDead;
        return listener is { IsImpostor: true, IsDead: false } or { IsDead: true };
    }

    private async Task SendAutoRadioToneAsync(CancellationToken cancellationToken)
    {
        const int samplesPerFrame = 960;
        var frame = new byte[samplesPerFrame * sizeof(short)];
        long frameNumber = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (impostorRadioTransmitting)
                {
                    for (var sample = 0; sample < samplesPerFrame; sample++)
                    {
                        var value = (short)(Math.Sin(2d * Math.PI * 440d *
                            (frameNumber * samplesPerFrame + sample) / 48_000d) * 6000d);
                        BitConverter.TryWriteBytes(frame.AsSpan(sample * sizeof(short), sizeof(short)), value);
                    }

                    var sent = peerManager.BroadcastMonoPcm48k(frame, CanReceiveRadioAudio);
                    if (frameNumber % 50 == 0)
                    {
                        Log("RADIO-TEST", $"state={currentGameState?.GameState} recipients={sent} frame={frameNumber}");
                    }
                    frameNumber++;
                }
                await Task.Delay(20, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ApplyImpostorRadioStatus(int clientId, JsonElement data, bool active)
    {
        var sender = currentGameState?.Players.SingleOrDefault(player => player.ClientId == clientId);
        if (active && (sender is null || sender.IsDead || currentGameState is null ||
            !CanUseRadio(currentGameState, sender)))
        {
            return;
        }

        var version = data.TryGetProperty("impostorRadioVersion", out var versionElement) &&
                      versionElement.TryGetInt64(out var parsedVersion)
            ? parsedVersion
            : -1;
        if (impostorRadioStates.TryGetValue(clientId, out var previous) && version < previous.Version)
        {
            return;
        }

        impostorRadioStates[clientId] = new RadioStatus(version, active, DateTimeOffset.UtcNow);
        if (previous?.Active != active)
        {
            Log("INFO", $"インポスターラジオ: client={clientId} active={active}");
            RefreshPeerMixes();
        }
    }

    private bool IsImpostorRadioActive(int clientId) =>
        impostorRadioStates.TryGetValue(clientId, out var status) &&
        status.Active && DateTimeOffset.UtcNow - status.SeenAt < TimeSpan.FromSeconds(3);

    private void RefreshPeerMixes()
    {
        foreach (var (socketId, clientId) in peerClientIds)
        {
            RefreshPeerMix(socketId, clientId);
        }

        LogMixSnapshot();
    }

    private void RefreshPeerMix(string socketId, int clientId)
    {
        if (currentGameState is null)
        {
            return;
        }

        var me = currentGameState.Players.SingleOrDefault(player => player.IsLocal);
        var other = currentGameState.Players.SingleOrDefault(player => player.ClientId == clientId);
        if (me is null || other is null)
        {
            var unmappedMix = new PeerVoiceMix(0d, 0d, 0d, "unmapped-player");
            audioSession?.SetPeerMix(socketId, unmappedMix);
            PeerMixChanged?.Invoke(clientId, unmappedMix);
            return;
        }

        var mix = SpatialVoicePolicy.Calculate(currentGameState, me, other, spatialVoiceSettings,
            IsImpostorRadioActive(clientId), CanHearNosJackalRadio(currentGameState, other, me));
        mix = PlayerAudioConfig.For(other, Volatile.Read(ref playerConfigs)).Apply(mix);
        var effect = VoiceDisguiseEffectPolicy.Select(currentGameState, me, other,
            activeLobbySettings, voiceEffectStrength, mix.Audible, IsImpostorRadioActive(clientId));
        audioSession?.SetPeerMix(socketId, mix, effect);
        PeerMixChanged?.Invoke(clientId, mix);
    }

    private void StartGameTracking(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var processInfo = GameProcessScanner.CreateProcessInfo(process);
        gameReader = new AmongUsMemoryReaderService();
        gameReader.Error += (_, message) => Log("ERROR", $"game reader: {message}");
        gameReader.StateChanged += (_, state) => _ = SynchronizeGameStateAsync(state);
        gameReader.SetProcess(processInfo);
        gameReader.Start();
        Log("INFO", $"Among Us状態追跡開始 pid={processId}");
    }

    private async Task SynchronizeGameStateAsync(AmongUsState state)
    {
        if (lifetimeToken.IsCancellationRequested) return;
        var acquired = false;
        try
        {
            await gameStateGate.WaitAsync(lifetimeToken);
            acquired = true;
            await SynchronizeGameStateCoreAsync(state);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log("ERROR", $"ゲーム状態同期失敗: {exception.Message}");
        }
        finally
        {
            if (acquired) gameStateGate.Release();
        }
    }

    private async Task SynchronizeGameStateCoreAsync(AmongUsState state)
    {
        var wasHost = currentGameState?.IsHost == true;
        ApplyGameState(state);
        if (!socket.Connected) return;
        var local = state.Players.SingleOrDefault(player => player.IsLocal);
        var targetLobby = state.GameState is GameState.Menu or GameState.Unknown || local is null ? "MENU" : state.LobbyCode;
        var version = Volatile.Read(ref serverConnectionVersion);
        if (string.Equals(targetLobby, currentJoinedLobby, StringComparison.Ordinal) &&
            (targetLobby == "MENU" || Volatile.Read(ref joinedConnectionVersion) == version))
        {
            if (state.IsHost && !wasHost)
            {
                ApplyLobbySettings(ownLobbySettings);
                BroadcastLobbySettings();
            }
            await PublishPublicLobbyIfChangedAsync(state);
            return;
        }

        if (impostorRadioTransmitting) SetImpostorRadioTransmitting(false);
        await socket.EmitAsync("leave").WaitAsync(lifetimeToken);
        currentJoinedLobby = "MENU";
        lastPublishedPublicLobbySignature = string.Empty;
        ResetPeerState();
        spatialVoiceSettings = new SpatialVoiceSettings(
            SpatialAudio: spatialAudioEnabled,
            CrewVolumeAsGhost: crewVolumeAsGhost,
            GhostVolumeAsImpostor: ghostVolumeAsImpostor);
        activeLobbySettings = new LobbySettings();
        hasActiveLobbySettings = false;
        LobbySettingsChanged?.Invoke(null);
        hostClientId = state.HostId > 0 ? state.HostId : null;
        impostorRadioStates.Clear();
        nosRadioReports.Clear();
        nosRadioSession = string.Empty;
        if (targetLobby == "MENU")
        {
            Log("INFO", "ゲーム状態に追従してロビー退出");
            return;
        }

        await socket.EmitAsync("id", local!.Id, state.ClientId, string.Empty, string.Empty, state.ClientId.ToString()).WaitAsync(lifetimeToken);
        await socket.EmitAsync("join", targetLobby, local.Id, state.ClientId, state.IsHost).WaitAsync(lifetimeToken);
        // A disconnect can run while the send is awaiting transport I/O.
        // Never restore membership belonging to that disconnected socket.
        if (!socket.Connected || version != Volatile.Read(ref serverConnectionVersion)) return;
        Volatile.Write(ref joinedConnectionVersion, version);
        currentJoinedLobby = targetLobby;
        Log("INFO", $"ゲーム状態に追従してロビー参加 code={targetLobby} client={state.ClientId} player={local.Id}");
        if (state.IsHost)
        {
            ApplyLobbySettings(ownLobbySettings);
            BroadcastLobbySettings();
        }
        await PublishPublicLobbyIfChangedAsync(state);
        LobbySettingsChanged?.Invoke(CurrentLobbySettings);
    }

    private async Task RecoverServerSessionAsync()
    {
        if (lifetimeToken.IsCancellationRequested) return;
        var acquired = false;
        try
        {
            await gameStateGate.WaitAsync(lifetimeToken);
            acquired = true;
            // Read the latest snapshot after acquiring the gate; an old snapshot
            // captured by OnConnected could otherwise rejoin a lobby already left.
            if (currentGameState is { } state)
                await SynchronizeGameStateCoreAsync(state);
            else if (options.LobbyCode is { } lobby && socket.Connected &&
                (currentJoinedLobby != lobby || Volatile.Read(ref joinedConnectionVersion) != Volatile.Read(ref serverConnectionVersion)))
                await JoinLobbyCoreAsync();
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log("ERROR", $"サーバー再接続後のロビー復旧失敗: {exception.Message}");
        }
        finally
        {
            if (acquired) gameStateGate.Release();
        }
    }

    private void LogMixSnapshot()
    {
        if (currentGameState is null || peerClientIds.Count == 0)
        {
            return;
        }

        var me = currentGameState.Players.SingleOrDefault(player => player.IsLocal);
        if (me is null)
        {
            return;
        }

        var mixes = peerClientIds
            .Select(pair =>
            {
                var player = currentGameState.Players.SingleOrDefault(candidate => candidate.ClientId == pair.Value);
                var mix = player is null
                    ? new PeerVoiceMix(0d, 0d, 0d, "unmapped-player")
                    : SpatialVoicePolicy.Calculate(currentGameState, me, player, spatialVoiceSettings,
                        IsImpostorRadioActive(pair.Value), CanHearNosJackalRadio(currentGameState, player, me));
                return $"{pair.Value}:{mix.Gain:0.000}:{mix.Pan:0.00}:{mix.Reason}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
        var signature = string.Join('|', mixes);
        if (signature == lastMixSignature)
        {
            return;
        }

        lastMixSignature = signature;
        Log("MIX", signature);
    }

    private async Task RunPeerOperationAsync(Func<Task> operation)
    {
        try
        {
            lifetimeToken.ThrowIfCancellationRequested();
            await operation();
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log("ERROR", $"WebRTC処理失敗: {exception.Message}");
        }
    }

    private async Task RunPeerOperationAsync(string remoteSocketId, Func<Task> operation)
    {
        if (lifetimeToken.IsCancellationRequested) return;
        var gate = peerOperationGates.GetOrAdd(remoteSocketId, _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        try
        {
            await gate.WaitAsync(lifetimeToken);
            acquired = true;
            await RunPeerOperationAsync(operation);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (acquired) gate.Release();
        }
    }

    private Task SendSignalAsync(string remoteSocketId, object data)
    {
        if (lifetimeToken.IsCancellationRequested) return Task.FromCanceled(lifetimeToken);
        return socket.EmitAsync("signal", new { to = remoteSocketId, data }).WaitAsync(lifetimeToken);
    }

    private static string DescribeCandidate(JsonElement signal)
    {
        if (!signal.TryGetProperty("candidate", out var candidate))
        {
            return string.Empty;
        }
        var text = candidate.ValueKind == JsonValueKind.String
            ? candidate.GetString()
            : candidate.TryGetProperty("candidate", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = Array.IndexOf(parts, "typ");
        var candidateType = typeIndex >= 0 && typeIndex + 1 < parts.Length ? parts[typeIndex + 1] : "unknown";
        var protocol = parts.Length > 2 && parts[2].Equals("udp", StringComparison.OrdinalIgnoreCase)
            ? "udp"
            : parts.Length > 2 && parts[2].Equals("tcp", StringComparison.OrdinalIgnoreCase) ? "tcp" : "other";
        var component = parts.Length > 1 && ushort.TryParse(parts[1], out var parsedComponent)
            ? parsedComponent.ToString() : "?";
        var addressKind = parts.Length > 4 && parts[4].EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? "mdns"
            : "ip";
        var lineIndex = candidate.ValueKind == JsonValueKind.Object &&
            candidate.TryGetProperty("sdpMLineIndex", out var line) && line.TryGetInt32(out var index)
                ? index.ToString() : "?";
        return $"({candidateType}/{protocol}/component={component}/{addressKind}/mline={lineIndex})";
    }

    private void Observe(string eventName)
    {
        socket.On(eventName, response =>
        {
            var payload = response.GetValue<JsonElement>();
            Log("EVENT", $"{eventName} {payload.GetRawText()}");
        });
    }

    private async Task JoinLobbyAsync()
    {
        await gameStateGate.WaitAsync(lifetimeToken);
        try
        {
            if (socket.Connected && (currentJoinedLobby != options.LobbyCode ||
                Volatile.Read(ref joinedConnectionVersion) != Volatile.Read(ref serverConnectionVersion)))
                await JoinLobbyCoreAsync();
        }
        finally
        {
            gameStateGate.Release();
        }
    }

    private async Task JoinLobbyCoreAsync()
    {
        Log(
            "INFO",
            $"ロビー参加: code={options.LobbyCode} playerId={options.PlayerId} clientId={options.ClientId} host={options.IsHost}");

        var version = Volatile.Read(ref serverConnectionVersion);
        // TanukiBCL v3.2.7 ConnectionController.joinLobby と同じ送信順序・引数。
        await socket.EmitAsync("leave").WaitAsync(lifetimeToken);
        await socket.EmitAsync("id", options.PlayerId, options.ClientId, string.Empty, string.Empty, string.Empty).WaitAsync(lifetimeToken);
        await socket.EmitAsync("join", options.LobbyCode!, options.PlayerId, options.ClientId, options.IsHost).WaitAsync(lifetimeToken);
        if (!socket.Connected || version != Volatile.Read(ref serverConnectionVersion)) return;
        Volatile.Write(ref joinedConnectionVersion, version);
        currentJoinedLobby = options.LobbyCode!;
        if (currentGameState is { } state)
            await PublishPublicLobbyIfChangedAsync(state);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            return new ValueTask(disposalTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        TryCleanup("session cancellation", lifetimeCancellation.Cancel);
        TryCleanup("game reader", () => gameReader?.Dispose());
        // Drain the in-flight game join/reload. Its socket waits use lifetimeToken.
        await gameStateGate.WaitAsync();
        try
        {
            // Keep each peer operation's gate held until its actual work ends;
            // disposing the peer manager before then would race SDP mutations.
            foreach (var gate in peerOperationGates.Values)
            {
                await gate.WaitAsync();
                gate.Release();
            }
            if (socket.Connected)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.EmitAsync("leave").WaitAsync(timeout.Token);
                    await socket.DisconnectAsync().WaitAsync(timeout.Token);
                }
                catch (Exception exception)
                {
                    Log("WARN", $"終了処理: {exception.Message}");
                }
            }

            TryCleanup("audio session", () => audioSession?.Dispose());
            TryCleanup("peer manager", peerManager.Dispose);
            TryCleanup("voice socket", socket.Dispose);
        }
        finally
        {
            gameStateGate.Release();
            // Memory-reader callbacks can already be queued when Dispose is
            // called. Leave this managed semaphore alive so late callbacks can
            // observe cancellation instead of touching a disposed gate.
        }
    }

    private void TryCleanup(string resource, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception exception) { Log("WARN", $"終了処理 ({resource}): {exception.Message}"); }
    }

    private void Log(string level, string message)
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{label}/{level}] {message}");
    }

    private sealed record RadioStatus(long Version, bool Active, DateTimeOffset SeenAt);

    public sealed record NosRadioReport(int ClientId, IReadOnlyList<NosRadioData> Radios, DateTimeOffset ReceivedAt);
}
