using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class ServerReconnectSelfTest
{
    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var server = new LoopbackServer();
        var options = ProbeOptions.Parse(["--server", server.Address.ToString(), "--lobby", "ABCDEF",
            "--client-id", "19", "--player-id", "2", "--host"]);
        await using var probe = new VoiceServerProbe(options, "server-reconnect-test");
        var state = new AmongUsState
        {
            GameState = GameState.Lobby, LobbyCode = "ABCDEF", ClientId = 19, IsHost = true,
            HostId = 19, Players = [new Player { Id = 2, ClientId = 19, IsLocal = true, Name = "Local" }]
        };
        var peerKnown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementKnown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retainedPeerKnown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retainedPeerCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leavingPeerKnown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leavingPeerCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer20Registrations = 0;
        var peer20Closures = 0;
        probe.PeerMixChanged += (id, _) =>
        {
            if (id == 20)
            {
                if (Interlocked.Increment(ref peer20Registrations) == 1) peerKnown.TrySetResult();
                else replacementKnown.TrySetResult();
            }
            if (id == 22) retainedPeerKnown.TrySetResult();
            if (id == 23) leavingPeerKnown.TrySetResult();
        };
        probe.PeerConnectionStatusChanged += (id, status) =>
        {
            if (id == 20 && status == "closed")
            {
                if (Interlocked.Increment(ref peer20Closures) == 1) peerCleared.TrySetResult();
                else replacementCleared.TrySetResult();
            }
            if (id == 22 && status == "closed") retainedPeerCleared.TrySetResult();
            if (id == 23 && status == "closed") leavingPeerCleared.TrySetResult();
        };
        probe.ApplyGameState(state);
        var run = probe.RunAsync(timeout.Token);
        try
        {
            await server.ExpectJoinAsync(1, "ABCDEF", 2, 19, timeout.Token);
            await server.ExpectLobbyAsync(1, "ABCDEF", "Local", false, "", "ja", 1, timeout.Token);
            probe.SetOwnLobbySettings(new LobbySettings
            {
                PublicLobbyOn = true, PublicLobbyTitle = "Loopback test", PublicLobbyLanguage = "en"
            });
            await server.ExpectLobbyAsync(1, "ABCDEF", "Local", true, "Loopback test", "en", 1, timeout.Token);
            await server.SendEventAsync(1, "setClient", "old-peer", new { clientId = 20 });
            await peerKnown.Task.WaitAsync(timeout.Token);
            if (!probe.IsPeerPresent(20)) throw new InvalidOperationException("test peer was not registered");
            await server.SendEventAsync(1, "setClient", "replacement-peer", new { clientId = 20 });
            await Task.WhenAll(peerCleared.Task, replacementKnown.Task).WaitAsync(timeout.Token);
            if (probe.IsPeerSocketPresent("old-peer") || !probe.IsPeerSocketPresent("replacement-peer"))
                throw new InvalidOperationException("a refreshed client kept its stale socket peer");
            Console.WriteLine("[PASS] a refreshed player retires its previous socket and clears the old status");
            await server.SendEventAsync(1, "setClient", "retained-peer", new { clientId = 22 });
            await retainedPeerKnown.Task.WaitAsync(timeout.Token);
            await server.SendEventAsync(1, "setClients", new Dictionary<string, object>
            {
                ["retained-peer"] = new { clientId = 22 }
            });
            await replacementCleared.Task.WaitAsync(timeout.Token);
            if (probe.IsPeerPresent(20) || !probe.IsPeerPresent(22) || retainedPeerCleared.Task.IsCompleted)
                throw new InvalidOperationException("setClients did not prune only peers omitted by the authoritative roster");
            Console.WriteLine("[PASS] setClients prunes a departed peer and preserves a retained peer");
            await server.SendEventAsync(1, "setClient", "leaving-peer", new { clientId = 23 });
            await leavingPeerKnown.Task.WaitAsync(timeout.Token);
            await server.SendEventAsync(1, "leave", "leaving-peer");
            await leavingPeerCleared.Task.WaitAsync(timeout.Token);
            if (probe.IsPeerPresent(23) || !probe.IsPeerPresent(22))
                throw new InvalidOperationException("leave kept a stale peer or removed an unrelated peer");
            Console.WriteLine("[PASS] leave clears the departed peer's UI status without removing another peer");
            server.DropConnection(1);
            await retainedPeerCleared.Task.WaitAsync(timeout.Token);
            if (probe.IsPeerPresent(22)) throw new InvalidOperationException("disconnect kept stale peers");

            // No game-state event is sent here: reconnect must use the retained
            // snapshot even if neither the lobby code nor memory state changes.
            await server.ExpectJoinAsync(2, "ABCDEF", 2, 19, timeout.Token);
            await server.ExpectLobbyAsync(2, "ABCDEF", "Local", true, "Loopback test", "en", 1, timeout.Token);
            Console.WriteLine("[PASS] transport interruption automatically rejoins the unchanged game lobby and clears old peers");

            // The explicit reload also raises OnConnected. It must not race the
            // automatic recovery into a second leave/id/join cycle.
            var reload = probe.RestartServerConnectionAsync(timeout.Token);
            await server.ExpectJoinAsync(3, "ABCDEF", 2, 19, timeout.Token);
            await server.ExpectLobbyAsync(3, "ABCDEF", "Local", true, "Loopback test", "en", 1, timeout.Token);
            await reload.WaitAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            if (server.GetJoinCount(3) != 1)
                throw new InvalidOperationException("manual reload and automatic recovery both joined the lobby");
            Console.WriteLine("[PASS] manual reload remains a single lobby join");
            Console.WriteLine("[PASS] public lobby announcements match 3.2.8 on initial join, settings change, reconnect and reload");

            // Dispose while a reload owns the game gate; cancellation must drain
            // it before the socket is destroyed, and repeated Dispose is safe.
            var stoppingReload = probe.RestartServerConnectionAsync(timeout.Token);
            var dispose = probe.DisposeAsync().AsTask();
            await dispose.WaitAsync(TimeSpan.FromSeconds(4), timeout.Token);
            await IgnoreCancellationAsync(stoppingReload);
            await IgnoreCancellationAsync(run);
            await probe.DisposeAsync();
            Console.WriteLine("[PASS] disposal cancels an active reload, drains its game gate and stops the run");

            await VerifyStaticLobbyAsync(server, timeout.Token);
            Console.WriteLine("[PASS] Socket.IO reconnect lifecycle verified against loopback transport (not live 3.2.7 interoperability)");
            return 0;
        }
        finally
        {
            timeout.Cancel();
            await IgnoreCancellationAsync(run);
        }
    }

    private static async Task VerifyStaticLobbyAsync(LoopbackServer server, CancellationToken cancellationToken)
    {
        var firstConnection = server.ConnectionCount + 1;
        var options = ProbeOptions.Parse(["--server", server.Address.ToString(), "--lobby", "STATIC",
            "--client-id", "28", "--player-id", "3"]);
        await using var probe = new VoiceServerProbe(options, "static-reconnect-test");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = probe.RunAsync(cancellation.Token);
        try
        {
            await server.ExpectJoinAsync(firstConnection, "STATIC", 3, 28, cancellationToken, isHost: false);
            server.DropConnection(firstConnection);
            await server.ExpectJoinAsync(firstConnection + 1, "STATIC", 3, 28, cancellationToken, isHost: false);
            Console.WriteLine("[PASS] configured probe lobby also rejoins without a game reader");
        }
        finally
        {
            cancellation.Cancel();
            await IgnoreCancellationAsync(run);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    // Minimal Engine.IO v4 / Socket.IO test endpoint. It listens only on loopback
    // and exercises the real SocketIOClient, including its reconnect machinery.
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly ConcurrentDictionary<int, WebSocket> connections = new();
        private readonly ConcurrentDictionary<int, int> joinCounts = new();
        private readonly ConcurrentBag<Task> handlers = [];
        private readonly Channel<(int Connection, JsonElement Event)> events = Channel.CreateUnbounded<(int, JsonElement)>();
        private readonly Task acceptTask;
        private int connectionCount;

        public LoopbackServer()
        {
            listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            acceptTask = AcceptAsync();
        }

        public Uri Address { get; }
        public int ConnectionCount => Volatile.Read(ref connectionCount);
        public int GetJoinCount(int id) => joinCounts.GetValueOrDefault(id);

        public void DropConnection(int id) => connections[id].Abort();

        public Task SendEventAsync(int id, params object[] data) =>
            SendAsync(connections[id], "42" + JsonSerializer.Serialize(data), cancellation.Token);

        public async Task ExpectJoinAsync(int id, string lobby, int player, int client, CancellationToken token, bool isHost = true)
        {
            var relevantEvents = new List<string>();
            await foreach (var received in events.Reader.ReadAllAsync(token))
            {
                if (received.Connection != id) continue;
                var data = received.Event;
                var name = data[0].GetString();
                if (name is not ("leave" or "id" or "join")) continue;
                relevantEvents.Add(name);
                if (name == "id" && (data[1].GetInt32() != player || data[2].GetInt32() != client))
                    throw new InvalidOperationException("rejoin sent wrong player/client identity");
                if (name != "join") continue;
                if (!relevantEvents.SequenceEqual(["leave", "id", "join"]) ||
                    data[1].GetString() != lobby || data[2].GetInt32() != player || data[3].GetInt32() != client ||
                    data[4].GetBoolean() != isHost)
                    throw new InvalidOperationException("rejoin event order or lobby identity differs from 3.2.7");
                return;
            }
            throw new InvalidOperationException("server stopped before lobby join");
        }

        public async Task ExpectLobbyAsync(int id, string lobbyCode, string host, bool isPublic,
            string title, string language, int players, CancellationToken token)
        {
            await foreach (var received in events.Reader.ReadAllAsync(token))
            {
                if (received.Connection != id || received.Event[0].GetString() != "lobby") continue;
                var data = received.Event;
                if (data.GetArrayLength() != 3 || data[1].GetString() != lobbyCode)
                    throw new InvalidOperationException("public lobby event argument shape is incorrect");
                var payload = data[2];
                if (payload.GetProperty("id").GetInt32() != -1 ||
                    payload.GetProperty("host").GetString() != host ||
                    payload.GetProperty("isPublic").GetBoolean() != isPublic ||
                    payload.GetProperty("title").GetString() != title ||
                    payload.GetProperty("language").GetString() != language ||
                    payload.GetProperty("current_players").GetInt32() != players ||
                    payload.GetProperty("max_players").GetInt32() != 15 ||
                    payload.GetProperty("mods").GetString() != "NONE" ||
                    payload.GetProperty("gameState").GetInt32() != 0)
                    throw new InvalidOperationException("public lobby event differs from the released wire format");
                return;
            }
            throw new InvalidOperationException("server stopped before public lobby announcement");
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    handlers.Add(HandleAsync(client, Interlocked.Increment(ref connectionCount)));
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client, int id)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var header = new List<byte>();
                    var single = new byte[1];
                    while (header.Count < 8192)
                    {
                        if (await stream.ReadAsync(single, cancellation.Token) == 0) return;
                        header.Add(single[0]);
                        if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var key = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
                        .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
                    await stream.WriteAsync(response, cancellation.Token);
                    using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
                    connections[id] = socket;
                    await SendAsync(socket, "0" + JsonSerializer.Serialize(new
                    {
                        sid = $"engine-{id}", upgrades = Array.Empty<string>(), pingInterval = 60000, pingTimeout = 60000,
                        maxPayload = 1000000
                    }), cancellation.Token);
                    var buffer = new byte[65536];
                    while (!cancellation.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (!result.EndOfMessage) throw new InvalidOperationException("unexpected fragmented self-test packet");
                        var packet = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (packet.StartsWith("40", StringComparison.Ordinal))
                            await SendAsync(socket, "40" + JsonSerializer.Serialize(new { sid = $"socket-{id}" }), cancellation.Token);
                        else if (packet == "2") await SendAsync(socket, "3", cancellation.Token);
                        else if (packet.StartsWith("42", StringComparison.Ordinal))
                        {
                            var data = JsonSerializer.Deserialize<JsonElement>(packet[2..]);
                            if (data[0].GetString() == "join") joinCounts.AddOrUpdate(id, 1, (_, count) => count + 1);
                            await events.Writer.WriteAsync((id, data), cancellation.Token);
                        }
                        else if (packet == "41") break;
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
                finally { connections.TryRemove(id, out _); }
            }
        }

        private static async Task SendAsync(WebSocket socket, string packet, CancellationToken token) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes(packet).AsMemory(), WebSocketMessageType.Text, true, token);

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            await acceptTask;
            foreach (var socket in connections.Values) socket.Abort();
            await Task.WhenAll(handlers);
            events.Writer.TryComplete();
            cancellation.Dispose();
        }
    }
}
