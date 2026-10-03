using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

internal sealed record DebugInfoSnapshot(string ModName, string Live, string SnrRoles,
    string GameState, string VoiceConnection, AmongUsState? State = null,
    IReadOnlyDictionary<int, VoiceServerProbe.NosRadioReport>? NosRadioReports = null);

internal sealed record DebugLiveRow(string NameId, string RoleTeam, string Status,
    string Conversation, string Appearance, string Size, string Position, bool IsLocal);

internal sealed record DebugSnrRow(int PlayerId, string Role, string AssignedTeam,
    string WinnerTeam, string TeamTag, string Modifier, string GhostRole);

public partial class DebugInfoWindow : Window
{
    private readonly Func<DebugInfoSnapshot> capture;
    private readonly Func<Task<string>> captureSnrRoles;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool snrBusy;

    internal DebugInfoWindow(Func<DebugInfoSnapshot> capture, Func<Task<string>> captureSnrRoles)
    {
        InitializeComponent();
        this.capture = capture;
        this.captureSnrRoles = captureSnrRoles;
        DebugTitleText.Text = "デバッグ情報 " + VoiceView.FormatVersionLabel(UpdateCatalog.CurrentVersion);
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void MinimizeDebugButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseDebugButton_Click(object sender, RoutedEventArgs e) => Close();

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
            SnrPanel.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
            SnrReadButton.IsEnabled = !snrBusy && snapshot.State?.Mod == AmongUsModType.SuperNewRoles;
            DebugText.Visibility = tab is 0 or 1 ? Visibility.Collapsed : Visibility.Visible;
            if (tab == 0)
            {
                LiveSummaryText.Text = snapshot.Live;
                LivePlayersGrid.ItemsSource = snapshot.State?.Players.Select(FormatLiveRow).ToArray() ?? [];
                NosRadioPanel.Visibility = snapshot.State?.Mod == AmongUsModType.NebulaOnTheShip
                    ? Visibility.Visible : Visibility.Collapsed;
                if (NosRadioPanel.Visibility == Visibility.Visible)
                    NosRadioText.Text = FormatNosRadioReports(snapshot.State!, snapshot.NosRadioReports);
            }
            DebugText.Text = tab switch
            {
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

    private async void SnrReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (snrBusy || capture().State?.Mod != AmongUsModType.SuperNewRoles) return;
        snrBusy = true;
        SnrReadButton.IsEnabled = false;
        SnrReadButton.Content = "取得中…";
        SnrStatusText.Text = "SNR役職を取得中…";
        SnrPlayersGrid.ItemsSource = null;
        SnrJsonText.Clear();
        try
        {
            ApplySnrCapture(await captureSnrRoles());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or TimeoutException or JsonException or ArgumentException or
            System.ComponentModel.Win32Exception)
        {
            SnrStatusText.Text = $"取得に失敗しました: {error.Message}";
        }
        finally
        {
            snrBusy = false;
            SnrReadButton.Content = "SNR役職を取得";
            SnrReadButton.IsEnabled = capture().State?.Mod == AmongUsModType.SuperNewRoles;
        }
    }

    private void ApplySnrCapture(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok" ||
            !root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SNR役職の取得結果が不正です");
        var rows = players.EnumerateArray().Select(player => new DebugSnrRow(
            player.GetProperty("playerId").GetInt32(), EnumLabel(player, "role"),
            EnumLabel(player, "assignedTeam"), EnumLabel(player, "winnerTeam"),
            EnumLabel(player, "teamTag"), EnumLabel(player, "modifier"),
            EnumLabel(player, "ghostRole"))).ToArray();
        SnrPlayersGrid.ItemsSource = rows;
        SnrJsonText.Text = JsonSerializer.Serialize(players, new JsonSerializerOptions { WriteIndented = true });
        if (root.TryGetProperty("diagnostics", out var diagnostics) &&
            diagnostics.ValueKind == JsonValueKind.Array && diagnostics.GetArrayLength() > 0)
            SnrJsonText.Text += "\n\n取得診断:\n" +
                JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true });
        var capturedAt = root.TryGetProperty("capturedAt", out var time) ? time.GetString() : null;
        var pid = root.TryGetProperty("pid", out var process) ? process.GetInt32().ToString() : "?";
        var version = root.TryGetProperty("version", out var release) ? release.GetString() : null;
        SnrStatusText.Text = $"取得時刻: {capturedAt} / PID: {pid} / SNR: {version}";
        if (rows.Length == 0) SnrStatusText.Text += " / プレイヤー情報はまだ初期化されていません";
    }

    private static string EnumLabel(JsonElement player, string field)
    {
        if (!player.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Object)
            return "未取得";
        var name = value.TryGetProperty("name", out var text) ? text.GetString() : null;
        var number = value.TryGetProperty("value", out var id) ? id.GetInt64().ToString() : "?";
        return $"{(string.IsNullOrEmpty(name) ? "不明" : name)} ({number})";
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

    private static string FormatNosRadioReports(AmongUsState state,
        IReadOnlyDictionary<int, VoiceServerProbe.NosRadioReport>? reports)
    {
        var lines = new List<string>();
        foreach (var player in state.Players)
        {
            IReadOnlyList<NosRadioData>? radios = player.IsLocal
                ? state.NosLocalMicPosition is null ? null : state.NosRadios
                : reports?.TryGetValue(player.Id, out var report) == true &&
                  report.ClientId == player.ClientId ? report.Radios : null;
            lines.Add($"{player.Name} / PlayerId: {player.Id} / ClientId: {player.ClientId} ／ " +
                (radios is null ? player.IsLocal ? "未取得" : "相手から未受信" : $"{radios.Count}件"));
            if (radios is null) continue;
            for (var index = 0; index < radios.Count; index++)
            {
                var radio = radios[index];
                var kind = radio.Kind switch { 0 => "Impostor", 1 => "Jackal", 2 => "Lovers", _ => "Unknown" };
                var hearable = string.Join(", ", Enumerable.Range(0, 32)
                    .Where(id => ((unchecked((uint)radio.HearableMask) >> id) & 1u) != 0));
                lines.Add($"  #{index} Kind: {radio.Kind} ({kind}) ／ Name: " +
                    $"{(string.IsNullOrEmpty(radio.Name) ? "(名称なし)" : radio.Name)} ／ " +
                    $"NameLength: {radio.Name.Length} ／ HearableMask: {radio.HearableMask} " +
                    $"(0x{unchecked((uint)radio.HearableMask):X8}) ／ 声が届くPlayerId: " +
                    (hearable.Length == 0 ? "なし" : hearable));
            }
        }
        return string.Join("\n", lines);
    }

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
        var state = new AmongUsState { Mod = AmongUsModType.SuperNewRoles, Players = [new Player
        {
            Id = 7, ClientId = 9, Name = "test", IsLocal = true, X = 1.25, Y = -2.5, RoleTeam = 2,
            SnrRole = new SnrRoleData(1, "Jackal", null, null, null, null, true, true)
        }] };
        var radioReports = new Dictionary<int, VoiceServerProbe.NosRadioReport>();
        var window = new DebugInfoWindow(() => new DebugInfoSnapshot(
            "SuperNewRoles", "live-state", "snr-roles", "game-json", "voice-json", state, radioReports),
            () => Task.FromResult(string.Empty));
        try
        {
            if (window.DebugTabs.Items.Count != 5 || window.SaveLogButton is null ||
                window.WindowStyle != WindowStyle.None ||
                window.DebugTitleText.Text != "デバッグ情報 " + VoiceView.FormatVersionLabel(UpdateCatalog.CurrentVersion) ||
                window.MinimizeDebugButton is null || window.CloseDebugButton is null)
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
                if ((index == 1
                        ? window.SnrPanel.Visibility != Visibility.Visible || !window.SnrReadButton.IsEnabled ||
                          window.DebugText.Visibility != Visibility.Collapsed
                        : window.DebugText.Text != expected[index - 1]) ||
                    window.ModNameText.Text != "起動中のMOD: SuperNewRoles")
                    throw new InvalidOperationException("Debug tab did not display its selected snapshot");
            }
            window.ApplySnrCapture("""
                {"status":"ok","pid":123,"capturedAt":"2026-10-03T00:00:00Z","version":"3.3.0.0",
                 "players":[{"playerId":7,"role":{"value":1,"name":"Jackal"},
                 "assignedTeam":{"value":2,"name":"Neutral"},"winnerTeam":null,
                 "teamTag":null,"modifier":null,"ghostRole":null}]}
                """);
            if (window.SnrPlayersGrid.Items.Count != 1 ||
                window.SnrPlayersGrid.Items[0] is not DebugSnrRow snrRow ||
                snrRow.Role != "Jackal (1)" || snrRow.AssignedTeam != "Neutral (2)" ||
                snrRow.GhostRole != "未取得" ||
                !window.SnrStatusText.Text.Contains("SNR: 3.3.0.0"))
                throw new InvalidOperationException("Developer SNR snapshot table did not show helper roles");
            state.Mod = AmongUsModType.None;
            window.DebugTabs.SelectedIndex = 1;
            window.Refresh();
            if (window.SnrReadButton.IsEnabled)
                throw new InvalidOperationException("SNR snapshot capture was enabled for another game type");
            state.Mod = AmongUsModType.SuperNewRoles;
            state.Mod = AmongUsModType.NebulaOnTheShip;
            state.NosLocalMicPosition = new VoicePosition(0, 0);
            state.NosRadios = [new NosRadioData(0, 1 << 8, "Impostor")];
            state.Players.Add(new Player { Id = 8, ClientId = 10, Name = "remote" });
            radioReports[8] = new VoiceServerProbe.NosRadioReport(10,
                [new NosRadioData(1, 1 << 7, "Jackal")], DateTimeOffset.UtcNow);
            window.DebugTabs.SelectedIndex = 0;
            window.Refresh();
            if (window.NosRadioPanel.Visibility != Visibility.Visible ||
                !window.NosRadioText.Text.Contains("remote / PlayerId: 8 / ClientId: 10 ／ 1件") ||
                !window.NosRadioText.Text.Contains("Kind: 1 (Jackal)") ||
                !window.NosRadioText.Text.Contains("0x00000080") ||
                !window.NosRadioText.Text.Contains("声が届くPlayerId: 7"))
                throw new InvalidOperationException("Developer NoS radio reports did not show remote channels");
            radioReports[8] = radioReports[8] with { ClientId = 11 };
            window.Refresh();
            if (!window.NosRadioText.Text.Contains("remote / PlayerId: 8 / ClientId: 10 ／ 相手から未受信"))
                throw new InvalidOperationException("Developer NoS radio reports accepted a mismatched client");
        }
        finally { window.Close(); }
        Console.WriteLine("[PASS] Developer debug window switches live, SNR, game and voice snapshots");
    }
}
