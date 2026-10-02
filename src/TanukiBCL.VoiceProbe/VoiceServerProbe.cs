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
    private readonly ConcurrentDictionary<int, RadioStatus> impostorRadioStates = new();
    private AmongUsState? currentGameState;
    private AmongUsMemoryReaderService? gameReader;
    private readonly SemaphoreSlim gameStateGate = new(1, 1);
    private string currentJoinedLobby = "MENU";
    private string lastMixSignature = string.Empty;
    private SpatialVoiceSettings spatialVoiceSettings = new();
    private LobbySettings ownLobbySettings = new();
    private LobbySettings activeLobbySettings = new();
    private volatile bool hasActiveLobbySettings;
    private int? hostClientId;
    private AudioDeviceSession? audioSession;
    private bool microphoneMuted;
    private bool deafened;
    private double masterVolume = 100d;
    private double microphoneGain = 100d;
    private readonly object radioTransmitGate = new();
    private volatile bool impostorRadioTransmitting;
    private volatile bool localVadTalking;
    private long impostorRadioVersion;
    private DateTimeOffset lastRadioStatusSentAt;

    public VoiceServerProbe(ProbeOptions options, string label = "probe")
    {
        this.options = options;
        this.label = label;
        socket = new SocketIOClient.SocketIO(options.Server, new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = true,
            ReconnectionAttempts = 3,
            ReconnectionDelay = 500,
            ReconnectionDelayMax = 2_000,
            ConnectionTimeout = TimeSpan.FromSeconds(10)
        });
        peerManager = new WebRtcPeerManager(label, SendSignalAsync, sendTestTone: !options.LiveAudio && !options.AutoRadioTone);
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
            if (peerClientIds.TryGetValue(remoteSocketId, out var clientId))
            {
                PeerConnectionStatusChanged?.Invoke(clientId, "data-ready");
            }
        };
        peerManager.PeerDataChannelStalled += remoteSocketId =>
        {
            _ = RunPeerOperationAsync(async () =>
            {
                // Give the lower socket ID the first chance to re-offer. If it
                // is an Electron peer that does not retry, recover locally too.
                if (string.CompareOrdinal(socket.Id, remoteSocketId) > 0)
                {
                    await Task.Delay(4_000);
                }
                if (!socket.Connected || !peerClientIds.ContainsKey(remoteSocketId) ||
                    peerManager.HasOpenDataChannel(remoteSocketId))
                {
                    return;
                }
                var attempt = stalledReconnectAttempts.AddOrUpdate(remoteSocketId, 1, (_, count) => count + 1);
                if (attempt > 2)
                {
                    Log("WARN", $"データチャネル復旧の再試行上限 peer={remoteSocketId}");
                    return;
                }
                Log("INFO", $"データチャネル停滞を再接続 peer={remoteSocketId} attempt={attempt}");
                await peerManager.ReconnectAsync(remoteSocketId);
            });
        };
        peerManager.PeerConnectionFailed += remoteSocketId =>
        {
            if (string.CompareOrdinal(socket.Id, remoteSocketId) < 0)
            {
                Log("INFO", $"失敗したpeerを自動再接続 peer={remoteSocketId}");
                _ = RunPeerOperationAsync(() => peerManager.ReconnectAsync(remoteSocketId));
            }
        };
        peerManager.PeerConnectionStateChanged += (remoteSocketId, state) =>
        {
            if (peerClientIds.TryGetValue(remoteSocketId, out var clientId))
            {
                PeerConnectionStatusChanged?.Invoke(clientId, state.ToString());
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

    public event Action<int>? LocalAudioFrameSent;

    public event Action<string>? ConnectionStatusChanged;

    public event Action<int, string>? PeerConnectionStatusChanged;

    public event Action<int>? PeerTestToneSent;

    public event Action<bool>? ImpostorRadioTransmitChanged;

    public event Action<bool>? ImpostorRadioAvailabilityChanged;

    public bool CanUseImpostorRadio =>
        currentGameState is { GameState: GameState.Tasks or GameState.Discussion } state &&
        state.Players.Any(player => player.IsLocal && player.IsImpostor && !player.IsDead) &&
        (spatialVoiceSettings.ImpostorRadioEnabled || spatialVoiceSettings.ImpostorRadioOnlyMode);

    public LobbySettings? CurrentLobbySettings =>
        hasActiveLobbySettings && currentJoinedLobby != "MENU" ? activeLobbySettings : null;

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
        currentGameState = state;
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

    public void SetMicrophoneGain(double gainPercent)
    {
        microphoneGain = Math.Clamp(gainPercent, 0d, 300d);
        audioSession?.SetMicrophoneGain(microphoneGain);
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
        return peerManager.ReconnectAsync(socketId);
    }

    public async Task RejoinCurrentGameLobbyAsync()
    {
        await gameStateGate.WaitAsync();
        try
        {
            var state = currentGameState ?? throw new InvalidOperationException("ゲーム状態をまだ取得していません。");
            var local = state.Players.SingleOrDefault(player => player.IsLocal)
                ?? throw new InvalidOperationException("ローカルプレイヤーを特定できません。");
            if (impostorRadioTransmitting)
            {
                SetImpostorRadioTransmitting(false);
            }
            await socket.EmitAsync("leave");
            ResetPeerState();
            currentJoinedLobby = "MENU";
            await Task.Delay(500);
            await socket.EmitAsync("id", local.Id, state.ClientId, string.Empty, string.Empty, state.ClientId.ToString());
            await socket.EmitAsync("join", state.LobbyCode, local.Id, state.ClientId, state.IsHost);
            currentJoinedLobby = state.LobbyCode;
            await Task.Delay(500);
            foreach (var remoteSocketId in peerClientIds.Keys.ToArray())
            {
                await peerManager.InitiateAsync(remoteSocketId);
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
        await gameStateGate.WaitAsync(cancellationToken);
        try
        {
            var state = currentGameState ?? throw new InvalidOperationException("ゲーム状態をまだ取得していません。");
            var local = state.Players.SingleOrDefault(player => player.IsLocal)
                ?? throw new InvalidOperationException("ローカルプレイヤーを特定できません。");

            ResetPeerState();
            currentJoinedLobby = "MENU";
            await socket.DisconnectAsync();
            await Task.Delay(500, cancellationToken);
            await socket.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await socket.EmitAsync("id", local.Id, state.ClientId, string.Empty, string.Empty, state.ClientId.ToString());
            await socket.EmitAsync("join", state.LobbyCode, local.Id, state.ClientId, state.IsHost);
            currentJoinedLobby = state.LobbyCode;
            await Task.Delay(500, cancellationToken);
            foreach (var remoteSocketId in peerClientIds.Keys.ToArray())
            {
                await peerManager.InitiateAsync(remoteSocketId);
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
        peerManager.RemoveAllPeers();
        foreach (var socketId in peerClientIds.Keys.ToArray())
        {
            audioSession?.RemovePeer(socketId);
        }
        peerClientIds.Clear();
        stalledReconnectAttempts.Clear();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
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
                        impostorRadioTransmitting ? CanReceiveImpostorRadioAudio : null);
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
                });
            audioSession.Start();
            audioSession.SetMicrophoneMuted(microphoneMuted);
            audioSession.SetDeafened(deafened);
            audioSession.SetMasterVolume(masterVolume);
            audioSession.SetMicrophoneGain(microphoneGain);
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

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private void RegisterHandlers()
    {
        socket.OnConnected += (_, _) =>
        {
            Log("OK", $"Socket.IO接続成功 socketId={socket.Id}");
            ConnectionStatusChanged?.Invoke("ボイスサーバー接続済み");
            connected.TrySetResult();
        };

        socket.OnDisconnected += (_, reason) =>
        {
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
        });
        socket.On("setClients", response =>
        {
            var clients = response.GetValue<JsonElement>();
            peerClientIds.Clear();
            stalledReconnectAttempts.Clear();
            foreach (var client in clients.EnumerateObject())
            {
                RegisterPeerClient(client.Name, client.Value);
            }
            Log("EVENT", $"setClients count={peerClientIds.Count}");
            foreach (var remoteSocketId in peerClientIds.Keys.Where(remote => string.CompareOrdinal(socket.Id, remote) < 0).ToArray())
            {
                _ = RunPeerOperationAsync(() => peerManager.InitiateAsync(remoteSocketId));
            }
        });
        socket.On("join", response =>
        {
            var remoteSocketId = response.GetValue<string>(0);
            RegisterPeerClient(remoteSocketId, response.GetValue<JsonElement>(1));
            Log("EVENT", $"join peer={remoteSocketId}");
            if (string.CompareOrdinal(socket.Id, remoteSocketId) < 0)
            {
                _ = RunPeerOperationAsync(() => peerManager.InitiateAsync(remoteSocketId));
            }
        });
        socket.On("leave", response =>
        {
            var remoteSocketId = response.GetValue<string>();
            Log("EVENT", $"leave peer={remoteSocketId}");
            if (peerClientIds.TryRemove(remoteSocketId, out var departedClientId))
            {
                stalledReconnectAttempts.TryRemove(remoteSocketId, out _);
                impostorRadioStates.TryRemove(departedClientId, out _);
            }
            peerManager.RemovePeer(remoteSocketId);
            audioSession?.RemovePeer(remoteSocketId);
        });
        Observe("VAD");
        socket.On("signal", response =>
        {
            var envelope = response.GetValue<JsonElement>();
            if (!envelope.TryGetProperty("from", out var fromElement) ||
                !envelope.TryGetProperty("data", out var data) ||
                fromElement.GetString() is not { Length: > 0 } remoteSocketId)
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
            _ = RunPeerOperationAsync(() => peerManager.ApplySignalAsync(remoteSocketId, data.Clone()));
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
                peerManager.RemovePeer(staleSocketId);
                audioSession?.RemovePeer(staleSocketId);
            }
            peerClientIds[socketId] = clientId;
            RefreshPeerMix(socketId, clientId);
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
            HearImpostorsInVents = next.HearImpostorsInVents,
            ImpostorsHearImpostorsInVents = next.ImpostersHearImpostersInvent,
            ImpostorRadioEnabled = next.ImpostorRadioEnabled,
            ImpostorRadioOnlyMode = next.ImpostorRadioOnlyMode,
            CommsSabotage = next.CommsSabotage,
            Haunting = next.Haunting,
            DeadOnly = next.DeadOnly,
            MeetingGhostOnly = next.MeetingGhostOnly
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
        foreach (var (socketId, clientId) in peerClientIds)
        {
            var player = state.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
            if (active && player is not { IsImpostor: true, IsDead: false } && player is not { IsDead: true })
            {
                continue;
            }

            peerManager.TrySendPeerData(socketId, payload);
            _ = RunPeerOperationAsync(() => SendSignalAsync(socketId, new { type = "bcl-control", payload }));
        }
        Log("INFO", $"インポスターラジオ送信: active={active} version={version}");
    }

    private bool CanReceiveImpostorRadioAudio(string remoteSocketId)
    {
        if (!peerClientIds.TryGetValue(remoteSocketId, out var clientId))
        {
            return false;
        }

        var player = currentGameState?.Players.SingleOrDefault(candidate => candidate.ClientId == clientId);
        return player is { IsImpostor: true, IsDead: false } or { IsDead: true };
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

                    var sent = peerManager.BroadcastMonoPcm48k(frame, CanReceiveImpostorRadioAudio);
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
        if (active && (sender is null || !sender.IsImpostor || sender.IsDead))
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

        var mix = SpatialVoicePolicy.Calculate(currentGameState, me, other, spatialVoiceSettings, IsImpostorRadioActive(clientId));
        audioSession?.SetPeerMix(socketId, mix);
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
        await gameStateGate.WaitAsync();
        try
        {
            var wasHost = currentGameState?.IsHost == true;
            ApplyGameState(state);
            var local = state.Players.SingleOrDefault(player => player.IsLocal);
            var targetLobby = state.GameState == GameState.Menu || local is null ? "MENU" : state.LobbyCode;
            if (string.Equals(targetLobby, currentJoinedLobby, StringComparison.Ordinal))
            {
                if (state.IsHost && !wasHost)
                {
                    ApplyLobbySettings(ownLobbySettings);
                    BroadcastLobbySettings();
                }
                return;
            }

            if (impostorRadioTransmitting)
            {
                SetImpostorRadioTransmitting(false);
            }
            await socket.EmitAsync("leave");
            currentJoinedLobby = "MENU";
            spatialVoiceSettings = new SpatialVoiceSettings();
            activeLobbySettings = new LobbySettings();
            hasActiveLobbySettings = false;
            LobbySettingsChanged?.Invoke(null);
            hostClientId = state.HostId > 0 ? state.HostId : null;
            impostorRadioStates.Clear();
            if (targetLobby == "MENU")
            {
                Log("INFO", "ゲーム状態に追従してロビー退出");
                return;
            }

            await socket.EmitAsync("id", local!.Id, state.ClientId, string.Empty, string.Empty, state.ClientId.ToString());
            await socket.EmitAsync("join", targetLobby, local.Id, state.ClientId, state.IsHost);
            currentJoinedLobby = targetLobby;
            Log("INFO", $"ゲーム状態に追従してロビー参加 code={targetLobby} client={state.ClientId} player={local.Id}");
            if (state.IsHost)
            {
                ApplyLobbySettings(ownLobbySettings);
                BroadcastLobbySettings();
            }
            LobbySettingsChanged?.Invoke(CurrentLobbySettings);
        }
        catch (Exception exception)
        {
            Log("ERROR", $"ゲーム状態同期失敗: {exception.Message}");
        }
        finally
        {
            gameStateGate.Release();
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
                    : SpatialVoicePolicy.Calculate(currentGameState, me, player, spatialVoiceSettings, IsImpostorRadioActive(pair.Value));
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
            await operation();
        }
        catch (Exception exception)
        {
            Log("ERROR", $"WebRTC処理失敗: {exception.Message}");
        }
    }

    private Task SendSignalAsync(string remoteSocketId, object data)
    {
        return socket.EmitAsync("signal", new { to = remoteSocketId, data });
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
        var addressKind = parts.Length > 4 && parts[4].EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? "mdns"
            : "ip";
        return $"({candidateType}/{addressKind})";
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
        Log(
            "INFO",
            $"ロビー参加: code={options.LobbyCode} playerId={options.PlayerId} clientId={options.ClientId} host={options.IsHost}");

        // TanukiBCL v3.2.5 ConnectionController.joinLobby と同じ送信順序・引数。
        await socket.EmitAsync("leave");
        await socket.EmitAsync("id", options.PlayerId, options.ClientId, string.Empty, string.Empty, string.Empty);
        await socket.EmitAsync("join", options.LobbyCode!, options.PlayerId, options.ClientId, options.IsHost);
        currentJoinedLobby = options.LobbyCode!;
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.Connected)
        {
            try
            {
                await socket.EmitAsync("leave");
                await socket.DisconnectAsync();
            }
            catch (Exception exception)
            {
                Log("WARN", $"終了処理: {exception.Message}");
            }
        }

        gameReader?.Dispose();
        audioSession?.Dispose();
        peerManager.Dispose();
        socket.Dispose();
        gameStateGate.Dispose();
    }

    private void Log(string level, string message)
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{label}/{level}] {message}");
    }

    private sealed record RadioStatus(long Version, bool Active, DateTimeOffset SeenAt);
}
