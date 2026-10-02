using System.Diagnostics;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class NosSnapshotDiagnostic
{
    public static async Task<int> RunAsync(int? processId)
    {
        if (processId is null)
        {
            Console.Error.WriteLine("--nos-snapshot には --game-process-id が必要です");
            return 1;
        }

        using var process = Process.GetProcessById(processId.Value);
        var info = GameProcessScanner.CreateProcessInfo(process);
        if (info.InstalledMod.Id != AmongUsModType.NebulaOnTheShip)
        {
            Console.Error.WriteLine($"PID {processId}: NoSを検出できません ({info.InstalledMod.Label})");
            return 1;
        }

        using var reader = new AmongUsMemoryReaderService();
        var completion = new TaskCompletionSource<AmongUsState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = string.Empty;
        reader.DiagnosticChanged += (_, value) => diagnostic = value;
        reader.StateChanged += (_, state) =>
        {
            if (state.NosLocalMicPosition is not null && state.Players.Any(player => player.NosPlayer is not null))
                completion.TrySetResult(state);
        };
        reader.SetProcess(info);
        reader.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var state = await completion.Task.WaitAsync(timeout.Token);
            Console.WriteLine($"[PASS] NoS snapshot PID={processId} state={state.GameState} " +
                $"lobby={state.LobbyCode} mic=({state.NosLocalMicPosition!.X:0.000}," +
                $"{state.NosLocalMicPosition.Y:0.000}) players={state.Players.Count} radios={state.NosRadios.Count}");
            foreach (var player in state.Players.Where(player => player.NosPlayer is not null))
            {
                var nos = player.NosPlayer!;
                Console.WriteLine($"  id={player.Id} client={player.ClientId} name={nos.Name} " +
                    $"impostor={nos.IsImpostor} neutral={nos.IsNeutral} jammed={nos.IsJammed} " +
                    $"speaker=({nos.SpeakerPositionX:0.000},{nos.SpeakerPositionY:0.000})");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"[FAIL] NoS snapshot timeout PID={processId}: {diagnostic}");
            return 1;
        }
        finally
        {
            reader.Stop();
        }
    }
}
