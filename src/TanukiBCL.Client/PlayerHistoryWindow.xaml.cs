using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TanukiBCL.Client;

public partial class PlayerHistoryWindow : Window
{
    private const int PageSize = 50;
    private string activeName = string.Empty;
    private long? activeFrom;
    private long? activeTo;
    private int offset;
    private int total;
    private bool busy;
    private string confirmDigest = string.Empty;
    private PlayerHistoryRecord[] currentHistory = [];

    public PlayerHistoryWindow() => InitializeComponent();

    private void BanType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        confirmDigest = string.Empty;
        if (IsLoaded) RenderHistory();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync();
    }

    private async Task SearchAsync()
    {
        var from = FromDate.SelectedDate is DateTime start
            ? new DateTimeOffset(start.Date).ToUnixTimeSeconds() : (long?)null;
        var to = ToDate.SelectedDate is DateTime end
            ? new DateTimeOffset(end.Date.AddDays(1)).ToUnixTimeSeconds() : (long?)null;
        if (from is not null && to is not null && from >= to)
        {
            StatusText.Text = "日付の範囲を確認してください。";
            return;
        }
        await LoadAsync(SearchName.Text.Trim(), from, to, 0);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) =>
        await LoadAsync(activeName, activeFrom, activeTo, Math.Max(0, offset - PageSize));

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (offset + PageSize < total)
            await LoadAsync(activeName, activeFrom, activeTo, offset + PageSize);
    }

    private async Task LoadAsync(string name, long? from, long? to, int nextOffset,
        string? banDigest = null, bool ipBan = false)
    {
        if (busy) return;
        if (string.IsNullOrEmpty(BanPassword.Password))
        {
            StatusText.Text = "管理者パスワードを入力してください。";
            return;
        }
        busy = true;
        StatusText.Text = "確認しています…";
        try
        {
            var (_, _, history, historyTotal, ipBansAvailable, error) = await PlayerBanClient.ManageAsync(new
            {
                password = BanPassword.Password,
                action = banDigest is null ? "list" : ipBan ? "add-history-ip" : "add-history",
                uidDigest = banDigest,
                reason = BanReason.Text,
                historyName = name,
                historyFrom = from,
                historyTo = to,
                historyOffset = nextOffset
            });
            if (error is not null) { StatusText.Text = error; return; }
            if (history is null)
            {
                StatusText.Text = "履歴機能はサーバーに未反映です。サーバーの更新後に利用できます。";
                return;
            }
            activeName = name;
            activeFrom = from;
            activeTo = to;
            offset = nextOffset;
            total = historyTotal;
            currentHistory = history;
            IpBanOption.IsEnabled = ipBansAvailable;
            if (!ipBansAvailable) BanType.SelectedIndex = 0;
            confirmDigest = string.Empty;
            StatusText.Text = banDigest is null ? "履歴を更新しました。" : "BANを登録しました。";
            TotalText.Text = $"該当: {total}人";
            PageText.Text = total == 0 ? "0人" :
                $"{offset + 1}〜{Math.Min(offset + PageSize, total)} / {total}";
            PreviousButton.IsEnabled = offset > 0;
            NextButton.IsEnabled = offset + PageSize < total;
            RenderHistory();
        }
        finally { busy = false; }
    }

    private void RenderHistory()
    {
        HistoryRows.Children.Clear();
        foreach (var item in currentHistory) AddHistoryRow(item);
    }

    private void AddHistoryRow(PlayerHistoryRecord item)
    {
        var ipBan = BanType.SelectedIndex == 1;
        var banned = ipBan ? item.IpBanned != 0 : item.IsUidBanned;
        var missingIp = ipBan && string.IsNullOrEmpty(item.PublicIp);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6),
            LastChildFill = true, Background = System.Windows.Media.Brushes.Transparent };
        var button = new Button { Content = banned ? "BAN済み" : missingIp ? "IP未確認" : "BAN登録",
            IsEnabled = !banned && !missingIp, Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) =>
        {
            if (confirmDigest != item.UidDigest)
            {
                confirmDigest = item.UidDigest;
                button.Content = "BANを確定";
                StatusText.Text = $"{item.DisplayName}を{(ipBan ? "IP" : "UID")}でBANする場合は、もう一度押してください。";
                return;
            }
            await LoadAsync(activeName, activeFrom, activeTo, offset, item.UidDigest, ipBan);
        };
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        var first = DateTimeOffset.FromUnixTimeSeconds(item.FirstSeenAt).ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        var last = DateTimeOffset.FromUnixTimeSeconds(item.LastSeenAt).ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        row.Children.Add(new TextBlock { Text = $"{item.DisplayName} ／ ID: …{item.UidDigest[^8..]}" +
            (item.FriendCode.Length > 0 ? $" ／ {item.FriendCode}" : "") +
            $" ／ IP: {(item.PublicIp.Length > 0 ? item.PublicIp : "未確認")}" +
            $" ／ UID: {(item.IsUidBanned ? "BAN済み" : "未BAN")} ／ IP: {(item.IpBanned != 0 ? "BAN済み" : "未BAN")}" +
            $"\n初回: {first} ／ 最終: {last}",
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        HistoryRows.Children.Add(row);
    }

    internal static void VerifyBanModes()
    {
        var window = new PlayerHistoryWindow();
        if (window.IpBanOption.IsEnabled) throw new InvalidOperationException("IP mode enabled before server capability.");
        window.currentHistory = [
            new("12345678", "UID banned", "", 100, 200, 1, "8.8.8.8", 1, 0),
            new("23456789", "IP banned", "", 100, 200, 1, "1.1.1.1", 0, 1),
            new("34567890", "No verified IP", "", 100, 200, 0)
        ];
        window.RenderHistory();
        VerifyButtons(window, [false, true, true]);
        window.IpBanOption.IsEnabled = true;
        window.BanType.SelectedIndex = 1;
        window.RenderHistory();
        VerifyButtons(window, [true, false, false]);
        window.Close();
    }

    private static void VerifyButtons(PlayerHistoryWindow window, bool[] enabled)
    {
        var actual = window.HistoryRows.Children.Cast<DockPanel>()
            .Select(row => ((Button)row.Children[0]).IsEnabled);
        if (!actual.SequenceEqual(enabled)) throw new InvalidOperationException("UID/IP BAN controls are not independent.");
    }
}
