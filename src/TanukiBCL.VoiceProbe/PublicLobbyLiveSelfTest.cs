using System.Text.Json;
using SocketIOClient;
using SocketIOClient.Transport;

namespace TanukiBCL.VoiceProbe;

internal static class PublicLobbyLiveSelfTest
{
    internal static async Task<int> RunAsync(ProbeOptions options)
    {
        using var socket = new SocketIOClient.SocketIO(options.Server, new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = false,
            ConnectionTimeout = TimeSpan.FromSeconds(10)
        });
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<(int State, string Message)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialList = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listingEvents = 0;
        socket.OnConnected += (_, _) => connected.TrySetResult();
        socket.On("new_lobbies", response =>
        {
            try
            {
                var value = response.GetValue<JsonElement>();
                if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var listing in value.EnumerateArray()) VerifyListingShape(listing);
                    Interlocked.Increment(ref listingEvents);
                    initialList.TrySetResult(value.GetArrayLength());
                }
            }
            catch (Exception error) { initialList.TrySetException(error); }
        });
        socket.On("update_lobby", _ => Interlocked.Increment(ref listingEvents));
        try
        {
            await socket.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(12));
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await socket.EmitAsync("lobbybrowser", true).WaitAsync(TimeSpan.FromSeconds(5));
            var initialCount = await initialList.Task.WaitAsync(TimeSpan.FromSeconds(8));
            // A deliberately impossible ID only requests the browser's code-lookup
            // acknowledgement; it cannot join a real Among Us lobby.
            await socket.EmitAsync("join_lobby", response =>
            {
                try { acknowledged.TrySetResult((response.GetValue<int>(0), response.GetValue<string>(1))); }
                catch (Exception error) { acknowledged.TrySetException(error); }
            }, int.MinValue).WaitAsync(TimeSpan.FromSeconds(5));
            var result = await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (result.State == 0)
                throw new InvalidOperationException("An impossible public lobby ID returned a successful code.");
            Console.WriteLine($"[PASS] live public-lobby subscription, initial list ({initialCount} entries), and join_lobby error acknowledgement; listingEvents={Volatile.Read(ref listingEvents)}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[FAIL] live public-lobby browser protocol: {error.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (socket.Connected)
                {
                    await socket.EmitAsync("lobbybrowser", false).WaitAsync(TimeSpan.FromSeconds(2));
                    await socket.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception) { }
        }
    }

    private static void VerifyListingShape(JsonElement listing)
    {
        if (listing.ValueKind != JsonValueKind.Object ||
            !listing.TryGetProperty("id", out var id) || !id.TryGetInt32(out _) ||
            !listing.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
            !listing.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String ||
            !listing.TryGetProperty("current_players", out var current) || !current.TryGetInt32(out _) ||
            !listing.TryGetProperty("max_players", out var maximum) || !maximum.TryGetInt32(out _) ||
            !listing.TryGetProperty("language", out var language) || language.ValueKind != JsonValueKind.String ||
            !listing.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.String ||
            !listing.TryGetProperty("gameState", out var gameState) || !gameState.TryGetInt32(out _))
            throw new InvalidOperationException("Public lobby listing does not match the released 3.2.8 wire schema.");
    }
}
