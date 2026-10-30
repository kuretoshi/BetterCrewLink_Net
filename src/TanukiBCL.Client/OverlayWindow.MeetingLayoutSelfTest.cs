using System.Windows;
using System.Windows.Controls;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class OverlayWindow
{
    private static void VerifyLargeNosMeetings()
    {
        foreach (var (width, height) in new[] { (1920d, 1080d), (1280d, 720d), (1024d, 768d) })
        foreach (var count in new[] { 15, 16, 18, 19, 24 })
            VerifyMeetingLayoutCase(width, height, count);
        Console.WriteLine("[PASS] WPF NoS meeting slots, viewport bounds, frozen identities and late-death reorder across 15 cases");
    }

    private static void VerifyMeetingLayoutCase(double width, double height, int count)
    {
        var settings = new ClientSettings { EnableOverlay = true, MeetingOverlay = true, OverlayPosition = "hidden" };
        var window = new OverlayWindow(0, settings) { Width = width, Height = height };
        window.placementTimer.Stop();
        try
        {
            var state = new AmongUsState { GameState = GameState.Discussion, Mod = AmongUsModType.NebulaOnTheShip };
            state.Players.AddRange(Enumerable.Range(0, count).Reverse().Select(id =>
                new Player { Id = id, ClientId = id + 100, IsLocal = id == 0, IsDead = id == 2, Name = "fixture" }));
            var peers = new Dictionary<int, OverlayPeerStatus> { [101] = new(true, true, false) };
            window.Update(state, peers, false, false, false);
            var layout = MeetingOverlayLayout.Create(width, height, false, true, count);
            if (window.meetingOrder.Count != count || window.MeetingCanvas.Children.Count != count ||
                window.meetingOrder[^1].Id != 2 || window.meetingSlots[1].Opacity != 1)
                throw new InvalidOperationException("Large NoS meeting lost death order or speaking identity");
            foreach (var (player, index) in window.meetingOrder.Select((player, index) => (player, index)))
            {
                var slot = window.meetingSlots[player.Id];
                var expected = layout.Slot(index);
                if (slot.Width != expected.Width || slot.Height != expected.Height ||
                    Canvas.GetLeft(slot) != expected.X || Canvas.GetTop(slot) != expected.Y)
                    throw new InvalidOperationException("NoS geometry was not applied to the actual WPF slot");
                var bounds = new Rect(Canvas.GetLeft(window.MeetingCanvas) + Canvas.GetLeft(slot),
                    Canvas.GetTop(window.MeetingCanvas) + Canvas.GetTop(slot), slot.Width, slot.Height);
                if (count > 15 && !new Rect(0, 0, width, height).Contains(bounds))
                    throw new InvalidOperationException("Large NoS meeting slot extends beyond the viewport");
            }
            var retained = window.meetingSlots[1];
            state.Players.First(player => player.Id == 1).IsDead = true;
            window.Update(state, peers, false, false, false);
            if (!ReferenceEquals(retained, window.meetingSlots[1]) || window.meetingOrder[^2].Id != 1 ||
                Canvas.GetTop(retained) != layout.Slot(count - 2).Y || Canvas.GetLeft(retained) != layout.Slot(count - 2).X)
                throw new InvalidOperationException("Large NoS late death changed the voice identity or retained its old position");
        }
        finally { window.Close(); }
    }
}
