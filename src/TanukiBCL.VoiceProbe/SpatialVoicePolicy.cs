using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed record SpatialVoiceSettings(
    double MaxDistance = 5.32d,
    bool SpatialAudio = true,
    bool HearImpostorsInVents = false,
    bool ImpostorsHearImpostorsInVents = false,
    bool ImpostorRadioEnabled = false,
    bool ImpostorRadioOnlyMode = false,
    bool CommsSabotage = false,
    bool HearThroughCameras = false,
    bool WallsBlockAudio = false,
    bool Haunting = false,
    double GhostVolumeAsImpostor = 0.1d,
    double CrewVolumeAsGhost = 1d,
    bool DeadOnly = false,
    bool MeetingGhostOnly = false,
    bool NosVoicePositions = false,
    bool NosFixerJammingVoiceBlock = true,
    bool JackalRadioEnabled = false,
    bool JackalHaunting = false,
    bool JackalHearOutsideVents = false,
    bool JackalTalkInVents = false,
    bool SidekickHaunting = false,
    bool SidekickHearOutsideVents = false,
    bool SidekickTalkInVents = false,
    bool TohNeutralKillerHaunting = false,
    bool NosNeutralKillerHaunting = false,
    bool VisionHearing = false);

internal sealed record PeerVoiceMix(
    double Gain,
    double Pan,
    double Distance,
    string Reason,
    bool Muffled = false,
    bool RadioHighPass = false,
    bool RadioEcho = false,
    bool CameraMuffled = false,
    bool Reverb = false)
{
    public bool Audible => Gain > 0.0001d;
}

internal static class SpatialVoicePolicy
{
    private const double ReferenceDistance = 0.1d;

