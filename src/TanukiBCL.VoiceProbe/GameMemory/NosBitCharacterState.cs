namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosBitCharacterState
{
    public int PlayerId { get; set; } = -1;
    public int Stage { get; set; } = -1;
    public bool IsValid => PlayerId is >= 0 and <= 255 && Stage is >= 0 and <= 6;
    public static int For(AmongUsState state, Player speaker)
    {
        if (state.Mod != AmongUsModType.NebulaOnTheShip || state.GameState != GameState.Tasks || speaker.IsDead || speaker.Disconnected) return -1;
        var rows = state.NosBitCharacters;
        if (rows.Count > 24 || rows.Any(p => p == null || !p.IsValid) || rows.Select(p => p.PlayerId).Distinct().Count() != rows.Count) return -1;
        int stage = rows.FirstOrDefault(p => p.PlayerId == speaker.Id)?.Stage ?? -1;
        return stage is >= 0 and < 6 ? stage : -1;
    }
}
