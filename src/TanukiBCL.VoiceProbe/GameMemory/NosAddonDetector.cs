using System.IO.Compression;
using System.Text.RegularExpressions;

namespace TanukiBCL.VoiceProbe.GameMemory;

internal static partial class NosAddonDetector
{
    [GeneratedRegex("\"Id\"\\s*:\\s*\"([A-Za-z0-9_.-]{1,128})\"", RegexOptions.CultureInvariant)]
    private static partial Regex AddonIdPattern();

    public static List<string> Read(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory)) return [];
        var directory = Path.Combine(gameDirectory, "Addons");
        if (!Directory.Exists(directory)) return [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                ReadArchive(file, ids);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Addons are optional; an unreadable directory must not stop game state updates.
        }
        return ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static void ReadArchive(string file, HashSet<string> ids)
    {
        try
        {
            using var archive = ZipFile.OpenRead(file);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.Equals("addon.meta", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.EndsWith("/addon.meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Length is < 1 or > 65536) continue;
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var id = AddonIdPattern().Match(reader.ReadToEnd()).Groups[1].Value;
                if (id.Length > 0) ids.Add(id);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Ignore damaged or inaccessible addon archives.
        }
    }
}
