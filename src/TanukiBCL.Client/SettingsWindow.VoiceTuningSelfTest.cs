using System.Windows.Controls;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class SettingsWindow
{
    private static void VerifyVoiceTuningControls()
    {
        var settings = new ClientSettings { VoiceEffectStrength = 37, NosRainbowStarEchoStrength = 64 };
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip,
            NosAddonIds = ["UchuAddon", "tanuki.bcl.roles"] };
        var window = new SettingsWindow(settings, true, new LobbySettings(), false, state, (_, _, _) => { }, _ => { });
        try
        {
            window.VoiceEffectPreviewCombo.SelectedItem = window.VoiceEffectPreviewCombo.Items.Cast<ComboBoxItem>()
                .Single(item => item.Tag is VoiceEffectPreviewKind.Rokurokubi);
            if (window.RokuroVoicePanel.Visibility != System.Windows.Visibility.Visible || window.RokuroBodyVolumeSlider.Value != 35 || window.RokuroBodyEchoSlider.Value != 50)
                throw new InvalidOperationException("Rokuro two-source controls/defaults missing");
            window.RokuroHeadVolumeSlider.Value = 80;
            window.RokuroBodyVolumeSlider.Value = 25;
            window.RokuroBodyEchoSlider.Value = 75;
            var savedRokuro = window.ReadSettingsControls().RokuroVoice;
            if (savedRokuro != new RokuroVoiceSettings(80, 25, 75) ||
                System.Text.Json.JsonSerializer.Deserialize<ClientSettings>(System.Text.Json.JsonSerializer.Serialize(window.ReadSettingsControls()))?.RokuroVoice != savedRokuro)
                throw new InvalidOperationException("Rokuro controls lost values during settings persistence");
            window.VoiceEffectPreviewCombo.SelectedItem = window.VoiceEffectPreviewCombo.Items.Cast<ComboBoxItem>()
                .Single(item => item.Tag is VoiceEffectPreviewKind.RainbowStar);
            if (window.TuningPitchSlider.Minimum != -100 || window.TuningPitchSlider.Maximum != 100 ||
                window.TuningPitchSlider.Value != 0 || window.TuningReverbSlider.Value != 64 || window.TuningEchoSlider.Value != 0)
                throw new InvalidOperationException("Voice tuning did not resolve legacy defaults/ranges");
            window.TuningPitchSlider.Value = -50;
            window.TuningReverbSlider.Value = 40;
            window.TuningEchoSlider.Value = 12;
            var tuned = settings.LocalVoiceEffectStrengths.Tuning("nosRainbowStar");
            if (tuned != new VoiceEffectTuning(-50, 40, 12) || settings.NosRainbowStarEchoStrength != 64 || settings.VoiceEffectStrength != 37)
                throw new InvalidOperationException("Voice tuning changed another axis or legacy setting");
            var clone = settings.Clone();
            if (clone.LocalVoiceEffectStrengths.Tuning("nosRainbowStar") != tuned)
                throw new InvalidOperationException("Voice tuning did not survive settings serialization");
            window.VoiceEffectPreviewCombo.SelectedIndex = 0;
            window.TuningPitchSlider.Value = 0;
            if (!settings.LocalVoiceEffectStrengths.Tuning("disguise", settings.VoiceEffectStrength).IsNeutral ||
                settings.LocalVoiceEffectStrengths.Tuning("nosRainbowStar") != tuned)
                throw new InvalidOperationException("Per-effect tuning is not independent");
        }
        finally { window.Close(); }
        Console.WriteLine("[PASS] Compact voice tuning: signed pitch, independent axes, legacy preservation and round-trip");
    }
}
