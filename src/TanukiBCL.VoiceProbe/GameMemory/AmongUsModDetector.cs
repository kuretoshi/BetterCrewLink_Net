using System.Text.RegularExpressions;

namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class AmongUsModDetector
{
    private static readonly (string Dll, AmongUsModType Mod)[] PreferredLoadedModules =
    [
        ("SuperNewRoles.dll", AmongUsModType.SuperNewRoles),
        ("Nebula.dll", AmongUsModType.NebulaOnTheShip),
        ("TownOfHost_ForE_EM.dll", AmongUsModType.TownOfHostForE),
        ("TownOfHost_ForE.dll", AmongUsModType.TownOfHostForE),
        ("TownOfHostForE_EM.dll", AmongUsModType.TownOfHostForE),
        ("TownOfHostForE.dll", AmongUsModType.TownOfHostForE)
    ];

    public static AmongUsMod Detect(string processPath, IEnumerable<string> loadedModules,
        IEnumerable<string> pluginFiles)
    {
        var moduleNames = loadedModules.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (dll, mod) in PreferredLoadedModules)
        {
            if (moduleNames.Contains(dll)) return AmongUsMod.For(mod);
        }

        if (Regex.IsMatch(processPath, @"(^|[\\/])[^\\/]*TOH4E(?:[_-]EM)?[^\\/]*([\\/]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return AmongUsMod.For(AmongUsModType.TownOfHostForE);

        foreach (var file in pluginFiles)
        {
            if (IsToh4eDll(file)) return AmongUsMod.For(AmongUsModType.TownOfHostForE);

            var match = AmongUsMod.KnownMods.FirstOrDefault(mod => mod.Id != AmongUsModType.TownOfHostForE &&
                mod.DllStartsWith is not null &&
                Path.GetFileName(file).Contains(mod.DllStartsWith, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return AmongUsMod.For(AmongUsModType.None);
    }

    internal static bool IsToh4eDll(string file)
    {
        var name = Path.GetFileName(file).Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return name.Equals("TownOfHostForE.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("TownOfHostForEEM.dll", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> ReadPluginFiles(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return [];
        var pluginsDirectory = Path.Combine(gameDirectory, "BepInEx", "plugins");
        if (!File.Exists(Path.Combine(gameDirectory, "winhttp.dll")) ||
            !Directory.Exists(pluginsDirectory)) return [];

        try
        {
            return Directory.EnumerateFiles(pluginsDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(file => Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
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
