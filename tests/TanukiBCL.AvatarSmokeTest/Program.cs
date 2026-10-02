using System.Security.Cryptography;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TanukiBCL.Client;
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
            if (red == blue || red == ghost || red == disconnected)
            {
                throw new InvalidOperationException("Avatar appearance or state badge did not change the rendered image.");
            }

            Console.WriteLine("[PASS] v3.2.7 player/ghost templates, palette coloring, status badges, and WPF rendering");
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
}
