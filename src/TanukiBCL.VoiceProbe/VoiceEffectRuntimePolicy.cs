using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed record SelectedVoiceEffect(VoiceEffectTuning Tuning, double Grit,
    bool PushActive, VoiceEffectTuning BibiriTuning);

internal static class VoiceEffectRuntimePolicy
{
    private static readonly VoiceEffectStrengths ActivationStrengths = new();

    public static bool ShouldRecordBibiri(AmongUsState state, VoiceEffectTuning tuning) =>
        TanukiAddon.IsEnabled(state) && !tuning.IsNeutral &&
        state.GameState is GameState.Tasks or GameState.Discussion;

    public static SelectedVoiceEffect Select(AmongUsState state, Player listener, Player speaker,
        LobbySettings lobby, SpatialVoiceSettings spatial, PeerVoiceMix mix,
        VoiceEffectStrengths strengths, int disguiseStrength, bool radioActive)
    {
        // Strength no longer decides whether an ability is active. Keep the
        // released policy's priority and gates, then resolve its personal axes.
        var activationLobby = ActivationStrengths.ApplyTo(lobby);
        var effect = VoiceDisguiseEffectPolicy.Select(state, listener, speaker, activationLobby,
            100, mix.Audible, radioActive);
        var selected = effect is null ? new VoiceEffectTuning() : strengths.Tuning(effect.EffectId, disguiseStrength);
        var star = mix.Audible && VoiceDisguiseEffectPolicy.ShouldApplyRainbowStarEcho(state, speaker, activationLobby)
            ? strengths.Tuning("nosRainbowStar") : new VoiceEffectTuning();
        var pushActive = mix.Audible && spatial.SnrPushReverb &&
            state.Mod == AmongUsModType.SuperNewRoles && state.GameState == GameState.Tasks &&
            speaker.SnrPushedFalling && speaker.IsDead && !speaker.Disconnected && !speaker.IsDummy &&
            !state.AirshipMeetingByOutfit;
        var push = pushActive ? strengths.Tuning("snrPush") : new VoiceEffectTuning();
        return new SelectedVoiceEffect(VoiceEffectTunings.Combine([selected, star, push]),
            effect?.EffectId == "nosBerserker" ? VoiceEffectChain.GritAmount(selected) : 0,
            pushActive, strengths.Tuning("nosBibiri"));
    }
}
