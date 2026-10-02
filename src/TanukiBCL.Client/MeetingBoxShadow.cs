using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TanukiBCL.Client;

// The released meeting HUD uses 0 0 r r color, with border-radius: r.
// Unlike a drop shadow of the translucent border, CSS starts with an opaque
// expanded rounded box and knocks out the original border box after blurring.
internal sealed record MeetingBoxShadow(BitmapSource Image, Rect Bounds)
{
    internal static MeetingBoxShadow Create(double width, double height, double radius, Color color, DpiScale dpi)
    {
        var scaleX = dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY;
        // CSS Backgrounds 3 §6.1.2: Gaussian sigma = blur radius / 2.
        // Four sigma of padding keeps truncated tails well below one alpha byte.
        var padX = Math.Ceiling((radius * 3 + 1) * scaleX) / scaleX;
        var padY = Math.Ceiling((radius * 3 + 1) * scaleY) / scaleY;
        var pixelsX = checked((int)Math.Ceiling((width + 2 * padX) * scaleX));
        var pixelsY = checked((int)Math.Ceiling((height + 2 * padY) * scaleY));
        var mask = new double[checked(pixelsX * pixelsY)];
        var expanded = new Rect(-radius, -radius, width + 2 * radius, height + 2 * radius);
        var original = new Rect(0, 0, width, height);
        for (var y = 0; y < pixelsY; y++)
        for (var x = 0; x < pixelsX; x++)
            mask[y * pixelsX + x] = Coverage(x, y, expanded, radius * 2);
        var horizontal = Blur(mask, pixelsX, pixelsY, radius * scaleX / 2, true);
        var blurred = Blur(horizontal, pixelsX, pixelsY, radius * scaleY / 2, false);
        var bytes = new byte[checked(mask.Length * 4)];
        for (var y = 0; y < pixelsY; y++)
        for (var x = 0; x < pixelsX; x++)
        {
            var i = y * pixelsX + x;
            var alpha = Math.Clamp(blurred[i] * (1 - Coverage(x, y, original, radius)), 0, 1) * color.A;
            bytes[i * 4] = Byte(color.B * alpha / 255);
            bytes[i * 4 + 1] = Byte(color.G * alpha / 255);
            bytes[i * 4 + 2] = Byte(color.R * alpha / 255);
            bytes[i * 4 + 3] = Byte(alpha);
        }
        var image = BitmapSource.Create(pixelsX, pixelsY, 96 * scaleX, 96 * scaleY,
            PixelFormats.Pbgra32, null, bytes, pixelsX * 4);
        image.Freeze();
        return new(image, new Rect(-padX, -padY, pixelsX / scaleX, pixelsY / scaleY));

        double Coverage(int x, int y, Rect rect, double corner)
        {
            corner = Math.Min(corner, Math.Min(rect.Width, rect.Height) / 2);
            var hits = 0;
            // Subpixel coverage only affects the rounded silhouette/knockout.
            for (var sy = 0; sy < 4; sy++)
            for (var sx = 0; sx < 4; sx++)
            {
                var px = (x + (sx + 0.5) / 4) / scaleX - padX;
                var py = (y + (sy + 0.5) / 4) / scaleY - padY;
                if (px < rect.Left || px > rect.Right || py < rect.Top || py > rect.Bottom) continue;
                var dx = px - Math.Clamp(px, rect.Left + corner, rect.Right - corner);
                var dy = py - Math.Clamp(py, rect.Top + corner, rect.Bottom - corner);
                if (dx * dx + dy * dy <= corner * corner) hits++;
            }
            return hits / 16d;
        }
    }

    private static byte Byte(double value) => (byte)Math.Floor(value + 0.5);

    private static double[] Blur(double[] input, int width, int height, double sigma, bool horizontal)
    {
        if (sigma <= 0) return input;
        var support = (int)Math.Ceiling(4 * sigma);
        var kernel = new double[2 * support + 1];
        var sum = 0d;
        for (var k = -support; k <= support; k++)
            sum += kernel[k + support] = Math.Exp(-k * k / (2 * sigma * sigma));
        for (var k = 0; k < kernel.Length; k++) kernel[k] /= sum;
        var output = new double[input.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = 0d;
            for (var k = -support; k <= support; k++)
            {
                var px = horizontal ? x + k : x;
                var py = horizontal ? y : y + k;
                if (px >= 0 && px < width && py >= 0 && py < height)
                    value += input[py * width + px] * kernel[k + support];
            }
            output[y * width + x] = value;
        }
        return output;
    }

    internal static void Verify()
    {
        foreach (var scale in new[] { 1d, 1.5, 2d })
        {
            var shadow = Create(120, 60, 6, Colors.Red, new DpiScale(scale, scale));
            var stride = shadow.Image.PixelWidth * 4;
            var pixels = new byte[stride * shadow.Image.PixelHeight];
            shadow.Image.CopyPixels(pixels, stride, 0);
            int Alpha(double x, double y)
            {
                var px = (int)((x - shadow.Bounds.X) * scale);
                var py = (int)((y - shadow.Bounds.Y) * scale);
                return pixels[py * stride + px * 4 + 3];
            }
            if (Alpha(60, 30) != 0 || Alpha(60, 1) != 0 || Alpha(1, 30) != 0)
                throw new InvalidOperationException("Meeting shadow must be knocked out inside the border box");
            if (Alpha(60, -2) < 210 || Alpha(60, -10) >= 40 || Alpha(60, -15) > 1)
                throw new InvalidOperationException("Meeting shadow spread or Gaussian falloff incorrect");
            // The corner is rounded, not a rectangular knockout.
            if (Alpha(0.2, 0.2) < 100)
                throw new InvalidOperationException("Rounded meeting shadow corner was clipped as a rectangle");
            for (var i = 0; i < pixels.Length; i += 4)
                if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != pixels[i + 3])
                    throw new InvalidOperationException("Meeting shadow premultiplied color is incorrect");
        }
        Console.WriteLine("[PASS] Meeting CSS shadow spread, Gaussian falloff, rounded knockout and premultiplied color at 100/150/200% DPI");
    }
}
