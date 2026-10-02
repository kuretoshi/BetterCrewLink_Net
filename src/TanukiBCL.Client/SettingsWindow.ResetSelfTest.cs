using System.IO;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private static void VerifyResetDefaults()
    {
        var settings = new ClientSettings { MasterVolume = 43, NatFix = true,
            ServerUrl = "https://example.test", PlayerConfigMap = new() { [7] = PlayerAudioConfig.Default } };
        var saves = new List<ClientSettings>();
        var fail = false;
        var resetCount = 0;
        var window = new SettingsWindow(settings, true, null, false, null, (_, _, _) => { }, value =>
        {
            if (fail) throw new IOException("injected reset failure");
            saves.Add(value.Clone());
        });
        window.SettingsReset += () => resetCount++;
        try
        {
            Click(window.ResetDefaultsButton);
            Click(window.ConfirmDialog.CancelButton);
            RequireLive(settings.MasterVolume == 43 && saves.Count == 0, "Canceled reset changed settings");
            Click(window.ResetDefaultsButton);
            window.UpdateCurrentGameState(new AmongUsState { IsHost = true, GameState = GameState.Tasks });
            Click(window.ConfirmDialog.ConfirmButton);
            RequireLive(!window.ResetDefaultsButton.IsEnabled && saves.Count == 0,
                "Host reset allowed after game started during confirmation");
            window.UpdateCurrentGameState(new AmongUsState { IsHost = true, GameState = GameState.Discussion });
            RequireLive(!window.ResetDefaultsButton.IsEnabled, "Host meeting reset allowed");
            window.UpdateCurrentGameState(new AmongUsState { IsHost = false, GameState = GameState.Tasks });
            RequireLive(window.ResetDefaultsButton.IsEnabled, "Non-host reset blocked");
            window.UpdateCurrentGameState(new AmongUsState { IsHost = true, GameState = GameState.Lobby });
            window.DistanceSlider.Value = 9;
            RequireLive(window.lobbyPending, "Reset test has no pending draft");
            fail = true;
            Click(window.ResetDefaultsButton);
            Click(window.ConfirmDialog.ConfirmButton);
            RequireLive(settings.MasterVolume == 43 && window.lobbyPending && resetCount == 0,
                "Failed reset discarded prior settings or draft");
            fail = false;
            Click(window.ResetDefaultsButton);
            Click(window.ConfirmDialog.ConfirmButton);
            var defaults = new ClientSettings();
            defaults.Normalize();
            RequireLive(settings.ContentEquals(defaults) && saves.Count == 1 && saves[0].ContentEquals(defaults),
                "Reset did not persist all defaults including player configuration");
            RequireLive(!window.lobbyPending && !window.settingsReady && resetCount == 1,
                "Reset did not invalidate drafts and notify session");
            PumpFor(TimeSpan.FromMilliseconds(850));
            RequireLive(saves.Count == 1 && settings.ContentEquals(defaults), "Old draft overwrote reset");
        }
        finally { window.Close(); }
    }
}
