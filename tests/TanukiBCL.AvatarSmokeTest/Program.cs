using System.Security.Cryptography;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TanukiBCL.Client;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.AvatarSmokeTest;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            _ = new Application();
            var red = Render(0, false, "connected", false, false, args.FirstOrDefault());
            var blue = Render(1, false, "connected", true, false);
            var ghost = Render(0, true, "novoice", false, true);
            var disconnected = Render(0, false, "disconnected", false, false);
            if (args.Length > 2) Render(0, false, "connected", false, true, args[2]);
            if (red == blue || red == ghost || red == disconnected)
            {
                throw new InvalidOperationException("Avatar appearance or state badge did not change the rendered image.");
            }
            VerifyAppearanceChangedAvatar();
            VerifyVoiceEffectSettingsControls();

            var voiceView = RenderVoiceView(args.Skip(1).FirstOrDefault());
            if (voiceView.Length != 64) throw new InvalidOperationException("VoiceView did not render.");

            Console.WriteLine("[PASS] v3.2.7 avatar, compact VoiceView, and voice-effect settings WPF rendering");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static string Render(int colorId, bool isDead, string connectionState, bool muted, bool usingRadio,
        string? previewPath = null)
    {
        var avatar = new PlayerAvatar();
        avatar.SetPlayer(new Player { Name = "Test", ColorId = colorId, IsDead = isDead }, null);
        avatar.SetVisualState(talking: true, muted, deafened: false, connectionState, usingRadio);
        avatar.Measure(new Size(67, 67));
        avatar.Arrange(new Rect(0, 0, 67, 67));
        avatar.UpdateLayout();

        var bitmap = new RenderTargetBitmap(67, 67, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(avatar);
        if (previewPath is not null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(previewPath);
            encoder.Save(output);
        }
        var pixels = new byte[67 * 67 * 4];
        bitmap.CopyPixels(pixels, 67 * 4, 0);
        if (pixels.All(value => value == 0))
        {
            throw new InvalidOperationException("Avatar rendered as an empty image.");
        }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static void VerifyAppearanceChangedAvatar()
    {
        var avatar = new PlayerAvatar();
        var player = new Player { Name = "Original", AppearanceName = "Disguised",
            ColorId = 1, CurrentOutfit = 1, AppearanceColorId = 2 };
        avatar.SetPlayer(player, null, hideWhenAppearanceChanged: true);
        if (avatar.FindName("AvatarBody") is not System.Windows.Shapes.Ellipse body ||
            body.Visibility != Visibility.Hidden)
            throw new InvalidOperationException("Changed outfit should hide the avatar during tasks.");
        avatar.SetPlayer(player, null, hideWhenAppearanceChanged: false);
        if (body.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Changed outfit should be visible during meetings.");
    }

    private static void VerifyVoiceEffectSettingsControls()
    {
        var assembly = typeof(VoiceView).Assembly;
        var settingsType = assembly.GetType("TanukiBCL.Client.ClientSettings", throwOnError: true)!;
        var windowType = assembly.GetType("TanukiBCL.Client.SettingsWindow", throwOnError: true)!;
        var settings = Activator.CreateInstance(settingsType, nonPublic: true)!;
        settingsType.GetProperty("VoiceEffectStrength")!.SetValue(settings, 63);
        var constructor = windowType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info => info.GetParameters().Length == 6);
        var window = (Window)constructor.Invoke([settings, true, null, false, null,
            (Action<int, PlayerAudioConfig, bool>)((_, _, _) => { })]);
        try
        {
            if (window.FindName("VoiceEffectStrengthSlider") is not Slider slider || slider.Value != 63d ||
                window.FindName("VoiceEffectEnabledCheck") is not CheckBox toggle || toggle.IsChecked != true)
                throw new InvalidOperationException("Voice disguise settings did not initialize from v3.2.7 defaults.");
        }
        finally
        {
            window.Close();
        }
    }

    private static string RenderVoiceView(string? previewPath)
    {
        var view = new VoiceView();
        var game = new AmongUsState
        {
            GameState = GameState.Tasks,
            LobbyCode = "NFBNOC",
            Players =
            [
                new Player { ClientId = 1, Name = "開発者くれとし", ColorId = 2, IsLocal = true },
                new Player { ClientId = 2, Name = "クルー", ColorId = 1 },
                new Player { ClientId = 3, Name = "インポスター", ColorId = 3 },
                new Player { ClientId = 4, Name = "幽霊", ColorId = 5, IsDead = true }
            ]
        };
        var peers = new Dictionary<int, VoicePlayerStatus>
        {
            [2] = new("connected", true, false, new ConnectionQuality(RttMs: 25d)),
            [3] = new("novoice", false, false),
            [4] = new("connected", false, false, new ConnectionQuality(RttMs: 180d))
        };
        view.Update(game, connected: true, localTalking: true, muted: false, deafened: false, peers,
            serverQuality: new ConnectionQuality(ServerPingMs: 25d));
        view.SetDetectedMod("Nebula on the Ship");
        view.Measure(new Size(280, 390));
        view.Arrange(new Rect(0, 0, 280, 390));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(280, 390, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var pixels = new byte[280 * 390 * 4];
        bitmap.CopyPixels(pixels, 280 * 4, 0);
        if (previewPath is not null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(previewPath);
            encoder.Save(output);
        }
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
}
