using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosDyingVoiceState
{
    [JsonRequired] public int PlayerId { get; set; }
    [JsonRequired] public long Started { get; set; }
    [JsonRequired] public double Seconds { get; set; }
    [JsonRequired] public double Range { get; set; }
    [JsonRequired] public double X { get; set; }
    [JsonRequired] public double Y { get; set; }
}
