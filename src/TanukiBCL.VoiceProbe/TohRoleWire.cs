using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class TohRoleWire
{
    public static string Serialize(string lobbyCode, Player player,
        IReadOnlyList<Player>? impostorPlayers = null)
    {
        Dictionary<string, object?>? role = null;
        if (player.TohRole is { } value)
        {
            role = new Dictionary<string, object?>
            {
                ["roleId"] = value.RoleId,
                ["roleName"] = value.RoleName,
                ["isNeutralKiller"] = value.IsNeutralKiller,
                ["isKiller"] = value.IsKiller,
                ["customRoleType"] = value.CustomRoleType
            };
            if (value.OpportunistCanKill is { } canKill)
                role["opportunistCanKill"] = canKill;
        }
        return JsonSerializer.Serialize(new
        {
            type = "toh4e-role", lobbyCode,
            targetClientId = player.ClientId, targetPlayerId = player.Id, role,
            impostors = impostorPlayers?.Where(candidate => !candidate.Disconnected).Select(candidate => new
            {
                playerId = candidate.Id, clientId = candidate.ClientId,
                isImpostor = TohRoleCatalog.IsImpostor(candidate)
            }).ToArray()
        });
    }

    public static bool TryRead(JsonElement data, out TohRoleData? parsed)
    {
        parsed = null;
        if (!data.TryGetProperty("role", out var role)) return false;
        if (role.ValueKind == JsonValueKind.Null) return true;
        if (role.ValueKind != JsonValueKind.Object ||
            !role.TryGetProperty("roleId", out var id) || !id.TryGetInt32(out var roleId) ||
            !role.TryGetProperty("roleName", out var name) ||
            name.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
            !role.TryGetProperty("isNeutralKiller", out var neutral) ||
            neutral.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) ||
            !role.TryGetProperty("isKiller", out var killer) ||
            killer.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            return false;
        bool? canKill = null;
        string? faction = null;
        if (role.TryGetProperty("customRoleType", out var team))
        {
            if (team.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
            faction = team.ValueKind == JsonValueKind.String ? team.GetString() : null;
            if (faction is not null && faction is not
                ("Impostor" or "Madmate" or "Crewmate" or "Neutral" or "Animals")) return false;
        }
        if (role.TryGetProperty("opportunistCanKill", out var opportunist))
        {
            if (opportunist.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            canKill = opportunist.GetBoolean();
        }
        parsed = new TohRoleData(roleId,
            name.ValueKind == JsonValueKind.Null ? null : name.GetString(),
            neutral.ValueKind == JsonValueKind.Null ? null : neutral.GetBoolean(),
            killer.ValueKind == JsonValueKind.Null ? null : killer.GetBoolean(), canKill, faction);
        return true;
    }
}
