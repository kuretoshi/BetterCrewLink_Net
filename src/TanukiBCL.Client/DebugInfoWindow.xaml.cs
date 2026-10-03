using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

internal sealed record DebugInfoSnapshot(string ModName, string Live, string SnrRoles,
    string GameState, string VoiceConnection, AmongUsState? State = null);

internal sealed record DebugLiveRow(string NameId, string RoleTeam, string Status,
    string Conversation, string Appearance, string Size, string Position, bool IsLocal);

public partial class DebugInfoWindow : Window
{
    private readonly Func<DebugInfoSnapshot> capture;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    internal DebugInfoWindow(Func<DebugInfoSnapshot> capture)
    {
        InitializeComponent();
        this.capture = capture;
        refreshTimer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            refreshTimer.Start();
        };
        Closed += (_, _) => refreshTimer.Stop();
    }

    private void DebugTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DebugTabs) && IsLoaded) Refresh();
    }

    private void Refresh()
    {
        try
        {
            var snapshot = capture();
            ModNameText.Text = $"起動中のMOD: {snapshot.ModName}";
            var tab = DebugTabs.SelectedIndex;
            HelpText.Text = tab == 4
                ? "最新のログ（最大64KB）を1秒ごとに更新します。"
                : "ゲーム・音声の状態を自動更新します。";
            LivePanel.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
            DebugText.Visibility = tab == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (tab == 0)
            {
                LiveSummaryText.Text = snapshot.Live;
                LivePlayersGrid.ItemsSource = snapshot.State?.Players.Select(FormatLiveRow).ToArray() ?? [];
            }
            DebugText.Text = tab switch
            {
                1 => snapshot.SnrRoles,
                2 => snapshot.GameState,
                3 => snapshot.VoiceConnection,
                4 => ReadLogTail(),
                _ => string.Empty
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (DebugTabs.SelectedIndex == 0) LiveSummaryText.Text = $"診断情報を取得できませんでした: {exception.Message}";
            else DebugText.Text = $"診断情報を取得できませんでした: {exception.Message}";
        }
    }

    private static DebugLiveRow FormatLiveRow(Player player)
    {
        var role = player.SnrRole?.RoleName ?? player.TohRole?.RoleName ??
            (player.IsImpostor ? "Impostor" : "Crewmate");
        var roleDetails = player.SnrRole is { } snr
            ? $"\nSNR IsNeutral: {Value(snr.IsNeutral)}\nSNR CanKill: {Value(snr.CanKill)}" +
              $"\n第三陣営キル役職の対象: {(snr.IsNeutralKiller ? "はい" : "いいえ／未取得")}"
            : player.TohRole is { } toh
                ? $"\nRoleId: {toh.RoleId}\nIKiller: {Value(toh.IsKiller)}" +
                  (toh.RoleName == "Opportunist" ? $"\nOpportunist.CanKill: {Value(toh.OpportunistCanKill)}" : "")
                : player.NosPlayer is { } nos
                    ? $"\nIsNeutral={nos.IsNeutral}\nIsKiller={nos.IsKiller}\nIsImpostor={nos.IsImpostor}\nIsCrewmate={nos.IsCrewmate}"
                    : "";
        var appearance = $"{(string.IsNullOrEmpty(player.AppearanceName) ? player.Name : player.AppearanceName)}" +
            $"\nOutfit: {player.CurrentOutfit} / 色: {player.ColorId} → {player.AppearanceColorId}" +
            $"\nSkin: {Empty(player.AppearanceSkinId)}\nHat: {Empty(player.AppearanceHatId)}" +
            $"\nVisor: {Empty(player.AppearanceVisorId)}\nPet: {(player.PetId == 0 ? "なし" : player.PetId)}";
        if (player.NosPlayer is { } nosAppearance)
            appearance += $"\nNoS: {nosAppearance.Name} / RGB: " +
                $"{nosAppearance.ColorR:F3}, {nosAppearance.ColorG:F3}, {nosAppearance.ColorB:F3}";
        var size = player.NosPlayer is { } nosSize
            ? $"BodyRateX: {NullableNumber(nosSize.BodyRateX)}\nBodyRateY: {NullableNumber(nosSize.BodyRateY)}"
            : "";
        if (player.SnrRole is { HasJumbo: true } jumbo && jumbo.JumboCurrentSize is { } current &&
            jumbo.JumboMaxSize is > 0d)
            size += $"{(size.Length > 0 ? "\n" : "")}ジャンボ: {Math.Min(100, current / jumbo.JumboMaxSize.Value * 100):F0}%";
        return new DebugLiveRow(
            $"{player.Name}{(player.IsLocal ? "（自分）" : "")}\nID: {player.Id} / Client: {player.ClientId}",
            $"{role}{roleDetails}\n本体陣営値: {player.RoleTeam}",
            $"{(player.Disconnected ? "切断" : player.IsDead ? "死亡" : "生存")}{(player.InVent ? " / ベント" : "")}",
            player.NosPlayer is { } nosConversation
                ? $"IsJammed (会議中のフィクサー妨害): {Value(nosConversation.IsJammed)}"
                : "—",
            appearance,
            size,
            $"{player.X:F2}, {player.Y:F2}", player.IsLocal);
    }

    private static string Value(bool? value) => value?.ToString() ?? "未取得";

    private static string NullableNumber(double? value) => value?.ToString("F3") ?? "未取得";

    private static string Empty(string value) => string.IsNullOrEmpty(value) ? "なし" : value;

    private static string ReadLogTail()
    {
        var path = SupportLog.DefaultPath;
        if (!File.Exists(path)) return "ログはありません。";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-Math.Min(stream.Length, 64 * 1_024), SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void SaveLogButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SupportLog.DefaultPath;
        if (!File.Exists(source))
        {
            MessageBox.Show(this, "保存するログはありません。", "デバッグ情報");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "デバッグログを保存",
            FileName = $"TanukiBCL-debug-{DateTimeOffset.Now:yyyy-MM-dd-HH-mm-ss}.log",
            DefaultExt = ".log",
            Filter = "ログファイル (*.log)|*.log"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
            MessageBox.Show(this, "ログを保存しました。", "デバッグ情報");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"ログを保存できませんでした: {exception.Message}", "デバッグ情報");
        }
    }

    internal static void VerifyUi()
    {
        var state = new AmongUsState { Players = [new Player
        {
            Id = 7, ClientId = 9, Name = "test", IsLocal = true, X = 1.25, Y = -2.5, RoleTeam = 2,
            SnrRole = new SnrRoleData(1, "Jackal", null, null, null, null, true, true)
        }] };
        var window = new DebugInfoWindow(() => new DebugInfoSnapshot(
            "SuperNewRoles", "live-state", "snr-roles", "game-json", "voice-json", state));
        try
        {
            if (window.DebugTabs.Items.Count != 5 || window.SaveLogButton is null)
                throw new InvalidOperationException("Released debug tabs or log save control are missing");
            window.DebugTabs.SelectedIndex = 0;
            window.Refresh();
            if (window.LiveSummaryText.Text != "live-state" ||
                window.LivePlayersGrid.Items.Count != 1 ||
                window.LivePlayersGrid.Items[0] is not DebugLiveRow row ||
                !row.NameId.Contains("test（自分）") || !row.RoleTeam.Contains("Jackal") ||
                !row.RoleTeam.Contains("本体陣営値: 2") ||
                row.Position != "1.25, -2.50" || !row.IsLocal ||
                window.LivePanel.Visibility != Visibility.Visible ||
                window.DebugText.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Developer live player table did not display its state");
            var expected = new[] { "snr-roles", "game-json", "voice-json" };
            for (var index = 1; index <= expected.Length; index++)
            {
                window.DebugTabs.SelectedIndex = index;
                window.Refresh();
                if (window.DebugText.Text != expected[index - 1] ||
                    window.ModNameText.Text != "起動中のMOD: SuperNewRoles")
                    throw new InvalidOperationException("Debug tab did not display its selected snapshot");
            }
        }
        finally { window.Close(); }
        Console.WriteLine("[PASS] Developer debug window switches live, SNR, game and voice snapshots");
    }
}
