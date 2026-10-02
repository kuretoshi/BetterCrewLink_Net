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
    bool MeetingGhostOnly = false);

internal sealed record PeerVoiceMix(
    double Gain,
    double Pan,
    double Distance,
    string Reason,
    bool Muffled = false,
    bool RadioHighPass = false,
    bool RadioEcho = false,
    bool CameraMuffled = false)
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
        bool otherUsingImpostorRadio = false)
    {
        var deltaX = other.X - me.X;
        var deltaY = other.Y - me.Y;
        var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        var pan = CalculatePan(deltaX, settings);

        if (other.Disconnected)
        {
            return Muted(pan, distance, "disconnected");
        }

        if (settings.DeadOnly && (!me.IsDead || !other.IsDead))
        {
            return Muted(0, distance, "dead-only");
        }

        switch (state.GameState)
        {
            case GameState.Menu:
            case GameState.Unknown:
                return Muted(pan, distance, "not-in-game");

            case GameState.Lobby:
                return ApplyListenerVolume(new PeerVoiceMix(1d, 0d, distance, "lobby"), me, other, settings);

            case GameState.Discussion:
                if (otherUsingImpostorRadio)
                {
                    return CanHearImpostorRadio(me, other, settings)
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

        if (otherUsingImpostorRadio)
        {
            return CanHearImpostorRadio(me, other, settings)
                ? ApplyListenerVolume(CreateTaskRadioMix(me, other, distance), me, other, settings)
                : Muted(0, distance, "radio-private");
        }

        if (settings.MeetingGhostOnly)
        {
            return Muted(pan, distance, "meeting-ghost-only");
        }

        if (settings.ImpostorRadioOnlyMode && !me.IsDead)
        {
            return Muted(pan, distance, "radio-only");
        }

        if (settings.CommsSabotage && state.CommsSabotaged && !me.IsDead && !me.IsImpostor)
        {
            return Muted(pan, distance, "comms-sabotage");
        }

        if (other.InVent &&
            !(settings.HearImpostorsInVents ||
              (settings.ImpostorsHearImpostorsInVents && me.IsImpostor && me.InVent)))
        {
            return Muted(pan, distance, "peer-in-vent");
        }

        var baseGain = 1d;
        if (!me.IsDead && other.IsDead)
        {
            if (!me.IsImpostor || !settings.Haunting)
            {
                return Muted(pan, distance, "living-cannot-hear-ghost");
            }

            baseGain *= settings.GhostVolumeAsImpostor;
        }

        // When the speaker is outside proximity, v3.2.7 can hear them from the
        // selected camera instead. Camera reception uses that camera's position
        // for both attenuation and panning.
        var cameraMuffle = false;
        if (distance > settings.MaxDistance && settings.HearThroughCameras &&
            state.CurrentCamera != CameraLocation.None &&
            CameraGeometry.TryRelativePosition(state.Map, state.CurrentCamera, other,
                out var cameraDeltaX, out var cameraDeltaY))
        {
            var cameraDistance = Math.Sqrt(cameraDeltaX * cameraDeltaX + cameraDeltaY * cameraDeltaY);
            if (cameraDistance <= settings.MaxDistance)
            {
                deltaX = cameraDeltaX;
                distance = cameraDistance;
                pan = CalculatePan(deltaX, settings);
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

        var distanceGain = LinearDistanceGain(distance, settings.MaxDistance);
        if (distanceGain <= 0)
        {
            return Muted(pan, distance, "out-of-range");
        }

        if (!cameraMuffle && settings.WallsBlockAudio && !me.IsDead &&
            WallCollision.Intersects(me, other, state.Map, state.ClosedDoors))
        {
            return Muted(pan, distance, "wall-blocked");
        }

        return ApplyListenerVolume(new PeerVoiceMix(baseGain * distanceGain, pan, distance,
            cameraMuffle ? "camera" : "proximity", Muffled: ventMuffle && !cameraMuffle,
            CameraMuffled: cameraMuffle), me, other, settings);
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

    private static double CalculatePan(double deltaX, SpatialVoiceSettings settings) =>
        settings.SpatialAudio && settings.MaxDistance > 0
            ? Math.Clamp(deltaX / settings.MaxDistance, -1d, 1d)
            : 0d;

    private static bool CanHearImpostorRadio(Player me, Player other, SpatialVoiceSettings settings) =>
        (settings.ImpostorRadioEnabled || settings.ImpostorRadioOnlyMode) &&
        other.IsImpostor && !other.IsDead &&
        ((me.IsImpostor && !me.IsDead) || me.IsDead);

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
