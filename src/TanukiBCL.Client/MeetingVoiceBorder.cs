using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace TanukiBCL.Client;

internal sealed class MeetingVoiceBorder : Border
{
    private bool? target;
    private AnimationClock? fade;
    private double fadeStart;
    private double fadeEnd;
    private double reversingStart;
    private double shorteningFactor = 1;
    private double shadowRadius;
    private MeetingBoxShadow? shadow;
    private (Size Size, DpiScale Dpi) shadowLayout;
    internal Color ShadowColor { get; private set; }

    internal void SetShadow(Color color, double radius)
    {
        if (ShadowColor == color && shadowRadius == radius) return;
        ShadowColor = color;
        shadowRadius = radius;
        shadow = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var layout = (RenderSize, VisualTreeHelper.GetDpi(this));
        if (RenderSize.Width > 0 && RenderSize.Height > 0 && shadowRadius > 0)
        {
            if (shadow is null || !shadowLayout.Equals(layout))
            {
                shadow = MeetingBoxShadow.Create(RenderSize.Width, RenderSize.Height,
                    shadowRadius, ShadowColor, layout.Item2);
                shadowLayout = layout;
            }
            drawingContext.DrawImage(shadow.Image, shadow.Bounds);
        }
        base.OnRender(drawingContext);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    internal void SetTalking(bool talking)
    {
        if (target == talking) return;
        var initial = target is null;
        target = talking;
        var from = Opacity;
        var to = talking ? 1d : 0d;
        if (initial) { Opacity = to; return; }
        if (from == to)
        {
            ApplyAnimationClock(OpacityProperty, null);
            fade = null;
            Opacity = to;
            shorteningFactor = 1;
            return;
        }
        // CSS Transitions §3: retain the reversing-adjusted start value and
        // shortening factor across repeated reversals, not merely elapsed time.
        if (fade?.CurrentState == ClockState.Active && reversingStart == to && fadeEnd != fadeStart)
        {
            var easedProgress = (from - fadeStart) / (fadeEnd - fadeStart);
            shorteningFactor = Math.Clamp(Math.Abs(easedProgress * shorteningFactor + 1 - shorteningFactor), 0, 1);
            reversingStart = fadeEnd;
        }
        else
        {
            shorteningFactor = 1;
            reversingStart = from;
        }
        fadeStart = from;
        fadeEnd = to;
        var duration = TimeSpan.FromMilliseconds(400 * shorteningFactor);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        // CSS transition: opacity 400ms uses the default 'ease' curve.
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration),
            new KeySpline(0.25, 0.1, 0.25, 1)));
        fade = (AnimationClock)((Timeline)animation).CreateClock(true);
        ApplyAnimationClock(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Verify()
    {
        MeetingBoxShadow.Verify();
        VerifyShadowRender();
        var box = new MeetingVoiceBorder();
        box.SetTalking(false);
        if (box.Opacity != 0) throw new InvalidOperationException("Initial meeting visibility incorrect");
        box.SetTalking(true);
        var first = box.fade!;
        first.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(200), TimeSeekOrigin.BeginTime);
        var midpoint = box.Opacity;
        if (midpoint <= 0.5 || midpoint >= 1) throw new InvalidOperationException("CSS ease fade midpoint incorrect");
        box.SetTalking(true);
        if (!ReferenceEquals(first, box.fade)) throw new InvalidOperationException("Unchanged VAD restarted fade");
        first.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(400), TimeSeekOrigin.BeginTime);
        if (Math.Abs(box.Opacity - 1) > 0.00001) throw new InvalidOperationException("Fade did not reach one");
        box.SetTalking(false);
        box.fade!.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(400), TimeSeekOrigin.BeginTime);
        if (Math.Abs(box.Opacity) > 0.00001) throw new InvalidOperationException("Fade did not reach zero");
        Console.WriteLine("[PASS] Meeting opacity initial state, 400ms CSS ease endpoints and unchanged VAD clock reuse");
        var reversing = new MeetingVoiceBorder();
        reversing.SetTalking(false);
        reversing.SetTalking(true);
        reversing.fade!.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(200), TimeSeekOrigin.BeginTime);
        var reached = reversing.Opacity;
        reversing.SetTalking(false);
        if (Math.Abs(reversing.shorteningFactor - reached) > 0.00001 ||
            Math.Abs(reversing.fade!.Timeline.Duration.TimeSpan.TotalMilliseconds - 400 * reached) > 0.001)
            throw new InvalidOperationException("Interrupted fade did not shorten by eased progress");
        reversing.fade.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(200 * reached), TimeSeekOrigin.BeginTime);
        var beforeSecondReverse = reversing.Opacity;
        reversing.SetTalking(true);
        if (Math.Abs(reversing.shorteningFactor - (1 - beforeSecondReverse)) > 0.00001 ||
            Math.Abs(reversing.fadeStart - beforeSecondReverse) > 0.00001)
            throw new InvalidOperationException("Repeated reversal lost accumulated shortening factor or current value");
        reversing.fade!.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(1), TimeSeekOrigin.BeginTime);
        reversing.SetTalking(false);
        if (reversing.shorteningFactor != 1 || reversing.fade!.Timeline.Duration.TimeSpan.TotalMilliseconds != 400)
            throw new InvalidOperationException("Completed transition retained reversal shortening");
        var immediate = new MeetingVoiceBorder();
        immediate.SetTalking(false);
        immediate.SetTalking(true);
        immediate.fade!.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        immediate.SetTalking(false);
        if (immediate.fade is not null || immediate.Opacity != 0)
            throw new InvalidOperationException("Zero-progress reversal should cancel without a new transition");
        Console.WriteLine("[PASS] Interrupted and repeated CSS reversal shortening; completed transition duration reset");
    }

    private static void VerifyShadowRender()
    {
        var box = new MeetingVoiceBorder
        {
            Width = 120, Height = 60, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x37, 0, 0, 0)),
            Background = Brushes.Transparent
        };
        box.SetShadow(Colors.Red, 6);
        box.SetTalking(true);
        var canvas = new Canvas { Width = 190, Height = 120 };
        Canvas.SetLeft(box, 25); Canvas.SetTop(box, 25); canvas.Children.Add(box);
        byte[] Render()
        {
            canvas.Measure(new Size(190, 120));
            canvas.Arrange(new Rect(0, 0, 190, 120));
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(190, 120, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var pixels = new byte[190 * 120 * 4];
            bitmap.CopyPixels(pixels, 190 * 4, 0);
            return pixels;
        }
        var first = Render();
        var outside = (23 * 190 + 85) * 4;
        var center = (55 * 190 + 85) * 4;
        if (first[outside + 2] < 210 || first[center + 3] != 0)
            throw new InvalidOperationException("WPF meeting shadow was clipped outside its slot or filled its interior");
        var cached = box.shadow;
        box.InvalidateVisual();
        Render();
        if (cached is null || !ReferenceEquals(cached, box.shadow))
            throw new InvalidOperationException("Unchanged meeting geometry did not reuse its shadow raster");
        box.SetShadow(Colors.Lime, 6);
        var recolored = Render();
        if (recolored[outside + 1] < 210 || recolored[outside + 2] != 0 || ReferenceEquals(cached, box.shadow))
            throw new InvalidOperationException("Meeting live color change did not replace the rendered shadow");
        cached = box.shadow;
        box.Width = 140;
        Render();
        if (ReferenceEquals(cached, box.shadow))
            throw new InvalidOperationException("Meeting resize retained stale shadow geometry");
        Console.WriteLine("[PASS] WPF meeting shadow outside slot, transparent interior, cached redraw, live color and resize");
    }
}
