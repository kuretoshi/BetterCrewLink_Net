using BetterCrewLinkKai.DotNet.Models;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class VoiceMixService
{
    private static readonly VoiceCollisionService CollisionService = new();
    private const double PannerReferenceDistance = 1d;
    private const double PannerZOffset = -0.5d;

    public IReadOnlyDictionary<int, PlayerVoiceMix> Calculate(AmongUsState state, AppSettings settings, int impostorRadioClientId = -1)
    {
        var result = new Dictionary<int, PlayerVoiceMix>();
        var me = state.Players.FirstOrDefault(static player => player.IsLocal);
        if (me is null)
        {
            return result;
        }

        foreach (var other in state.Players.Where(static player => !player.IsLocal && !player.Disconnected))
        {
            result[other.ClientId] = CalculatePlayer(state, settings, me, other, impostorRadioClientId);
        }

        return result;
    }

    private static PlayerVoiceMix CalculatePlayer(AmongUsState state, AppSettings settings, Player me, Player other, int impostorRadioClientId)
    {
        var lobby = settings.LocalLobbySettings;
        var volume = state.GameState switch
        {
            GameState.Lobby => 1d,
            GameState.Tasks => 1d,
            GameState.Discussion => 1d,
            _ => 0d
        };
        var reason = volume <= 0 ? "menu" : "audible";
        var panX = other.X - me.X;
        var panY = other.Y - me.Y;
        var isMuffled = false;
        var usesGhostReverb = false;
        var usesVoiceEffect = false;
        var skipDistanceCheck = false;

        if (volume <= 0)
        {
            return Muted(other, reason);
        }

        if (state.GameState == GameState.Tasks)
        {
            if (lobby.MeetingGhostOnly)
            {
                return Muted(other, "meeting-ghost-only");
            }

            if (!me.IsDead && lobby.CommsSabotage && state.CommsSabotaged && !me.IsImpostor)
            {
                return Muted(other, "comms-sabotage");
            }

            if (other.InVent && !(lobby.HearImpostorsInVents || (lobby.ImpostorsHearImpostorsInVent && me.InVent)))
            {
                return Muted(other, "other-in-vent");
            }

            if (lobby.WallsBlockAudio &&
                !me.IsDead &&
                CollisionService.PoseCollide(me, other, state.Map, state.ClosedDoors))
            {
                isMuffled = true;
                reason = "wall-collision";
            }

            if (me.IsImpostor &&
                other.IsImpostor &&
                lobby.ImpostorRadioEnabled &&
                other.ClientId == impostorRadioClientId)
            {
                skipDistanceCheck = true;
                isMuffled = true;
                reason = "impostor-radio";
            }

            if (!me.IsDead && other.IsDead)
            {
                if (CanHearGhosts(me, lobby))
                {
                    volume = settings.GhostVolumeAsImpostor / 100d;
                    usesGhostReverb = true;
                    reason = "ghost-audible";
                }
                else
                {
                    return Muted(other, "dead-player");
                }
            }

            if (((me.InVent && !me.IsDead) || (other.InVent && !other.IsDead)) && !skipDistanceCheck)
            {
                isMuffled = true;
                volume = Math.Min(volume, 0.5d);
                reason = "vent-muffled";
            }

            usesVoiceEffect = lobby.VoiceEffectEnabled &&
                              settings.VoiceEffectStrength > 0 &&
                              !me.IsDead &&
                              !other.IsDead &&
                              !isMuffled &&
                              IsSkinChanged(other);
        }
        else if (state.GameState == GameState.Discussion)
        {
            panX = 0;
            panY = 0;
            if (!me.IsDead && other.IsDead)
            {
                return Muted(other, "dead-player-discussion");
            }
        }

        if (lobby.DeadOnly)
        {
            panX = 0;
            panY = 0;
            if (!me.IsDead || !other.IsDead)
            {
                return Muted(other, "dead-only");
            }
        }

        var maxDistance = GetEffectiveMaxDistance(lobby, state, me);
        var distance = Math.Sqrt((panX * panX) + (panY * panY));
        if (!skipDistanceCheck && distance > maxDistance)
        {
            if (lobby.HearThroughCameras &&
                state.GameState == GameState.Tasks &&
                CollisionService.TryGetCameraPan(other, state.Map, state.CurrentCamera, out var cameraPanX, out var cameraPanY))
            {
                panX = cameraPanX;
                panY = cameraPanY;
                distance = Math.Sqrt((panX * panX) + (panY * panY));
                maxDistance = 3d;
                isMuffled = true;
                reason = "camera";
            }

            if (distance > maxDistance)
            {
                return Muted(other, "out-of-range");
            }
        }
        else if (!skipDistanceCheck && reason == "wall-collision")
        {
            return Muted(other, "wall-collision");
        }

        if (!skipDistanceCheck)
        {
            volume *= CalculateLinearDistanceGain(distance, maxDistance);
        }

        if (!settings.EnableSpatialAudio || skipDistanceCheck)
        {
            panX = 0;
            panY = 0;
        }

        var playerConfig = GetPlayerConfig(settings, other);
        if (playerConfig.IsMuted)
        {
            return Muted(other, "player-muted");
        }

        volume *= playerConfig.Volume;
        volume *= settings.MasterVolume / 100d;
        if (me.IsDead && !other.IsDead)
        {
            volume *= settings.CrewVolumeAsGhost / 100d;
        }

        return new PlayerVoiceMix
        {
            PlayerId = other.Id,
            ClientId = other.ClientId,
            Name = other.Name,
            Volume = Math.Clamp(volume, 0d, 2d),
            PanX = panX,
            PanY = panY,
            IsMuffled = isMuffled,
            UsesGhostReverb = usesGhostReverb,
            UsesVoiceEffect = usesVoiceEffect,
            VoiceEffectStrength = settings.VoiceEffectStrength,
            Reason = reason
        };
    }

    private static double GetEffectiveMaxDistance(LobbySettings lobby, AmongUsState state, Player me)
    {
        return lobby.MaxDistance <= 0.6d ? 1d : Math.Max(1d, lobby.MaxDistance);
    }

    private static double CalculateLinearDistanceGain(double planarDistance, double maxDistance)
    {
        var distance = Math.Sqrt((planarDistance * planarDistance) + (PannerZOffset * PannerZOffset));
        if (distance <= PannerReferenceDistance)
        {
            return 1d;
        }

        if (maxDistance <= PannerReferenceDistance)
        {
            return 0d;
        }

        return Math.Clamp(1d - ((distance - PannerReferenceDistance) / (maxDistance - PannerReferenceDistance)), 0d, 1d);
    }

    private static bool CanHearGhosts(Player player, LobbySettings lobby)
    {
        return (player.IsImpostor && lobby.Haunting) ||
               (player.IsThirdParty && lobby.ThirdPartyHaunting);
    }

    private static bool IsSkinChanged(Player player)
    {
        return !string.IsNullOrWhiteSpace(player.NormalSkinId) &&
               !string.Equals(player.SkinId, player.NormalSkinId, StringComparison.Ordinal);
    }

    private static SocketConfig GetPlayerConfig(AppSettings settings, Player player)
    {
        return settings.PlayerConfigMap.TryGetValue(player.NameHash.ToString(), out var byHash)
            ? byHash
            : settings.PlayerConfigMap.TryGetValue(player.Name, out var byName)
                ? byName
                : new SocketConfig();
    }

    private static PlayerVoiceMix Muted(Player player, string reason)
    {
        return new PlayerVoiceMix
        {
            PlayerId = player.Id,
            ClientId = player.ClientId,
            Name = player.Name,
            Volume = 0,
            Reason = reason
        };
    }
}
