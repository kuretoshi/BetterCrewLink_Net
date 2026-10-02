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
    bool Haunting = false,
    double GhostVolumeAsImpostor = 1d,
    double CrewVolumeAsGhost = 1d,
    bool DeadOnly = false,
    bool MeetingGhostOnly = false);

internal sealed record PeerVoiceMix(
    double Gain,
    double Pan,
    double Distance,
    string Reason)
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
        var pan = settings.SpatialAudio && settings.MaxDistance > 0
            ? Math.Clamp(deltaX / settings.MaxDistance, -1d, 1d)
            : 0d;

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
                return new PeerVoiceMix(1d, 0d, distance, "lobby");

            case GameState.Discussion:
                if (otherUsingImpostorRadio)
                {
                    return CanHearImpostorRadio(me, other, settings)
                        ? new PeerVoiceMix(1d, 0d, distance, "impostor-radio")
                        : Muted(0, distance, "radio-private");
                }
                return !me.IsDead && other.IsDead
                    ? Muted(0, distance, "living-cannot-hear-ghost")
                    : new PeerVoiceMix(1d, 0d, distance, "meeting");

            case GameState.Tasks:
                break;
        }

        if (otherUsingImpostorRadio)
        {
            return CanHearImpostorRadio(me, other, settings)
                ? new PeerVoiceMix(1d, 0d, distance, "impostor-radio")
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
        else if (me.IsDead && !other.IsDead)
        {
            baseGain *= settings.CrewVolumeAsGhost;
        }

        var distanceGain = LinearDistanceGain(distance, settings.MaxDistance);
        if (distanceGain <= 0)
        {
            return Muted(pan, distance, "out-of-range");
        }

        return new PeerVoiceMix(baseGain * distanceGain, pan, distance, "proximity");
    }

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

    private static bool CanHearImpostorRadio(Player me, Player other, SpatialVoiceSettings settings) =>
        (settings.ImpostorRadioEnabled || settings.ImpostorRadioOnlyMode) &&
        other.IsImpostor && !other.IsDead &&
        ((me.IsImpostor && !me.IsDead) || me.IsDead);

    private static PeerVoiceMix Muted(double pan, double distance, string reason) => new(0d, pan, distance, reason);
}
