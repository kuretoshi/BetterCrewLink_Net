using System.Text.Json;
using BetterCrewLinkKai.DotNet.Models;
using NAudio.Wave;
using SocketIO.Core;
using SocketIOClient;
using SocketIOClient.Transport;

namespace BetterCrewLinkKai.DotNet.Services;

public enum VoiceSessionState
{
    Disconnected,
    Connecting,
    Connected
}

public sealed class VoiceClient
{
    public string SocketId { get; init; } = string.Empty;

    public int PlayerId { get; init; }

    public int ClientId { get; init; }

    public bool IsTalking { get; set; }

    public bool IsUsingRadio { get; set; }

    public bool IsMuted { get; set; }

    public bool IsDeafened { get; set; }
}

public sealed class VoicePeerState
{
    public string SocketId { get; init; } = string.Empty;

    public int PlayerId { get; init; }

    public int ClientId { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsTalking { get; init; }

    public bool IsUsingRadio { get; init; }

    public bool IsMuted { get; init; }

    public bool IsDeafened { get; init; }

    public bool IsConnected { get; init; }

    public VoicePeerConnectionState PeerConnectionState { get; init; } = VoicePeerConnectionState.Disconnected;

    public PlayerVoiceMix? Mix { get; init; }
}

public sealed class VoiceSessionService : IDisposable
{
    private readonly VoiceMixService voiceMixService = new();
    private readonly PeerConnectionService peerConnectionService = new();
    private SocketIOClient.SocketIO? socket;
    private AmongUsState? lastState;
    private AppSettings? lastSettings;
    private string currentLobby = "MENU";
    private string lastLobbyPresenceKey = string.Empty;
    private string lastMobileHostKey = string.Empty;
    private string lastMutePresenceKey = string.Empty;
    private CancellationTokenSource? obsOverlayLoopCts;
    private bool localTalking;
    private bool localMuted;
    private bool localDeafened;
    private bool disposed;
    private readonly object audioSendLock = new();
    private readonly Queue<VoiceAudioFrame> pendingAudioFrames = [];
    private readonly object peerTalkingLock = new();
    private readonly Dictionary<string, DateTimeOffset> peerTalkingUntil = [];
    private readonly object peerAudioSequenceLock = new();
    private readonly Dictionary<string, long> peerLastAudioSequence = [];
    private readonly object peerWebRtcAudioLock = new();
    private readonly Dictionary<string, DateTimeOffset> peerLastWebRtcAudioAt = [];
    private readonly Queue<string> debugLog = [];
    private readonly SemaphoreSlim socketConnectionLock = new(1, 1);
    private long localAudioSequence;
    private bool isFlushingAudioFrames;
    private const int MaxPendingAudioFrames = 12;
    private const int MaxAudioFramesPerSignal = 4;
    private const double RemoteTalkingLevelThreshold = 3.0d;
    private const int PeerTalkingHoldMilliseconds = 650;
    private const int MaxRemoteVoiceFrameAgeMilliseconds = 1500;
    private const int WebRtcLegacySuppressMilliseconds = 2000;

    public VoiceSessionState State { get; private set; } = VoiceSessionState.Disconnected;

    public string StatusText { get; private set; } = "切断済み";

    public int ServerHostId { get; private set; }

    public Dictionary<string, VoiceClient> SocketClients { get; } = [];

    public IReadOnlyDictionary<int, PlayerVoiceMix> VoiceMix { get; private set; } = new Dictionary<int, PlayerVoiceMix>();

    public IReadOnlyList<VoicePeerState> Peers { get; private set; } = [];

    public AmongUsState? CurrentState => lastState;

    public IReadOnlyDictionary<string, VoicePeerConnection> PeerConnections => peerConnectionService.Peers;

    public bool UseNatFix => peerConnectionService.UseNatFix;

    public IReadOnlyList<string> IceServers => peerConnectionService.IceServers;

    public int ImpostorRadioClientId { get; private set; } = -1;

    public LobbySettings? HostLobbySettings { get; private set; }

    public event EventHandler? StateChanged;

    public event EventHandler<string>? Error;
    public event EventHandler<string>? DebugLogChanged;

    public event EventHandler<PeerDataPayloadEventArgs>? PeerDataPayloadQueued;

    public event EventHandler<PeerAudioFrameEventArgs>? PeerAudioFrameQueued;

    public event EventHandler<PeerAudioFrameEventArgs>? PeerAudioFrameReceived;

    public VoiceSessionService()
    {
        peerConnectionService.PeersChanged += (_, _) =>
        {
            LogDebug("peer " + string.Join(", ", peerConnectionService.Peers.Values.Select(static peer => $"{Short(peer.SocketId)}:{peer.State}:{peer.Initiator}")));
            RebuildPeers();
            NotifyStateChanged();
        };
        peerConnectionService.SignalPayloadQueued += (_, args) =>
        {
            LogDebug($"rtc signal > {Short(args.SocketId)} {GetPayloadType(args.Payload)}");
            DiagnosticLog.WebRtc($"rtc signal queued > {args.SocketId}\n{SerializeForLog(args.Payload)}");
            _ = EmitPeerSignalAsync(args.SocketId, args.Payload);
        };
        peerConnectionService.DataPayloadQueued += (_, args) =>
        {
            LogDebug($"data fallback > {Short(args.SocketId)} {Trim(args.Payload)}");
            PeerDataPayloadQueued?.Invoke(this, args);
        };
        peerConnectionService.AudioFrameQueued += (_, args) =>
        {
            if (args.Frame.IsTransmitting)
            {
                LogDebug($"audio fallback > {Short(args.SocketId)} lvl={args.Frame.Level:0.0}");
            }

            PeerAudioFrameQueued?.Invoke(this, args);
        };
        peerConnectionService.AudioFrameReceived += (_, args) =>
        {
            LogDebug($"webrtc audio < {Short(args.SocketId)} lvl={args.Frame.Level:0.0}");
            ApplyWebRtcPeerAudioFrame(args.SocketId, args.Frame);
        };
        peerConnectionService.Error += (_, message) =>
        {
            StatusText = message;
            LogDebug("rtc error " + message);
            DiagnosticLog.WebRtc("rtc error " + message);
            Error?.Invoke(this, message);
            NotifyStateChanged();
        };
    }

    public async Task ConnectAsync(string serverUrl, string lobbyCode, CancellationToken cancellationToken = default)
    {
        await socketConnectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectSocketAsync(serverUrl, lobbyCode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            socketConnectionLock.Release();
        }
    }

    private async Task ConnectSocketAsync(string serverUrl, string lobbyCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ArgumentException("Server URL is empty.", nameof(serverUrl));
        }

        var normalizedServerUrl = NormalizeServerUrl(serverUrl);
        State = VoiceSessionState.Connecting;
        StatusText = "接続中";
        NotifyStateChanged();
        LogDebug($"socket preparing {normalizedServerUrl}");

        await DisconnectSocketAsync().ConfigureAwait(false);

        socket = new SocketIOClient.SocketIO(normalizedServerUrl, new SocketIOOptions
        {
            EIO = EngineIO.V3,
            Transport = TransportProtocol.WebSocket,
            Reconnection = true,
            ReconnectionAttempts = 3,
            ReconnectionDelay = 500,
            ReconnectionDelayMax = 1500,
            ConnectionTimeout = TimeSpan.FromSeconds(10)
        });

