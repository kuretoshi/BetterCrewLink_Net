using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class LocalJammingPolicy
{
    // Matches released 3.2.8 VoiceController.onGameState -> AudioController.setJammed.
    public static bool IsJammed(AmongUsState? state, LobbySettings settings) =>
        state?.Mod == AmongUsModType.NebulaOnTheShip &&
        settings.NosFixerJammingVoiceBlock &&
        state.Players.Any(player => player.IsLocal && player.NosPlayer?.IsJammed == true);
}

internal static class LocalVadVisibilityPolicy
{
    // Released 3.2.8 hides the talking indicator for a jammed local NoS player
    // or a shifted player outside discussion, without muting shifted speech.
    public static bool IsHidden(AmongUsState? state, LobbySettings settings)
    {
        var local = state?.Players.FirstOrDefault(player => player.IsLocal);
        if (local is null) return false;
        return LocalJammingPolicy.IsJammed(state, settings) ||
            (state!.GameState != GameState.Discussion && local.ShiftedColor != -1);
    }
}
