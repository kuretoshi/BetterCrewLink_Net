using System.Windows;

namespace TanukiBCL.Client;

internal sealed record MeetingOverlayLayout(double Width, double Height, bool OldHud,
    int NosPlayerCount = 0, double NosUnit = 0)
{
    internal static MeetingOverlayLayout Create(double windowWidth, double windowHeight, bool oldHud,
        bool noS = false, int playerCount = 0)
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
        return new(w, w / 1.72, false, noS && playerCount > 15 ? playerCount : 0,
            Math.Min(windowHeight / 6, windowWidth / (8.54 * 1.2)));
    }

    internal Rect Slot(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (NosPlayerCount > 15) return NosSlot(index);
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

    private Rect NosSlot(int index)
    {
        // Official NoS More15Helpers, projected from the serialized MeetingHud prefab.
        // UI camera half-height 3; AspectSize fits its 8.54 * 1.2-wide background.
        var compact = NosPlayerCount <= 18;
        var columns = compact ? 3 : 4;
        var scale = compact ? 0.95 : 0.76;
        var width = 2.7 * scale * NosUnit;
        var height = 0.63 * scale * NosUnit;
        var originX = -3.1 + 0.25 + (compact ? 0.1 : -0.355);
        var originY = 1.5 + (compact ? 0.145 : 0);
        return new Rect(Width / 2 + (originX + index % columns * 2.9 * scale * (compact ? 1 : 0.974)) * NosUnit - width / 2,
            Height / 2 - (originY - index / columns * 0.76 * scale * (compact ? 0.89 : 1)) * NosUnit - height / 2,
            width, height);
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
        VerifyNoS();
        Console.WriteLine("[PASS] Legacy/modern meeting HUD dimensions, CSS percentage margins and row wrapping");
    }

    private static void VerifyNoS()
    {
        // Independent projected prefab anchors, including AspectSize's narrow-screen limit.
        var anchors = new[] {
            (1920d, 1080d, 16, new Rect(234.15, 190.035, 461.7, 107.73)),
            (1920d, 1080d, 19, new Rect(198.42, 226.908, 369.36, 86.184)),
            (1024d, 768d, 16, new Rect(109.064793130367, 189.726775956284, 256.299765807963, 59.803278688525)),
            (1024d, 768d, 19, new Rect(89.230288836846, 210.195784543325, 205.039812646370, 47.842622950820))
        };
        foreach (var (width, height, count, expected) in anchors)
        {
            var layout = Create(width, height, false, true, count);
            var actual = layout.Slot(0);
            actual.Offset((width - layout.Width) / 2, (height - layout.Height) / 2);
            if (Math.Abs(actual.X - expected.X) > 0.000001 || Math.Abs(actual.Y - expected.Y) > 0.000001 ||
                Math.Abs(actual.Width - expected.Width) > 0.000001 || Math.Abs(actual.Height - expected.Height) > 0.000001)
                throw new InvalidOperationException("NoS meeting projection differs from serialized prefab anchors");
        }
        foreach (var count in new[] { 15, 16, 18, 19, 24 })
        {
            var layout = Create(1920, 1080, false, true, count);
            var columns = count <= 18 ? 3 : 4;
            if (layout.Slot(columns).X != layout.Slot(0).X || layout.Slot(columns).Y <= layout.Slot(0).Y)
                throw new InvalidOperationException("NoS meeting column boundary differs from More15Helpers");
            if (Create(1920, 1080, false).Slot(count - 1) != Create(1920, 1080, false, false, count).Slot(count - 1) ||
                Create(1920, 1080, true).Slot(count - 1) != Create(1920, 1080, true, true, count).Slot(count - 1))
                throw new InvalidOperationException("NoS meeting projection changed normal or old HUD geometry");
        }
        if (Create(1920, 1080, false).Slot(14) != Create(1920, 1080, false, true, 15).Slot(14))
            throw new InvalidOperationException("NoS meeting projection changed the 15-player HUD");
        Console.WriteLine("[PASS] NoS 16/18/19/24 meeting projection, 4:3 AspectSize and unchanged normal/legacy HUD");
    }
}
