using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

public sealed record PlayerAudioConfig(double Volume = 1d, bool IsMuted = false)
{
    public static PlayerAudioConfig Default { get; } = new();

    public PlayerAudioConfig Normalize() => this with
    {
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0d, 2d) : 1d
    };

    public static PlayerAudioConfig For(Player player, IReadOnlyDictionary<int, PlayerAudioConfig> configs)
    {
        if (configs.TryGetValue(player.PlayerConfigId, out var config) ||
            configs.TryGetValue(player.NameHash, out config))
        {
            return config.Normalize();
        }

        return Default;
    }

    internal PeerVoiceMix Apply(PeerVoiceMix mix) => mix with
    {
        Gain = IsMuted ? 0d : mix.Gain * Volume,
        Reason = IsMuted ? "player-muted" : Volume == 0d ? "player-volume-zero" : mix.Reason
    };
}
