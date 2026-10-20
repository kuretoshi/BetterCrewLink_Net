using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosVoiceJammingState
{
    [JsonRequired] public int PlayerId { get; set; }
    [JsonRequired] public bool Muted { get; set; }
    [JsonRequired] public bool Lowpass { get; set; }
    [JsonRequired] public double Pitch { get; set; }
    public bool IsValid => double.IsFinite(Pitch) && Pitch is >= .5d and <= 2d;
}
