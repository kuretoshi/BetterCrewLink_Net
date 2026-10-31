using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class RokuroVoiceSelfTest
{
    public static void Verify()
    {
        var count = 0;
        void Check(string name, bool value) { if (!value) throw new InvalidOperationException("Rokuro: " + name); count++; }
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip, GameState = GameState.Tasks };
        var me = new Player { Id = 1 };
        var other = new Player { Id = 2, X = 1, NosPlayer = new NosPlayerData
            { BodyType = 3, NeckLength = 40, SpeakerPositionX = 60 } };
        var settings = new SpatialVoiceSettings();
        PeerVoiceMix Calculate(bool radio = false, bool enabled = true) => RokuroVoicePolicy.Apply(state, me, other, settings,
            SpatialVoicePolicy.Calculate(state, me, other, settings), new(), enabled, radio, false);
        var bodyOnly = Calculate();
        Check("body audible while head out of range", bodyOnly.Gain == 0 && bodyOnly.RokuroBody is { Gain: > 0 } && bodyOnly.Audible);
        me.X = 59; var headOnly = Calculate();
        Check("head audible while body out of range", headOnly.Gain > 0 && headOnly.RokuroBody is { Gain: 0 });
        me.X = 0;
        Check("radio bypass", Calculate(radio: true).RokuroBody is null);
        Check("host off bypass", Calculate(enabled: false).RokuroBody is null);
        state.GameState = GameState.Discussion; Check("meeting bypass", Calculate().RokuroBody is null);
        state.GameState = GameState.Tasks;
        other.IsDead = true; Check("death bypass", Calculate().RokuroBody is null); other.IsDead = false;
        other.NosPlayer.NeckLength = 0; Check("retraction bypass", Calculate().RokuroBody is null); other.NosPlayer.NeckLength = 40;
        var muted = new PlayerAudioConfig(IsMuted: true).Apply(bodyOnly);
        Check("player mute covers both routes", !muted.Audible && muted.RokuroBody is { Gain: 0 });
        var half = new PlayerAudioConfig(.5).Apply(bodyOnly);
        Check("player volume covers body", half.RokuroBody!.Gain == bodyOnly.RokuroBody!.Gain / 2);
        var (dry, dryReads) = Render(0, false);
        var (echo, echoReads) = Render(100, false);
        var (stopped, _) = Render(100, true);
        double Energy(float[] data, double from, double to) => data.Skip((int)(from * 96000)).Take((int)((to - from) * 96000)).Select(x => (double)x*x).Sum();
        Check("body direct sound", Energy(dry, .025, .055) > .01);
        Check("echo zero has no delayed speech", Energy(dry, .205, .235) < 1e-8);
        Check("body echo adds 180 ms tail", Energy(echo, .205, .235) > .01);
        Check("mute clears queued echo", Energy(stopped, .2, .5) == 0);
        Check("one input read per output block", dryReads == 50 && echoReads == 50);
        Check("finite bounded audio", echo.All(x => float.IsFinite(x) && Math.Abs(x) < .981));
        var (loud, _) = Render(100, false, 1, 3f, 1);
        Check($"overlapping boosted sources stay below clipping (peak={loud.Max(Math.Abs):F6})", loud.All(x => float.IsFinite(x) && Math.Abs(x) < .981) && loud.Max(Math.Abs) > .9);
        Console.WriteLine($"[PASS] Rokuro dual voice: {count} spatial, radio/meeting/death/retraction, shared mute/volume, DSP echo and single-read checks");
    }

    private static (float[] Samples, int Reads) Render(int echo, bool mute, float headVolume = 0, float level = .25f, double bodyGain = .35)
    {
        var source = new Pulse(level);
        var tap = new RokuroVoiceTap(source);
        var head = new VolumeSampleProvider(new PanningSampleProvider(tap)) { Volume = headVolume };
        var mixer = new RokuroVoiceSampleProvider(head, tap);
        mixer.SetMix(new(bodyGain, .5, echo));
        var output = new float[96000];
        for (var offset = 0; offset < output.Length; offset += 1920)
        {
            if (mute && offset == 9600) mixer.SetMix(new(0, .5, echo));
            mixer.Read(output, offset, 1920);
        }
        return (output, source.Reads);
    }

    private sealed class Pulse(float level) : ISampleProvider
    {
        private int position;
        public int Reads;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            Reads++;
            for (var index = 0; index < count; index++, position++)
                buffer[offset + index] = position is >= 960 and < 2880 ? (float)(level * Math.Sin(2 * Math.PI * 500 * position / 48000)) : 0;
            return count;
        }
    }
}
