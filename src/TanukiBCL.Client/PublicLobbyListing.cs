using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

internal sealed record PublicLobbyListing(
    int Id, string Title, string Host, int CurrentPlayers, int MaxPlayers,
    string Language, string Mod, bool IsPublic, int GameState, long StateTime)
{
    internal bool CanShowCode(AmongUsModType installedMod) =>
        GameState == 0 && CurrentPlayers != MaxPlayers && Mod == ModId(installedMod);

    internal string ModName => AmongUsMod.KnownMods.FirstOrDefault(mod => ModId(mod.Id) == Mod)?.Label ?? Mod;

    internal string LanguageName => UiLocalization.Languages.FirstOrDefault(language => language.Code == Language)?.Name ?? "English";

    internal string Status(long nowMilliseconds)
    {
        var state = GameState == 0 ? "Lobby" : "In game";
        if (StateTime <= 0) return state;
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, nowMilliseconds - StateTime));
        return $"{state} {((int)elapsed.TotalMinutes) % 60:00}:{elapsed.Seconds:00}";
    }

    internal static IReadOnlyList<PublicLobbyListing> Sort(IEnumerable<PublicLobbyListing> lobbies) =>
        lobbies.OrderBy(lobby => lobby.GameState == 0 ? 0 : 1)
            .ThenByDescending(lobby => lobby.CurrentPlayers == lobby.MaxPlayers)
            .ThenByDescending(lobby => lobby.CurrentPlayers)
            .ToArray();

    internal static bool TryParse(JsonElement element, out PublicLobbyListing? lobby)
    {
        lobby = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryInt(element, "id", out var id) ||
            !TryInt(element, "current_players", out var currentPlayers) ||
            !TryInt(element, "max_players", out var maxPlayers) ||
            !TryInt(element, "gameState", out var gameState) ||
            id < 0 || currentPlayers < 0 || maxPlayers < 0)
            return false;
        lobby = new PublicLobbyListing(id, String(element, "title"), String(element, "host"),
            currentPlayers, maxPlayers, String(element, "language"), String(element, "mods"),
            element.TryGetProperty("isPublic", out var publicValue) && publicValue.ValueKind == JsonValueKind.True,
            gameState, TryLong(element, "stateTime", out var stateTime) ? stateTime : 0);
        return true;
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : string.Empty;

    internal static string ModId(AmongUsModType mod) => mod switch
    {
        AmongUsModType.TownOfHostForE => "TOH4E",
        AmongUsModType.SuperNewRoles => "SUPER_NEW_ROLES",
        AmongUsModType.TownOfUsMira => "TOWN_OF_US_MIRA",
        AmongUsModType.TownOfUs => "TOWN_OF_US",
        AmongUsModType.TheOtherRoles => "THE_OTHER_ROLES",
        AmongUsModType.LasMonjas => "LAS_MONJAS",
        AmongUsModType.NebulaOnTheShip => "NoS",
        AmongUsModType.Other => "OTHER",
        _ => "NONE"
    };

    internal static void Verify()
    {
        using var document = JsonDocument.Parse("""
            {"id":42,"title":"test","host":"owner","current_players":3,"max_players":4,
             "language":"ja","mods":"NoS","isPublic":true,"gameState":0,"stateTime":1234}
            """);
        if (!TryParse(document.RootElement, out var lobby) || lobby is null ||
            !lobby.CanShowCode(AmongUsModType.NebulaOnTheShip) ||
            lobby.CanShowCode(AmongUsModType.None) ||
            lobby.Status(62_234) != "Lobby 01:01" ||
            lobby.ModName != "Nebula on the Ship" ||
            lobby.LanguageName != UiLocalization.Languages.First(language => language.Code == "ja").Name)
            throw new InvalidOperationException("Public lobby payload, eligibility, or labels differ from 3.2.8");
        var ordered = Sort([
            lobby with { Id = 1, GameState = 1, CurrentPlayers = 4 },
            lobby with { Id = 2, CurrentPlayers = 4 },
            lobby with { Id = 3, CurrentPlayers = 2 },
            lobby
        ]);
        if (string.Join(',', ordered.Select(item => item.Id)) != "2,42,3,1" ||
            ordered[0].CanShowCode(AmongUsModType.NebulaOnTheShip))
            throw new InvalidOperationException("Public lobby sorting or full-lobby protection differs from 3.2.8");
    }
}
