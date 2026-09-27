using System.Text.Json;
using SocketIOClient;
using SocketIOClient.Transport;

namespace TanukiBCL.VoiceProbe;

internal sealed class VoiceServerProbe : IAsyncDisposable
{
    private readonly ProbeOptions options;
    private readonly string label;
    private readonly SocketIOClient.SocketIO socket;
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource peerVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WebRtcPeerManager peerManager;

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
        peerManager = new WebRtcPeerManager(label, SendSignalAsync);
        peerManager.PeerVerified += socketId =>
        {
            Log("OK", $"P2P双方向通信成功 peer={socketId}");
            peerVerified.TrySetResult();
        };

        RegisterHandlers();
    }

    public Task Connected => connected.Task;

    public Task PeerVerified => peerVerified.Task;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Log("INFO", $"接続開始: {options.Server}");
        // SocketIOClient 3.1.2 は、接続完了後に渡したトークンをキャンセルすると
        // 内部TaskCompletionSourceを再度完了させようとするため、待機側で制限する。
        await socket.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        if (options.LobbyCode is not null)
        {
            await JoinLobbyAsync();
        }
        else
        {
            Log("INFO", "疎通確認モードです。ロビー参加は行いません。");
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private void RegisterHandlers()
    {
        socket.OnConnected += (_, _) =>
        {
            Log("OK", $"Socket.IO接続成功 socketId={socket.Id}");
            connected.TrySetResult();
        };

        socket.OnDisconnected += (_, reason) => Log("WARN", $"切断: {reason}");
        socket.OnError += (_, error) => Log("ERROR", error);
        socket.OnReconnectAttempt += (_, attempt) => Log("INFO", $"再接続試行: {attempt}");

        socket.On("clientPeerConfig", response =>
        {
            var configuration = response.GetValue<JsonElement>();
            peerManager.Configure(configuration);
            Log("EVENT", "clientPeerConfig received (credentials redacted)");
        });
        Observe("setHost");
        Observe("setClient");
        Observe("setClients");
        socket.On("join", response =>
        {
            var remoteSocketId = response.GetValue<string>(0);
            Log("EVENT", $"join peer={remoteSocketId}");
            _ = RunPeerOperationAsync(() => peerManager.InitiateAsync(remoteSocketId));
        });
        socket.On("leave", response =>
        {
            var remoteSocketId = response.GetValue<string>();
            Log("EVENT", $"leave peer={remoteSocketId}");
            peerManager.RemovePeer(remoteSocketId);
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
            Log("EVENT", $"signal {type} < {remoteSocketId}");
            _ = RunPeerOperationAsync(() => peerManager.ApplySignalAsync(remoteSocketId, data.Clone()));
        });
        Observe("error");
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

        peerManager.Dispose();
        socket.Dispose();
    }

    private void Log(string level, string message)
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{label}/{level}] {message}");
    }
}
