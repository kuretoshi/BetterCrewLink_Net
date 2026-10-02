using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace TanukiBCL.Client;

internal sealed class MeetingVoiceBorder : Border
{
    private bool? target;
    private AnimationClock? fade;

    internal void SetTalking(bool talking)
    {
        if (target == talking) return;
        var initial = target is null;
        target = talking;
        var from = Opacity;
        var to = talking ? 1d : 0d;
        if (initial) { Opacity = to; return; }
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        // CSS transition: opacity 400ms uses the default 'ease' curve.
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
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
    }
}
