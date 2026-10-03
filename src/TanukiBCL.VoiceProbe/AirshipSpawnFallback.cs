using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

// Mirrors the released AudioController's 15-second post-meeting Airship window.
internal sealed class AirshipSpawnFallback
{
    private GameState? previousGameState;
    private DateTimeOffset until;

    public void Update(AmongUsState state, DateTimeOffset now)
    {
        if (state.Map != MapType.Airship || state.GameState != GameState.Tasks)
        {
            until = default;
        }
        else if (previousGameState != GameState.Tasks && state.OldGameState == GameState.Discussion)
        {
            until = now.AddSeconds(15);
        }

        previousGameState = state.GameState;
    }

    public bool IsActive(AmongUsState state, DateTimeOffset now) =>
        state.Map == MapType.Airship && state.GameState == GameState.Tasks && now < until;
}
