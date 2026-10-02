using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TanukiBCL.Client;

// TanukiBCL 3.2.7 GamePlatform.ts and REQUEST_PLATFORMS_AVAILABLE.
internal sealed record GameLaunchPlatform(string Key, string Name, string LaunchType,
    string RunPath, string[] Execute, bool IsDefault = false)
{
    public bool IsValid
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Name) &&
                    (LaunchType == "URI" && Uri.TryCreate(RunPath, UriKind.Absolute, out _) ||
                     LaunchType == "EXE" && Path.IsPathFullyQualified(RunPath) &&
                     Execute is { Length: > 0 } && !string.IsNullOrWhiteSpace(Execute[0]) &&
                     !Path.IsPathRooted(Execute[0]) && Path.GetFileName(Execute[0]) == Execute[0]);
            }
            catch (ArgumentException) { return false; }
        }
    }
}

internal static class GameLauncher
{
    internal static IReadOnlyList<GameLaunchPlatform> Available(
        IReadOnlyDictionary<string, GameLaunchPlatform> custom)
    {
        var available = new List<GameLaunchPlatform>();
        if (HasProtocol("steam"))
            available.Add(new("STEAM", "Steam", "URI", "steam://rungameid/945360", [""], true));
        if (HasProtocol("com.epicgames.launcher"))
            available.Add(new("EPIC", "Epic Games", "URI",
                "com.epicgames.launcher://apps/963137e4c29d4c79a81323b8fab03a40?action=launch&silent=true",
                [""], true));
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            var amongUsPackage = packages?.GetSubKeyNames().FirstOrDefault(name =>
                name.StartsWith("Innersloth.AmongUs", StringComparison.OrdinalIgnoreCase));
            if (amongUsPackage is not null)
            {
                using var package = packages!.OpenSubKey(amongUsPackage);
                if (package?.GetValue("PackageRootFolder") is string root &&
                    File.Exists(Path.Combine(root, "Among Us.exe")))
                    available.Add(new("MICROSOFT", "Microsoft Store", "EXE", root, ["Among Us.exe"], true));
            }
        }
        catch (System.Security.SecurityException) { /* The store is unavailable for this user. */ }
        catch (UnauthorizedAccessException) { /* The store is unavailable for this user. */ }
        foreach (var (key, platform) in custom)
        {
            if (platform is null || platform.Key != key || !platform.IsValid ||
                available.Any(existing => existing.Key == key)) continue;
            if (platform.LaunchType == "URI" ||
                File.Exists(Path.Combine(platform.RunPath, platform.Execute[0])))
                available.Add(platform);
        }
        return available;
    }

    private static bool HasProtocol(string protocol)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(protocol);
            return key?.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static void Launch(GameLaunchPlatform platform)
    {
        if (!platform.IsValid) throw new InvalidDataException("ゲーム起動先が不正です。");
        if (platform.LaunchType == "URI")
        {
            Process.Start(new ProcessStartInfo(platform.RunPath) { UseShellExecute = true });
            return;
        }
        var executable = Path.GetFullPath(Path.Combine(platform.RunPath, platform.Execute[0]));
        if (!File.Exists(executable)) throw new FileNotFoundException("ゲーム実行ファイルがありません。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = platform.RunPath
        };
        foreach (var argument in platform.Execute.Skip(1)) start.ArgumentList.Add(argument);
        Process.Start(start);
    }

    internal static void Verify()
    {
        var custom = new GameLaunchPlatform("custom", "NoS", "EXE", @"C:\Games\NoS", ["Among Us.exe"]);
        if (!custom.IsValid || new GameLaunchPlatform("bad", "Bad", "EXE", @"C:\Games", ["..\\evil.exe"]).IsValid ||
            new GameLaunchPlatform("bad", "Bad", "URI", "relative-url", []).IsValid)
            throw new InvalidOperationException("Game launch platform validation failed");
        var settings = new ClientSettings { LaunchPlatform = custom.Key,
            CustomPlatforms = new Dictionary<string, GameLaunchPlatform> { [custom.Key] = custom } };
        var restored = settings.Clone();
        restored.Normalize();
        if (restored.LaunchPlatform != custom.Key ||
            !restored.CustomPlatforms.TryGetValue(custom.Key, out var copy) ||
            copy.RunPath != custom.RunPath || !copy.Execute.SequenceEqual(custom.Execute))
            throw new InvalidOperationException("Custom game platform did not survive settings round-trip");
        var available = Available(new Dictionary<string, GameLaunchPlatform>());
        if (available.Any(platform => !platform.IsValid || !platform.IsDefault))
            throw new InvalidOperationException("Detected game launch platform is invalid");
        Console.WriteLine($"[PASS] Game platform validation/settings round-trip; detected: {string.Join(", ", available.Select(item => item.Name))}");
    }
}
