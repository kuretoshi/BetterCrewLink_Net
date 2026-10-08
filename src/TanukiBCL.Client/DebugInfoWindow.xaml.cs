using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

internal sealed record DebugInfoSnapshot(string ModName, string Live, string GameState, string VoiceConnection,
    AmongUsState? State = null, IReadOnlyDictionary<int, VoiceServerProbe.NosRadioReport>? NosRadioReports = null,
    IReadOnlyCollection<int>? RadioClientIds = null, NosLoadedContentsStatus? NosContents = null);

internal sealed record DebugSnrRow(int PlayerId, string Role, string AssignedTeam,
    string WinnerTeam, string TeamTag, string Modifier, string GhostRole);

/// <summary>3.2.9 DebugWindow: one page of searchable player cards and collapsible sections.</summary>
public partial class DebugInfoWindow : Window
{
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xB7, 0xAA, 0xBD));
    private static readonly Brush Section = new SolidColorBrush(Color.FromRgb(0x44, 0x40, 0x4A));
    private static readonly Brush Primary = new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26));
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0xC9, 0xC0, 0xCC));
    private readonly Func<DebugInfoSnapshot> capture;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<string> collapsedPlayers = [];
    // Formatted LoadedContents.json for the revision last shown; dropped with this window.
    private (string Revision, string Text)? loadedContents;

    internal DebugInfoWindow(Func<DebugInfoSnapshot> capture)
    {
        InitializeComponent();
        this.capture = capture;
        DebugTitleText.Text = "デバッグ情報 " + VoiceView.FormatVersionLabel(UpdateCatalog.CurrentVersion);
        refreshTimer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            refreshTimer.Start();
        };
        Closed += (_, _) => refreshTimer.Stop();
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) Refresh();
    }

    private void Section_Expanded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void Refresh()
    {
        try
        {
            var snapshot = capture();
            var state = snapshot.State;
            var mod = state?.Mod ?? AmongUsModType.None;
            ModNameText.Text = $"起動中のMOD: {snapshot.ModName}";
            LiveSummaryText.Text = snapshot.Live;
            var status = mod == AmongUsModType.NebulaOnTheShip ? state?.NosReadStatus?.Message : state?.RoleReaderStatus;
            ModStatusPanel.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
            ModStatusText.Text = status ?? string.Empty;
            ModStatusPanel.Background = mod == AmongUsModType.NebulaOnTheShip && state?.NosReadStatus?.Failed == true
                ? new SolidColorBrush(Color.FromRgb(0x58, 0x1E, 0x24))
                : new SolidColorBrush(Color.FromRgb(0x1D, 0x30, 0x47));
            NosNoticePanel.Visibility = mod == AmongUsModType.NebulaOnTheShip ? Visibility.Visible : Visibility.Collapsed;
            RenderPlayers(snapshot);
            SnrHintText.Visibility = SnrExpander.Visibility =
                mod == AmongUsModType.SuperNewRoles ? Visibility.Visible : Visibility.Collapsed;
            NosExpander.Visibility = mod == AmongUsModType.NebulaOnTheShip ? Visibility.Visible : Visibility.Collapsed;
            if (SnrExpander.IsExpanded && state is not null) RenderSnr(state);
            if (NosExpander.IsExpanded && state is not null)
                NosContentsText.Text = FormatNos(state, snapshot.NosRadioReports, snapshot.NosContents);
            if (GameExpander.IsExpanded) GameStateText.Text = snapshot.GameState;
            if (VoiceExpander.IsExpanded) VoiceText.Text = snapshot.VoiceConnection;
            if (LogExpander.IsExpanded) LogText.Text = ReadLogTail();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LiveSummaryText.Text = $"診断情報を取得できませんでした: {exception.Message}";
        }
    }

    private static string PlayerKey(AmongUsState state, Player player) => $"{state.LobbyCode}:{player.ClientId}:{player.Id}";

    private void RenderPlayers(DebugInfoSnapshot snapshot)
    {
        PlayerCards.Children.Clear();
        var state = snapshot.State;
        var players = state?.Players ?? [];
        var search = SearchBox.Text.Trim();
        var filtered = players.Where(player => new[]
            {
                player.Name, player.Id.ToString(), RoleLabel(state!, player), player.SnrRole?.RoleName, player.TohRole?.RoleName
            }.Any(value => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true)).ToArray();
        PlayerCountText.Text = $"プレイヤー {filtered.Length} / {players.Count}人";
        foreach (var player in filtered) PlayerCards.Children.Add(BuildCard(snapshot, state!, player));
        EmptyPlayersText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPlayersText.Text = players.Count > 0 ? "検索に一致するプレイヤーはいません。" : "プレイヤー情報を待っています…";
    }

    private static string RoleLabel(AmongUsState state, Player player) => state.Mod switch
    {
        AmongUsModType.SuperNewRoles when player.SnrRole is { } snr => snr.RoleName ?? $"RoleId {snr.RoleId}",
        AmongUsModType.TownOfHostForE when player.TohRole is { } toh => toh.RoleName ?? $"RoleId {toh.RoleId}",
        AmongUsModType.NebulaOnTheShip when player.NosPlayer is { } nos =>
            nos.IsImpostor ? "Impostor" : nos.IsNeutral ? "Neutral" : nos.IsCrewmate ? "Crewmate" : "陣営不明",
        AmongUsModType.NebulaOnTheShip => string.Empty,
        _ => player.IsImpostor ? "Impostor" : player.IsThirdParty ? $"ThirdParty({player.RoleTeam})" : "Crewmate"
    };

    private Border BuildCard(DebugInfoSnapshot snapshot, AmongUsState state, Player player)
    {
        var key = PlayerKey(state, player);
        var collapsed = collapsedPlayers.Contains(key);
        var nosMissing = state.Mod == AmongUsModType.NebulaOnTheShip && player.NosPlayer is null;
        var header = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock { Text = player.Name, FontWeight = FontWeights.Bold, FontSize = 15,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        if (player.IsLocal) header.Children.Add(Chip("自分", Primary));
        header.Children.Add(Chip(player.Disconnected ? "切断" : player.IsDead ? "死亡" : "生存", Neutral));
        if (nosMissing) header.Children.Add(Chip("NoS未取得", ErrorBrush));
        var toggle = new Button
        {
            Content = collapsed ? "▼" : "▲", Width = 28, Height = 24, Padding = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Muted, BorderThickness = new Thickness(0),
            ToolTip = collapsed ? "詳細を展開" : "詳細を最小化", Tag = key
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle,
            $"{player.Name}の詳細を{(collapsed ? "展開" : "最小化")}");
        toggle.Click += (_, _) =>
        {
            if (!collapsedPlayers.Remove(key)) collapsedPlayers.Add(key);
            Refresh();
        };
        var top = new DockPanel();
        DockPanel.SetDock(toggle, Dock.Right);
        top.Children.Add(toggle);
        var ids = new TextBlock { Text = $"PlayerId: {player.Id} ／ ClientId: {player.ClientId}", Foreground = Muted,
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        DockPanel.SetDock(ids, Dock.Right);
        top.Children.Add(ids);
        top.Children.Add(header);
        var role = RoleLabel(state, player);
        var titleArea = new StackPanel { Margin = new Thickness(14, 10, 10, 10) };
        titleArea.Children.Add(top);
        titleArea.Children.Add(new TextBlock
        {
            Text = $"{(state.Mod == AmongUsModType.NebulaOnTheShip ? "陣営" : "役職")}: {(role.Length == 0 ? "未取得" : role)}",
            Foreground = Primary, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        var card = new StackPanel();
        card.Children.Add(new Border
        {
            Child = titleArea,
            Background = new SolidColorBrush(player.IsLocal ? Color.FromArgb(0x1A, 0xBA, 0x68, 0xC8) : Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF))
        });
        if (!collapsed)
        {
            var details = new StackPanel { Margin = new Thickness(14, 4, 14, 14) };
            details.Children.Add(Pair(
                Group("ベタクル側の判定", [
                    ("RoleTeam（本体）", Text(player.RoleTeam.ToString())),
                    ("IsImpostor", Flag(nosMissing ? null : player.IsImpostor)),
                    ("IsThirdParty", Flag(nosMissing ? null : player.IsThirdParty))]),
                Group("MOD固有の値", ModValues(state.Mod, player))));
            details.Children.Add(Pair(Group("座標・サイズ", Geometry(state.Mod, player)),
                Group("外見・コスチューム", Appearance(state.Mod, player))));
            details.Children.Add(Radio(snapshot, state, player));
            card.Children.Add(new Border { BorderBrush = Section, BorderThickness = new Thickness(0, 1, 0, 0), Child = details });
        }
        return new Border
        {
            Child = card, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(nosMissing ? 2 : 1),
            BorderBrush = nosMissing ? ErrorBrush : player.IsLocal ? Primary : Section,
            Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x1A, 0x23)), Tag = key
        };
    }

    private static Border Chip(string text, Brush brush) => new()
    {
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = brush,
        Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = brush }
    };

    private static UIElement Flag(bool? value) => value is null
        ? Chip("未取得", Warning)
        : value.Value ? Chip("true / はい", Success) : Chip("false / いいえ", Neutral);

    private static UIElement Text(string value) => new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 13 };

    private static Grid Pair(UIElement left, UIElement right)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static Border Group(string title, IReadOnlyList<(string Label, UIElement Value)> values)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star), MinWidth = 110 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < values.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = values[row].Label, Foreground = Muted, FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };
            var value = values[row].Value;
            if (value is FrameworkElement element)
            {
                element.Margin = new Thickness(0, 3, 0, 3);
                element.HorizontalAlignment = HorizontalAlignment.Left;
            }
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        if (values.Count == 0)
            grid.Children.Add(new TextBlock { Text = "MOD固有値なし", Foreground = Muted, FontSize = 12 });
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(grid);
        return new Border { Child = panel, Padding = new Thickness(12), BorderBrush = Section,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    }

    private static string EnumLabel(long? value, string? name, bool available) =>
        value is null ? available ? "なし" : "未取得" : $"{name ?? "名称不明"} ({value})";

    private static string TeamLabel(SnrTeamValue? team, bool available) =>
        EnumLabel(team?.Value, team?.Name, available);

    private static IReadOnlyList<(string, UIElement)> ModValues(AmongUsModType mod, Player player)
    {
        if (mod == AmongUsModType.SuperNewRoles)
        {
            var snr = player.SnrRole;
            return [
                ("Role", Text(EnumLabel(snr?.RoleId, snr?.RoleName, snr is not null))),
                ("Modifier", Text(EnumLabel(snr?.ModifierId, snr?.ModifierName, snr is not null))),
                ("GhostRole", Text(EnumLabel(snr?.GhostRoleId, snr?.GhostRoleName, snr is not null))),
                ("IsNeutral", Flag(snr?.IsNeutral)),
                ("CanKill", Flag(snr?.CanKill))];
        }
        if (mod == AmongUsModType.NebulaOnTheShip)
        {
            var nos = player.NosPlayer;
            return [
                ("IsImpostor", Flag(nos?.IsImpostor)), ("IsCrewmate", Flag(nos?.IsCrewmate)),
                ("IsNeutral", Flag(nos?.IsNeutral)), ("IsKiller", Flag(nos?.IsKiller)),
                ("IsImpostorlike", Flag(nos?.IsImpostorlike)), ("IsJammed", Flag(nos?.IsJammed))];
        }
        if (mod == AmongUsModType.TownOfHostForE)
        {
            var toh = player.TohRole;
            var values = new List<(string, UIElement)>
            {
                ("RoleId", Text(toh?.RoleId.ToString() ?? "未取得")),
                ("RoleName", Text(toh?.RoleName ?? "未取得")),
                ("CustomRoleType", Text(toh?.CustomRoleType ?? "未取得")),
                ("TOH Impostor", Flag(player.TohImpostor)),
                ("IKiller", Flag(toh?.IsKiller)),
                ("IsNeutralKiller", Flag(toh?.IsNeutralKiller))
            };
            if (toh?.RoleName == "Opportunist") values.Add(("Opportunist.CanKill", Flag(toh.OpportunistCanKill)));
            return values;
        }
        return [];
    }

    private static string Number(double? value, int digits = 3) =>
        value is { } number && double.IsFinite(number) ? number.ToString($"F{digits}") : "未取得";

    private static IReadOnlyList<(string, UIElement)> Geometry(AmongUsModType mod, Player player)
    {
        var values = new List<(string, UIElement)> { ("X / Y", Text($"{Number(player.X, 2)} / {Number(player.Y, 2)}")) };
        if (mod == AmongUsModType.NebulaOnTheShip)
        {
            values.Add(("BodyRateX", Text(Number(player.NosPlayer?.BodyRateX))));
            values.Add(("BodyRateY", Text(Number(player.NosPlayer?.BodyRateY))));
        }
        if (player.SnrRole is { HasJumbo: true } jumbo)
            values.Add(("Jumbo current / max", Text($"{Number(jumbo.JumboCurrentSize)} / {Number(jumbo.JumboMaxSize)}")));
        return values;
    }

    private static string Costume(string? value) => value is null ? "未取得" : value.Length == 0 ? "なし" : value;

    private static IReadOnlyList<(string, UIElement)> Appearance(AmongUsModType mod, Player player)
    {
        var values = new List<(string, UIElement)>
        {
            ("外見名 / Outfit", Text($"{(string.IsNullOrEmpty(player.AppearanceName) ? player.Name : player.AppearanceName)} / {player.CurrentOutfit}")),
            ("色ID（元 → 外見）", Text($"{player.ColorId} → {player.AppearanceColorId}")),
            ("Skin（元 → 外見）", Text($"{Costume(player.SkinId)} → {Costume(player.AppearanceSkinId)}")),
            ("Hat（元 → 外見）", Text($"{Costume(player.HatId)} → {Costume(player.AppearanceHatId)}")),
            ("Visor（元 → 外見）", Text($"{Costume(player.VisorId)} → {Costume(player.AppearanceVisorId)}"))
        };
        if (mod == AmongUsModType.NebulaOnTheShip)
        {
            values.Add(("NoS Skin", Text(Costume(player.NosPlayer?.Skin?.Name))));
            values.Add(("NoS Hat", Text(Costume(player.NosPlayer?.Hat?.Name))));
            values.Add(("NoS Visor", Text(Costume(player.NosPlayer?.Visor?.Name))));
        }
        if (mod == AmongUsModType.SuperNewRoles)
        {
            values.Add(("SNR Hat2", Text(Costume(player.SnrRole?.Hat2Id))));
            values.Add(("SNR Visor2", Text(Costume(player.SnrRole?.Visor2Id))));
        }
        return values;
    }

    private static IReadOnlyList<NosRadioData>? RadiosFor(AmongUsState state, Player player,
        IReadOnlyDictionary<int, VoiceServerProbe.NosRadioReport>? reports) => player.IsLocal
        ? state.NosLocalMicPosition is null ? null : state.NosRadios
        : reports?.TryGetValue(player.Id, out var report) == true && report.ClientId == player.ClientId ? report.Radios : null;

    private static Border Radio(DebugInfoSnapshot snapshot, AmongUsState state, Player player)
    {
        var panel = new StackPanel();
        var title = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(new TextBlock { Text = "Radio", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center });
        var active = snapshot.RadioClientIds?.Contains(player.ClientId);
        title.Children.Add(Chip(active is null ? "無線送信状態：未取得" : active.Value ? "無線送信：ON" : "無線送信：OFF",
            active is null ? Warning : active.Value ? Success : Neutral));
        panel.Children.Add(title);
        if (state.Mod == AmongUsModType.NebulaOnTheShip)
        {
            var radios = RadiosFor(state, player, snapshot.NosRadioReports);
            panel.Children.Add(new TextBlock
            {
                Text = $"RadioData（話せるチャンネル）: {(radios is null ? player.IsLocal ? "未取得" : "相手から未受信" : $"{radios.Count}件")}",
                Foreground = Muted, FontSize = 12
            });
            if (radios is { Count: 0 }) panel.Children.Add(Text("利用できるチャンネルなし"));
            for (var index = 0; radios is not null && index < radios.Count; index++)
                panel.Children.Add(new Border
                {
                    BorderBrush = Section, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 2, 0, 2),
                    Margin = new Thickness(0, 6, 0, 0), Child = Text(FormatRadio(state, radios[index], index))
                });
        }
        return new Border { Child = panel, Padding = new Thickness(12), Margin = new Thickness(0, 10, 0, 0),
            BorderBrush = Section, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    }

    private static string FormatRadio(AmongUsState state, NosRadioData radio, int index)
    {
        var kind = radio.Kind switch { 0 => "Impostor", 1 => "Jackal", 2 => "Lovers", _ => "Unknown" };
        var recipients = Enumerable.Range(0, 32)
            .Where(id => ((unchecked((uint)radio.HearableMask) >> id) & 1u) != 0)
            .Select(id => state.Players.FirstOrDefault(player => player.Id == id) is { } recipient
                ? $"{recipient.Name}（ID: {id}）" : $"ID: {id}")
            .ToArray();
        return $"#{index} {(string.IsNullOrEmpty(radio.Name) ? "名称なし" : radio.Name)} ／ Kind: {radio.Kind} ({kind}) ／ " +
            $"NameLength: {radio.Name.Length}\n声が届く対象: {(recipients.Length == 0 ? "なし" : string.Join("、", recipients))}\n" +
            $"HearableMask: {radio.HearableMask} / 0x{unchecked((uint)radio.HearableMask):X8}";
    }

    private void RenderSnr(AmongUsState state)
    {
        SnrStatusText.Text = $"{state.RoleReaderStatus ?? "SNR役職未取得"}（約5秒ごとの自動取得結果）";
        SnrPlayersGrid.ItemsSource = state.Players.Select(player =>
        {
            var snr = player.SnrRole;
            var available = snr is not null;
            return new DebugSnrRow(player.Id, EnumLabel(snr?.RoleId, snr?.RoleName, available),
                TeamLabel(snr?.AssignedTeam, available), TeamLabel(snr?.WinnerTeam, available),
                TeamLabel(snr?.TeamTag, available), EnumLabel(snr?.ModifierId, snr?.ModifierName, available),
                EnumLabel(snr?.GhostRoleId, snr?.GhostRoleName, available));
        }).ToArray();
    }

    private string FormatNos(AmongUsState state,
        IReadOnlyDictionary<int, VoiceServerProbe.NosRadioReport>? reports, NosLoadedContentsStatus? contents)
    {
        var lines = new List<string>
        {
            $"読み取り状態: {state.NosReadStatus?.Message ?? "未取得"}",
            $"TBCLFields定義バージョン: {state.NosReadStatus?.SchemaVersion?.ToString() ?? "未取得"}",
            "",
            "■ コスチューム（Skin / Hat / Visor）"
        };
        foreach (var player in state.Players)
            lines.Add($"{player.Name}（ID: {player.Id}）: {Costume(player.NosPlayer?.Skin?.Name)} / " +
                $"{Costume(player.NosPlayer?.Hat?.Name)} / {Costume(player.NosPlayer?.Visor?.Name)}");
        lines.Add("");
        lines.Add("■ 無線（RadioData）");
        foreach (var player in state.Players)
        {
            var radios = RadiosFor(state, player, reports);
            lines.Add($"{player.Name} / PlayerId: {player.Id} / ClientId: {player.ClientId} ／ " +
                (radios is null ? player.IsLocal ? "未取得" : "相手から未受信" : $"{radios.Count}件"));
            for (var index = 0; radios is not null && index < radios.Count; index++)
                lines.Add("  " + FormatRadio(state, radios[index], index).Replace("\n", "\n  "));
        }
        lines.Add("");
        lines.Add("■ 一覧ファイル（LoadedContents.json）");
        lines.Add(contents is null ? "未取得" : $"{contents.Path}\n状態: {contents.Status}");
        if (contents?.Revision is { } revision)
        {
            if (loadedContents?.Revision != revision)
                loadedContents = (revision, FormatLoadedContents(contents.Path));
            if (loadedContents.Value.Text.Length > 0) lines.Add(loadedContents.Value.Text);
        }
        return string.Join("\n", lines);
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string FormatLoadedContents(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 4 * 1024 * 1024) return string.Empty;
            using var document = JsonDocument.Parse(stream);
            var formatted = JsonSerializer.Serialize(document.RootElement, IndentedJson);
            return formatted.Length > 64 * 1024 ? formatted[..(64 * 1024)] + "\n…（64KB以降は省略）" : formatted;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return string.Empty;
        }
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private bool CardText(int index, string text) => PlayerCards.Children[index] is DependencyObject card &&
        Descendants<TextBlock>(card).Any(block => block.Text.Contains(text, StringComparison.Ordinal));

    internal static void VerifyUi()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        var state = new AmongUsState
        {
            Mod = AmongUsModType.SuperNewRoles, LobbyCode = "ABCDEF", RoleReaderStatus = "SNR役職を自動更新中",
            Players = [new Player
            {
                Id = 7, ClientId = 9, Name = "test", IsLocal = true, X = 1.25, Y = -2.5, RoleTeam = 2,
                SnrRole = new SnrRoleData(1, "Jackal", null, null, null, null, true, true,
                    AssignedTeam: new SnrTeamValue(2, "Neutral"))
            }]
        };
        var radioReports = new Dictionary<int, VoiceServerProbe.NosRadioReport>();
        var contentsFile = Path.Combine(Path.GetTempPath(), $"tanuki-debug-contents-{Guid.NewGuid():N}.json");
        File.WriteAllText(contentsFile, "{\"Version\":20261005}");
        var contentsRevision = "1";
        var window = new DebugInfoWindow(() => new DebugInfoSnapshot(
            state.Mod.ToString(), "live-state", "game-json", "voice-json", state, radioReports, [9],
            new NosLoadedContentsStatus(contentsFile, "読み取り成功", contentsRevision)));
        try
        {
            Require(window.SaveLogButton is not null && window.WindowStyle == WindowStyle.None &&
                window.DebugTitleText.Text == "デバッグ情報 " + VoiceView.FormatVersionLabel(UpdateCatalog.CurrentVersion) &&
                window.MinimizeDebugButton is not null && window.CloseDebugButton is not null,
                "Debug window frame or log save control is missing");
            window.Refresh();
            Require(window.LiveSummaryText.Text == "live-state" && window.ModNameText.Text == "起動中のMOD: SuperNewRoles" &&
                window.ModStatusText.Text == "SNR役職を自動更新中" && window.PlayerCards.Children.Count == 1 &&
                window.PlayerCountText.Text == "プレイヤー 1 / 1人" &&
                window.CardText(0, "役職: Jackal") && window.CardText(0, "PlayerId: 7 ／ ClientId: 9") &&
                window.CardText(0, "1.25 / -2.50") && window.CardText(0, "無線送信：ON") &&
                window.CardText(0, "Jackal (1)") && window.SnrExpander.Visibility == Visibility.Visible,
                "Debug player card did not show role, IDs, position and radio state");
            window.SnrExpander.IsExpanded = true;
            window.Refresh();
            Require(window.SnrPlayersGrid.Items.Count == 1 && window.SnrPlayersGrid.Items[0] is DebugSnrRow
                {
                    Role: "Jackal (1)", AssignedTeam: "Neutral (2)", WinnerTeam: "なし", GhostRole: "なし"
                }, "SNR teams were not shown from automatic role data");
            window.GameExpander.IsExpanded = window.VoiceExpander.IsExpanded = true;
            window.Refresh();
            Require(window.GameStateText.Text == "game-json" && window.VoiceText.Text == "voice-json",
                "Game or voice section did not show its snapshot");
            // Collapsing survives automatic refresh, lobby-scoped like upstream.
            var toggle = Descendants<Button>(window.PlayerCards.Children[0]).Single();
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.Refresh();
            Require(!window.CardText(0, "ベタクル側の判定") && window.CardText(0, "役職: Jackal") &&
                window.CardText(0, "生存"), "Collapsed player card lost its summary or kept details");
            window.SearchBox.Text = "nobody";
            window.Refresh();
            Require(window.PlayerCards.Children.Count == 0 && window.EmptyPlayersText.Text == "検索に一致するプレイヤーはいません。",
                "Player search did not filter");
            window.SearchBox.Text = "jack";
            window.Refresh();
            Require(window.PlayerCards.Children.Count == 1, "Player search did not match role names");
            window.SearchBox.Text = string.Empty;

            state.Mod = AmongUsModType.NebulaOnTheShip;
            state.NosReadStatus = new NosReadStatus(true, "NoSプレイヤーデータ未取得: PlayerId 8", 20261005);
            state.NosLocalMicPosition = new VoicePosition(0, 0);
            state.NosRadios = [new NosRadioData(0, 1 << 8, "Impostor")];
            state.Players[0].NosPlayer = new NosPlayerData { IsImpostor = true, Hat = new NosCostumeData("nos_hat") };
            state.Players.Add(new Player { Id = 8, ClientId = 10, Name = "remote" });
            radioReports[8] = new VoiceServerProbe.NosRadioReport(10,
                [new NosRadioData(1, 1 << 7, "Jackal")], DateTimeOffset.UtcNow);
            window.NosExpander.IsExpanded = true;
            window.Refresh();
            Require(window.PlayerCards.Children.Count == 2 && window.CardText(1, "NoS未取得") &&
                window.PlayerCards.Children[1] is Border { BorderThickness.Left: 2 } &&
                window.CardText(1, "#0 Jackal ／ Kind: 1 (Jackal)") && window.CardText(1, "声が届く対象: test（ID: 7）") &&
                window.CardText(1, "0x00000080") && window.CardText(1, "無線送信：OFF") &&
                window.NosNoticePanel.Visibility == Visibility.Visible &&
                window.ModStatusText.Text == "NoSプレイヤーデータ未取得: PlayerId 8",
                "NoS player card did not show missing data or remote radio channels");
            Require(window.NosContentsText.Text.Contains("TBCLFields定義バージョン: 20261005") &&
                window.NosContentsText.Text.Contains("test（ID: 7）: 未取得 / nos_hat / 未取得") &&
                window.NosContentsText.Text.Contains("状態: 読み取り成功") &&
                window.NosContentsText.Text.Contains("\"Version\": 20261005"),
                "NoS costume, schema or LoadedContents section is incomplete");
            // The file is formatted once per revision, not re-read on every one-second refresh.
            File.WriteAllText(contentsFile, "{\"Version\":20261006}");
            window.Refresh();
            Require(window.NosContentsText.Text.Contains("\"Version\": 20261005"),
                "LoadedContents.json was re-read without a new revision");
            contentsRevision = "2";
            window.Refresh();
            Require(window.NosContentsText.Text.Contains("\"Version\": 20261006"),
                "LoadedContents.json was not re-read for a new revision");
            radioReports[8] = radioReports[8] with { ClientId = 11 };
            window.Refresh();
            Require(window.CardText(1, "相手から未受信"), "Debug NoS radio accepted a mismatched client");
        }
        finally
        {
            window.Close();
            File.Delete(contentsFile);
        }
        Console.WriteLine("[PASS] 3.2.9 debug player cards, search, collapse, SNR teams, NoS radio and contents");
    }
}
