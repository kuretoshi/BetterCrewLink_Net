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

    internal static IReadOnlyList<PublicLobbyListing> Sort(IEnumerable<PublicLobbyListing> lobbies)
    {
        var sorted = lobbies.ToArray();
        if (sorted.Length < 2) return sorted;

        // The released comparator is not symmetric for mixed full/non-full
        // lobbies. Preserve its comparisons and V8's short-array natural-run
        // plus binary-insertion behavior instead of substituting a new order.
        var descending = Compare(sorted[1], sorted[0]) < 0;
        var runLength = 2;
        while (runLength < sorted.Length)
        {
            var comparison = Compare(sorted[runLength], sorted[runLength - 1]);
            if (descending ? comparison >= 0 : comparison < 0) break;
            runLength++;
        }
        if (descending) Array.Reverse(sorted, 0, runLength);

        for (var index = runLength; index < sorted.Length; index++)
        {
            var item = sorted[index];
            var low = 0;
            var high = index;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (Compare(item, sorted[middle]) < 0) high = middle;
                else low = middle + 1;
            }
            Array.Copy(sorted, low, sorted, low + 1, index - low);
            sorted[low] = item;
        }
        return sorted;
    }

    private static int Compare(PublicLobbyListing a, PublicLobbyListing b)
    {
        if (a.GameState == 0 && b.GameState != 0) return -1;
        if (b.GameState == 0 && a.GameState != 0) return 1;
        if (b.CurrentPlayers == b.MaxPlayers && a.CurrentPlayers != a.MaxPlayers) return -1;
        return a.CurrentPlayers < b.CurrentPlayers ? 1 : a.CurrentPlayers > b.CurrentPlayers ? -1 : 0;
    }

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
        if (string.Join(',', ordered.Select(item => item.Id)) != "42,3,2,1" ||
            ordered.Single(item => item.Id == 2).CanShowCode(AmongUsModType.NebulaOnTheShip))
            throw new InvalidOperationException("Public lobby sorting or full-lobby protection differs from 3.2.8");
        var alternate = Sort([
            lobby with { Id = 3, CurrentPlayers = 2 },
            lobby with { Id = 2, CurrentPlayers = 4 },
            lobby,
            lobby with { Id = 1, GameState = 1, CurrentPlayers = 4 }
        ]);
        if (string.Join(',', alternate.Select(item => item.Id)) != "42,2,3,1")
            throw new InvalidOperationException("Public lobby sort lost the released input-order behavior");
        var longer = Sort(Enumerable.Range(0, 32).Select(id => lobby with
        {
            Id = id,
            GameState = id % 7 == 0 ? 1 : 0,
            CurrentPlayers = id * 5 % 6 + 1,
            MaxPlayers = 6
        }));
        if (string.Join(',', longer.Select(item => item.Id)) !=
            "13,19,25,31,2,8,20,26,3,9,15,27,4,10,16,22,5,11,17,23,29,6,12,18,24,30,1,14,21,28,7,0")
            throw new InvalidOperationException("Public lobby short-array order differs from the released renderer");
    }
}