        RegisterSocketHandlers(socket);
        LogDebug($"socket connecting {normalizedServerUrl}");
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await WaitForSocketTaskAsync(socket.ConnectAsync(connectTimeout.Token), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (!cancellationToken.IsCancellationRequested)
        {
            State = VoiceSessionState.Disconnected;
            StatusText = $"Socket.IO接続タイムアウト: {normalizedServerUrl}";
            LogDebug($"socket connect timeout {normalizedServerUrl}");
            NotifyStateChanged();
            await DisconnectSocketAsync().ConfigureAwait(false);
            throw new TimeoutException(StatusText, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            State = VoiceSessionState.Disconnected;
            StatusText = $"Socket.IO接続タイムアウト: {normalizedServerUrl}";
            LogDebug($"socket connect timeout {normalizedServerUrl}");
            NotifyStateChanged();
            await DisconnectSocketAsync().ConfigureAwait(false);
            throw new TimeoutException(StatusText, ex);
        }
        catch (Exception ex)
        {
            State = VoiceSessionState.Disconnected;
            StatusText = $"Socket.IO接続失敗: {ex.Message}";
            LogDebug($"socket connect failed {ex.GetType().Name}: {ex.Message}");
            NotifyStateChanged();
            await DisconnectSocketAsync().ConfigureAwait(false);
            throw;
        }

        State = VoiceSessionState.Connected;
        StatusText = $"ボイスサーバーに接続しました: {serverUrl}";
        LogDebug($"socket connected {normalizedServerUrl} id={socket.Id}");
        currentLobby = string.IsNullOrWhiteSpace(lobbyCode) ? "MENU" : lobbyCode;
        lastLobbyPresenceKey = string.Empty;
        StartObsOverlayLoop();
        NotifyStateChanged();
    }

    public async Task UpdateGameStateAsync(AmongUsState state, AppSettings? settings = null, CancellationToken cancellationToken = default)
    {
        lastState = state;
        if (settings is not null)
        {
            lastSettings = settings;
        }

        if (state.IsHost && settings is not null)
        {
            HostLobbySettings = CloneLobbySettings(settings.LocalLobbySettings);
        }

        if (settings is not null)
        {
            peerConnectionService.ConfigureNatFix(settings.NatFix);
            VoiceMix = voiceMixService.Calculate(state, CreateEffectiveSettings(settings, state), ImpostorRadioClientId);
            RebuildPeers(state);
            NotifyStateChanged();
        }

        var localPlayer = state.Players.FirstOrDefault(static player => player.IsLocal);
        if (state.GameState == GameState.Menu || string.Equals(state.LobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            if (currentLobby != "MENU")
            {
                if (socket is not null && State == VoiceSessionState.Connected)
                {
                    await socket.EmitAsync("leave");
                }

                LogDebug($"emit leave {currentLobby}");
                currentLobby = "MENU";
                lastLobbyPresenceKey = string.Empty;
                lastMobileHostKey = string.Empty;
                lastMutePresenceKey = string.Empty;
                SocketClients.Clear();
                peerConnectionService.Clear();
                ClearPendingAudioFrames();
                ClearPeerTalkingState();
                ClearPeerAudioSequenceState();
                ClearPeerWebRtcAudioState();
                Peers = [];
                HostLobbySettings = null;
                lastSettings = null;
                StatusText = "ボイスロビーから退出しました";
                NotifyStateChanged();
            }

            return;
        }

        if (localPlayer is null)
        {
            return;
        }

        if ((socket is null || State != VoiceSessionState.Connected) && settings is not null)
        {
            await ConnectSocketAsync(settings.ServerUrl, "MENU", cancellationToken).ConfigureAwait(false);
        }

        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        await socket.EmitAsync("id", localPlayer.Id, state.ClientId);

        if (currentLobby != state.LobbyCode)
        {
            await socket.EmitAsync("leave");
            await socket.EmitAsync("join", state.LobbyCode, localPlayer.Id, state.ClientId, state.IsHost);
            LogDebug($"emit join {state.LobbyCode} p={localPlayer.Id} c={state.ClientId} host={state.IsHost}");
            currentLobby = state.LobbyCode;
            lastLobbyPresenceKey = string.Empty;
            lastMobileHostKey = string.Empty;
            lastMutePresenceKey = string.Empty;
            ClearPendingAudioFrames();
            ClearPeerTalkingState();
            ClearPeerAudioSequenceState();
            ClearPeerWebRtcAudioState();
            StatusText = $"ボイスロビーに参加しました: {state.LobbyCode}";
            NotifyStateChanged();
        }

        await EmitLobbyPresenceAsync(state, settings, cancellationToken);
        await EmitMobileHostInfoAsync(state, settings, cancellationToken);
        await EmitLocalMuteStateIfNeededAsync(cancellationToken);
        await EmitObsOverlayStateAsync(cancellationToken);
    }

    public async Task EmitVadAsync(bool talking, CancellationToken cancellationToken = default)
    {
        localTalking = talking;

        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        await socket.EmitAsync("VAD", talking);
        await EmitObsOverlayStateAsync(cancellationToken);
    }

    public void SetLocalImpostorRadio(bool isUsingRadio)
    {
        var localPlayer = lastState?.Players.FirstOrDefault(static player => player.IsLocal);
        ImpostorRadioClientId = isUsingRadio && localPlayer is not null ? localPlayer.ClientId : -1;
        BroadcastPeerDataAsync(new
        {
            impostorRadio = ImpostorRadioClientId != -1
        });
        NotifyStateChanged();
    }

    public void SetLocalMuteState(bool muted, bool deafened)
    {
        localMuted = muted;
        localDeafened = deafened;
        lastMutePresenceKey = string.Empty;
        _ = Task.Run(() => BroadcastPeerDataAsync(new
        {
            muted,
            deafened
        }));
        NotifyStateChanged();
    }

    public void ApplyPeerData(string peerSocketId, string json)
    {
        if (!SocketClients.TryGetValue(peerSocketId, out var client))
        {
            return;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("impostorRadio", out var impostorRadioElement))
        {
            var isUsingRadio = impostorRadioElement.GetBoolean();
            foreach (var socketClient in SocketClients.Values)
            {
                socketClient.IsUsingRadio = socketClient.SocketId == peerSocketId && isUsingRadio;
            }

            RecalculateRadioClient();
        }

        if (root.TryGetProperty("muted", out var mutedElement) &&
            mutedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            client.IsMuted = mutedElement.GetBoolean();
            if (client.IsMuted)
            {
                SetClientTalking(peerSocketId, client, talking: false, scheduleTimeout: false);
            }
        }

        if (root.TryGetProperty("deafened", out var deafenedElement) &&
            deafenedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            client.IsDeafened = deafenedElement.GetBoolean();
            if (client.IsDeafened)
            {
                client.IsMuted = true;
                SetClientTalking(peerSocketId, client, talking: false, scheduleTimeout: false);
            }
        }

        if (TryReadLobbySettings(root, out var lobbySettings) && IsHostClient(client))
        {
            HostLobbySettings = lobbySettings;
        }

        if (lastState is not null)
        {
            RebuildPeers(lastState);
        }

        NotifyStateChanged();
    }

    public string CreateLobbySettingsPayload(AppSettings settings)
    {
        return JsonSerializer.Serialize(ToOriginalSettingsShape(settings));
    }

    public void BroadcastLobbySettings(AppSettings settings)
    {
        BroadcastPeerDataAsync(ToOriginalSettingsShape(settings));
    }

    public void QueueLocalAudioFrame(VoiceAudioFrame frame)
    {
        if (State != VoiceSessionState.Connected)
        {
            return;
        }

        peerConnectionService.BroadcastAudioFrame(frame);
        if (!frame.IsTransmitting)
        {
            return;
        }

        QueuePeerAudioFrameForSend(frame);
    }

    public void SubmitRemoteAudioFrame(string socketId, VoiceAudioFrame frame)
    {
        if (SocketClients.ContainsKey(socketId))
        {
            PeerAudioFrameReceived?.Invoke(this, new PeerAudioFrameEventArgs(socketId, frame));
        }
    }

    public async Task DisconnectAsync()
    {
        await socketConnectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
        await DisconnectSocketAsync();
        State = VoiceSessionState.Disconnected;
        StatusText = "切断済み";
        currentLobby = "MENU";
        lastLobbyPresenceKey = string.Empty;
        lastMobileHostKey = string.Empty;
        lastMutePresenceKey = string.Empty;
        SocketClients.Clear();
        peerConnectionService.Clear();
        ClearPendingAudioFrames();
        ClearPeerTalkingState();
        ClearPeerAudioSequenceState();
        ClearPeerWebRtcAudioState();
        VoiceMix = new Dictionary<int, PlayerVoiceMix>();
        Peers = [];
        ImpostorRadioClientId = -1;
        HostLobbySettings = null;
        lastSettings = null;
        NotifyStateChanged();
        }
        finally
        {
            socketConnectionLock.Release();
        }
    }

    public void Disconnect()
    {
        Task.Run(DisconnectAsync).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Disconnect();
        socketConnectionLock.Dispose();
    }

    private void RegisterSocketHandlers(SocketIOClient.SocketIO nextSocket)
    {
        nextSocket.OnConnected += (_, _) =>
        {
            State = VoiceSessionState.Connected;
            LogDebug("socket event connected");
            StatusText = "ボイスサーバーに接続しました";
            NotifyStateChanged();
        };

        nextSocket.OnDisconnected += (_, reason) =>
        {
            State = VoiceSessionState.Disconnected;
            LogDebug($"socket event disconnected {reason}");
            StatusText = $"切断されました: {reason}";
            currentLobby = "MENU";
            lastLobbyPresenceKey = string.Empty;
            lastMobileHostKey = string.Empty;
            lastMutePresenceKey = string.Empty;
            SocketClients.Clear();
            peerConnectionService.Clear();
            ClearPendingAudioFrames();
            ClearPeerTalkingState();
            ClearPeerAudioSequenceState();
            ClearPeerWebRtcAudioState();
            Peers = [];
            ImpostorRadioClientId = -1;
            HostLobbySettings = null;
            lastSettings = null;
            NotifyStateChanged();
        };

        nextSocket.OnError += (_, error) =>
        {
            StatusText = $"Socket.IOエラー: {error}";
            LogDebug($"socket error {error}");
            Error?.Invoke(this, StatusText);
            NotifyStateChanged();
        };

        nextSocket.On("setHost", response =>
        {
            ServerHostId = response.GetValue<int>();
            LogDebug($"event setHost {ServerHostId}");
            NotifyStateChanged();
        });

        nextSocket.On("clientPeerConfig", response =>
        {
            var raw = response.GetValue<JsonElement>();
            var forceRelayOnly = raw.TryGetProperty("forceRelayOnly", out var forceRelayOnlyElement) &&
                                 forceRelayOnlyElement.ValueKind == JsonValueKind.True;
            var iceServers = ReadPeerIceServers(raw).ToArray();
            peerConnectionService.ConfigureServerPeerConfig(iceServers, forceRelayOnly);
            LogDebug("event clientPeerConfig " + Trim(raw.GetRawText()));
            RebuildPeers();
            NotifyStateChanged();
        });

        nextSocket.On("setClient", response =>
        {
            var socketId = response.GetValue<string>(0);
            var client = ReadClient(response.GetValue<JsonElement>(1), socketId);
            if (!string.IsNullOrWhiteSpace(socketId))
            {
                SocketClients[socketId] = client;
                LogDebug($"event setClient {Short(socketId)} p={client.PlayerId} c={client.ClientId}");
                peerConnectionService.EnsurePeer(socketId, client, initiator: false);
                RebuildPeers();
                _ = EmitLocalMuteStateIfNeededAsync();
                NotifyStateChanged();
            }
        });

        nextSocket.On("setClients", response =>
        {
            SocketClients.Clear();
            var clients = response.GetValue<JsonElement>();
            foreach (var clientProperty in clients.EnumerateObject())
            {
                SocketClients[clientProperty.Name] = ReadClient(clientProperty.Value, clientProperty.Name);
            }

            foreach (var client in SocketClients.Values)
            {
                peerConnectionService.EnsurePeer(client.SocketId, client, initiator: false);
            }

            LogDebug($"event setClients count={SocketClients.Count}");
            RebuildPeers();
            _ = EmitLocalMuteStateIfNeededAsync();
            NotifyStateChanged();
        });

        nextSocket.On("join", response =>
        {
            var socketId = response.GetValue<string>(0);
            var client = ReadClient(response.GetValue<JsonElement>(1), socketId);
            if (!string.IsNullOrWhiteSpace(socketId))
            {
                SocketClients[socketId] = client;
                LogDebug($"event join {Short(socketId)} p={client.PlayerId} c={client.ClientId}");
                _ = peerConnectionService.InitiatePeerAsync(socketId, client);
                RebuildPeers();
                _ = EmitLocalMuteStateIfNeededAsync();
                NotifyStateChanged();
            }
        });

        nextSocket.On("leave", response =>
        {
            var socketId = response.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(socketId) && SocketClients.Remove(socketId))
            {
                LogDebug($"event leave {Short(socketId)}");
                peerConnectionService.RemovePeer(socketId);
                RemovePeerTalkingState(socketId);
                RemovePeerAudioSequenceState(socketId);
                RecalculateRadioClient();
                RebuildPeers();
                NotifyStateChanged();
            }
        });

        nextSocket.On("VAD", response =>
        {
            var data = response.GetValue<JsonElement>();
            if (data.TryGetProperty("socketId", out var socketIdElement) &&
            SocketClients.TryGetValue(socketIdElement.GetString() ?? string.Empty, out var client))
        {
            var socketId = socketIdElement.GetString() ?? string.Empty;
            var talking = data.TryGetProperty("activity", out var activity) &&
                          activity.GetBoolean() &&
                          !client.IsMuted &&
                          !client.IsDeafened;
            if (talking)
            {
                LogDebug($"event VAD {Short(socketId)} talking");
            }

            SetClientTalking(socketId, client, talking, scheduleTimeout: talking);
            RebuildPeers();
            NotifyStateChanged();
        }
        });

        nextSocket.On("signal", response =>
        {
            var signal = response.GetValue<JsonElement>();
            DiagnosticLog.WebRtc("socket signal <\n" + signal.GetRawText());
            HandleSignal(signal);
        });
    }

