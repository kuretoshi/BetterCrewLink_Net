using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosParrotState
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SpoofTalking { get; set; }
    [JsonRequired] public int OwnerId { get; set; }
    [JsonRequired] public int BirdId { get; set; }
    [JsonRequired] public double X { get; set; }
    [JsonRequired] public double Y { get; set; }
    [JsonRequired] public double Range { get; set; }
    public double Seconds { get; set; } = 1.5;
    [JsonRequired] public int RecordId { get; set; }
    [JsonRequired] public int SourceId { get; set; }
    [JsonRequired] public long Started { get; set; }
    [JsonRequired] public int[] Clips { get; set; } = [];
    [JsonRequired] public int PlayId { get; set; }
    [JsonRequired] public int PlayClip { get; set; }
    [JsonRequired] public long PlayStarted { get; set; }
    public bool IsValid(long now) => Mode is null or "clone" &&
        (SpoofTalking != true || Mode == "clone") &&
        (Mode != "clone" || Seconds == 10 && SourceId == OwnerId && BirdId == 1000000 + OwnerId && Clips is { Length: <= 1 }) &&
        OwnerId is >= 0 and <= 255 && BirdId > 0 &&
        double.IsFinite(X) && Math.Abs(X) <= 10000 && double.IsFinite(Y) && Math.Abs(Y) <= 10000 &&
        double.IsFinite(Range) && Range is >= 1 and <= 10 && double.IsFinite(Seconds) && Seconds >= .5 && Seconds <= (Mode == "clone" ? 10 : 5) && RecordId >= 0 && SourceId is >= 0 and <= 255 &&
        Started >= 0 && Started <= now + 100 && (RecordId == 0 || Started > 0) &&
        Clips is { Length: <= 5 } && Clips.All(id => id > 0) && Clips.Distinct().Count() == Clips.Length &&
        PlayId >= 0 && PlayClip >= 0 && PlayStarted >= 0 && PlayStarted <= now + 100 &&
        (PlayId == 0 || PlayStarted > 0 && Clips.Contains(PlayClip));
    internal bool Near(Player? player, AmongUsState state) => player != null && !player.Disconnected && !player.InVent &&
        (X - player.X) * (X - player.X) + (Y - player.Y) * (Y - player.Y) <= Range * Range &&
        !WallCollision.Intersects(player, new Player { X = X, Y = Y }, state.Map, state.ClosedDoors);
}
