using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class AvatarImageService
{
    private readonly Dictionary<string, ImageSource> cache = [];
    private byte[]? playerTemplate;
    private int pixelWidth;
    private int pixelHeight;
    private int stride;

    public ImageSource GetPlayerImage(Color main, Color shadow)
    {
        var key = $"{main.R:X2}{main.G:X2}{main.B:X2}-{shadow.R:X2}{shadow.G:X2}{shadow.B:X2}";
        if (cache.TryGetValue(key, out var image))
        {
            return image;
        }

        EnsureTemplateLoaded();
        var pixels = (byte[])playerTemplate!.Clone();

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var a = pixels[i + 3];
            if (a == 0)
            {
                continue;
            }

            var (h, s, _) = RgbToHsv(r, g, b);
            if (s > 0.4 && (IsBetween(h, 240, 30) || IsBetween(h, 0, 100) || IsBetween(h, 120, 40)))
            {
                var color = Mix(Colors.Black, shadow, b / 255d);
                color = Mix(color, main, r / 255d);
                color = Mix(color, Color.FromRgb(0x9A, 0xCA, 0xD5), g / 255d);
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
            }
        }

        var source = BitmapSource.Create(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        source.Freeze();
        cache[key] = source;
        return source;
    }

    private void EnsureTemplateLoaded()
    {
        if (playerTemplate is not null)
        {
            return;
        }

        var uri = new Uri("pack://application:,,,/Assets/avatar/player-template.png", UriKind.Absolute);
        var bitmap = new BitmapImage(uri);
        var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        pixelWidth = formatted.PixelWidth;
        pixelHeight = formatted.PixelHeight;
        stride = pixelWidth * 4;
        playerTemplate = new byte[stride * pixelHeight];
        formatted.CopyPixels(playerTemplate, stride, 0);
    }

    private static Color Mix(Color current, Color next, double weight)
    {
        weight = Math.Clamp(weight, 0, 1);
        var inverse = 1 - weight;
        return Color.FromRgb(
            (byte)Math.Round((current.R * inverse) + (next.R * weight)),
            (byte)Math.Round((current.G * inverse) + (next.G * weight)),
            (byte)Math.Round((current.B * inverse) + (next.B * weight)));
    }

    private static bool IsBetween(double h, double h1, double maxDifference)
    {
        return 180 - Math.Abs(Math.Abs(h - h1) - 180) < maxDifference;
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        var red = r / 255d;
        var green = g / 255d;
        var blue = b / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var chroma = max - min;
        var hue = 0d;

        if (chroma != 0)
        {
            if (max == red)
            {
                hue = 60 * (((green - blue) / chroma) % 6);
            }
            else if (max == green)
            {
                hue = 60 * (((blue - red) / chroma) + 2);
            }
            else
            {
                hue = 60 * (((red - green) / chroma) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max == 0 ? 0 : chroma / max, max);
    }
}