    private void HandleSignal(JsonElement signalEnvelope)
    {
        if (!signalEnvelope.TryGetProperty("data", out var data))
        {
            return;
        }

        if (!signalEnvelope.TryGetProperty("from", out var fromElement))
        {
            return;
        }

        var from = fromElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(from))
        {
            return;
        }

        if (!SocketClients.TryGetValue(from, out var client) &&
            signalEnvelope.TryGetProperty("client", out var clientElement))
        {
            client = ReadClient(clientElement, from);
            SocketClients[from] = client;
        }

        if (client is null)
        {
            Error?.Invoke(this, $"Signal from unknown socket: {from}");
            LogDebug($"signal < unknown {Short(from)}");
            return;
        }

        if (data.TryGetProperty("type", out _))
        {
            LogDebug($"rtc signal < {Short(from)} {GetSignalType(data)}");
            DiagnosticLog.WebRtc($"rtc signal < {from} {GetSignalType(data)}\n{data.GetRawText()}");
            _ = ApplyPeerSignalAsync(from, client, data);
            return;
        }

        if (data.TryGetProperty("mobilePlayerInfo", out var mobilePlayerInfo))
        {
            ApplyMobilePlayerInfo(client, mobilePlayerInfo);
            return;
        }

        if (data.TryGetProperty("voiceFrame", out var voiceFrameElement))
        {
            LogDebug($"audio fallback < {Short(from)}");
            ApplyPeerAudioFrame(from, client, voiceFrameElement);
            return;
        }

        if (data.TryGetProperty("voiceFrames", out var voiceFramesElement) &&
            voiceFramesElement.ValueKind == JsonValueKind.Array)
        {
            ApplyPeerAudioFrames(from, client, voiceFramesElement);
            return;
        }

