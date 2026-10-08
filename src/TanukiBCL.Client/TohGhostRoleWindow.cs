using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TanukiBCL.VoiceProbe;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

internal sealed class TohGhostRoleWindow : Window
{
    private readonly Dictionary<string, bool> values;
    private readonly Action<Dictionary<string, bool>>? changed;
    private readonly IReadOnlyList<TohRoleDefinition> catalog;
    private readonly bool hasExplicitSettings;

    public TohGhostRoleWindow(LobbySettings settings, IReadOnlyList<TohRoleDefinition> catalog,
        bool readOnly, Action<Dictionary<string, bool>>? changed)
    {
        Title = "幽霊の声が聞こえる役職設定";
        Width = 490;
        Height = 630;
        MinWidth = 370;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(32, 29, 38));
        Foreground = Brushes.White;
        this.catalog = catalog;
        this.changed = readOnly ? null : changed;
        hasExplicitSettings = settings.TohGhostRoles is not null;
        values = settings.TohGhostRoles is { } saved
            ? new Dictionary<string, bool>(saved, StringComparer.Ordinal)
            : new Dictionary<string, bool>(StringComparer.Ordinal);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = readOnly ? "ホストの設定（閲覧のみ）" : "キルできる役職ごとに幽霊の声を設定します。",
            Margin = new Thickness(0, 0, 0, 16)
        });
        foreach (var team in new[] { (Type: "Neutral", Label: "第三陣営"),
                     (Type: "Animals", Label: "アニマルズ") })
            AddGroup(root, team.Type, team.Label, settings.TohNeutralKillerHaunting);
        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroll;
    }

    private void AddGroup(StackPanel root, string team, string label, bool fallback)
    {
        var roles = catalog.Where(role => role.CustomRoleType == team && role.IsKiller == true).ToArray();
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var heading = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(heading);
        if (roles.Length == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "役職一覧の受信待ちです。ホスト・参加者とも3.2.19以降が必要です。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
            root.Children.Add(panel);
            return;
        }
        var all = new CheckBox { Content = "この陣営を一括設定", Margin = new Thickness(0, 10, 0, 9),
            IsEnabled = changed is not null };
        panel.Children.Add(all);
        var roleChecks = new List<(TohRoleDefinition Role, CheckBox Check)>();
        foreach (var role in roles)
        {
            var check = new CheckBox { Content = role.DisplayName, Margin = new Thickness(14, 5, 0, 5),
                IsEnabled = changed is not null, IsChecked = Enabled(role.RoleName, fallback) };
            panel.Children.Add(check);
            roleChecks.Add((role, check));
        }
        void Refresh()
        {
            var enabled = roles.Count(role => Enabled(role.RoleName, fallback));
            heading.Text = $"{label}　{enabled} / {roles.Length} 役職がオン";
            all.IsChecked = enabled == roles.Length;
        }
        foreach (var (role, check) in roleChecks)
        {
            check.Click += (_, _) =>
            {
                MaterializeDefaults(fallback);
                values[role.RoleName] = check.IsChecked == true;
                Refresh();
                changed?.Invoke(new Dictionary<string, bool>(values, StringComparer.Ordinal));
            };
        }
        all.Click += (_, _) =>
        {
            MaterializeDefaults(fallback);
            foreach (var (role, check) in roleChecks)
            {
                values[role.RoleName] = all.IsChecked == true;
                check.IsChecked = all.IsChecked;
            }
            Refresh();
            changed?.Invoke(new Dictionary<string, bool>(values, StringComparer.Ordinal));
        };
        Refresh();
        root.Children.Add(panel);
    }

    private bool Enabled(string role, bool fallback) => values.TryGetValue(role, out var enabled)
        ? enabled : !hasExplicitSettings && fallback;

    private void MaterializeDefaults(bool fallback)
    {
        foreach (var role in catalog.Where(role => role.CustomRoleType is "Neutral" or "Animals" &&
            role.IsKiller == true))
            values.TryAdd(role.RoleName, Enabled(role.RoleName, fallback));
    }

    internal static void VerifyBehavior()
    {
        var catalog = new List<TohRoleDefinition>
        {
            new(1, "Jackal", "ジャッカル", "Neutral", true),
            new(2, "Dog", "ドッグ", "Animals", true),
            new(3, "Crewmate", "クルーメイト", "Crewmate", false)
        };
        Dictionary<string, bool>? saved = null;
        var window = new TohGhostRoleWindow(new LobbySettings
        {
            TohGhostRoles = new Dictionary<string, bool> { ["UnrelatedOldRole"] = true }
        }, catalog, false, roles => saved = roles);
        var root = (StackPanel)((ScrollViewer)window.Content).Content;
        var neutral = (StackPanel)root.Children[1];
        var group = (CheckBox)neutral.Children[1];
        group.IsChecked = true;
        group.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (saved?.GetValueOrDefault("Jackal") != true ||
            saved.GetValueOrDefault("Dog") ||
            saved.GetValueOrDefault("UnrelatedOldRole") != true ||
            ((TextBlock)neutral.Children[0]).Text != "第三陣営　1 / 1 役職がオン")
            throw new InvalidOperationException("TOH group edit changed another faction or prior DLL settings");
        var readOnly = new TohGhostRoleWindow(new LobbySettings(), catalog, true, _ => { });
        var readOnlyRoot = (StackPanel)((ScrollViewer)readOnly.Content).Content;
        var readOnlyNeutral = (StackPanel)readOnlyRoot.Children[1];
        if (((CheckBox)readOnlyNeutral.Children[1]).IsEnabled)
            throw new InvalidOperationException("TOH guest role list is editable");
    }
}
