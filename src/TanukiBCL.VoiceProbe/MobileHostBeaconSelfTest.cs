using System.Diagnostics;
using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class MobileHostBeaconSelfTest
{
    public static int Run()
        => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var state = new AmongUsState
        {
            LobbyCode = "ABCDEF", GameState = GameState.Tasks, IsHost = true
        };
        var beacon = MobileHostBeacon.Create(state, true);
        if (beacon is null) return Fail("active lobby did not create a beacon");
        var json = JsonSerializer.SerializeToElement(beacon);
        if (json.GetProperty("to").GetString() != "ABCDEF_mobile" ||
            !json.GetProperty("data").GetProperty("mobileHostInfo")
                .GetProperty("isHostingMobile").GetBoolean() ||
            !json.GetProperty("data").GetProperty("mobileHostInfo")
                .GetProperty("isGameHost").GetBoolean())
            return Fail("3.2.7 mobile beacon shape differs");

        state.IsHost = false;
        json = JsonSerializer.SerializeToElement(MobileHostBeacon.Create(state, true));
        if (json.GetProperty("data").GetProperty("mobileHostInfo")
            .GetProperty("isGameHost").GetBoolean()) return Fail("non-host beacon claims game host");
        if (MobileHostBeacon.Create(state, false) is not null)
            return Fail("disabled mobile host created a beacon");
        state.GameState = GameState.Menu;
        if (MobileHostBeacon.Create(state, true) is not null)
            return Fail("menu state created a beacon");

        state.GameState = GameState.Tasks;
        using var response = JsonDocument.Parse("""
            {"mobilePlayerInfo":{"code":"ABCDEF"}}
            """);
        if (!MobileHostBeacon.IsResponseForLobby(response.RootElement, state))
            return Fail("matching mobile client was not detected");
        state.LobbyCode = "OTHER";
        if (MobileHostBeacon.IsResponseForLobby(response.RootElement, state))
            return Fail("different lobby mobile client was detected");

        await VerifySchedulerAsync();
        await VerifyCancellationAsync();
        Console.WriteLine("[PASS] 3.2.7 mobile host payload, live 5-second scheduler, failed-send recovery, cancellation and lobby detection");
        return 0;
    }

    private static async Task VerifySchedulerAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();
        var secondSend = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new AmongUsState { LobbyCode = "ABCDEF", GameState = GameState.Lobby };
        var attempts = 0;
        var errors = 0;
        var loop = MobileHostBeacon.RunAsync(() => MobileHostBeacon.Create(state, true), beacon =>
        {
            var payload = JsonSerializer.SerializeToElement(beacon);
            if (++attempts == 1)
            {
                if (payload.GetProperty("to").GetString() != "ABCDEF_mobile")
                    throw new InvalidOperationException("first beacon targeted the wrong lobby");
                state = new AmongUsState { LobbyCode = "NEWROOM", GameState = GameState.Tasks, IsHost = true };
                throw new IOException("simulated transport error");
            }
            if (payload.GetProperty("to").GetString() != "NEWROOM_mobile" ||
                !payload.GetProperty("data").GetProperty("mobileHostInfo").GetProperty("isGameHost").GetBoolean())
                throw new InvalidOperationException("scheduler reused a stale lobby or host value");
            secondSend.TrySetResult(watch.Elapsed);
            return Task.CompletedTask;
        }, error =>
        {
            if (error is not IOException) throw error;
            errors++;
        }, cancellation.Token);
        try
        {
            if (attempts != 1 || errors != 1)
                throw new InvalidOperationException("beacon was not attempted immediately or failure was not reported");
            var secondAt = await secondSend.Task.WaitAsync(cancellation.Token);
            if (secondAt < TimeSpan.FromSeconds(4.5) || secondAt > TimeSpan.FromSeconds(8))
                throw new InvalidOperationException($"beacon did not repeat at the 5-second interval ({secondAt})");
        }
        finally
        {
            cancellation.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        if (attempts != 2 || errors != 1)
            throw new InvalidOperationException("scheduler did not recover from its failed send cleanly");
    }

    private static async Task VerifyCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var pendingSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var loop = MobileHostBeacon.RunAsync(() => new object(), _ =>
        {
            attempts++;
            return pendingSend.Task;
        }, error => throw new InvalidOperationException("cancelled send was reported as a failure", error), cancellation.Token);
        try
        {
            cancellation.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(2));
            if (attempts != 1)
                throw new InvalidOperationException("cancellation did not stop the pending send");
            await MobileHostBeacon.RunAsync(() => new object(), _ =>
            {
                throw new InvalidOperationException("cancelled scheduler emitted a beacon");
            }, error => throw error, cancellation.Token);
        }
        finally
        {
            pendingSend.TrySetResult();
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[FAIL] {message}");
        return 1;
    }
}