    public static PeerVoiceMix Calculate(
        AmongUsState state,
        Player me,
        Player other,
        SpatialVoiceSettings settings,
        bool otherUsingImpostorRadio = false,
        bool nosJackalRadioHearable = false,
        bool airshipSpawnFallback = false)
    {
        var isNos = state.Mod == AmongUsModType.NebulaOnTheShip;
        var isSnr = state.Mod == AmongUsModType.SuperNewRoles;
        var meJackal = isSnr && me.SnrRole?.IsJackal == true;
        var meSidekick = isSnr && me.SnrRole?.IsSidekick == true;
        var meJackalTeam = meJackal || meSidekick;
        var otherJackalTeam = isSnr && other.SnrRole?.IsJackalTeam == true;
        var snrKillerHearingGhosts = isSnr && settings.JackalHaunting &&
            me.SnrRole?.IsNeutralKiller == true;
        var tohHearingGhosts = state.Mod == AmongUsModType.TownOfHostForE &&
            settings.TohNeutralKillerHaunting && me.TohRole?.IsKiller == true;
        var nosHearingGhosts = isNos && settings.NosNeutralKillerHaunting &&
            me.NosPlayer is { IsNeutral: true, IsKiller: true, IsImpostor: false };
        var canHearGhosts = meJackal ? snrKillerHearingGhosts
            : meSidekick ? settings.SidekickHaunting
            : tohHearingGhosts || nosHearingGhosts || snrKillerHearingGhosts ||
              me.IsImpostor && settings.Haunting;
        var airshipMeetingFallback = state.Map == MapType.Airship && state.AirshipMeetingByOutfit;
        var postMeetingSpawnFallback = state.Map == MapType.Airship &&
            state.GameState == GameState.Tasks && airshipSpawnFallback;
        var useNosPositions = isNos && settings.NosVoicePositions;
        var meX = useNosPositions ? state.NosLocalMicPosition?.X ?? me.X : me.X;
        var meY = useNosPositions ? state.NosLocalMicPosition?.Y ?? me.Y : me.Y;
        var otherX = useNosPositions ? other.NosPlayer?.SpeakerPositionX ?? other.X : other.X;
        var otherY = useNosPositions ? other.NosPlayer?.SpeakerPositionY ?? other.Y : other.Y;
        var maxDistance = ResolveMaxDistance(state, me, settings);
        var deltaX = otherX - meX;
        var deltaY = otherY - meY;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        var pan = CalculatePan(deltaX, settings.SpatialAudio, maxDistance);

        if (other.Disconnected || other.IsDummy)
        {
            return Muted(pan, distance, other.IsDummy ? "dummy" : "disconnected");
        }

        if (isNos && settings.NosFixerJammingVoiceBlock &&
            (me.NosPlayer?.IsJammed == true || other.NosPlayer?.IsJammed == true))
        {
            return Muted(pan, distance, "nos-fixer-jamming");
        }

        if (settings.DeadOnly && (!me.IsDead || !other.IsDead))
        {
            return Muted(0, distance, "dead-only");
        }
        // Released spatialAudio.ts centers the panner before its range check
        // in dead-only mode, making eligible ghost-to-ghost speech room-wide.
        var deadOnlyGhostConversation = settings.DeadOnly && me.IsDead && other.IsDead;

        switch (state.GameState)
        {
            case GameState.Menu:
            case GameState.Unknown:
                return Muted(pan, distance, "not-in-game");

            case GameState.Lobby:
                // The released renderer keeps the ordinary PannerNode distance
                // limit and position in the lobby; only walls/cameras/vent
                // effects are task-specific.
                if (deadOnlyGhostConversation)
                    return new PeerVoiceMix(1d, 0d, distance, "dead-only-ghost");
                if (distance > maxDistance)
                    return Muted(pan, distance, "out-of-range");
                return ApplyListenerVolume(new PeerVoiceMix(
                    settings.SpatialAudio ? LinearDistanceGain(distance, maxDistance) : 1d,
                    pan, distance, "lobby"), me, other, settings);

            case GameState.Discussion:
        if (otherUsingImpostorRadio)
        {
            return CanHearRadio(state, me, other, settings, nosJackalRadioHearable, isSnr)
                ? ApplyListenerVolume(new PeerVoiceMix(1d, 0d, distance, "impostor-radio",
                    RadioHighPass: true, RadioEcho: true), me, other, settings)
                        : Muted(0, distance, "radio-private");
                }
                return !me.IsDead && other.IsDead
                    ? Muted(0, distance, "living-cannot-hear-ghost")
                    : ApplyListenerVolume(new PeerVoiceMix(1d, 0d, distance, "meeting"), me, other, settings);

            case GameState.Tasks:
                break;
        }

        // The released Airship fallback always hides ghosts from living players
        // during a meeting, even when haunting would otherwise enable them.
        if (airshipMeetingFallback && !me.IsDead && other.IsDead)
            return Muted(0d, distance, "airship-meeting-ghost");

        // During the post-meeting spawn window upstream still applies wall
        // occlusion before its usual distance bypass (including for radio).
        if (postMeetingSpawnFallback && settings.WallsBlockAudio && !me.IsDead &&
            WallCollision.Intersects(new Player { X = meX, Y = meY },
                new Player { X = otherX, Y = otherY }, state.Map, state.ClosedDoors))
            return Muted(pan, distance, "airship-spawn-wall");

        if (otherUsingImpostorRadio && !deadOnlyGhostConversation)
        {
            return CanHearRadio(state, me, other, settings, nosJackalRadioHearable, isSnr)
                ? ApplyListenerVolume(CreateTaskRadioMix(me, other, distance), me, other, settings)
                : Muted(0, distance, "radio-private");
        }

        var radioOnlyGhostHearing = settings.ImpostorRadioOnlyMode &&
            !me.IsDead && other.IsDead && canHearGhosts;
        if (settings.MeetingGhostOnly && !radioOnlyGhostHearing)
        {
            return Muted(pan, distance, "meeting-ghost-only");
        }

        if (settings.ImpostorRadioOnlyMode && !me.IsDead && !radioOnlyGhostHearing)
        {
            return Muted(pan, distance, "radio-only");
        }

        if (settings.CommsSabotage && state.CommsSabotaged && !me.IsDead && !me.IsImpostor)
        {
            return Muted(pan, distance, "comms-sabotage");
        }

        var canHearVented = (meJackalTeam || otherJackalTeam) && me.InVent
            ? meJackalTeam && otherJackalTeam &&
              (meSidekick || other.SnrRole?.IsSidekick == true
                  ? settings.SidekickTalkInVents : settings.JackalTalkInVents)
            : settings.HearImpostorsInVents ||
              settings.ImpostorsHearImpostorsInVents && me.InVent;
        if (other.InVent && !canHearVented)
        {
            return Muted(pan, distance, "peer-in-vent");
        }

        var baseGain = 1d;
        var ghostReverb = false;
        if (!me.IsDead && other.IsDead)
        {
            if (!canHearGhosts)
            {
                return Muted(pan, distance, "living-cannot-hear-ghost");
            }

            baseGain *= settings.GhostVolumeAsImpostor;
            ghostReverb = true;
        }

        if (meJackalTeam && me.InVent && !other.InVent &&
            !(meJackal ? settings.JackalHearOutsideVents : settings.SidekickHearOutsideVents))
            return Muted(pan, distance, "snr-vent-private");

        if (deadOnlyGhostConversation)
            return new PeerVoiceMix(1d, 0d, distance, "dead-only-ghost");

        // When the speaker is outside proximity, v3.2.7 can hear them from the
        // selected camera instead. Camera reception uses that camera's position
        // for both attenuation and panning.
        var cameraMuffle = false;
        if (!airshipMeetingFallback && !(postMeetingSpawnFallback && !me.IsDead) &&
            distance > maxDistance && settings.HearThroughCameras &&
            state.CurrentCamera != CameraLocation.None &&
            CameraGeometry.TryRelativePosition(state.Map, state.CurrentCamera,
                new Player { X = otherX, Y = otherY },
                out var cameraDeltaX, out var cameraDeltaY))
        {
            var cameraDistance = Math.Sqrt(cameraDeltaX * cameraDeltaX + cameraDeltaY * cameraDeltaY);
            if (cameraDistance <= maxDistance)
            {
                deltaX = cameraDeltaX;
                distance = cameraDistance;
                pan = CalculatePan(deltaX, settings.SpatialAudio, maxDistance);
                cameraMuffle = true;
            }
        }

        // v3.2.7 applies a 2 kHz low-pass to living vent audio, or a 2.3 kHz
        // low-pass to camera audio. Only an otherwise-unmodified gain is reduced.
        var ventMuffle = (me.InVent && !me.IsDead) || (other.InVent && !other.IsDead);
        if ((ventMuffle || cameraMuffle) && Math.Abs(baseGain - 1d) < 0.0001d)
        {
            baseGain = cameraMuffle ? 0.8d : 0.5d;
        }

        if (airshipMeetingFallback || postMeetingSpawnFallback && !me.IsDead)
        {
            // The meeting fallback skips distance and ordinary walls; the
            // post-meeting spawn window skips distance after the wall check above.
            return ApplyListenerVolume(new PeerVoiceMix(baseGain, 0d, distance,
                airshipMeetingFallback ? "airship-meeting-fallback" : "airship-spawn-fallback",
                Muffled: ventMuffle, Reverb: ghostReverb), me, other, settings);
        }

        if (distance > maxDistance ||
            (settings.SpatialAudio && LinearDistanceGain(distance, maxDistance) <= 0))
        {
            return Muted(pan, distance, "out-of-range");
        }
        // 3.2.7 checks the proximity limit before centering the PannerNode.
        // Turning spatial audio off therefore keeps the range/wall rules but
        // removes both stereo placement and distance attenuation inside range.
        var distanceGain = settings.SpatialAudio ? LinearDistanceGain(distance, maxDistance) : 1d;

        if (!cameraMuffle && settings.WallsBlockAudio && !me.IsDead &&
            WallCollision.Intersects(new Player { X = meX, Y = meY },
                new Player { X = otherX, Y = otherY }, state.Map, state.ClosedDoors))
        {
            return Muted(pan, distance, "wall-blocked");
        }

        return ApplyListenerVolume(new PeerVoiceMix(baseGain * distanceGain, pan, distance,
            cameraMuffle ? "camera" : "proximity", Muffled: ventMuffle && !cameraMuffle,
            CameraMuffled: cameraMuffle, Reverb: ghostReverb), me, other, settings);
    }