        ApplyPeerData(from, data.GetRawText());
    }

    private void ApplyPeerAudioFrame(string socketId, VoiceClient client, JsonElement voiceFrameElement)
    {
        if (ApplyPeerAudioFrame(socketId, client, voiceFrameElement, notify: true))
        {
            RebuildPeers();
            NotifyStateChanged();
        }
    }

    private async Task ApplyPeerSignalAsync(string socketId, VoiceClient client, JsonElement data)
    {
        await peerConnectionService.ApplySignalAsync(socketId, client, data).ConfigureAwait(false);
        RebuildPeers();
        NotifyStateChanged();
    }

    public async Task ReconnectPeersAsync(CancellationToken cancellationToken = default)
    {
        if (State != VoiceSessionState.Connected)
        {
            return;
        }

        foreach (var client in SocketClients.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForSocketTaskAsync(
                peerConnectionService.InitiatePeerAsync(client.SocketId, client),
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        RebuildPeers();
        NotifyStateChanged();
    }

    private void ApplyWebRtcPeerAudioFrame(string socketId, VoiceAudioFrame frame)
    {
        if (!SocketClients.TryGetValue(socketId, out var client) || client.IsMuted || client.IsDeafened)
        {
            return;
        }

        var talking = frame.IsTransmitting && frame.Level >= RemoteTalkingLevelThreshold;
        MarkPeerWebRtcAudio(socketId);
        SetClientTalking(socketId, client, talking, scheduleTimeout: talking);
        peerConnectionService.MarkConnected(socketId);
        SubmitRemoteAudioFrame(socketId, frame);
        RebuildPeers();
        NotifyStateChanged();
    }

    private void ApplyPeerAudioFrames(string socketId, VoiceClient client, JsonElement voiceFramesElement)
    {
        var receivedAny = false;
        foreach (var frameElement in voiceFramesElement.EnumerateArray())
        {
            receivedAny |= ApplyPeerAudioFrame(socketId, client, frameElement, notify: false);
        }

        if (receivedAny)
        {
            RebuildPeers();
            NotifyStateChanged();
        }
    }

    private bool ApplyPeerAudioFrame(string socketId, VoiceClient client, JsonElement voiceFrameElement, bool notify)
    {
        if (HasRecentPeerWebRtcAudio(socketId))
        {
            return false;
        }

        if (!AcceptPeerAudioSequence(socketId, voiceFrameElement))
        {
            return false;
        }

        var frame = ReadVoiceFrame(voiceFrameElement);
        if (frame is null || client.IsMuted || client.IsDeafened)
        {
            return false;
        }

        var talking = frame.IsTransmitting && frame.Level >= RemoteTalkingLevelThreshold;
        SetClientTalking(socketId, client, talking, scheduleTimeout: talking);
        peerConnectionService.MarkConnected(socketId);
        SubmitRemoteAudioFrame(socketId, frame);
        if (notify)
        {
            RebuildPeers();
            NotifyStateChanged();
        }

        return true;
    }

    private void ApplyMobilePlayerInfo(VoiceClient sender, JsonElement mobilePlayerInfo)
    {
        if (!IsHostClient(sender) || lastState is null)
        {
            return;
        }

        var nextState = CloneState(lastState);
        nextState.HostId = GetInt(mobilePlayerInfo, "hostId", nextState.HostId);
        nextState.ClientId = GetInt(mobilePlayerInfo, "clientId", nextState.ClientId);
        nextState.LobbyCode = GetString(mobilePlayerInfo, "lobbyCode", nextState.LobbyCode);
        nextState.GameState = ReadGameState(GetString(mobilePlayerInfo, "gameState", nextState.GameState.ToString()), nextState.GameState);
        nextState.Map = ReadMap(GetString(mobilePlayerInfo, "map", nextState.Map.ToString()), nextState.Map);

        if (mobilePlayerInfo.TryGetProperty("players", out var playersElement) &&
            playersElement.ValueKind == JsonValueKind.Array)
        {
            var localPlayer = nextState.Players.FirstOrDefault(static player => player.IsLocal);
            var remotePlayers = playersElement
                .EnumerateArray()
                .Select(ReadMobilePlayer)
                .Where(static player => player is not null)
                .Select(static player => player!)
                .Where(player => localPlayer is null || player.ClientId != localPlayer.ClientId)
                .ToList();

            nextState.Players = localPlayer is null ? remotePlayers : [localPlayer, .. remotePlayers];
        }

        lastState = nextState;
        if (lastSettings is not null)
        {
            VoiceMix = voiceMixService.Calculate(nextState, CreateEffectiveSettings(lastSettings, nextState), ImpostorRadioClientId);
        }

        RebuildPeers(nextState);
        NotifyStateChanged();
    }

    private void BroadcastPeerDataAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        peerConnectionService.BroadcastDataPayload(json);
        _ = EmitPeerDataAsync(payload);
    }

    private async Task EmitPeerDataAsync(object payload, CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        var localPlayer = lastState?.Players.FirstOrDefault(static player => player.IsLocal);
        var client = localPlayer is null
            ? null
            : new
            {
                playerId = localPlayer.Id,
                clientId = localPlayer.ClientId
            };

        foreach (var peer in peerConnectionService.Peers.Values)
        {
            await socket.EmitAsync("signal", new
            {
                to = peer.SocketId,
                client,
                data = payload
            });
        }
    }

    private async Task EmitPeerSignalAsync(string socketId, object payload)
    {
        if (socket is null || State != VoiceSessionState.Connected || lastState is null)
        {
            return;
        }

        var localPlayer = lastState.Players.FirstOrDefault(static player => player.IsLocal);
        var client = localPlayer is null
            ? null
            : new
            {
                playerId = localPlayer.Id,
                clientId = localPlayer.ClientId
            };

        await socket.EmitAsync("signal", new
        {
            to = socketId,
            client,
            data = payload
        });
        LogDebug($"emit signal {Short(socketId)} {GetPayloadType(payload)}");
        DiagnosticLog.WebRtc($"socket signal > {socketId} {GetPayloadType(payload)}\n{SerializeForLog(payload)}");
    }

    private async Task EmitPeerAudioFramesAsync(IReadOnlyList<VoiceAudioFrame> frames, CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected || lastState is null || frames.Count == 0)
        {
            return;
        }

        var localPlayer = lastState.Players.FirstOrDefault(static player => player.IsLocal);
        var client = localPlayer is null
            ? null
            : new
            {
                playerId = localPlayer.Id,
                clientId = localPlayer.ClientId
            };
        var payload = frames.Select(ToVoiceFramePayload).ToArray();
        object data = payload.Length == 1
            ? new
            {
                voiceFrame = payload[0]
            }
            : new
            {
                voiceFrames = payload
            };

        foreach (var peer in peerConnectionService.Peers.Values)
        {
            await socket.EmitAsync("signal", new
            {
                to = peer.SocketId,
                client,
                data
            });
        }
    }

    private void QueuePeerAudioFrameForSend(VoiceAudioFrame frame)
    {
        lock (audioSendLock)
        {
            while (pendingAudioFrames.Count >= MaxPendingAudioFrames)
            {
                pendingAudioFrames.Dequeue();
            }

            pendingAudioFrames.Enqueue(frame);
            if (isFlushingAudioFrames)
            {
                return;
            }

            isFlushingAudioFrames = true;
        }

        _ = Task.Run(FlushPeerAudioFramesAsync);
    }

