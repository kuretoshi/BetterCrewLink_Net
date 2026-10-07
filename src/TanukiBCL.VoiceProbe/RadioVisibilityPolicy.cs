using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

// NoS indicators follow the selected sender channel and its listener mask.
internal static class RadioVisibilityPolicy
{
    public static bool IsVisible(AmongUsState state, LobbySettings settings, int clientId,
        bool remoteRadioActive, Func<Player, Player, bool> canNosRadioReach)
    {
        if (!remoteRadioActive) return false;
        var local = state.Players.FirstOrDefault(player => player.IsLocal);
        var sender = state.Players.FirstOrDefault(player => player.ClientId == clientId);
        if (local is null || sender is null || sender.IsLocal || sender.IsDead ||
            sender.Disconnected || sender.Bugged) return false;

        if (state.Mod == AmongUsModType.NebulaOnTheShip)
            return canNosRadioReach(sender, local);

        var jackalEnabled = settings.JackalRadioEnabled && !settings.ImpostorRadioOnlyMode;
        if (state.Mod == AmongUsModType.SuperNewRoles && sender.SnrRole?.IsJackalTeam == true)
            return jackalEnabled && !local.IsDead && local.SnrRole?.IsJackalTeam == true;

        return sender.IsImpostor && !local.IsDead && local.IsImpostor &&
            (settings.ImpostorRadioEnabled || settings.ImpostorRadioOnlyMode);
    }
}
