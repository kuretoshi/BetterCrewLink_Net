using System.Diagnostics;
using System.Runtime.InteropServices;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class GameProcessScanner
{
    public static async Task<int> RunAsync(TimeSpan timeout, GameScanExpectation expectation)
    {
        GameState? expectedGameState = null;
        if (expectation.GameState is not null)
        {
            if (!Enum.TryParse<GameState>(expectation.GameState, true, out var parsedGameState))
            {
                Console.Error.WriteLine($"ゲーム状態の期待値が不正です: {expectation.GameState}");
                return 1;
            }

            expectedGameState = parsedGameState;
        }

        var processes = Process.GetProcessesByName("Among Us")
            .OrderBy(process => process.Id)
            .ToArray();
        if (processes.Length == 0)
        {
            Console.Error.WriteLine("Among Usプロセスが見つかりません。");
            return 1;
        }

        Console.WriteLine($"Among Usを{processes.Length}プロセス検出しました: {string.Join(", ", processes.Select(p => p.Id))}");
        using var cancellation = new CancellationTokenSource(timeout);
        var tasks = processes.Select(process => ReadProcessAsync(process, expectedGameState, cancellation.Token)).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        PrintResults(results);
        return Validate(results, expectation, expectedGameState) ? 0 : 1;
    }

    private static async Task<ProcessReadResult> ReadProcessAsync(
        Process process,
        GameState? expectedGameState,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = CreateProcessInfo(process);

            using var reader = new AmongUsMemoryReaderService();
            var completion = new TaskCompletionSource<AmongUsState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var diagnostics = string.Empty;
            var error = string.Empty;
            reader.DiagnosticChanged += (_, message) => diagnostics = message;
            reader.Error += (_, message) => error = message;
            reader.StateChanged += (_, state) =>
            {
                if (IsReady(state, expectedGameState))
                {
                    completion.TrySetResult(state);
                }
            };
            reader.SetProcess(info);
            reader.Start();

            try
            {
                var state = await completion.Task.WaitAsync(cancellationToken);
                return new ProcessReadResult(process.Id, info.Is64Bit, state, diagnostics, null);
            }
            catch (OperationCanceledException)
            {
                return new ProcessReadResult(process.Id, info.Is64Bit, null, diagnostics, error.Length > 0 ? error : "読み取りタイムアウト");
            }
            finally
            {
                reader.Stop();
            }
        }
        catch (Exception exception)
        {
            return new ProcessReadResult(process.Id, false, null, string.Empty, exception.Message);
        }
    }

    internal static AmongUsProcessInfo CreateProcessInfo(Process process)
    {
        var processPath = process.MainModule?.FileName ?? string.Empty;
        var gameDirectory = Path.GetDirectoryName(processPath) ?? string.Empty;
        var loadedModules = process.Modules.Cast<ProcessModule>()
            .Select(module => module.ModuleName).ToArray();
        var hasGameAssembly = loadedModules.Contains("GameAssembly.dll", StringComparer.OrdinalIgnoreCase);
        return new AmongUsProcessInfo
        {
            ProcessId = process.Id,
            ProcessPath = processPath,
            GameDirectory = gameDirectory,
            HasGameAssembly = hasGameAssembly,
            Is64Bit = IsProcess64Bit(process),
            InstalledMod = AmongUsModDetector.Detect(processPath, loadedModules,
                AmongUsModDetector.ReadPluginFiles(gameDirectory))
        };
    }

    private static void PrintResults(IEnumerable<ProcessReadResult> results)
    {
        foreach (var result in results.OrderBy(result => result.ProcessId))
        {
            if (result.State is null)
            {
                Console.WriteLine($"PID {result.ProcessId}: [FAIL] {result.Error}; {result.Diagnostic}");
                continue;
            }

            var local = result.State.Players.SingleOrDefault(player => player.IsLocal);
            var alive = result.State.Players.Count(player => !player.IsDead && !player.Disconnected);
            var dead = result.State.Players.Count(player => player.IsDead && !player.Disconnected);
            var impostors = result.State.Players.Count(player => player.IsImpostor && !player.Disconnected);
            Console.WriteLine(
                $"PID {result.ProcessId}: state={result.State.GameState} lobby={result.State.LobbyCode} " +
                $"client={result.State.ClientId} local={local?.Name ?? "?"} " +
                $"role={(local?.IsImpostor == true ? "Impostor" : "Crewmate")} " +
                $"players={result.State.Players.Count} alive={alive} dead={dead} impostors={impostors}");

            foreach (var player in result.State.Players.OrderBy(player => player.ClientId))
            {
                Console.WriteLine(
                    $"  {(player.IsLocal ? '*' : ' ')} id={player.Id} client={player.ClientId} name={player.Name} " +
                    $"role={(player.IsImpostor ? "Impostor" : "Crewmate")} dead={player.IsDead} " +
                    $"pos=({player.X:0.0000},{player.Y:0.0000})");
            }

            if (local is not null)
            {
                var settings = new SpatialVoiceSettings();
                Console.WriteLine("  voice mix (TanukiBCL v3.2.5 defaults):");
                foreach (var other in result.State.Players.Where(player => !player.IsLocal).OrderBy(player => player.ClientId))
                {
                    var mix = SpatialVoicePolicy.Calculate(result.State, local, other, settings);
                    Console.WriteLine(
                        $"    client={other.ClientId} name={other.Name} gain={mix.Gain:0.000} " +
                        $"pan={mix.Pan:+0.00;-0.00;0.00} distance={mix.Distance:0.00} reason={mix.Reason}");
                }
            }
        }
    }

    internal static bool IsReady(AmongUsState state, GameState? expectedGameState) =>
        state.Players.Count > 0 && (expectedGameState.HasValue
            ? state.GameState == expectedGameState.Value
            : state.GameState is GameState.Tasks or GameState.Discussion);

    internal static bool Validate(
        IReadOnlyCollection<ProcessReadResult> results,
        GameScanExpectation expectation,
        GameState? expectedGameState)
    {
        var states = results.Where(result => result.State is not null).Select(result => result.State!).ToArray();
        var localPlayers = states.SelectMany(state => state.Players.Where(player => player.IsLocal)).ToArray();
        var expectedPlayers = expectation.Players;
        var passed = expectedPlayers > 0 && results.Count == expectedPlayers &&
                     states.Length == expectedPlayers &&
                     states.Select(state => state.LobbyCode).Distinct(StringComparer.Ordinal).Count() == 1 &&
                     states.Select(state => state.GameState).Distinct().Count() == 1 &&
                     states.All(state => state.Players.Count == expectedPlayers) &&
                     (expectedGameState is null || states.All(state => state.GameState == expectedGameState)) &&
                     (expectation.Alive is null || states.All(state => CountAlive(state) == expectation.Alive)) &&
                     (expectation.Dead is null || states.All(state => CountDead(state) == expectation.Dead)) &&
                     (expectation.Impostors is null || states.All(state => CountImpostors(state) == expectation.Impostors)) &&
                     localPlayers.Length == expectedPlayers &&
                     localPlayers.Select(player => player!.ClientId).Distinct().Count() == expectedPlayers &&
                     localPlayers.Count(player => player!.IsImpostor) == CountImpostors(states.FirstOrDefault());

        var voiceRulesPassed = states.Length > 0 && states.Length == results.Count && states.All(state =>
        {
            var locals = state.Players.Where(player => player.IsLocal).ToArray();
            if (locals.Length != 1) return false;
            var me = locals[0];
            var mixes = state.Players
                .Where(player => !player.IsLocal)
                .Select(player => (Player: player, Mix: SpatialVoicePolicy.Calculate(state, me, player, new SpatialVoiceSettings())))
                .ToArray();

            return state.GameState switch
            {
                GameState.Discussion => me.IsDead
                    ? mixes.All(item => item.Mix.Audible && item.Mix.Gain == 1d && item.Mix.Pan == 0d && item.Mix.Reason == "meeting")
                    : mixes.All(item => item.Player.IsDead
                        ? !item.Mix.Audible && item.Mix.Pan == 0d && item.Mix.Reason == "living-cannot-hear-ghost"
                        : item.Mix.Audible && item.Mix.Gain == 1d && item.Mix.Pan == 0d && item.Mix.Reason == "meeting"),
                GameState.Tasks when expectation.ExpectNearby => me.IsDead
                    ? mixes.All(item => item.Mix.Audible && item.Mix.Reason == "proximity")
                    : mixes.All(item => item.Player.IsDead
                        ? !item.Mix.Audible && item.Mix.Reason == "living-cannot-hear-ghost"
                        : item.Mix.Audible && item.Mix.Reason == "proximity"),
                GameState.Tasks => mixes.All(item => me.IsDead || !item.Player.IsDead
                    ? item.Mix.Reason is "proximity" or "out-of-range"
                    : !item.Mix.Audible && item.Mix.Reason == "living-cannot-hear-ghost"),
                GameState.Lobby => mixes.All(item => item.Mix.Audible && item.Mix.Gain == 1d && item.Mix.Pan == 0d && item.Mix.Reason == "lobby"),
                _ => false
            };
        });

        Console.WriteLine(passed
            ? $"[PASS] 全{expectedPlayers}プロセスでゲーム状態と指定した期待値が一致しました。"
            : "[FAIL] ゲーム状態が指定した期待値と一致しません。上のPID別結果を確認してください。");
        Console.WriteLine(voiceRulesPassed
            ? "[PASS] 現在のゲーム状態に応じた音声の可聴・遮断・定位ルールが一致しました。"
            : "[FAIL] 音声ルールが現在のゲーム状態または生死状態と一致しません。");
        return passed && voiceRulesPassed;
    }

    private static int CountAlive(AmongUsState state) =>
        state.Players.Count(player => !player.IsDead && !player.Disconnected);

    private static int CountDead(AmongUsState state) =>
        state.Players.Count(player => player.IsDead && !player.Disconnected);

    private static int CountImpostors(AmongUsState? state) =>
        state?.Players.Count(player => player.IsImpostor && !player.Disconnected) ?? -1;

    private static bool IsProcess64Bit(Process process)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        return IsWow64Process(process.Handle, out var wow64) && !wow64;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);

    internal sealed record ProcessReadResult(
        int ProcessId,
        bool Is64Bit,
        AmongUsState? State,
        string Diagnostic,
        string? Error);
}

internal sealed record GameScanExpectation(
    string? GameState,
    int? Alive,
    int? Dead,
    int? Impostors,
    bool ExpectNearby,
    int Players = 5);