    private async Task FlushPeerAudioFramesAsync()
    {
        while (true)
        {
            List<VoiceAudioFrame> frames;
            lock (audioSendLock)
            {
                if (pendingAudioFrames.Count == 0)
                {
                    isFlushingAudioFrames = false;
                    return;
                }

                frames = [];
                while (frames.Count < MaxAudioFramesPerSignal && pendingAudioFrames.Count > 0)
                {
                    var nextFrame = pendingAudioFrames.Dequeue();
                    if ((DateTimeOffset.UtcNow - nextFrame.CapturedAt).TotalMilliseconds <= MaxRemoteVoiceFrameAgeMilliseconds)
                    {
                        frames.Add(nextFrame);
                    }
                }
            }

            if (frames.Count == 0)
            {
                continue;
            }

            try
            {
                await EmitPeerAudioFramesAsync(frames);
            }
            catch (Exception ex)
            {
                StatusText = $"音声フレーム送信に失敗しました: {ex.Message}";
                Error?.Invoke(this, StatusText);
                NotifyStateChanged();
            }
        }
    }

    private void ClearPendingAudioFrames()
    {
        lock (audioSendLock)
        {
            pendingAudioFrames.Clear();
        }
    }

    private void SetClientTalking(string socketId, VoiceClient client, bool talking, bool scheduleTimeout)
    {
        if (string.IsNullOrWhiteSpace(socketId))
        {
            return;
        }

        var wasTalking = client.IsTalking;
        if (!talking)
        {
            RemovePeerTalkingState(socketId);
            client.IsTalking = false;
            if (wasTalking)
            {
                _ = EmitObsOverlayStateAsync();
            }

            return;
        }

        var activeUntil = DateTimeOffset.UtcNow.AddMilliseconds(PeerTalkingHoldMilliseconds);
        lock (peerTalkingLock)
        {
            peerTalkingUntil[socketId] = activeUntil;
        }

        client.IsTalking = true;
        if (!wasTalking)
        {
            _ = EmitObsOverlayStateAsync();
        }

        if (scheduleTimeout)
        {
            _ = Task.Delay(PeerTalkingHoldMilliseconds + 80).ContinueWith(_ => ExpirePeerTalkingState(socketId));
        }
    }

    private void ExpirePeerTalkingState(string socketId)
    {
        var shouldNotify = false;
        lock (peerTalkingLock)
        {
            if (!peerTalkingUntil.TryGetValue(socketId, out var activeUntil) ||
                activeUntil > DateTimeOffset.UtcNow)
            {
                return;
            }

            peerTalkingUntil.Remove(socketId);
            if (SocketClients.TryGetValue(socketId, out var client) && client.IsTalking)
            {
                client.IsTalking = false;
                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            RebuildPeers();
            _ = EmitObsOverlayStateAsync();
            NotifyStateChanged();
        }
    }

    private void RemovePeerTalkingState(string socketId)
    {
        lock (peerTalkingLock)
        {
            peerTalkingUntil.Remove(socketId);
        }
    }

    private void ClearPeerTalkingState()
    {
        lock (peerTalkingLock)
        {
            peerTalkingUntil.Clear();
        }
    }

    private bool AcceptPeerAudioSequence(string socketId, JsonElement voiceFrameElement)
    {
        if (!TryGetLong(voiceFrameElement, "sequence", out var sequence))
        {
            return true;
        }

        lock (peerAudioSequenceLock)
        {
            if (peerLastAudioSequence.TryGetValue(socketId, out var lastSequence) &&
                sequence <= lastSequence)
            {
                return false;
            }

            peerLastAudioSequence[socketId] = sequence;
            return true;
        }
    }

    private void RemovePeerAudioSequenceState(string socketId)
    {
        lock (peerAudioSequenceLock)
        {
            peerLastAudioSequence.Remove(socketId);
        }
    }

    private void ClearPeerAudioSequenceState()
    {
        lock (peerAudioSequenceLock)
        {
            peerLastAudioSequence.Clear();
        }

        Interlocked.Exchange(ref localAudioSequence, 0);
    }

    private void MarkPeerWebRtcAudio(string socketId)
    {
        lock (peerWebRtcAudioLock)
        {
            peerLastWebRtcAudioAt[socketId] = DateTimeOffset.UtcNow;
        }
    }

    private bool HasRecentPeerWebRtcAudio(string socketId)
    {
        lock (peerWebRtcAudioLock)
        {
            return peerLastWebRtcAudioAt.TryGetValue(socketId, out var receivedAt) &&
                   (DateTimeOffset.UtcNow - receivedAt).TotalMilliseconds <= WebRtcLegacySuppressMilliseconds;
        }
    }

    private void ClearPeerWebRtcAudioState()
    {
        lock (peerWebRtcAudioLock)
        {
            peerLastWebRtcAudioAt.Clear();
        }
    }

    private async Task EmitLocalMuteStateIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (socket is null || State != VoiceSessionState.Connected)
        {
            return;
        }

        var peerIds = string.Join(',', peerConnectionService.Peers.Keys.Order(StringComparer.Ordinal));
        var key = $"{currentLobby}|{localMuted}|{localDeafened}|{peerIds}";
        if (key == lastMutePresenceKey)
        {
            return;
        }

        lastMutePresenceKey = key;
        await EmitPeerDataAsync(new
        {
            muted = localMuted,
            deafened = localDeafened
        }, cancellationToken);
    }

