using System.Diagnostics;
using System.Runtime.InteropServices;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class GameProcessScanner
{
    public static async Task<int> RunAsync(TimeSpan timeout)
    {
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
        var tasks = processes.Select(process => ReadProcessAsync(process, cancellation.Token)).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        PrintResults(results);
        return Validate(results) ? 0 : 1;
    }

    private static async Task<ProcessReadResult> ReadProcessAsync(Process process, CancellationToken cancellationToken)
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
                if (state.GameState is GameState.Tasks or GameState.Discussion && state.Players.Count > 0)
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
        var hasGameAssembly = process.Modules.Cast<ProcessModule>()
            .Any(module => string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        return new AmongUsProcessInfo
        {
            ProcessId = process.Id,
            ProcessPath = processPath,
            GameDirectory = Path.GetDirectoryName(processPath) ?? string.Empty,
            HasGameAssembly = hasGameAssembly,
            Is64Bit = IsProcess64Bit(process),
            InstalledMod = AmongUsMod.KnownMods[0]
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

    private static bool Validate(IReadOnlyCollection<ProcessReadResult> results)
    {
        var states = results.Where(result => result.State is not null).Select(result => result.State!).ToArray();
        var localPlayers = states.Select(state => state.Players.SingleOrDefault(player => player.IsLocal)).Where(player => player is not null).ToArray();
        var passed = results.Count == 5 &&
                     states.Length == 5 &&
                     states.Select(state => state.LobbyCode).Distinct(StringComparer.Ordinal).Count() == 1 &&
                     states.All(state => state.Players.Count == 5) &&
                     states.All(state => state.Players.Count(player => player.IsDead && !player.Disconnected) == 1) &&
                     states.All(state => state.Players.Count(player => player.IsImpostor && !player.Disconnected) == 1) &&
                     localPlayers.Length == 5 &&
                     localPlayers.Select(player => player!.ClientId).Distinct().Count() == 5 &&
                     localPlayers.Count(player => player!.IsImpostor) == 1;

        Console.WriteLine(passed
            ? "[PASS] 5プロセス、4生存/1死亡、4クルーメイト/1インポスターを全視点で確認しました。"
            : "[FAIL] 期待した5プロセスのゲーム状態と一致しません。上のPID別結果を確認してください。");
        return passed;
    }

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

    private sealed record ProcessReadResult(
        int ProcessId,
        bool Is64Bit,
        AmongUsState? State,
        string Diagnostic,
        string? Error);
}
