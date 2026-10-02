using System.Text.RegularExpressions;

namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class AmongUsModDetector
{
    private static readonly (string Dll, AmongUsModType Mod)[] PreferredLoadedModules =
    [
        ("SuperNewRoles.dll", AmongUsModType.SuperNewRoles),
        ("Nebula.dll", AmongUsModType.NebulaOnTheShip),
        ("TownOfHost_ForE_EM.dll", AmongUsModType.TownOfHostForE),
        ("TownOfHost_ForE.dll", AmongUsModType.TownOfHostForE)
    ];

    public static AmongUsMod Detect(string processPath, IEnumerable<string> loadedModules,
        IEnumerable<string> pluginFiles)
    {
        if (Regex.IsMatch(processPath, @"(^|[\\/])[^\\/]*TOH4E(?:[_-]EM)?[^\\/]*([\\/]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return AmongUsMod.For(AmongUsModType.TownOfHostForE);

        var moduleNames = loadedModules.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (dll, mod) in PreferredLoadedModules)
        {
            if (moduleNames.Contains(dll)) return AmongUsMod.For(mod);
        }

        foreach (var file in pluginFiles)
        {
            var match = AmongUsMod.KnownMods.FirstOrDefault(mod => mod.DllStartsWith is not null &&
                Path.GetFileName(file).Contains(mod.DllStartsWith, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return AmongUsMod.For(AmongUsModType.None);
    }

    public static IEnumerable<string> ReadPluginFiles(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return [];
        var pluginsDirectory = Path.Combine(gameDirectory, "BepInEx", "plugins");
        if (!File.Exists(Path.Combine(gameDirectory, "winhttp.dll")) ||
            !Directory.Exists(pluginsDirectory)) return [];

        try
        {
            return Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
