using System.Text.Json;
using SocketIOClient;
using SocketIOClient.Transport;

namespace TanukiBCL.VoiceProbe;

internal sealed class VoiceServerProbe : IAsyncDisposable
{
    private readonly ProbeOptions options;
    private readonly SocketIOClient.SocketIO socket;
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VoiceServerProbe(ProbeOptions options)
    {
        this.options = options;
        socket = new SocketIOClient.SocketIO(options.Server, new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = true,
            ReconnectionAttempts = 3,
            ReconnectionDelay = 500,
            ReconnectionDelayMax = 2_000,
            ConnectionTimeout = TimeSpan.FromSeconds(10)
        });

        RegisterHandlers();
    }

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

        Observe("clientPeerConfig");
        Observe("setHost");
        Observe("setClient");
        Observe("setClients");
        Observe("join");
        Observe("leave");
        Observe("VAD");
        Observe("signal");
        Observe("error");
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

        socket.Dispose();
    }

    private static void Log(string level, string message)
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {message}");
    }
}