    private async Task EmitLobbyPresenceAsync(AmongUsState state, AppSettings? settings, CancellationToken cancellationToken)
    {
        if (socket is null || settings is null || string.Equals(state.LobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var key = string.Join('|',
            state.LobbyCode,
            state.IsHost,
            state.MaxPlayers,
            state.Map,
            state.CurrentServer,
            settings.NatFix);

        if (key == lastLobbyPresenceKey)
        {
            return;
        }

        lastLobbyPresenceKey = key;
        if (state.IsHost)
        {
            await socket.EmitAsync("setHost", state.LobbyCode, state.ClientId);
        }
    }

    private async Task EmitMobileHostInfoAsync(AmongUsState state, AppSettings? settings, CancellationToken cancellationToken)
    {
        if (socket is null ||
            settings is null ||
            !settings.MobileHost ||
            !state.IsHost ||
            string.Equals(state.LobbyCode, "MENU", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var activePlayers = state.Players
            .Where(static player => !player.Disconnected)
            .OrderBy(static player => player.Id)
            .ToArray();
        var key = string.Join('|',
            state.LobbyCode,
            state.ClientId,
            state.HostId,
            state.GameState,
            string.Join(',', activePlayers.Select(static player => $"{player.Id}:{player.ClientId}:{player.NameHash}:{player.ColorId}:{player.IsDead}:{player.IsImpostor}:{player.X:0.00}:{player.Y:0.00}:{player.InVent}")));

        if (key == lastMobileHostKey)
        {
            return;
        }

        lastMobileHostKey = key;
        await EmitPeerDataAsync(new
        {
            mobilePlayerInfo = new
            {
                lobbyCode = state.LobbyCode,
                hostId = state.HostId,
                clientId = state.ClientId,
                gameState = state.GameState.ToString(),
                map = state.Map.ToString(),
                players = activePlayers.Select(static player => new
                {
                    playerId = player.Id,
                    clientId = player.ClientId,
                    name = player.Name,
                    nameHash = player.NameHash,
                    colorId = player.ColorId,
                    isDead = player.IsDead,
                    isImpostor = player.IsImpostor,
                    isThirdParty = player.IsThirdParty,
                    inVent = player.InVent,
                    x = player.X,
                    y = player.Y
                }).ToArray()
            }
        }, cancellationToken);
    }

    private async Task EmitObsOverlayStateAsync(CancellationToken cancellationToken = default)
    {
        if (socket is null ||
            State != VoiceSessionState.Connected ||
            lastState is null ||
            lastSettings is null ||
            !lastSettings.ObsOverlay ||
            string.IsNullOrWhiteSpace(lastSettings.ObsSecret) ||
            lastSettings.ObsSecret.Length != 9 ||
            lastState.GameState is GameState.Menu or GameState.Unknown)
        {
            return;
        }

        var localPlayer = lastState.Players.FirstOrDefault(static player => player.IsLocal);
        var socketClients = SocketClients.Values.ToArray();
        var socketClientsBySocketId = SocketClients.ToDictionary(
            static pair => pair.Key,
            static pair => new
            {
                playerId = pair.Value.PlayerId,
                clientId = pair.Value.ClientId
            });
        var playerSocketIds = SocketClients
            .GroupBy(static pair => pair.Value.ClientId)
            .ToDictionary(static group => group.Key, static group => group.Last().Key);
        var audioConnected = peerConnectionService.Peers
            .ToDictionary(static pair => pair.Key, static pair => pair.Value.State == VoicePeerConnectionState.Connected);
        var connectedClientIds = socketClients
            .Select(static client => client.ClientId)
            .ToHashSet();
        if (localPlayer is not null)
        {
            connectedClientIds.Add(localPlayer.ClientId);
        }

        var otherTalking = socketClients
            .GroupBy(static client => client.ClientId)
            .ToDictionary(static group => group.Key, static group => group.Any(static client => client.IsTalking));
        var otherDead = lastState.Players
            .GroupBy(static player => player.ClientId)
            .ToDictionary(static group => group.Key, static group => group.First().IsDead);

        var payload = new
        {
            overlayState = new
            {
                gameState = (int)lastState.GameState,
                oldGameState = (int)lastState.GameState,
                players = lastState.Players.Select(player =>
                {
                    var colors = GetPlayerColorStrings(player.ColorId, lastState.PlayerColors);
                    return new
                    {
                        ptr = 0,
                        id = player.Id,
                        clientId = player.ClientId,
                        inVent = player.InVent,
                        isDead = player.IsDead,
                        name = player.Name,
                        nameHash = player.NameHash,
                        colorId = player.ColorId,
                        hatId = player.HatId,
                        petId = 0,
                        skinId = player.SkinId,
                        visorId = player.VisorId,
                        currentOutfit = 0,
                        appearanceName = player.Name,
                        appearanceColorId = player.ColorId,
                        appearanceHatId = player.HatId,
                        appearanceSkinId = player.SkinId,
                        appearanceVisorId = player.VisorId,
                        appearanceId = player.AppearanceId,
                        disconnected = player.Disconnected,
                        roleTeam = 0,
                        isImpostor = player.IsImpostor,
                        isThirdParty = player.IsThirdParty,
                        isLocal = player.IsLocal,
                        taskPtr = 0,
                        objectPtr = 0,
                        shiftedColor = -1,
                        bugged = false,
                        x = player.X,
                        y = player.Y,
                        isDummy = false,
                        realColor = colors,
                        usingRadio = player.ClientId == ImpostorRadioClientId && localPlayer?.IsImpostor == true,
                        connected = connectedClientIds.Contains(player.ClientId)
                    };
                }).ToArray()
            },
            socketClients = socketClientsBySocketId,
            playerSocketIds,
            audioConnected,
            otherTalking,
            otherDead,
            localTalking,
            localIsAlive = localPlayer is null || !localPlayer.IsDead,
            muted = localMuted,
            deafened = localDeafened,
            impostorRadioClientId = ImpostorRadioClientId,
            mod = "NONE",
            oldMeetingHud = false
        };

        await socket.EmitAsync("signal", new
        {
            to = lastSettings.ObsSecret,
            data = payload
        });
    }

    private async Task DisconnectSocketAsync()
    {
        StopObsOverlayLoop();

        if (socket is null)
        {
            return;
        }

        try
        {
            await WaitForSocketTaskAsync(socket.EmitAsync("leave"), TimeSpan.FromSeconds(2));
            await WaitForSocketTaskAsync(socket.DisconnectAsync(), TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best effort cleanup.
        }
        finally
        {
            socket.Dispose();
            socket = null;
        }
    }

    private static async Task WaitForSocketTaskAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new TimeoutException();
        }

        await task.ConfigureAwait(false);
    }

    private void StartObsOverlayLoop()
    {
        StopObsOverlayLoop();
        obsOverlayLoopCts = new CancellationTokenSource();
        var token = obsOverlayLoopCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await EmitObsOverlayStateAsync(token);
                    await timer.WaitForNextTickAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    private void StopObsOverlayLoop()
    {
        var cts = obsOverlayLoopCts;
        if (cts is null)
        {
            return;
        }

        obsOverlayLoopCts = null;
        cts.Cancel();
        cts.Dispose();
    }

    private void RebuildPeers()
    {
        if (lastState is not null)
        {
            RebuildPeers(lastState);
        }
    }

    private void RebuildPeers(AmongUsState state)
    {
        var playersByPlayerId = state.Players
            .Where(static player => !player.IsLocal)
            .GroupBy(static player => player.Id)
            .ToDictionary(static group => group.Key, static group => group.First());
        var playersByClientId = state.Players
            .Where(static player => !player.IsLocal)
            .GroupBy(static player => player.ClientId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var mixByClientId = VoiceMix.Values.ToDictionary(static mix => mix.ClientId);
        var peerConnections = PeerConnections;

        Peers = SocketClients.Values
            .Select(client =>
            {
                playersByPlayerId.TryGetValue(client.PlayerId, out var player);
                if (player is null)
                {
                    playersByClientId.TryGetValue(client.ClientId, out player);
                }

                mixByClientId.TryGetValue(client.ClientId, out var mix);
                peerConnections.TryGetValue(client.SocketId, out var connection);
                return new VoicePeerState
                {
                    SocketId = client.SocketId,
                    PlayerId = player?.Id ?? client.PlayerId,
                    ClientId = player?.ClientId ?? client.ClientId,
                    Name = player?.Name ?? string.Empty,
                    IsTalking = client.IsTalking,
                    IsUsingRadio = client.IsUsingRadio,
                    IsMuted = client.IsMuted,
                    IsDeafened = client.IsDeafened,
                    IsConnected = player is not null,
                    PeerConnectionState = connection?.State ?? VoicePeerConnectionState.Disconnected,
                    Mix = mix
                };
            })
            .OrderByDescending(static peer => peer.IsConnected)
            .ThenBy(static peer => peer.ClientId)
            .ToList();
    }

    private void RecalculateRadioClient()
    {
        ImpostorRadioClientId = SocketClients.Values.FirstOrDefault(static client => client.IsUsingRadio)?.ClientId ?? -1;
    }

    private bool IsHostClient(VoiceClient client)
    {
        return lastState is not null && (client.ClientId == lastState.HostId || client.ClientId == ServerHostId);
    }

    private AppSettings CreateEffectiveSettings(AppSettings settings, AmongUsState state)
    {
        if (state.IsHost || HostLobbySettings is null)
        {
            return settings;
        }

        return new AppSettings
        {
            AlwaysOnTop = settings.AlwaysOnTop,
            Language = settings.Language,
            Microphone = settings.Microphone,
            Speaker = settings.Speaker,
            PushToTalkMode = settings.PushToTalkMode,
            ServerUrl = settings.ServerUrl,
            ServerUrls = settings.ServerUrls,
            PushToTalkShortcut = settings.PushToTalkShortcut,
            DeafenShortcut = settings.DeafenShortcut,
            MuteShortcut = settings.MuteShortcut,
            ImpostorRadioShortcut = settings.ImpostorRadioShortcut,
            HideCode = settings.HideCode,
            NatFix = settings.NatFix,
            CompactOverlay = settings.CompactOverlay,
            OverlayPosition = settings.OverlayPosition,
            EnableOverlay = settings.EnableOverlay,
            MeetingOverlay = settings.MeetingOverlay,
            LocalLobbySettings = CloneLobbySettings(HostLobbySettings),
            GhostVolumeAsImpostor = settings.GhostVolumeAsImpostor,
            CrewVolumeAsGhost = settings.CrewVolumeAsGhost,
            MasterVolume = settings.MasterVolume,
            MicrophoneGain = settings.MicrophoneGain,
            MicrophoneGainEnabled = settings.MicrophoneGainEnabled,
            VoiceEffectStrength = settings.VoiceEffectStrength,
            MicSensitivity = settings.MicSensitivity,
            MicSensitivityEnabled = settings.MicSensitivityEnabled,
            MobileHost = settings.MobileHost,
            VadEnabled = settings.VadEnabled,
            HardwareAcceleration = settings.HardwareAcceleration,
            EchoCancellation = settings.EchoCancellation,
            NoiseSuppression = settings.NoiseSuppression,
            OldSampleDebug = settings.OldSampleDebug,
            EnableSpatialAudio = settings.EnableSpatialAudio,
            PlayerConfigMap = settings.PlayerConfigMap,
            ObsOverlay = settings.ObsOverlay,
            ObsSecret = settings.ObsSecret,
            LaunchPlatform = settings.LaunchPlatform,
            CustomPlatforms = settings.CustomPlatforms
        };
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LogDebug(string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss} {message}";
        DiagnosticLog.Voice(message);
        lock (debugLog)
        {
            debugLog.Enqueue(line);
            while (debugLog.Count > 12)
            {
                debugLog.Dequeue();
            }

            DebugLogChanged?.Invoke(this, string.Join(Environment.NewLine, debugLog));
        }
    }

    private static string Short(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value.Length <= 6 ? value : value[..6];
    }

    private static string Trim(string value)
    {
        return value.Length <= 90 ? value : value[..90] + "...";
    }

    private static string GetSignalType(JsonElement element)
    {
        return element.TryGetProperty("type", out var type) ? type.GetString() ?? "?" : "?";
    }

    private static string GetPayloadType(object payload)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return GetSignalType(document.RootElement);
        }
        catch
        {
            return payload.GetType().Name;
        }
    }

    private static string SerializeForLog(object payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return $"{payload.GetType().FullName}: {ex.Message}";
        }
    }

    private static string NormalizeServerUrl(string serverUrl)
    {
        var trimmed = serverUrl.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://{trimmed}";
    }

    private static IEnumerable<PeerIceServer> ReadPeerIceServers(JsonElement peerConfig)
    {
        if (!peerConfig.TryGetProperty("iceServers", out var iceServersElement) ||
            iceServersElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var iceServerElement in iceServersElement.EnumerateArray())
        {
            if (iceServerElement.ValueKind != JsonValueKind.Object ||
                !iceServerElement.TryGetProperty("urls", out var urlsElement))
            {
                continue;
            }

            var username = GetOptionalString(iceServerElement, "username");
            var credential = GetOptionalString(iceServerElement, "credential");
            foreach (var url in ReadIceServerUrls(urlsElement))
            {
                yield return new PeerIceServer(url, username, credential);
            }
        }
    }

    private static IEnumerable<string> ReadIceServerUrls(JsonElement urlsElement)
    {
        if (urlsElement.ValueKind == JsonValueKind.String)
        {
            var url = urlsElement.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return url;
            }

            yield break;
        }

        if (urlsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var urlElement in urlsElement.EnumerateArray())
        {
            if (urlElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var url = urlElement.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return url;
            }
        }
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string[] GetPlayerColorStrings(int colorId, IReadOnlyList<PlayerColorPair> dynamicColors)
    {
        if (colorId >= 0 && colorId < dynamicColors.Count)
        {
            return [ToColorHex(dynamicColors[colorId].Main), ToColorHex(dynamicColors[colorId].Shadow)];
        }

        return colorId switch
        {
            0 => ["#C51111", "#7A0838"],
            1 => ["#132ED1", "#09158E"],
            2 => ["#117F2D", "#0A4D2E"],
            3 => ["#ED54BA", "#AB2BAD"],
            4 => ["#EF7D0D", "#B33E15"],
            5 => ["#F5F557", "#C38823"],
            6 => ["#3F474E", "#1E1F26"],
            7 => ["#FFFFFF", "#8394BF"],
            8 => ["#6B2FBB", "#3B177C"],
            9 => ["#71491E", "#5E2615"],
            10 => ["#38FEDC", "#24A8BE"],
            _ => ["#50EF39", "#15A742"]
        };
    }

    private static string ToColorHex(uint color)
    {
        var red = (byte)(color & 0x000000ff);
        var green = (byte)((color & 0x0000ff00) >> 8);
        var blue = (byte)((color & 0x00ff0000) >> 16);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    private static VoiceClient ReadClient(JsonElement element, string socketId)
    {
        return new VoiceClient
        {
            SocketId = socketId,
            PlayerId = element.TryGetProperty("playerId", out var playerId) ? playerId.GetInt32() : 0,
            ClientId = element.TryGetProperty("clientId", out var clientId) ? clientId.GetInt32() : 0
        };
    }

    private static AmongUsState CloneState(AmongUsState state)
    {
        return new AmongUsState
        {
            GameState = state.GameState,
            LobbyCodeInt = state.LobbyCodeInt,
            LobbyCode = state.LobbyCode,
            Players = state.Players.Select(static player => new Player
            {
                Id = player.Id,
                ClientId = player.ClientId,
                Name = player.Name,
                NameHash = player.NameHash,
                ColorId = player.ColorId,
                HatId = player.HatId,
                SkinId = player.SkinId,
                NormalSkinId = player.NormalSkinId,
                VisorId = player.VisorId,
                AppearanceId = player.AppearanceId,
                Disconnected = player.Disconnected,
                IsImpostor = player.IsImpostor,
                IsThirdParty = player.IsThirdParty,
                IsDead = player.IsDead,
                IsLocal = player.IsLocal,
                X = player.X,
                Y = player.Y,
                InVent = player.InVent
            }).ToList(),
            PlayerColors = state.PlayerColors.Select(static color => new PlayerColorPair
            {
                Main = color.Main,
                Shadow = color.Shadow
            }).ToList(),
            IsHost = state.IsHost,
            ClientId = state.ClientId,
            HostId = state.HostId,
            CommsSabotaged = state.CommsSabotaged,
            CurrentServer = state.CurrentServer,
            MaxPlayers = state.MaxPlayers,
            Map = state.Map,
            CurrentCamera = state.CurrentCamera,
            ClosedDoors = [.. state.ClosedDoors]
        };
    }

    private static Player? ReadMobilePlayer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Player
        {
            Id = GetInt(element, "playerId"),
            ClientId = GetInt(element, "clientId"),
            Name = GetString(element, "name"),
            NameHash = GetInt(element, "nameHash"),
            ColorId = GetInt(element, "colorId"),
            IsDead = GetBool(element, "isDead"),
            IsImpostor = GetBool(element, "isImpostor"),
            IsThirdParty = GetBool(element, "isThirdParty"),
            InVent = GetBool(element, "inVent"),
            X = GetDouble(element, "x"),
            Y = GetDouble(element, "y")
        };
    }

    private static GameState ReadGameState(string value, GameState fallback)
    {
        return Enum.TryParse<GameState>(value, ignoreCase: true, out var result) ? result : fallback;
    }

    private static MapType ReadMap(string value, MapType fallback)
    {
        return Enum.TryParse<MapType>(value, ignoreCase: true, out var result) ? result : fallback;
    }

    private static bool TryReadLobbySettings(JsonElement element, out LobbySettings settings)
    {
        settings = new LobbySettings();
        if (!TryGetDouble(element, "maxDistance", "MaxDistance", out var maxDistance))
        {
            return false;
        }

        settings.MaxDistance = maxDistance;
        settings.VisionHearing = GetBool(element, "visionHearing", "VisionHearing");
        settings.Haunting = GetBool(element, "haunting", "Haunting");
        settings.ThirdPartyHaunting = GetBool(element, "thirdPartyHaunting", "ThirdPartyHaunting");
        settings.HearImpostorsInVents = GetBool(element, "hearImpostorsInVents", "HearImpostorsInVents");
        settings.ImpostorsHearImpostorsInVent = GetBool(element, "impostersHearImpostersInvent", "ImpostorsHearImpostorsInVent");
        settings.ImpostorRadioEnabled = GetBool(element, "impostorRadioEnabled", "ImpostorRadioEnabled");
        settings.CommsSabotage = GetBool(element, "commsSabotage", "CommsSabotage");
        settings.VoiceEffectEnabled = GetBool(element, "voiceEffectEnabled", "VoiceEffectEnabled", true);
        settings.DeadOnly = GetBool(element, "deadOnly", "DeadOnly");
        settings.MeetingGhostOnly = GetBool(element, "meetingGhostOnly", "MeetingGhostOnly");
        settings.HearThroughCameras = GetBool(element, "hearThroughCameras", "HearThroughCameras");
        settings.WallsBlockAudio = GetBool(element, "wallsBlockAudio", "WallsBlockAudio");
        settings.PublicLobbyOn = GetBool(element, "publicLobby_on", "PublicLobbyOn");
        settings.PublicLobbyTitle = GetString(element, "publicLobby_title", "PublicLobbyTitle");
        settings.PublicLobbyLanguage = GetString(element, "publicLobby_language", "PublicLobbyLanguage", "ja");
        settings.PublicLobbyMods = GetString(element, "publicLobby_mods", "PublicLobbyMods", "NONE");
        return true;
    }

    private static LobbySettings CloneLobbySettings(LobbySettings settings)
    {
        return new LobbySettings
        {
            MaxDistance = settings.MaxDistance,
            VisionHearing = settings.VisionHearing,
            Haunting = settings.Haunting,
            ThirdPartyHaunting = settings.ThirdPartyHaunting,
            HearImpostorsInVents = settings.HearImpostorsInVents,
            ImpostorsHearImpostorsInVent = settings.ImpostorsHearImpostorsInVent,
            ImpostorRadioEnabled = settings.ImpostorRadioEnabled,
            CommsSabotage = settings.CommsSabotage,
            VoiceEffectEnabled = settings.VoiceEffectEnabled,
            DeadOnly = settings.DeadOnly,
            MeetingGhostOnly = settings.MeetingGhostOnly,
            HearThroughCameras = settings.HearThroughCameras,
            WallsBlockAudio = settings.WallsBlockAudio,
            PublicLobbyOn = settings.PublicLobbyOn,
            PublicLobbyTitle = settings.PublicLobbyTitle,
            PublicLobbyLanguage = settings.PublicLobbyLanguage,
            PublicLobbyMods = settings.PublicLobbyMods
        };
    }

    private static object ToOriginalLobbySettingsShape(LobbySettings settings)
    {
        return new
        {
            maxDistance = settings.MaxDistance,
            visionHearing = settings.VisionHearing,
            haunting = settings.Haunting,
            thirdPartyHaunting = settings.ThirdPartyHaunting,
            hearImpostorsInVents = settings.HearImpostorsInVents,
            impostersHearImpostersInvent = settings.ImpostorsHearImpostorsInVent,
            impostorRadioEnabled = settings.ImpostorRadioEnabled,
            commsSabotage = settings.CommsSabotage,
            voiceEffectEnabled = settings.VoiceEffectEnabled,
            deadOnly = settings.DeadOnly,
            meetingGhostOnly = settings.MeetingGhostOnly,
            hearThroughCameras = settings.HearThroughCameras,
            wallsBlockAudio = settings.WallsBlockAudio,
            publicLobby_on = false,
            publicLobby_title = string.Empty,
            publicLobby_language = settings.PublicLobbyLanguage,
            publicLobby_mods = settings.PublicLobbyMods
        };
    }

    private static object ToOriginalSettingsShape(AppSettings settings)
    {
        var lobby = settings.LocalLobbySettings;
        return new
        {
            maxDistance = lobby.MaxDistance,
            visionHearing = lobby.VisionHearing,
            haunting = lobby.Haunting,
            thirdPartyHaunting = lobby.ThirdPartyHaunting,
            hearImpostorsInVents = lobby.HearImpostorsInVents,
            impostersHearImpostersInvent = lobby.ImpostorsHearImpostorsInVent,
            impostorRadioEnabled = lobby.ImpostorRadioEnabled,
            commsSabotage = lobby.CommsSabotage,
            voiceEffectEnabled = lobby.VoiceEffectEnabled,
            deadOnly = lobby.DeadOnly,
            meetingGhostOnly = lobby.MeetingGhostOnly,
            hearThroughCameras = lobby.HearThroughCameras,
            wallsBlockAudio = lobby.WallsBlockAudio,
            publicLobby_on = false,
            publicLobby_title = string.Empty,
            publicLobby_language = lobby.PublicLobbyLanguage,
            publicLobby_mods = lobby.PublicLobbyMods,
            natFix = settings.NatFix
        };
    }

    private static bool TryGetDouble(JsonElement element, string camelName, string pascalName, out double value)
    {
        value = 0;
        if (!TryGetProperty(element, camelName, pascalName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.Number ? property.GetDouble() : 0;
        return property.ValueKind == JsonValueKind.Number;
    }

    private static bool GetBool(JsonElement element, string camelName, string pascalName, bool fallback = false)
    {
        return TryGetProperty(element, camelName, pascalName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
    }

    private static bool GetBool(JsonElement element, string name, bool fallback = false)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
    }

    private static int GetInt(JsonElement element, string name, int fallback = 0)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : fallback;
    }

    private static double GetDouble(JsonElement element, string name, double fallback = 0)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number ? property.GetDouble() : fallback;
    }

    private static string GetString(JsonElement element, string camelName, string pascalName, string fallback = "")
    {
        return TryGetProperty(element, camelName, pascalName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static string GetString(JsonElement element, string name, string fallback = "")
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static bool TryGetProperty(JsonElement element, string camelName, string pascalName, out JsonElement property)
    {
        return element.TryGetProperty(camelName, out property) || element.TryGetProperty(pascalName, out property);
    }

    private object ToVoiceFramePayload(VoiceAudioFrame frame)
    {
        return new
        {
            buffer = Convert.ToBase64String(frame.Buffer),
            encoding = frame.Format.Encoding.ToString(),
            sampleRate = frame.Format.SampleRate,
            bitsPerSample = frame.Format.BitsPerSample,
            channels = frame.Format.Channels,
            level = frame.Level,
            capturedAtUnixMs = frame.CapturedAt.ToUnixTimeMilliseconds(),
            sequence = Interlocked.Increment(ref localAudioSequence)
        };
    }

    private static VoiceAudioFrame? ReadVoiceFrame(JsonElement element)
    {
        if (!element.TryGetProperty("buffer", out var bufferElement) ||
            bufferElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var bufferText = bufferElement.GetString();
        if (string.IsNullOrWhiteSpace(bufferText))
        {
            return null;
        }

        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(bufferText);
        }
        catch (FormatException)
        {
            return null;
        }

        var sampleRate = GetInt(element, "sampleRate", 48000);
        var bitsPerSample = GetInt(element, "bitsPerSample", 16);
        var channels = Math.Clamp(GetInt(element, "channels", 1), 1, 2);
        var encoding = GetString(element, "encoding", WaveFormatEncoding.Pcm.ToString());
        var format = string.Equals(encoding, WaveFormatEncoding.IeeeFloat.ToString(), StringComparison.OrdinalIgnoreCase)
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, bitsPerSample, channels);
        if (TryGetLong(element, "capturedAtUnixMs", out var capturedAtUnixMs))
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(capturedAtUnixMs);
            if (age.TotalMilliseconds > MaxRemoteVoiceFrameAgeMilliseconds)
            {
                return null;
            }
        }

        var level = GetDouble(element, "level");
        return new VoiceAudioFrame(buffer, format, level, isTransmitting: true);
    }

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }
}
