using System.Windows;

namespace TanukiBCL.Client;

internal sealed record MeetingOverlayLayout(double Width, double Height, bool OldHud)
{
    internal static MeetingOverlayLayout Create(double windowWidth, double windowHeight, bool oldHud)
    {
        if (!double.IsFinite(windowWidth) || !double.IsFinite(windowHeight) || windowWidth <= 0 || windowHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowWidth));
        if (oldHud)
        {
            const double ipadRatio = 854d / 579;
            // Preserve released Overlay.tsx, including its [hudWidth,hudWidth]
            // return and width-based 0.96 multiplier (not a guessed correction).
            var width = windowWidth / (windowHeight * 0.96) > ipadRatio
                ? windowWidth * 0.96 * ipadRatio : windowWidth;
            return new(width, width, true);
        }
        var difference = Math.Abs(windowWidth / windowHeight - 1.7);
        var w = windowWidth / (difference < 0.25 ? 1.192 : difference < 0.5 ? 1.146 : 1.591);
        return new(w, w / 1.72, false);
    }

    internal Rect Slot(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var containerWidth = Width * (OldHud ? 0.8845 : 1);
        var slotWidth = containerWidth * (OldHud ? 0.4641 : 0.30);
        var slotHeight = Height * 0.105 * (OldHud ? 1 : 1.09);
        var leftMargin = containerWidth * (OldHud ? 0 : 0.024);
        var rightMargin = containerWidth * (OldHud ? 0.0234 : 0.0023);
        // CSS percentage margin-bottom is relative to the container WIDTH.
        var bottomMargin = containerWidth * (OldHud ? 0.02 : 0.019);
        var columns = OldHud ? 2 : 3;
        return new Rect(Width * (OldHud ? 0.047 : 0.004) + leftMargin +
            index % columns * (slotWidth + leftMargin + rightMargin),
            Height * (OldHud ? 0.184703 : 0.15) + index / columns * (slotHeight + bottomMargin),
            slotWidth, slotHeight);
    }

    internal static void Verify()
    {
        static void Near(double actual, double expected)
        { if (Math.Abs(actual - expected) > 0.000001) throw new InvalidOperationException($"Meeting layout: {actual} != {expected}"); }
        var modern = new MeetingOverlayLayout(1000, 600, false);
        var first = modern.Slot(0); var nextRow = modern.Slot(3);
        Near(first.X, 28); Near(first.Y, 90); Near(first.Width, 300); Near(first.Height, 68.67);
        Near(modern.Slot(1).X, 354.3); Near(nextRow.X, 28); Near(nextRow.Y, 177.67);
        var legacy = new MeetingOverlayLayout(1000, 1000, true);
        Near(legacy.Slot(0).X, 47); Near(legacy.Slot(0).Y, 184.703);
        Near(legacy.Slot(0).Width, 410.49645); Near(legacy.Slot(0).Height, 105);
        Near(legacy.Slot(1).X, 478.19375); Near(legacy.Slot(2).Y, 307.393);
        Near(Create(1000, 1000, true).Width, 1000);
        var wide = Create(1920, 1080, true);
        Near(wide.Width, 1920 * 0.96 * 854 / 579); Near(wide.Height, wide.Width);
        Near(Create(1700, 1000, false).Width, 1700 / 1.192);
        Near(Create(2100, 1000, false).Width, 2100 / 1.146);
        Near(Create(2500, 1000, false).Width, 2500 / 1.591);
        Console.WriteLine("[PASS] Legacy/modern meeting HUD dimensions, CSS percentage margins and row wrapping");
    }
}
