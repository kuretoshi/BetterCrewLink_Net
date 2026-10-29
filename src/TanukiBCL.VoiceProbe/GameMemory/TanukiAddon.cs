namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class TanukiAddon
{
    public static bool IsEnabled(AmongUsState? state) =>
        state?.Mod == AmongUsModType.NebulaOnTheShip &&
        state.NosAddonIds.Contains("tanuki.bcl.roles", StringComparer.Ordinal);
}
