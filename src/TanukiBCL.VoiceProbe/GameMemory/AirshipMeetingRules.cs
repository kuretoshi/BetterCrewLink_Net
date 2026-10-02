namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class AirshipMeetingRules
{
    // v3.2.7 GameReader.ts: all active players wear the meeting outfit, but
    // no player has a genuinely disguised appearance.
    public static bool IsMeetingByOutfit(GameState phase, MapType map, IReadOnlyList<Player> players)
    {
        if (phase != GameState.Tasks || map != MapType.Airship) return false;
        var active = players.Where(player => !player.Disconnected).ToArray();
        return active.Length >= 2 && active.All(player => player.CurrentOutfit == 1) &&
            !active.Any(player => player.HasVisibleAppearanceChanged());
    }
}
