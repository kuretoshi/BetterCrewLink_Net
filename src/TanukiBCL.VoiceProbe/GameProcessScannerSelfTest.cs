using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class GameProcessScannerSelfTest
{
    public static int Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var baseHost = new Player { ClientId = 2, Name = "開発者くれとし", NameHash = 138444898 };
        var decoratedHost = new Player
        {
            Name = "\n開発者くれとし\n\nTown Of Host For E EM v6190.416\n",
            NameHash = 1854123896
        };
        Require(GameCodeCodec.LocalHostCode(baseHost) == "46282" &&
            GameCodeCodec.LocalHostCode(decoratedHost) == "46282",
            "TOH host decoration split the local voice lobby");
        var disconnectedGuests = new[]
        {
            baseHost,
            new Player { Name = "former guest", NameHash = -139038, ClientId = 2, Disconnected = true },
            new Player { Name = "dummy", NameHash = 7654321, ClientId = 2, IsDummy = true }
        };
        Require(GameCodeCodec.LocalHostCode(2, disconnectedGuests) == "46282",
            "Disconnected players reused the host client ID and replaced the local voice lobby");
        Require(TohHostName.HasMarker("<color=red>Town\u200B Of Host For E EM</color>") &&
            TohHostName.ForLocalCode("\n開発者くれとし\n<color=red>Town Of Host For E EM</color>") ==
            baseHost.Name,
            "TOH rich-text or zero-width host marker was not normalized");

        var expectation = new GameScanExpectation("Lobby", 4, 0, 0, false, 4);
        var results = Enumerable.Range(0, 4).Select(local =>
            new GameProcessScanner.ProcessReadResult(local + 1, false, new AmongUsState
            {
                GameState = GameState.Lobby,
                LobbyCode = "ABCDEF",
                Players = Enumerable.Range(0, 4).Select(id => new Player
                {
                    Id = id, ClientId = id + 20, IsLocal = id == local,
                    Name = $"Player{id}", X = id * 2d
                }).ToList()
            }, "", null)).ToArray();
        var state = results[0].State!;
        Require(GameProcessScanner.IsReady(state, GameState.Lobby), "Explicit lobby scan never completes");
        Require(!GameProcessScanner.IsReady(state, null), "Default must still wait for a game");
        Require(!GameProcessScanner.IsReady(state, GameState.Tasks), "Wrong state accepted");
        var snrState = new AmongUsState
        {
            Mod = AmongUsModType.SuperNewRoles,
            GameState = GameState.Tasks,
            Players = [new Player { Id = 0, Name = "SNR player" }]
        };
        Require(!GameProcessScanner.IsReady(snrState, GameState.Tasks),
            "SNR scan completed before the live role reader");
        snrState.Players[0].SnrRole = new SnrRoleData(1, "Crewmate", 0, "None", 0, "None");
        Require(GameProcessScanner.IsReady(snrState, GameState.Tasks),
            "SNR scan did not complete after role discovery");
        var tohState = new AmongUsState
        {
            Mod = AmongUsModType.TownOfHostForE,
            GameState = GameState.Tasks,
            Players = [new Player { Id = 0, Name = "TOH player" }]
        };
        Require(!GameProcessScanner.IsReady(tohState, GameState.Tasks),
            "TOH scan completed before the host role reader");
        tohState.Players[0].TohRole = new TohRoleData(0, "Crewmate", false, false);
        Require(GameProcessScanner.IsReady(tohState, GameState.Tasks),
            "TOH scan did not complete after role discovery");
        var hostOnlyToh = Enumerable.Range(0, 4).Select(local =>
            new GameProcessScanner.ProcessReadResult(local + 10, true, new AmongUsState
            {
                Mod = local == 0 ? AmongUsModType.TownOfHostForE : AmongUsModType.None,
                GameState = GameState.Tasks,
                LobbyCode = "TOH123",
                Players = Enumerable.Range(0, 4).Select(id => new Player
                {
                    Id = id, ClientId = id + 20, IsLocal = id == local,
                    Name = $"Player{id}", IsImpostor = id == (local == 0 ? 3 : local),
                    TohRole = local == 0 ? new TohRoleData(id == 3 ? 1 : 0,
                        id == 3 ? "Impostor" : "Crewmate", false, id == 3) : null
                }).ToList()
            }, "", null)).ToArray();
        Require(GameProcessScanner.Validate(hostOnlyToh,
            new GameScanExpectation("Tasks", 4, 0, 1, false, 4), GameState.Tasks),
            "TOH host-only scan rejected client-specific vanilla self roles");
        Require(GameProcessScanner.Validate(results, expectation, GameState.Lobby), "Four-player lobby failed");
        var departed = Enumerable.Range(0, 2).Select(local =>
            new GameProcessScanner.ProcessReadResult(local + 30, true, new AmongUsState
            {
                GameState = GameState.Tasks,
                LobbyCode = "46282",
                Players = Enumerable.Range(0, 4).Select(id => new Player
                {
                    Id = id,
                    ClientId = id < 2 ? id + 2 : local + 2,
                    IsLocal = id == local,
                    Disconnected = id >= 2,
                    Name = $"Player{id}"
                }).ToList()
            }, "", null)).ToArray();
        Require(GameProcessScanner.Validate(departed,
            new GameScanExpectation("Tasks", 2, 0, 0, false, 2), GameState.Tasks),
            "Departed players with reused client IDs invalidated the remaining live game");
        Require(!GameProcessScanner.Validate(results, expectation with { Players = 5 }, GameState.Lobby), "Wrong count accepted");
        Require(!GameProcessScanner.Validate(results, expectation with { Alive = 3 }, GameState.Lobby), "Wrong alive count accepted");
        Require(!GameProcessScanner.Validate([], expectation, GameState.Lobby), "Empty scan passed");
        var failed = results.ToArray();
        failed[0] = failed[0] with { State = null, Error = "timeout" };
        Require(!GameProcessScanner.Validate(failed, expectation, GameState.Lobby), "Partial scan passed");
        results[1].State!.LobbyCode = "UVWXYZ";
        Require(!GameProcessScanner.Validate(results, expectation, GameState.Lobby), "Different lobbies passed");
        Require(ProbeOptions.Parse(["--scan-game"]).ExpectedPlayers == 5, "Default count changed");
        Require(ProbeOptions.Parse(["--scan-game", "--expected-players", "4"]).ExpectedPlayers == 4, "Count not parsed");
        try
        {
            ProbeOptions.Parse(["--expected-players", "0"]);
            throw new InvalidOperationException("Zero count accepted");
        }
        catch (ArgumentException) { }
        Console.WriteLine("[PASS] Game scan readiness, count, state, missing reads and lobby consistency");
        return 0;
    }
}
