using System.Text.RegularExpressions;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed record TohRoleDefinition(int RoleId, string RoleName, string DisplayName,
    string CustomRoleType, bool? IsKiller);

internal static class TohRoleCatalog
{
    private static readonly HashSet<string> Teams = ["Impostor", "Madmate", "Crewmate", "Neutral", "Animals"];

    public static bool IsValid(IReadOnlyList<TohRoleDefinition>? roles)
    {
        if (roles is null || roles.Count > 1024) return false;
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (role is null || string.IsNullOrEmpty(role.RoleName) ||
                !ids.Add(role.RoleId) || !names.Add(role.RoleName) ||
                !Regex.IsMatch(role.RoleName, "^[A-Za-z][A-Za-z0-9_]{0,127}$") ||
                string.IsNullOrWhiteSpace(role.DisplayName) || role.DisplayName.Length > 256 ||
                !Teams.Contains(role.CustomRoleType)) return false;
        }
        return true;
    }

    public static bool IsImpostor(Player player) => player.TohRole is { } role
        ? player.IsImpostor && role.CustomRoleType == "Impostor"
        : player.IsImpostor && player.TohImpostor == true;
}
