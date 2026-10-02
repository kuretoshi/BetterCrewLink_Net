using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

/// <summary>
/// Recolors the v3.2.7 avatar templates using the same three-channel blend as
/// src/main/avatarGenerator.ts. Game-provided palettes supersede the defaults.
/// </summary>
internal static class AvatarImageFactory
{
    private const int RainbowColorId = -99234;
    private static readonly (Color Main, Color Shadow)[] DefaultColors =
    [
        (Rgb(0xC5, 0x11, 0x11), Rgb(0x7A, 0x08, 0x38)),
        (Rgb(0x13, 0x2E, 0xD1), Rgb(0x09, 0x15, 0x8E)),
        (Rgb(0x11, 0x7F, 0x2D), Rgb(0x0A, 0x4D, 0x2E)),
        (Rgb(0xED, 0x54, 0xBA), Rgb(0xAB, 0x2B, 0xAD)),
        (Rgb(0xEF, 0x7D, 0x0D), Rgb(0xB3, 0x3E, 0x15)),
        (Rgb(0xF5, 0xF5, 0x57), Rgb(0xC3, 0x88, 0x23)),
        (Rgb(0x3F, 0x47, 0x4E), Rgb(0x1E, 0x1F, 0x26)),
        (Rgb(0xFF, 0xFF, 0xFF), Rgb(0x83, 0x94, 0xBF)),
        (Rgb(0x6B, 0x2F, 0xBB), Rgb(0x3B, 0x17, 0x7C)),
        (Rgb(0x71, 0x49, 0x1E), Rgb(0x5E, 0x26, 0x15)),
        (Rgb(0x38, 0xFE, 0xDC), Rgb(0x24, 0xA8, 0xBE)),
        (Rgb(0x50, 0xEF, 0x39), Rgb(0x15, 0xA7, 0x42))
    ];

    private static readonly ConcurrentDictionary<(bool Dead, uint Main, uint Shadow), BitmapSource> Cache = new();
    private static readonly Lazy<BitmapSource> PlayerTemplate = new(() => Load("player.png"));
    private static readonly Lazy<BitmapSource> GhostTemplate = new(() => Load("ghost.png"));
    private static readonly Lazy<BitmapSource> RainbowPlayer = new(() => Load("rainbow-alive.png"));
    private static readonly Lazy<BitmapSource> RainbowGhost = new(() => Load("rainbow-dead.png"));

    public static BitmapSource Get(int colorId, bool isDead, IReadOnlyList<PlayerColorPair>? playerColors)
    {
        if (colorId == RainbowColorId)
        {
            return isDead ? RainbowGhost.Value : RainbowPlayer.Value;
        }

        var (main, shadow) = ResolveColors(colorId, playerColors);
        return Cache.GetOrAdd((isDead, Pack(main), Pack(shadow)), key =>
            Recolor(key.Dead ? GhostTemplate.Value : PlayerTemplate.Value, main, shadow));
    }

    private static (Color Main, Color Shadow) ResolveColors(int colorId, IReadOnlyList<PlayerColorPair>? playerColors)
    {
        if (playerColors is not null && colorId >= 0 && colorId < playerColors.Count)
        {
            var pair = playerColors[colorId];
            return (FromGameColor(pair.Main), FromGameColor(pair.Shadow));
        }

        return DefaultColors[colorId >= 0 && colorId < DefaultColors.Length ? colorId : 0];
    }

    private static BitmapSource Recolor(BitmapSource template, Color main, Color shadow)
    {
        var converted = new FormatConvertedBitmap(template, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];
            var highest = Math.Max(r, Math.Max(g, b));
            var chroma = highest - Math.Min(r, Math.Min(g, b));
            var saturation = highest == 0 ? 0d : chroma / (double)highest;
            if (saturation <= 0.4d) continue;

            var hue = chroma == 0 ? 0d : 60d * (highest == r
                ? (g - b) / (double)chroma
                : highest == g
                    ? 2d + (b - r) / (double)chroma
                    : 4d + (r - g) / (double)chroma);
            if (hue < 0) hue += 360d;
            if (!WithinHue(hue, 240d, 30d) &&
                !WithinHue(hue, 0d, 100d) &&
                !WithinHue(hue, 120d, 40d)) continue;

            // Color('#000').mix(shadow, b/255).mix(main, r/255)
            //     .mix('#9acad5', g/255), as in the upstream generator.
            pixels[offset] = Blend(Blend(shadow.B * b / 255d, main.B, r / 255d), 0xD5, g / 255d);
            pixels[offset + 1] = Blend(Blend(shadow.G * b / 255d, main.G, r / 255d), 0xCA, g / 255d);
            pixels[offset + 2] = Blend(Blend(shadow.R * b / 255d, main.R, r / 255d), 0x9A, g / 255d);
        }

        var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private static bool WithinHue(double hue, double target, double maximumDifference)
    {
        var difference = Math.Abs(hue - target);
        return Math.Min(difference, 360d - difference) < maximumDifference;
    }

    private static byte Blend(double start, double end, double weight) =>
        (byte)Math.Clamp(Math.Round(start * (1d - weight) + end * weight), 0d, 255d);

    private static BitmapSource Load(string file)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/TanukiBCL.Net;component/Assets/Avatar/{file}"));
        image.Freeze();
        return image;
    }

    private static Color FromGameColor(uint packed) =>
        Rgb((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16));

    private static uint Pack(Color color) =>
        (uint)(color.R << 16 | color.G << 8 | color.B);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
