using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class NosMegaphoneVoice
{
    public static NosMegaphoneState? Charging(AmongUsState state, Player speaker) =>
        state.Mod == AmongUsModType.NebulaOnTheShip && state.GameState == GameState.Tasks &&
        !speaker.IsDead && !speaker.Disconnected && !speaker.IsDummy && !speaker.Bugged
            ? state.NosMegaphones.FirstOrDefault(item => item.PlayerId == speaker.Id) : null;

    public static double PortableExtraRange(AmongUsState state, Player speaker) =>
        state.Mod == AmongUsModType.NebulaOnTheShip && state.GameState == GameState.Tasks &&
        !speaker.IsDead && !speaker.Disconnected && !speaker.IsDummy && !speaker.Bugged
            ? state.NosPortableMegaphones.FirstOrDefault(item => item.PlayerId == speaker.Id)?.ExtraRange ?? 0d
            : 0d;

    public static double Gain(double range, double deltaX, double deltaY) =>
        !double.IsFinite(range) || range <= 0d ? 0d :
        Math.Clamp((range - Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) / Math.Min(2d, range), 0d, 1d);
}
