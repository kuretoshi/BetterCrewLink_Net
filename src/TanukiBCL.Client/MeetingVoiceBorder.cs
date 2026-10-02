using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace TanukiBCL.Client;

internal sealed class MeetingVoiceBorder : Border
{
    private bool? target;
    private AnimationClock? fade;
    private double fadeStart;
    private double fadeEnd;
    private double reversingStart;
    private double shorteningFactor = 1;

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
}
