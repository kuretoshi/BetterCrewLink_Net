using System.Windows;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

public partial class VoiceView
{
    private static void VerifyGroupedRosterLayout()
    {
        if (CalculatePlayerLayout(1000, 1000, 1, 1).AvatarSize > 90 ||
            CalculatePlayerLayout(1000, 1000, 1).AvatarSize != 100)
            throw new InvalidOperationException("Grouped avatar must be capped at local90; standard must retain100");
        foreach (var count in new[] { 1, 2, 3, 6, 12 })
        foreach (var width in new[] { 280d, 400d })
        foreach (var height in new[] { 390d, 600d })
            VerifyGroupedRosterCase(count, width, height);
        Console.WriteLine("[PASS] Grouped roster full-width headers, centered rows, remote/local size cap and unchanged empty groups/standard layout");
    }

    private static void VerifyGroupedRosterCase(int count, double width, double height)
    {
        var view = new VoiceView();
        var game = new AmongUsState { GameState = GameState.Discussion, LobbyCode = "TEST" };
        game.Players.Add(new Player { Id = 0, ClientId = 100, IsLocal = true, Name = "Local" });
        game.Players.AddRange(Enumerable.Range(1, count).Select(id =>
            new Player { Id = id, ClientId = id + 100, Name = "Remote" }));
        var peers = new Dictionary<int, VoicePlayerStatus>();
        view.SetPlayerLayout("aliveAboveGhosts");
        foreach (var ghosts in new[] { 0, count / 2, count })
        {
            foreach (var player in game.Players) player.IsDead = !player.IsLocal && player.Id <= ghosts;
            view.Update(game, true, false, false, false, peers);
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            if (Math.Abs(view.GroupedPlayersPanel.ActualWidth - view.OtherPlayersScroll.ViewportWidth) > 0.5 ||
                Math.Abs(view.AlivePlayersHeader.ActualWidth - view.GroupedPlayersPanel.ActualWidth) > 0.5 ||
                Math.Abs(view.GhostPlayersHeader.ActualWidth - view.GroupedPlayersPanel.ActualWidth) > 0.5)
                throw new InvalidOperationException("Grouped roster headings do not span the available width");
            var aliveCenter = view.AlivePlayersPanel.TranslatePoint(new Point(view.AlivePlayersPanel.ActualWidth / 2, 0), view).X;
            var ghostCenter = view.GhostPlayersPanel.TranslatePoint(new Point(view.GhostPlayersPanel.ActualWidth / 2, 0), view).X;
            if (Math.Abs(aliveCenter - width / 2) > 0.5 || Math.Abs(ghostCenter - aliveCenter) > 0.5 ||
                view.remoteAvatars.Values.Any(avatar => avatar.Width > view.LocalAvatar.Width ||
                    avatar.ActualWidth > view.LocalAvatar.ActualWidth + 0.01 || avatar.ActualWidth > 90))
                throw new InvalidOperationException("Grouped rows are not centered consistently or exceed the local avatar size");
            if (view.AlivePlayersPanel.Children.Count != count - ghosts || view.GhostPlayersPanel.Children.Count != ghosts ||
                view.AlivePlayersHeader.Visibility != Visibility.Visible || view.GhostPlayersHeader.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Grouped roster changed an empty section or membership");
        }
        view.SetPlayerLayout("standard");
        view.UpdateLayout();
        if (view.OtherPlayersPanel.Children.Count != count || view.GroupedPlayersPanel.Visibility != Visibility.Collapsed ||
            view.remoteAvatars.Values.Any(avatar => avatar.Width > 100))
            throw new InvalidOperationException("Returning to standard roster changed its membership or size cap");
        view.popupCloseTimer.Stop();
    }
}
