using System.Text.RegularExpressions;

namespace TanukiBCL.VoiceProbe.GameMemory;

public static class TohHostName
{
    private static readonly Regex TagsAndZeroWidth = new("<[^>]*>|[\\u200B-\\u200D\\uFEFF]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Marker = new(@"town\s+of\s+host\s+for\s+e\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasMarker(string? name) =>
        !string.IsNullOrEmpty(name) && Marker.IsMatch(TagsAndZeroWidth.Replace(name, string.Empty));

    // A TOH4E host advertises a decorated name to vanilla guests. Local-game voice
    // identity must use the underlying host name that the MOD host itself hashes.
    public static string ForLocalCode(string name)
    {
        var plain = TagsAndZeroWidth.Replace(name, string.Empty);
        var marker = Marker.Match(plain);
        if (!marker.Success) return name;
        var originalName = plain[..marker.Index].Trim();
        return originalName.Length > 0 ? originalName : name;
    }
}
