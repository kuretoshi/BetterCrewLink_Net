using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL 3.2.8 VoiceController.getVisibleRadioClientIds / areRadioPartners.
// This is a display decision, independent of receiver volume and audio mix.
internal static class RadioVisibilityPolicy
{
    public static bool IsVisible(AmongUsState state, LobbySettings settings, int clientId,
        bool remoteRadioActive, Func<Player, bool> hasNosJackalRadio,
        Func<Player, Player, bool> canNosJackalRadioReach)
    {
        if (!remoteRadioActive) return false;
        var local = state.Players.FirstOrDefault(player => player.IsLocal);
        var sender = state.Players.FirstOrDefault(player => player.ClientId == clientId);
        if (local is null || sender is null || sender.IsLocal || sender.IsDead ||
            sender.Disconnected || sender.Bugged) return false;

        bool IsSnrJackal(Player player) =>
            state.Mod == AmongUsModType.SuperNewRoles && player.SnrRole?.IsJackalTeam == true;
        bool JackalChannelEnabled() => settings.JackalRadioEnabled && !settings.ImpostorRadioOnlyMode;
        bool CanUseRadio(Player player) => hasNosJackalRadio(player) || IsSnrJackal(player)
            ? JackalChannelEnabled()
            : player.IsImpostor && (settings.ImpostorRadioEnabled || settings.ImpostorRadioOnlyMode);
        if (!CanUseRadio(local) || !CanUseRadio(sender)) return false;

        if (state.Mod == AmongUsModType.NebulaOnTheShip && hasNosJackalRadio(sender))
            return JackalChannelEnabled() && canNosJackalRadioReach(sender, local);

        var teammates = state.Mod == AmongUsModType.NebulaOnTheShip && hasNosJackalRadio(local)
            ? canNosJackalRadioReach(local, sender)
            : IsSnrJackal(local)
                ? IsSnrJackal(sender)
                : local.IsImpostor && sender.IsImpostor && !IsSnrJackal(sender);
        if (!teammates) return false;
        return IsSnrJackal(local) ? JackalChannelEnabled()
            : settings.ImpostorRadioEnabled || settings.ImpostorRadioOnlyMode;
    }
}
