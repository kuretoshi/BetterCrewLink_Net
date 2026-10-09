using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal enum NosSizeEffectMode
{
    Squash,
    PitchUp,
    Jumbo,
    ToneOnly,
    Disguise,
    SourceFilter,
    Berserker
}

internal static class VoiceDisguiseEffectPolicy
{
    public static bool ShouldApplyRainbowStarEcho(AmongUsState state, Player speaker, LobbySettings lobby) =>
        state.Mod == AmongUsModType.NebulaOnTheShip && lobby.NosRainbowStarEcho &&
        state.GameState is GameState.Tasks or GameState.Discussion &&
        !speaker.IsDead && !speaker.Disconnected && !speaker.Bugged && !speaker.IsDummy &&
        speaker.NosRole?.IsRainbowStar == true;

    // TanukiBCL v3.2.7 voiceEffectRules.ts: NoS size effects have priority;
    // ordinary disguises are local-listener effects, not transmitted audio.
    public static NosSizeVoiceEffect? Select(AmongUsState state, Player listener, Player speaker,
        LobbySettings lobby, int strengthPercent, bool audible, bool impostorRadioActive)
    {
        if (!audible || speaker.IsDead || speaker.Disconnected || speaker.Bugged || speaker.IsDummy)
            return null;
        if (state.Mod == AmongUsModType.NebulaOnTheShip && lobby.NosCitrusVoiceEffect &&
            state.GameState is GameState.Tasks or GameState.Discussion &&
            speaker.NosPlayer?.Hat?.Name is "noshat_catudon_Citrus_Orange" or "noshat_catudon_Citrus_Lemon")
            return new NosSizeVoiceEffect(NosSizeEffectMode.Disguise, 1d, 1d);
        if (state.GameState != GameState.Tasks ||
            state.Map == MapType.Airship && state.AirshipMeetingByOutfit) return null;
        if (state.Mod == AmongUsModType.NebulaOnTheShip && lobby.NosBerserkerVoiceEffect &&
            speaker.NosRole?.RoleName == "berserker" &&
            (speaker.NosPlayer?.BodyType is { } bodyType
                ? bodyType == 2
                : speaker.NosRole.BodyType == 2 && speaker.NosRole.IsBerserking == true))
            return new NosSizeVoiceEffect(NosSizeEffectMode.Berserker, 1d, 1d);
        if (state.Mod == AmongUsModType.NebulaOnTheShip && lobby.NosRokurokubiVoiceEffect &&
            speaker.NosPlayer is { BodyType: 3, NeckLength: { } length } &&
            double.IsFinite(length) && length > 0d)
        {
            var pitch = 1d + Math.Min(1d, Math.Log(1d + length) / Math.Log(41d));
            var x = speaker.NosPlayer.BodyRateX;
            var formant = x is > 0d && double.IsFinite(x.Value)
                ? Math.Clamp(1d / x.Value, 0.55d, 1.7d) : 1d;
            return new NosSizeVoiceEffect(NosSizeEffectMode.SourceFilter, 0d, 1d,
                Pitch: pitch, Formant: formant);
        }
        var sizeEffect = NosSizeVoiceEffectPolicy.Select(state, speaker, lobby, audible);
        if (sizeEffect is not null) return sizeEffect;

        // 3.2.7 gives SNR Jumbo priority over the ordinary disguise. A role
        // without a valid live size must not fall through to that disguise.
        if (state.Mod == AmongUsModType.SuperNewRoles && lobby.SnrJumboVoice &&
            speaker.SnrRole is { HasJumbo: true } snrRole)
        {
            if (!audible || state.GameState != GameState.Tasks || speaker.IsDead ||
                speaker.Disconnected || speaker.IsDummy)
                return null;
            if (snrRole.JumboCurrentSize is not { } current ||
                snrRole.JumboMaxSize is not { } max ||
                !double.IsFinite(current) || !double.IsFinite(max) ||
                current <= 0d || max <= 0d)
                return null;
            var growth = Math.Min(1d, current / max);
            return new NosSizeVoiceEffect(NosSizeEffectMode.SourceFilter, 0d, 1d,
                Pitch: 1d - growth * 0.6d, Formant: 1d - growth * 0.45d);
        }

        if (!audible || state.GameState != GameState.Tasks || speaker.IsDead ||
            speaker.Disconnected || speaker.IsDummy || listener.IsDead ||
            !lobby.VoiceEffectEnabled || strengthPercent <= 0)
            return null;

        if (impostorRadioActive && listener.IsImpostor && speaker.IsImpostor &&
            (lobby.ImpostorRadioEnabled || lobby.ImpostorRadioOnlyMode))
            return null;
        if (impostorRadioActive && state.Mod == AmongUsModType.SuperNewRoles &&
            lobby.JackalRadioEnabled && !lobby.ImpostorRadioOnlyMode &&
            listener.SnrRole?.IsJackalTeam == true && speaker.SnrRole?.IsJackalTeam == true)
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
    double Squash = 0d, double Pitch = 1d, double Formant = 1d);

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

        var formant = Math.Clamp(1d / x, 0.55d, 1.7d);
        if (Math.Abs(x - 1d) <= 0.02d && y < 0.98d)
        {
            var amount = 1d - y;
            var squash = amount * amount * (3d - 2d * amount);
            return new NosSizeVoiceEffect(NosSizeEffectMode.SourceFilter, 0d, 1d,
                Squash: squash, Pitch: 1d + squash * 0.25d);
        }
        var pitch = y < 0.98d
            ? 1d + Math.Min(1d, Math.Log(1d / Math.Max(0.1d, y)) / Math.Log(10d))
            : y > 1.02d
                ? 1d - Math.Min(1d, Math.Log(y) / Math.Log(5d)) * 0.6d
                : 1d;
        if (pitch != 1d || x < 0.98d || x > 1.02d)
            return new NosSizeVoiceEffect(NosSizeEffectMode.SourceFilter, 0d, 1d,
                Pitch: pitch, Formant: formant);
        return null;
    }
}
