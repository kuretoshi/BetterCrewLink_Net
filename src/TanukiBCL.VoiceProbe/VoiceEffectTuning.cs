using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe;

internal sealed record VoiceEffectTuning(double Pitch = 0, double Reverb = 0, double Echo = 0)
{
    public bool IsNeutral => Pitch == 0 && Reverb == 0 && Echo == 0;
    public double PitchRatio => Math.Pow(2, Math.Clamp(Pitch, -100, 100) / 100);
}

internal sealed record VoiceEffectTuningOverride
{
    [JsonPropertyName("pitch")] public double? Pitch { get; init; }
    [JsonPropertyName("reverb")] public double? Reverb { get; init; }
    [JsonPropertyName("echo")] public double? Echo { get; init; }

    public static VoiceEffectTuningOverride From(VoiceEffectTuning tuning) => new()
        { Pitch = tuning.Pitch, Reverb = tuning.Reverb, Echo = tuning.Echo };
}

internal static class VoiceEffectTunings
{
    public static readonly string[] Ids = ["disguise", "snrJumbo", "snrPush", "nosSize", "nosRokurokubi",
        "nosBerserker", "nosCitrus", "nosRainbowStar", "nosVoiceJamming", "nosMegaphone", "nosBibiri"];

    public static VoiceEffectTuning Resolve(IReadOnlyDictionary<string, VoiceEffectTuningOverride>? values,
        string id, int legacyStrength = 100)
    {
        var strength = Math.Clamp(legacyStrength, 0, 100);
        var sign = id switch
        {
            "disguise" or "snrJumbo" or "nosSize" or "nosVoiceJamming" or "nosMegaphone" => -1,
            "nosRokurokubi" or "nosCitrus" => 1,
            _ => 0
        };
        var fallback = new VoiceEffectTuning(sign * strength,
            id is "snrPush" or "nosBerserker" or "nosRainbowStar" ? strength : 0,
            id is "nosBerserker" or "nosBibiri" ? strength : 0);
        var value = values?.GetValueOrDefault(id);
        return new VoiceEffectTuning(Bound(value?.Pitch, -100, 100, fallback.Pitch),
            Bound(value?.Reverb, 0, 100, fallback.Reverb), Bound(value?.Echo, 0, 100, fallback.Echo));
    }

    public static VoiceEffectTuning Combine(IEnumerable<VoiceEffectTuning> values)
    {
        var mix = values.Aggregate(new VoiceEffectTuning(), (current, next) =>
            new VoiceEffectTuning(current.Pitch + next.Pitch, Math.Max(current.Reverb, next.Reverb), Math.Max(current.Echo, next.Echo)));
        return mix with { Pitch = Math.Clamp(mix.Pitch, -100, 100) };
    }

    private static double Bound(double? value, double minimum, double maximum, double fallback) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, minimum, maximum) : fallback;
}
