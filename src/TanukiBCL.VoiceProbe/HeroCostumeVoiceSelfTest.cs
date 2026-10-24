using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class HeroCostumeVoiceSelfTest
{
    public static void Verify(Action<string, bool, bool> check)
    {
        var local = new Player { Id = 1, Name = "local" };
        var speaker = new Player { Id = 2, Name = "original", AppearanceName = "transformed" };
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Tasks,
            Players = [local, speaker] };
        var lobby = new LobbySettings { VoiceEffectEnabled = true };
        foreach (var kind in new[] { "super", "evil" })
        {
            state.NosHeroTransforms = [new() { PlayerId = 2, Kind = kind }];
            check("Hero costume: " + kind + " preserves normal voice", true,
                VoiceDisguiseEffectPolicy.Select(state, local, speaker, lobby, 100, true, false) == null);
        }
        state.NosHeroTransforms = [new() { PlayerId = 3, Kind = "super" }];
        check("Hero costume: other disguises retain their effect", true,
            VoiceDisguiseEffectPolicy.Select(state, local, speaker, lobby, 100, true, false)?.Mode == NosSizeEffectMode.Disguise);
        state.NosHeroTransforms.Clear();
        check("Hero costume: expiry removes the exemption", true,
            VoiceDisguiseEffectPolicy.Select(state, local, speaker, lobby, 100, true, false)?.Mode == NosSizeEffectMode.Disguise);
        state.GameState = GameState.Discussion;
        state.NosHeroTransforms = [new() { PlayerId = 2, Kind = "evil" }];
        state.NosVoiceJamming = [new() { PlayerId = 2, Pitch = 1.5 }];
        check("Hero costume: Jamminger ability still works", true,
            VoiceDisguiseEffectPolicy.Select(state, local, speaker, lobby, 100, true, false)?.Pitch == 1.5);
        var wire = JsonSerializer.SerializeToElement(MobileGameStateWire.Create(state, lobby, null));
        check("Hero costume: host relays transformation to mobile", true,
            wire.GetProperty("gameState").GetProperty("nosHeroTransforms")[0].GetProperty("kind").GetString() == "evil");
        VerifyFile(check);
    }
    private static void VerifyFile(Action<string, bool, bool> check)
    {
        string path = Path.Combine(Path.GetTempPath(), "tanuki-hero-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"protocol\":1,\"pid\":123,\"unixMs\":10000,\"players\":[{\"playerId\":2,\"kind\":\"super\"}]}");
            check("Hero costume: file validates process and heartbeat", true,
                NosVoiceRoleStateReader.ReadHeroTransforms(123, 10000, path).Count == 1 &&
                NosVoiceRoleStateReader.ReadHeroTransforms(124, 10000, path).Count == 0 &&
                NosVoiceRoleStateReader.ReadHeroTransforms(123, 11001, path).Count == 0 &&
                NosVoiceRoleStateReader.ReadHeroTransforms(123, 9800, path).Count == 0);
            File.WriteAllText(path, "{\"protocol\":1,\"pid\":123,\"unixMs\":10000,\"players\":[{\"playerId\":2,\"kind\":\"other\"}]}");
            check("Hero costume: unrelated role rejected", true,
                NosVoiceRoleStateReader.ReadHeroTransforms(123, 10000, path).Count == 0);
        }
        finally { File.Delete(path); }
    }
}
