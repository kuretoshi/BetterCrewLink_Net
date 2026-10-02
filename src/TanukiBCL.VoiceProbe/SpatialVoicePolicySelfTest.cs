using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class SpatialVoicePolicySelfTest
{
    public static int Run()
    {
        var failures = 0;
        foreach (var gameState in new[] { GameState.Tasks, GameState.Discussion })
        {
            var state = new AmongUsState { GameState = gameState };
            var settings = new SpatialVoiceSettings(ImpostorRadioEnabled: true);
            var sender = new Player { IsImpostor = true, X = 20 };

            Check($"{gameState}: impostor receives radio", true,
                SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true }, sender, settings, true).Audible);
            Check($"{gameState}: crewmate cannot receive radio", false,
                SpatialVoicePolicy.Calculate(state, new Player(), sender, settings, true).Audible);
            Check($"{gameState}: ghost receives radio", true,
                SpatialVoicePolicy.Calculate(state, new Player { IsDead = true }, sender, settings, true).Audible);
            Check($"{gameState}: radio stops when disabled", false,
                SpatialVoicePolicy.Calculate(state, new Player(), sender,
                    settings with { ImpostorRadioEnabled = false }, true).Audible);
            Check($"{gameState}: living cannot hear dead radio sender", false,
                SpatialVoicePolicy.Calculate(state, new Player { IsImpostor = true },
                    sender.WithDead(), settings, true).Audible);
        }

        var discussion = new AmongUsState { GameState = GameState.Discussion };
        Check("meeting: normal living conversation remains audible", true,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player(), new SpatialVoiceSettings()).Audible);
        Check("meeting: normal ghost voice remains private", false,
            SpatialVoicePolicy.Calculate(discussion, new Player(), new Player { IsDead = true },
                new SpatialVoiceSettings()).Audible);

        Console.WriteLine(failures == 0
            ? "[PASS] 3.2.7 radio policy: Tasks/Discussion, impostor/crew/ghost"
            : $"[FAIL] 3.2.7 radio policy: {failures} cases failed");
        return failures == 0 ? 0 : 1;

        void Check(string name, bool expected, bool actual)
        {
            if (expected == actual) return;
            failures++;
            Console.Error.WriteLine($"[FAIL] {name}: expected={expected} actual={actual}");
        }
    }

    private static Player WithDead(this Player source) => new()
    {
        IsImpostor = source.IsImpostor,
        IsDead = true,
        X = source.X,
        Y = source.Y
    };
}
