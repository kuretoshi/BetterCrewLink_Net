using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class NosRoleAudioSelfTest
{
    public static bool Verify()
    {
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip,
            GameState = GameState.Tasks, NosLocalMicPosition = new VoicePosition(20, 0) };
        var role = new NosRoleData(1, "rokurokubi", "Rokurokubi", "Rokurokubi.Instance", false);
        var me = new Player { NosRole = role };
        var other = new Player { X = 21 };
        var settings = new SpatialVoiceSettings();
        bool Near(double expected) => Math.Abs(SpatialVoicePolicy.Calculate(state, me, other, settings).Distance - expected) < 1e-6;
        if (!Near(1) || !SpatialVoicePolicy.Calculate(state, me, other, settings).Audible) return false;
        me.NosRole = null;
        me.NosPlayer = new NosPlayerData { BodyType = 3 };
        if (!Near(1)) return false;
        me.NosPlayer = null;
        if (!Near(1)) return false;
        me.X = 20;
        other.X = 60;
        other.NosRole = role;
        other.NosPlayer = new NosPlayerData { SpeakerPositionX = 21 };
        if (!Near(1)) return false;
        other.NosRole = null;
        other.NosPlayer.BodyType = 3;
        if (!Near(1)) return false;
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            other.NosPlayer.SpeakerPositionX = invalid;
            if (!Near(40)) return false;
        }
        other.NosPlayer.SpeakerPositionX = 21;
        other.Disconnected = true;
        if (!Near(1) || SpatialVoicePolicy.Calculate(state, me, other, settings).Audible) return false;
        other.Disconnected = false;
        other.IsDead = true;
        if (!Near(1) || SpatialVoicePolicy.Calculate(state, me, other, settings).Audible) return false;
        other.IsDead = false;
        state.GameState = GameState.Discussion;
        if (!Near(1)) return false;
        state.GameState = GameState.Tasks;
        state.Mod = AmongUsModType.None;
        if (!Near(40)) return false;
        state.Mod = AmongUsModType.NebulaOnTheShip;
        other.NosPlayer = null;
        other.NosRole = role;
        if (!Near(40)) return false;
        me.NosRole = role;
        state.NosLocalMicPosition = new VoicePosition(double.NaN, 0);
        if (!Near(40)) return false;
        state.NosLocalMicPosition = new VoicePosition(30, double.PositiveInfinity);
        if (!Near(40)) return false;

        var pulse = new float[48_000];
        pulse[0] = .5f;
        new BerserkerDsp(48_000).Process(pulse, 0, pulse.Length, 1);
        // Room/echo are independent profile axes now; the grit DSP must not
        // add either tail when those axes are zero.
        if (pulse.Skip(11_520).Any(value => Math.Abs(value) > .00001f) || !pulse.All(float.IsFinite)) return false;
        var dry = new float[48_000];
        dry[0] = .5f;
        new BerserkerDsp(48_000).Process(dry, 0, dry.Length, 0);
        if (dry[0] != .5f || dry.Skip(1).Any(value => value != 0)) return false;
        Console.WriteLine("NoS role audio: head/body fallbacks, role gates and isolated Berserker grit passed");
        return true;
    }
}
