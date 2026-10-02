using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class MobileHostBeacon
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Func<object?> createBeacon, Func<object, Task> send,
        Action<Exception> reportError, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (createBeacon() is { } beacon)
                {
                    try
                    {
                        // SocketIOClient 3.1.2 has no cancellation parameter for EmitAsync.
                        // A blocked transport must not hold up Stop or a settings restart.
                        await send(beacon).WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception error)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        reportError(error);
                    }
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public static object? Create(AmongUsState? state, bool enabled)
    {
        if (!enabled || state is null ||
            state.GameState is GameState.Menu or GameState.Unknown ||
            string.IsNullOrWhiteSpace(state.LobbyCode)) return null;

        return new
        {
            to = state.LobbyCode + "_mobile",
            data = new { mobileHostInfo = new { isHostingMobile = true, isGameHost = state.IsHost } }
        };
    }

    public static bool IsResponseForLobby(JsonElement data, AmongUsState? state)
    {
        if (state is null || state.GameState == GameState.Menu ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("mobilePlayerInfo", out var playerInfo) ||
            playerInfo.ValueKind != JsonValueKind.Object ||
            !playerInfo.TryGetProperty("code", out var code)) return false;
        return code.ValueKind == JsonValueKind.String &&
               code.GetString() == state.LobbyCode;
    }
}
