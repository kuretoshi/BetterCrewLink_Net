using System.Windows.Controls;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class OverlayWindow
{
    private static void VerifySnrMeetingPages()
    {
        foreach (var count in new[] { 16, 30, 31, 45 })
        foreach (var (width, height) in new[] { (1920d, 1080d), (1024d, 768d) })
        {
            var window = new OverlayWindow(0, new ClientSettings
                { EnableOverlay = true, MeetingOverlay = true, OverlayPosition = "hidden" })
                { Width = width, Height = height };
            window.placementTimer.Stop();
            try { VerifySnrPageSequence(window, count); }
            finally { window.Close(); }
        }
        Console.WriteLine("[PASS] WPF SNR actual 15-slot pages, page-only changes, local/remote voice, missing IDs, fixed card sizes and invalid-page clearing (8 cases)");
    }

    private static void VerifySnrPageSequence(OverlayWindow window, int count)
    {
        var ids = Enumerable.Range(0, count).Reverse().ToArray();
        var state = new AmongUsState { GameState = GameState.Discussion, Mod = AmongUsModType.SuperNewRoles };
        state.Players.AddRange(Enumerable.Range(0, count).Select(id =>
            new Player { Id = id, ClientId = 100 + id, IsLocal = id == 0, IsDead = id == 1, Disconnected = id == 2 }));
        var peers = new Dictionary<int, OverlayPeerStatus> { [115] = new(true, true, false) };
        var layout = MeetingOverlayLayout.Create(window.Width, window.Height, false);
        foreach (var page in new[] { 1, 2, (count + 14) / 15, 1 })
        {
            state.SnrMeeting = new(true, page, ids);
            window.Update(state, peers, true, false, false);
            var visible = state.SnrMeeting.PageIds();
            if (!window.meetingSlots.Keys.Order().SequenceEqual(visible.Order()))
                throw new InvalidOperationException("SNR page did not remove hidden participants");
            foreach (var (id, index) in visible.Select((id, index) => (id, index)))
            {
                var slot = window.meetingSlots[id];
                var expected = layout.Slot(index);
                if (slot.Width != expected.Width || slot.Height != expected.Height ||
                    Canvas.GetLeft(slot) != expected.X || Canvas.GetTop(slot) != expected.Y ||
                    slot.Opacity != (id is 0 or 15 ? 1 : 0))
                    throw new InvalidOperationException("SNR page resized/reordered a slot or used the wrong voice identity");
            }
        }
        state.SnrMeeting = new(true, 2, ids);
        // A missing live player must not slide the next identity into its slot.
        if (count > 17) state.Players.RemoveAll(player => player.Id == ids[16]);
        window.Update(state, peers, false, false, false);
        if (count > 17 && (window.meetingSlots.ContainsKey(ids[16]) ||
            Canvas.GetLeft(window.meetingSlots[ids[17]]) != layout.Slot(2).X ||
            Canvas.GetTop(window.meetingSlots[ids[17]]) != layout.Slot(2).Y))
            throw new InvalidOperationException("SNR missing participant shifted another voice slot");
        state.SnrMeeting = new(true, 99, ids);
        window.Update(state, peers, false, false, false);
        if (window.MeetingCanvas.Children.Count != 0) throw new InvalidOperationException("Invalid SNR page retained stale frames");
        state.SnrMeeting = null;
        window.Update(state, peers, false, false, false);
        if (window.MeetingCanvas.Children.Count != 0) throw new InvalidOperationException("Unreadable SNR page retained stale frames");
    }
}
