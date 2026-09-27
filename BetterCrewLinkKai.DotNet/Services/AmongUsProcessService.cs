using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class AmongUsProcessService : IDisposable
{
    private CancellationTokenSource? cancellationTokenSource;
    private Task? monitorTask;
    private int? currentProcessId;

    public event EventHandler<AmongUsProcessInfo?>? ProcessChanged;

    public AmongUsProcessInfo? Current { get; private set; }

    public static bool IsAmongUsForeground()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        foreach (var process in Process.GetProcessesByName("Among Us"))
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle != 0 && handle == foregroundWindow)
                {
                    return true;
                }
            }
            catch
            {
                // Access can fail when the app is not elevated but the game is.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    public void Start()
    {
        if (monitorTask is { IsCompleted: false })
        {
            return;
        }

        cancellationTokenSource = new CancellationTokenSource();
        monitorTask = MonitorAsync(cancellationTokenSource.Token);
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        cancellationTokenSource?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (!cancellationToken.IsCancellationRequested)
        {
            var next = currentProcessId is { } lockedProcessId
                ? TryGetAmongUsById(lockedProcessId) ?? TryFindAmongUs()
                : TryFindAmongUs();
            var nextProcessId = next?.ProcessId;

            if (nextProcessId != currentProcessId)
            {
                currentProcessId = nextProcessId;
                Current = next;
                ProcessChanged?.Invoke(this, next);
            }

            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static AmongUsProcessInfo? TryFindAmongUs()
    {
        var foregroundWindow = GetForegroundWindow();
        var candidates = Process.GetProcessesByName("Among Us")
            .OrderByDescending(process => process.MainWindowHandle != 0 && process.MainWindowHandle == foregroundWindow)
            .ThenByDescending(process => process.MainWindowHandle != 0)
            .ThenByDescending(process =>
            {
                try
                {
                    return process.StartTime;
                }
                catch
                {
                    return DateTime.MinValue;
                }
            })
            .ToArray();

        foreach (var process in candidates)
        {
            try
            {
                return CreateProcessInfo(process);
            }
            catch
            {
                // Access can fail when the app is not elevated but the game is.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static AmongUsProcessInfo? TryGetAmongUsById(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || !string.Equals(process.ProcessName, "Among Us", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return CreateProcessInfo(process);
        }
        catch
        {
            return null;
        }
    }

    private static AmongUsMod DetectInstalledMod(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return AmongUsMod.KnownMods[0];
        }

        var pluginsDirectory = Path.Combine(gameDirectory, "BepInEx", "plugins");
        if (!File.Exists(Path.Combine(gameDirectory, "winhttp.dll")) || !Directory.Exists(pluginsDirectory))
        {
            return AmongUsMod.KnownMods[0];
        }

        foreach (var file in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            var mod = AmongUsMod.KnownMods.FirstOrDefault(candidate =>
                candidate.DllStartsWith is not null &&
                fileName.StartsWith(candidate.DllStartsWith, StringComparison.OrdinalIgnoreCase));

            if (mod is not null)
            {
                return mod;
            }
        }

        return AmongUsMod.KnownMods.First(static mod => mod.Id == AmongUsModType.Other);
    }

    private static AmongUsProcessInfo CreateProcessInfo(Process process)
    {
        var processPath = process.MainModule?.FileName ?? string.Empty;
        var gameDirectory = Path.GetDirectoryName(processPath) ?? string.Empty;
        var hasGameAssembly = process.Modules
            .Cast<ProcessModule>()
            .Any(static module => string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));

        return new AmongUsProcessInfo
        {
            ProcessId = process.Id,
            ProcessPath = processPath,
            GameDirectory = gameDirectory,
            HasGameAssembly = hasGameAssembly,
            Is64Bit = IsProcess64Bit(process),
            InstalledMod = DetectInstalledMod(gameDirectory)
        };
    }

    private static bool IsProcess64Bit(Process process)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        return IsWow64Process(process.Handle, out var isWow64) && !isWow64;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
