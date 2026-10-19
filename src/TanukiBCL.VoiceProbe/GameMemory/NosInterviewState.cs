using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosInterviewState
{
    [JsonRequired] public int PlayerId { get; set; }
    [JsonRequired] public long Started { get; set; }
    [JsonRequired] public double Seconds { get; set; }
    [JsonRequired] public long Stopped { get; set; }
    [JsonRequired] public int[] Visible { get; set; } = [];
    [JsonRequired] public long PlayStarted { get; set; }
    public bool IsValid(long now) => PlayerId is >= 0 and <= 255 && Started > 0 && Started <= now + 100 &&
        double.IsFinite(Seconds) && Seconds is >= 1 and <= 30 &&
        (Stopped == 0 || Stopped >= Started && Stopped <= Started + Seconds * 1000) &&
        Visible is { Length: <= 255 } && Visible.Distinct().Count() == Visible.Length && Visible.All(id => id is >= 0 and <= 255) &&
        PlayStarted >= 0 && PlayStarted <= now + 100 &&
        (PlayStarted == 0 || Stopped > Started && PlayStarted >= Stopped);
}
