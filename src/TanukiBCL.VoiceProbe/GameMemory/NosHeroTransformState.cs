using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosHeroTransformState
{
    [JsonRequired]
    public int PlayerId { get; set; }
    [JsonRequired]
    public string Kind { get; set; } = "";
    [JsonIgnore]
    public bool IsValid => PlayerId is >= 0 and <= 255 && Kind is "super" or "evil";
}
