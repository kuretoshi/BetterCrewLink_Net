using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class MobileHostBeaconSelfTest
{
    public static int Run()
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
                .GetProperty("isGameHost").GetBoolean() ||
            MobileHostBeacon.Interval != TimeSpan.FromSeconds(5))
            return Fail("3.2.7 mobile beacon shape or interval differs");

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

        Console.WriteLine("[PASS] 3.2.7 mobile host beacon payload, timing and lobby detection");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[FAIL] {message}");
        return 1;
    }
}
