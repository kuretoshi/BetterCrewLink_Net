using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosWallEarState
{
    [JsonRequired] public int PlayerId { get; set; }
    [JsonRequired] public double X { get; set; }
    [JsonRequired] public double Y { get; set; }
    [JsonRequired] public double Range { get; set; }
    [JsonIgnore] public bool IsValid => PlayerId is >= 0 and <= 255 &&
        double.IsFinite(X) && Math.Abs(X) <= 10000 && double.IsFinite(Y) && Math.Abs(Y) <= 10000 &&
        double.IsFinite(Range) && Range is >= 1 and <= 10;
    internal static NosWallEarState? For(AmongUsState state, Player listener)
    {
        if (state.Mod != AmongUsModType.NebulaOnTheShip || state.GameState != GameState.Tasks ||
            listener.IsDead || listener.Disconnected || listener.InVent || state.NosWallEars.Count > 24) return null;
        NosWallEarState? result = null;
        foreach (var ear in state.NosWallEars)
        {
            if (ear is null || !ear.IsValid) return null;
            if (ear.PlayerId != listener.Id) continue;
            if (result != null) return null;
            result = ear;
        }
        if (result == null) return null;
        double x = result.X - listener.X, y = result.Y - listener.Y;
        return x * x + y * y <= 1.6 * 1.6 ? result : null;
    }
}
