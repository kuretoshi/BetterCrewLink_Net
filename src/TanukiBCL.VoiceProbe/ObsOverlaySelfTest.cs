using System.Buffers;
using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class ObsOverlaySelfTest
{
    public static int Run()
    {
        Verify();
        return 0;
    }

    public static void Verify()
    {
        var state = new AmongUsState
        {
            Mod = AmongUsModType.NebulaOnTheShip,
            GameState = GameState.Tasks,
            PlayerColors = [new PlayerColorPair { Main = 0x00332211, Shadow = 0x00665544 }],
            Players = [new Player { Id = 0, ClientId = 1, IsLocal = true, ColorId = 0,
                NosPlayer = new NosPlayerData { ColorR = 0.2, ColorG = 0.8, ColorB = 0.4 } }]
        };
        var peers = new Dictionary<int, ObsPeerState>();
        var player = state.Players[0];
        var frame = VerifyFrame(state, "#112233", "#445566");
        Check(frame.GetProperty("nosColor").GetString() == "#33cc66", "live NoS color changed");
        state.GameState = GameState.Lobby;
        player.NosPlayer = null;
        frame = VerifyFrame(state, "#112233", "#445566");
        Check(!frame.TryGetProperty("nosColor", out _), "missing NoS color should use realColor");
        player.NosLobbyColor = "#50ef39";
        frame = VerifyFrame(state, "#112233", "#445566");
        Check(frame.GetProperty("nosColor").GetString() == "#50ef39", "NoS lobby color changed");
        state.GameState = GameState.Tasks;
        player.NosLobbyColor = null;
        player.NosPlayer = new NosPlayerData { ColorR = 0.3, ColorG = 0.4, ColorB = 0.5 };
        frame = VerifyFrame(state, "#112233", "#445566");
        Check(frame.GetProperty("nosColor").GetString() == "#4d6680", "next NoS round did not update color");
        player.NosPlayer = null;
        foreach (var mod in new[] { AmongUsModType.NebulaOnTheShip, AmongUsModType.None,
            AmongUsModType.SuperNewRoles, AmongUsModType.TownOfHostForE })
        {
            state.Mod = mod;
            foreach (var id in new[] { -1, 999 })
            {
                player.ColorId = id;
                VerifyFrame(state, "#C51111", "#7A0838");
            }
        }
        state.PlayerColors.Clear();
        player.ColorId = 1;
        VerifyFrame(state, "#132ED1", "#09158E");

        var cache = new ObsOverlayPayloadCache();
        var now = DateTimeOffset.UtcNow;
        Check(cache.BuildIfChanged("OBSSECRET", state, peers, false, false, now: now) is not null,
            "initial OBS state missing");
        Check(cache.BuildIfChanged("OBSSECRET", state, peers, false, false, now: now.AddSeconds(1)) is null,
            "unchanged state was not deduplicated");
        state.GameState = GameState.Lobby;
        var lobby = cache.BuildIfChanged("OBSSECRET", state, peers, false, false, now: now.AddSeconds(2));
        Check(lobby?.GetProperty("overlayState").GetProperty("gameState").GetInt32() == (int)GameState.Lobby,
            "return to lobby did not publish a fresh frame");
        state.GameState = GameState.Tasks;
        Check(cache.BuildIfChanged("OBSSECRET", state, peers, false, false, now: now.AddSeconds(3)) is not null,
            "next match did not publish a fresh frame");
        Console.WriteLine("[PASS] OBS: NoS match/snapshot reset/lobby/next match, fallback colors, writer parity and cache");
    }

    private static JsonElement VerifyFrame(AmongUsState state, string main, string shadow)
    {
        var peers = new Dictionary<int, ObsPeerState>();
        var frame = ObsOverlayWire.Build(state, peers, false, false);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) ObsOverlayWire.Write(writer, state, peers, false, false);
        using var written = JsonDocument.Parse(buffer.WrittenMemory);
        Check(written.RootElement.GetRawText() == frame.GetRawText(), "OBS serializers disagree");
        var player = frame.GetProperty("overlayState").GetProperty("players")[0];
        Check(player.TryGetProperty("realColor", out var colors) && colors.GetArrayLength() == 2 &&
            colors[0].GetString() == main && colors[1].GetString() == shadow,
            "OBS needs both body and shadow colors even while NoS snapshots reset");
        return player;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
