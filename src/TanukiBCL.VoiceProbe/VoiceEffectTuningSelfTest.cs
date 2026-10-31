using System.Text.Json;

namespace TanukiBCL.VoiceProbe;

internal static class VoiceEffectTuningSelfTest
{
    public static bool Verify()
    {
        if (VoiceEffectTunings.Ids.Length != 11) return false;
        foreach (var id in VoiceEffectTunings.Ids)
        {
            var map = new Dictionary<string, VoiceEffectTuningOverride>
                { [id] = new() { Pitch = -50, Reverb = 23, Echo = 41 } };
            if (VoiceEffectTunings.Resolve(map, id) != new VoiceEffectTuning(-50, 23, 41)) return false;
            var saved = JsonSerializer.Deserialize<Dictionary<string, VoiceEffectTuningOverride>>(JsonSerializer.Serialize(map));
            if (VoiceEffectTunings.Resolve(saved, id) != new VoiceEffectTuning(-50, 23, 41)) return false;
            map[id] = new() { Pitch = -200, Reverb = 999, Echo = -5 };
            if (VoiceEffectTunings.Resolve(map, id) != new VoiceEffectTuning(-100, 100, 0)) return false;
            map[id] = new() { Pitch = double.NaN, Reverb = double.PositiveInfinity };
            if (VoiceEffectTunings.Resolve(map, id) != VoiceEffectTunings.Resolve(null, id)) return false;
        }
        if (VoiceEffectTunings.Resolve(null, "disguise", 37).Pitch != -37 ||
            VoiceEffectTunings.Resolve(null, "nosRokurokubi", 37).Pitch != 37 ||
            VoiceEffectTunings.Resolve(null, "nosBerserker", 37) != new VoiceEffectTuning(0, 37, 37)) return false;
        if (new VoiceEffectTuning(-100).PitchRatio != .5 || new VoiceEffectTuning(0).PitchRatio != 1 ||
            new VoiceEffectTuning(100).PitchRatio != 2) return false;
        var a = new VoiceEffectTuning(100, 15, 3);
        var b = new VoiceEffectTuning(100, 2, 25);
        var c = new VoiceEffectTuning(-100, 50, 4);
        return VoiceEffectTunings.Combine([a, b, c]) == new VoiceEffectTuning(100, 50, 25) &&
            VoiceEffectTunings.Combine([c, b, a]) == new VoiceEffectTuning(100, 50, 25);
    }
}
