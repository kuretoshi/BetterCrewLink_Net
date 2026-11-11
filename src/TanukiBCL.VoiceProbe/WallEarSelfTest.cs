using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class WallEarSelfTest
{
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    internal static int Run()
    {
        var me = new Player { Id = 1, X = 12, Y = -15 };
        var other = new Player { Id = 2, X = 15, Y = -15 };
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Tasks, Map = MapType.Fungle };
        var settings = new SpatialVoiceSettings(WallsBlockAudio: true, MaxDistance: 5.32);
        Check(!SpatialVoicePolicy.Calculate(state, me, other, settings).Audible, "ordinary wall blocks voice");
        state.NosWallEars = [new() { PlayerId = 1, X = 12, Y = -15, Range = 4 }];
        Check(SpatialVoicePolicy.Calculate(state, me, other, settings).Audible, "active ear bypasses wall");
        state.NosWallEars[0].Range = 2;
        Check(!SpatialVoicePolicy.Calculate(state, me, other, settings).Audible, "ear range enforced");
        state.NosWallEars[0].Range = 4;
        Check(SpatialVoicePolicy.Calculate(state, me, other, settings with { CommsSabotage = true }).Audible,
            "comms setting alone does not mute");
        state.CommsSabotaged = true;
        Check(!SpatialVoicePolicy.Calculate(state, me, other, settings with { CommsSabotage = true }).Audible, "ear respects comms restriction");
        state.CommsSabotaged = false;
        other.IsDead = true;
        Check(!SpatialVoicePolicy.Calculate(state, me, other, settings).Audible, "ear does not reveal ghost voices");
        other.IsDead = false;
        state.NosWallEars[0].PlayerId = 3;
        Check(!SpatialVoicePolicy.Calculate(state, me, other, settings).Audible, "other player cannot use listener ear");
        state.NosWallEars[0].PlayerId = 1;
        var wire = JsonSerializer.SerializeToElement(MobileGameStateWire.Create(state, new LobbySettings(), null));
        Check(wire.GetProperty("gameState").GetProperty("nosWallEars")[0].GetProperty("range").GetDouble() == 4,
            "NET host sends ear state to Web/mobile");
        state.GameState = GameState.Discussion;
        Check(NosWallEarState.For(state, me) == null, "meeting ignores leftover ear state");
        state.GameState = GameState.Tasks;
        me.X = 0;
        Check(NosWallEarState.For(state, me) == null, "teleport revokes remote hearing");
        var path = Path.Combine(Path.GetTempPath(), "tanuki-ear-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"protocol\":1,\"pid\":7,\"unixMs\":10000,\"players\":[{\"playerId\":1,\"x\":12,\"y\":-15,\"range\":4}]}");
            Check(NosVoiceRoleStateReader.ReadWallEars(7, 10000, path).Count == 1, "legacy ear state accepted");
            Check(NosVoiceRoleStateReader.ReadWallEars(8, 10000, path).Count == 0, "other game PID rejected");
            Check(NosVoiceRoleStateReader.ReadWallEars(7, 11001, path).Count == 0, "stale ear state rejected");
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS wall ear: NET spatial policy, host wire, range, privacy, lifetime and state validation");
        return 0;
    }
}
