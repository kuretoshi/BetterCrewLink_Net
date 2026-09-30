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
    private AmongUsState? currentGameState;
    private AmongUsMemoryReaderService? gameReader;
    private readonly SemaphoreSlim gameStateGate = new(1, 1);
    private string currentJoinedLobby = "MENU";
    private string lastMixSignature = string.Empty;
    private SpatialVoiceSettings spatialVoiceSettings = new();
    private int? hostClientId;
    private AudioDeviceSession? audioSession;
    private bool microphoneMuted;
    private bool deafened;

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
        peerManager = new WebRtcPeerManager(label, SendSignalAsync, sendTestTone: !options.LiveAudio);
        peerManager.PeerVerified += socketId =>
        {
            Log("OK", $"P2P双方向通信成功 peer={socketId}");
            peerVerified.TrySetResult();
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

    public event Action<int, PeerVoiceMix>? PeerMixChanged;

    public event Action<int, AudioTestResult>? PeerAudioVerified;

    public event Action<int, short[]>? PeerPcmReceived;

    public event Action<bool>? LocalVadChanged;

    public event Action<int>? LocalAudioFrameSent;

    public event Action<string>? ConnectionStatusChanged;

    public event Action<int, string>? PeerConnectionStatusChanged;

    public event Action<int>? PeerTestToneSent;

    public void ApplyGameState(AmongUsState state)
    {
        currentGameState = state;
        GameStateApplied?.Invoke(state);
        RefreshPeerMixes();
    }

    public void SetMicrophoneMuted(bool muted)
    {
        microphoneMuted = muted;
        audioSession?.SetMicrophoneMuted(muted);
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
                    var peerCount = peerManager.BroadcastMonoPcm48k(pcm.Span);
                    if (peerCount > 0)
                    {
                        LocalAudioFrameSent?.Invoke(peerCount);
                    }
                },
                talking =>
                {
                    LocalVadChanged?.Invoke(talking);
                    _ = socket.EmitAsync("VAD", talking);
                });
            audioSession.Start();
            audioSession.SetMicrophoneMuted(microphoneMuted);
            audioSession.SetDeafened(deafened);
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
            _ = RunPeerOperationAsync(() => peerManager.InitiateAsync(remoteSocketId));
        });
        socket.On("leave", response =>
        {
            var remoteSocketId = response.GetValue<string>();
            Log("EVENT", $"leave peer={remoteSocketId}");
            peerClientIds.TryRemove(remoteSocketId, out _);
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
            if (type == "offer" &&
                string.CompareOrdinal(socket.Id, remoteSocketId) < 0 &&
                peerManager.IsInitiating(remoteSocketId))
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
                peerManager.RemovePeer(staleSocketId);
                audioSession?.RemovePeer(staleSocketId);
            }
            peerClientIds[socketId] = clientId;
            RefreshPeerMix(socketId, clientId);
        }
    }

    private void ApplyPeerData(string remoteSocketId, string message)
    {
        if (!peerClientIds.TryGetValue(remoteSocketId, out var clientId) ||
            (hostClientId is int host && clientId != host))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("maxDistance", out var maxDistance) ||
                !maxDistance.TryGetDouble(out var distance))
            {
                return;
            }

            var current = spatialVoiceSettings;
            var next = current with
            {
                MaxDistance = distance,
                HearImpostorsInVents = ReadBool(data, "hearImpostorsInVents", current.HearImpostorsInVents),
                ImpostorsHearImpostorsInVents = ReadBool(data, "impostersHearImpostersInvent", current.ImpostorsHearImpostorsInVents),
                Haunting = ReadBool(data, "haunting", current.Haunting),
                DeadOnly = ReadBool(data, "deadOnly", current.DeadOnly),
                MeetingGhostOnly = ReadBool(data, "meetingGhostOnly", current.MeetingGhostOnly)
            };
            if (next == current)
            {
                return;
            }

            spatialVoiceSettings = next;
            Log("INFO", $"ロビー音声設定更新: distance={next.MaxDistance:0.##} vent={next.HearImpostorsInVents} impostorVent={next.ImpostorsHearImpostorsInVents}");
            RefreshPeerMixes();
        }
        catch (JsonException)
        {
            // Other peer messages are not necessarily lobby settings.
        }
    }

    private static bool ReadBool(JsonElement data, string name, bool fallback) =>
        data.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

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

        var mix = SpatialVoicePolicy.Calculate(currentGameState, me, other, spatialVoiceSettings);
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
            ApplyGameState(state);
            var local = state.Players.SingleOrDefault(player => player.IsLocal);
            var targetLobby = state.GameState == GameState.Menu || local is null ? "MENU" : state.LobbyCode;
            if (string.Equals(targetLobby, currentJoinedLobby, StringComparison.Ordinal))
            {
                return;
            }

            await socket.EmitAsync("leave");
            currentJoinedLobby = "MENU";
            spatialVoiceSettings = new SpatialVoiceSettings();
            hostClientId = null;
            if (targetLobby == "MENU")
            {
                Log("INFO", "ゲーム状態に追従してロビー退出");
                return;
            }

            await socket.EmitAsync("id", local!.Id, state.ClientId, string.Empty, string.Empty, state.ClientId.ToString());
            await socket.EmitAsync("join", targetLobby, local.Id, state.ClientId, state.IsHost);
            currentJoinedLobby = targetLobby;
            Log("INFO", $"ゲーム状態に追従してロビー参加 code={targetLobby} client={state.ClientId} player={local.Id}");
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
                    : SpatialVoicePolicy.Calculate(currentGameState, me, player, spatialVoiceSettings);
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
}
