using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal enum NosSizeEffectMode
{
    Squash,
    PitchUp,
    Jumbo,
    ToneOnly,
    Disguise
}

internal static class VoiceDisguiseEffectPolicy
{
    // TanukiBCL v3.2.7 voiceEffectRules.ts: NoS size effects have priority;
    // ordinary disguises are local-listener effects, not transmitted audio.
    public static NosSizeVoiceEffect? Select(AmongUsState state, Player listener, Player speaker,
        LobbySettings lobby, int strengthPercent, bool audible, bool impostorRadioActive)
    {
        var sizeEffect = NosSizeVoiceEffectPolicy.Select(state, speaker, lobby, audible);
        if (sizeEffect is not null) return sizeEffect;

        // The upstream selector exits entirely for a valid NoS body with zero
        // height; it does not fall through to the ordinary disguise effect.
        if (state.Mod == AmongUsModType.NebulaOnTheShip && lobby.NosSizeVoiceEffect &&
            speaker.NosPlayer is { BodyRateX: { } x, BodyRateY: 0d } &&
            double.IsFinite(x) && x > 0d)
            return null;

        if (!audible || state.GameState != GameState.Tasks || speaker.IsDead ||
            speaker.Disconnected || speaker.IsDummy || listener.IsDead ||
            !lobby.VoiceEffectEnabled || strengthPercent <= 0)
            return null;

        if (impostorRadioActive && listener.IsImpostor && speaker.IsImpostor &&
            (lobby.ImpostorRadioEnabled || lobby.ImpostorRadioOnlyMode))
            return null;

        static bool ChangedName(Player player) =>
            (string.IsNullOrEmpty(player.AppearanceName) ? player.Name : player.AppearanceName) != player.Name;
        if (!ChangedName(speaker) ||
            !state.Players.Any(player => !player.Disconnected && ChangedName(player)))
            return null;

        return new NosSizeVoiceEffect(NosSizeEffectMode.Disguise,
            Math.Clamp(strengthPercent, 0, 100) / 100d, 1d);
    }
}

internal sealed record NosSizeVoiceEffect(NosSizeEffectMode Mode, double Strength, double ToneRate,
    double Squash = 0d);

internal static class NosSizeVoiceEffectPolicy
{
    // TanukiBCL v3.2.7 voiceEffectRules.ts: NoS size effects take precedence
    // over the global disguise switch and also apply to dead listeners.
    public static NosSizeVoiceEffect? Select(AmongUsState state, Player speaker,
        LobbySettings lobby, bool audible)
    {
        var size = speaker.NosPlayer;
        if (!audible || state.GameState != GameState.Tasks ||
            state.Mod != AmongUsModType.NebulaOnTheShip || !lobby.NosSizeVoiceEffect ||
            speaker.IsDead || speaker.Disconnected || speaker.IsDummy ||
            size?.BodyRateX is not { } x || size.BodyRateY is not { } y ||
            !double.IsFinite(x) || !double.IsFinite(y) || x <= 0d || y < 0d)
            return null;

        if (x is >= 0.98d and <= 1.02d && y < 0.98d)
        {
            var squash = Math.Min(1d, 1d - y);
            return new NosSizeVoiceEffect(NosSizeEffectMode.Squash, squash, x, squash);
        }
        if (y == 0d) return null;
        if (y < 0.98d)
            return new NosSizeVoiceEffect(NosSizeEffectMode.PitchUp,
                Math.Min(1d, Math.Log(1d / y) / Math.Log(10d)), x);
        if (y > 1.02d)
            return new NosSizeVoiceEffect(NosSizeEffectMode.Jumbo,
                Math.Min(1d, Math.Log(y) / Math.Log(5d)), x);
        if (x < 0.98d || x > 1.02d)
            return new NosSizeVoiceEffect(NosSizeEffectMode.ToneOnly, 0d, x);
        return null;
    }
}
