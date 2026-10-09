using System.Diagnostics;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class GameReaderAllocationProfile
{
    internal static async Task<int> RunAsync(int? gameProcessId, TimeSpan duration)
    {
        if (gameProcessId is null) throw new ArgumentException("--game-process-id is required.");
        using var gameProcess = Process.GetProcessById(gameProcessId.Value);
        using var reader = new AmongUsMemoryReaderService();
        var stateCount = 0;
        var snapshotCount = 0;
        var nosRoleCount = 0;
        string? nosRoleStatus = null;
        string? nosAddons = null;
        reader.StateChanged += (_, state) =>
        {
            Interlocked.Increment(ref stateCount);
            if (state.NosLocalMicPosition is not null) Interlocked.Increment(ref snapshotCount);
            if (state.Mod == AmongUsModType.NebulaOnTheShip)
            {
                Volatile.Write(ref nosRoleCount, state.Players.Count(player => player.NosRole is not null));
                Volatile.Write(ref nosRoleStatus, state.NosRoleStatus);
                Volatile.Write(ref nosAddons, string.Join(',', state.NosAddonIds));
            }
        };
        reader.SetProcess(GameProcessScanner.CreateProcessInfo(gameProcess));
        reader.Start();
        await Task.Delay(TimeSpan.FromSeconds(10));
        var firstCount = Volatile.Read(ref stateCount);
        var firstSnapshots = Volatile.Read(ref snapshotCount);
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var watch = Stopwatch.StartNew();
        await Task.Delay(duration);
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
        reader.Stop();
        Console.WriteLine($"gameReader pid={gameProcessId} states={Volatile.Read(ref stateCount) - firstCount} " +
            $"nosSnapshots={Volatile.Read(ref snapshotCount) - firstSnapshots} " +
            $"nosRoles={Volatile.Read(ref nosRoleCount)} nosRoleStatus={Volatile.Read(ref nosRoleStatus)} " +
            $"nosAddons={Volatile.Read(ref nosAddons)} " +
            $"seconds={watch.Elapsed.TotalSeconds:0.0} allocatedMiB={allocated / 1048576d:0.00} " +
            $"rateMiBPerSecond={allocated / 1048576d / watch.Elapsed.TotalSeconds:0.000}");
        return 0;
    }
}
