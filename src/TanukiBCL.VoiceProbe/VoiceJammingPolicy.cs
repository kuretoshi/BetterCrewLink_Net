using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class VoiceJammingPolicy
{
    public static NosVoiceJammingState? For(AmongUsState? state, int id) =>
        state is { Mod: AmongUsModType.NebulaOnTheShip, GameState: GameState.Discussion }
            ? state.NosVoiceJamming.FirstOrDefault(entry => entry.PlayerId == id && entry.IsValid) : null;
}