    private static PeerVoiceMix ApplyListenerVolume(PeerVoiceMix mix, Player me, Player other,
        SpatialVoiceSettings settings) =>
        me.IsDead && !other.IsDead
            ? mix with { Gain = mix.Gain * settings.CrewVolumeAsGhost }
            : mix;

    private static double LinearDistanceGain(double distance, double maxDistance)
    {
        if (maxDistance <= ReferenceDistance || distance >= maxDistance)
        {
            return distance <= ReferenceDistance ? 1d : 0d;
        }

        if (distance <= ReferenceDistance)
        {
            return 1d;
        }

        // Web Audio PannerNode: distanceModel=linear, refDistance=0.1, rolloffFactor=1.
        return Math.Clamp(1d - (distance - ReferenceDistance) / (maxDistance - ReferenceDistance), 0d, 1d);
    }

    private static double ResolveMaxDistance(AmongUsState state, Player me, SpatialVoiceSettings settings)
    {
        var maxDistance = settings.VisionHearing && !me.IsImpostor
            ? state.LightRadius + 0.5d : settings.MaxDistance;
        if (!double.IsFinite(maxDistance) || maxDistance <= 0.6d) return 1d;
        return maxDistance;
    }

    private static double CalculatePan(double deltaX, bool spatialAudio, double maxDistance) =>
        spatialAudio && maxDistance > 0d
            ? Math.Clamp(deltaX / maxDistance, -1d, 1d)
            : 0d;

