using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class VoiceJammingSelfTest
{
    public static void Verify(Action<string, bool, bool> check)
    {
        var local = new Player { Id = 1, IsLocal = true };
        var target = new Player { Id = 2 };
        var entry = new NosVoiceJammingState { PlayerId = 2, Muted = true, Pitch = 1.5 };
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Discussion,
            Players = [local, target], NosVoiceJamming = [entry] };
        var settings = new LobbySettings { NosFixerJammingVoiceBlock = false };
        check("Jamminger: only marked speaker is muted", true,
            SpatialVoicePolicy.Calculate(state, local, target, new()).Gain == 0 && VoiceJammingPolicy.For(state, 1) == null);
        entry.PlayerId = 1;
        check("Jamminger: forced local mute overrides optional Fixer settings", true, LocalJammingPolicy.IsJammed(state, settings));
        entry.PlayerId = 2; entry.Muted = false; entry.Lowpass = true;
        check("Jamminger: low-pass does not mute the microphone", false, LocalJammingPolicy.IsJammed(state, settings));
        check("Jamminger: target gets the low-pass filter", true,
            SpatialVoicePolicy.Calculate(state, local, target, new()).FixerLowpass);
        check("Jamminger: random pitch feeds the source filter", true,
            VoiceDisguiseEffectPolicy.Select(state, local, target, settings, 0, true, false) is
                { Mode: NosSizeEffectMode.SourceFilter, Pitch: 1.5 });
        state.GameState = GameState.Tasks;
        check("Jamminger: meeting end releases every effect", true,
            VoiceJammingPolicy.For(state, 2) == null && !LocalJammingPolicy.IsJammed(state, settings));
        state.GameState = GameState.Discussion;
        var wire = JsonSerializer.SerializeToElement(MobileGameStateWire.Create(state, settings, null));
        check("Jamminger: host relays effects to mobile", true,
            wire.GetProperty("gameState").GetProperty("nosVoiceJamming")[0].GetProperty("pitch").GetDouble() == 1.5);
        VerifyFile(entry, check);
    }
    private static void VerifyFile(NosVoiceJammingState entry, Action<string, bool, bool> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "tanuki-jam-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { protocol = 1, pid = 123, unixMs = 10000,
                players = new[] { new { playerId = entry.PlayerId, muted = false, lowpass = true, pitch = 1.5 } } }));
            check("Jamminger: IPC validates process and heartbeat", true,
                NosVoiceRoleStateReader.ReadVoiceJamming(123, 10000, path).Count == 1 &&
                NosVoiceRoleStateReader.ReadVoiceJamming(124, 10000, path).Count == 0 &&
                NosVoiceRoleStateReader.ReadVoiceJamming(123, 11001, path).Count == 0);
            File.WriteAllText(path, "{\"protocol\":1,\"pid\":123,\"unixMs\":10000,\"players\":[{\"playerId\":2,\"muted\":true,\"lowpass\":false,\"pitch\":100}]}");
            check("Jamminger: invalid pitch is rejected", true, NosVoiceRoleStateReader.ReadVoiceJamming(123, 10000, path).Count == 0);
        }
        finally { File.Delete(path); }
    }
}
