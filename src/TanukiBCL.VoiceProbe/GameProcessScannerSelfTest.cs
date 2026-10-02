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

        var expectation = new GameScanExpectation("Lobby", 4, 0, 0, false, 4);
        var results = Enumerable.Range(0, 4).Select(local =>
            new GameProcessScanner.ProcessReadResult(local + 1, false, new AmongUsState
            {
                GameState = GameState.Lobby,
                LobbyCode = "ABCDEF",
                Players = Enumerable.Range(0, 4).Select(id => new Player
                {
                    Id = id, ClientId = id + 20, IsLocal = id == local, Name = $"Player{id}"
                }).ToList()
            }, "", null)).ToArray();
        var state = results[0].State!;
        Require(GameProcessScanner.IsReady(state, GameState.Lobby), "Explicit lobby scan never completes");
        Require(!GameProcessScanner.IsReady(state, null), "Default must still wait for a game");
        Require(!GameProcessScanner.IsReady(state, GameState.Tasks), "Wrong state accepted");
        Require(GameProcessScanner.Validate(results, expectation, GameState.Lobby), "Four-player lobby failed");
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