    private static bool CanHearRadio(AmongUsState state, Player me, Player other, SpatialVoiceSettings settings,
        bool nosJackalRadioHearable, bool isSnr) =>
        state.Mod == AmongUsModType.NebulaOnTheShip ? nosJackalRadioHearable :
        CanHearImpostorRadio(me, other, settings) ||
        CanHearJackalRadioAsGhost(me, other, settings) ||
        CanHearSnrJackalRadio(me, other, settings, isSnr);

    // 3.2.12: ghosts hear a living non-impostor's radio (SNR/NoS jackal) like impostor radio.
    internal static bool CanHearJackalRadioAsGhost(Player me, Player other, SpatialVoiceSettings settings) =>
        settings.JackalRadioEnabled && !settings.ImpostorRadioOnlyMode &&
        me.IsDead && !other.IsDead && !other.IsImpostor;

    private static bool CanHearImpostorRadio(Player me, Player other, SpatialVoiceSettings settings) =>
        (settings.ImpostorRadioEnabled || settings.ImpostorRadioOnlyMode) &&
        other.IsImpostor && !other.IsDead &&
        ((me.IsImpostor && !me.IsDead) || me.IsDead);

    private static bool CanHearSnrJackalRadio(Player me, Player other,
        SpatialVoiceSettings settings, bool isSnr) =>
        isSnr && settings.JackalRadioEnabled && !settings.ImpostorRadioOnlyMode &&
        !me.IsDead && !other.IsDead && me.SnrRole?.IsJackalTeam == true &&
        other.SnrRole?.IsJackalTeam == true;

    private static PeerVoiceMix CreateTaskRadioMix(Player me, Player other, double distance)
    {
        // In v3.2.7 the vent muffle is applied after radio selection, replacing
        // the radio high-pass while leaving its echo connected.
        var ventMuffle = (me.InVent && !me.IsDead) || (other.InVent && !other.IsDead);
        return new PeerVoiceMix(ventMuffle ? 0.5d : 1d, 0d, distance, "impostor-radio",
            Muffled: ventMuffle, RadioHighPass: !ventMuffle, RadioEcho: true);
    }

    private static PeerVoiceMix Muted(double pan, double distance, string reason) => new(0d, pan, distance, reason);
}
