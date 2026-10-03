using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SocketIOClient;
using SocketIOClient.Transport;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.Client;

internal sealed class PublicLobbyRow : INotifyPropertyChanged
{
    internal PublicLobbyRow(PublicLobbyListing lobby, bool canShowCode, string disabledReason, long now)
    {
        Lobby = lobby;
        CanShowCode = canShowCode;
        DisabledReason = disabledReason;
        Status = lobby.Status(now);
    }

    public PublicLobbyListing Lobby { get; }
    public string Players => $"{Lobby.CurrentPlayers}/{Lobby.MaxPlayers}";
    public bool CanShowCode { get; }
    public string DisabledReason { get; }
    public string Status { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshStatus(long now)
    {
        var value = Lobby.Status(now);
        if (value == Status) return;
        Status = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }
}

public partial class PublicLobbyBrowserWindow : Window
{
    private readonly string serverUrl;
    private readonly string language;
    private readonly AmongUsModType installedMod;
    private readonly Dictionary<int, PublicLobbyListing> lobbies = [];
    private readonly ObservableCollection<PublicLobbyRow> rows = [];
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SocketIOClient.SocketIO? socket;
    private int socketGeneration;
    private bool closing;

    internal PublicLobbyBrowserWindow(ClientSettings settings, AmongUsModType installedMod)
    {
        InitializeComponent();
        serverUrl = settings.ServerUrl;
        language = settings.Language;
        this.installedMod = installedMod;
        LobbyGrid.ItemsSource = rows;
        HeaderText.Text = UiLocalization.Translate(language, "lobbybrowser.header");
        TitleColumn.Header = UiLocalization.Translate(language, "lobbybrowser.list.title");
        HostColumn.Header = UiLocalization.Translate(language, "lobbybrowser.list.host");
        PlayersColumn.Header = UiLocalization.Translate(language, "lobbybrowser.list.players");
        ModsColumn.Header = UiLocalization.Translate(language, "lobbybrowser.list.mods");
        LanguageColumn.Header = UiLocalization.Translate(language, "lobbybrowser.list.language");
        DismissCodeButton.Content = UiLocalization.Translate(language, "buttons.close");
        statusTimer.Tick += (_, _) => RefreshStatuses();
        Loaded += async (_, _) =>
        {
            if (socket is null && !closing)
            {
                statusTimer.Start();
                await ReloadAsync();
            }
        };
    }

    private async Task ReloadAsync()
    {
        var generation = ++socketGeneration;
        var previous = socket;
        socket = null;
        await CloseSocketAsync(previous);
        if (closing || generation != socketGeneration) return;
        lobbies.Clear();
        RenderRows();
        ConnectionText.Text = "接続中…";
        ConnectionText.Visibility = Visibility.Visible;
        SocketIOClient.SocketIO current;
        try { current = new SocketIOClient.SocketIO(new Uri(serverUrl), new SocketIOOptions
        {
            Transport = TransportProtocol.WebSocket,
            Reconnection = true,
            ReconnectionAttempts = 3,
            ReconnectionDelay = 500,
            ReconnectionDelayMax = 2_000,
            ConnectionTimeout = TimeSpan.FromSeconds(10)
        }); }
        catch (Exception error)
        {
            ConnectionText.Text = $"接続できません: {error.Message}";
            return;
        }
        socket = current;
        current.OnConnected += (_, _) => _ = SubscribeAsync(current, generation);
        current.On("update_lobby", response =>
        {
            try { ApplyFromSocket(current, generation, response.GetValue<JsonElement>()); }
            catch (Exception error) { Trace.TraceWarning($"Public lobby update rejected: {error.Message}"); }
        });
        current.On("new_lobbies", response =>
        {
            try
            {
                var data = response.GetValue<JsonElement>();
                if (data.ValueKind == JsonValueKind.Array)
                    foreach (var item in data.EnumerateArray()) ApplyFromSocket(current, generation, item);
            }
            catch (Exception error) { Trace.TraceWarning($"Public lobby list rejected: {error.Message}"); }
        });
        current.On("remove_lobby", response =>
        {
            try
            {
                var id = response.GetValue<int>();
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!closing && ReferenceEquals(socket, current) && generation == socketGeneration &&
                        lobbies.Remove(id)) RenderRows();
                });
            }
            catch (Exception error) { Trace.TraceWarning($"Public lobby removal rejected: {error.Message}"); }
        });
        try { await current.ConnectAsync(); }
        catch (Exception error)
        {
            if (!closing && generation == socketGeneration)
                ConnectionText.Text = $"接続できません: {error.Message}";
        }
    }

    private async Task SubscribeAsync(SocketIOClient.SocketIO current, int generation)
    {
        if (closing || generation != socketGeneration || !ReferenceEquals(socket, current)) return;
        try
        {
            await current.EmitAsync("lobbybrowser", true);
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!closing && generation == socketGeneration)
                {
                    ConnectionText.Text = string.Empty;
                    ConnectionText.Visibility = Visibility.Collapsed;
                }
            });
        }
        catch (Exception error) { Trace.TraceWarning($"Public lobby subscription failed: {error.Message}"); }
    }

    private void ApplyFromSocket(SocketIOClient.SocketIO current, int generation, JsonElement data)
    {
        if (!PublicLobbyListing.TryParse(data, out var listing) || listing is null) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (closing || generation != socketGeneration || !ReferenceEquals(socket, current)) return;
            lobbies[listing.Id] = listing;
            RenderRows();
        });
    }

    private void RenderRows()
    {
        if (closing) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        rows.Clear();
        foreach (var lobby in PublicLobbyListing.Sort(lobbies.Values))
        {
            var reason = lobby.GameState != 0
                ? UiLocalization.Translate(language, "lobbybrowser.code_tooltips.in_progress")
                : lobby.MaxPlayers == lobby.CurrentPlayers
                    ? UiLocalization.Translate(language, "lobbybrowser.code_tooltips.full_lobby")
                    : lobby.Mod != PublicLobbyListing.ModId(installedMod)
                        ? $"{UiLocalization.Translate(language, "lobbybrowser.code_tooltips.incompatible")} '{AmongUsMod.For(installedMod).Label}' {UiLocalization.Translate(language, "lobbybrowser.code_tooltips.and")} '{lobby.ModName}'"
                        : string.Empty;
            rows.Add(new PublicLobbyRow(lobby, lobby.CanShowCode(installedMod), reason, now));
        }
    }

    private void RefreshStatuses()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var row in rows) row.RefreshStatus(now);
    }

    private async void ShowCode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PublicLobbyRow row } ||
            !row.CanShowCode || socket is not { Connected: true } current) return;
        try
        {
            var acknowledgement = new TaskCompletionSource<(int State, string Message)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await current.EmitAsync("join_lobby", response =>
            {
                try { acknowledgement.TrySetResult((response.GetValue<int>(0), response.GetValue<string>(1))); }
                catch (Exception error) { acknowledgement.TrySetException(error); }
            }, row.Lobby.Id);
            var result = await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (closing || !ReferenceEquals(socket, current)) return;
            CodeText.Text = result.State == 0
                ? $"{UiLocalization.Translate(language, "lobbybrowser.code")}: {result.Message}"
                : $"Error: {result.Message}";
        }
        catch (Exception error)
        {
            if (closing || !ReferenceEquals(socket, current)) return;
            CodeText.Text = $"Error: {error.Message}";
        }
        if (closing || !ReferenceEquals(socket, current)) return;
        CodeBackdrop.Visibility = Visibility.Visible;
        DismissCodeButton.Focus();
    }

    private void DismissCode_Click(object sender, RoutedEventArgs e) => CodeBackdrop.Visibility = Visibility.Collapsed;

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null;
             node = VisualTreeHelper.GetParent(node))
            if (node is Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private static async Task CloseSocketAsync(SocketIOClient.SocketIO? old)
    {
        if (old is null) return;
        try
        {
            if (old.Connected)
            {
                await old.EmitAsync("lobbybrowser", false).WaitAsync(TimeSpan.FromSeconds(2));
                await old.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception error) { Trace.TraceWarning($"Public lobby socket shutdown: {error.Message}"); }
        finally
        {
            try { old.Dispose(); }
            catch (Exception error) { Trace.TraceWarning($"Public lobby socket disposal: {error.Message}"); }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        closing = true;
        ++socketGeneration;
        statusTimer.Stop();
        var old = socket;
        socket = null;
        _ = CloseSocketAsync(old);
        base.OnClosing(e);
    }

    internal static void VerifyUi()
    {
        PublicLobbyListing.Verify();
        var window = new PublicLobbyBrowserWindow(new ClientSettings(), AmongUsModType.None);
        try
        {
            if (window.Width != 900 || window.Height != 500 || window.LobbyGrid.Columns.Count != 7 ||
                window.HeaderText.Text != UiLocalization.Translate("ja", "lobbybrowser.header"))
                throw new InvalidOperationException("Public lobby browser frame or table differs from 3.2.8");
            window.lobbies[7] = new PublicLobbyListing(7, "Sample", "Host", 2, 4,
                "en", "NONE", true, 0, 0);
            window.RenderRows();
            if (window.rows.Count != 1 || !window.rows[0].CanShowCode ||
                window.rows[0].Players != "2/4" || window.rows[0].Lobby.Title != "Sample")
                throw new InvalidOperationException("Public lobby list did not bind a joinable row");
            var row = window.rows[0];
            window.RefreshStatuses();
            if (!ReferenceEquals(row, window.rows[0]))
                throw new InvalidOperationException("Public lobby status refresh reset the list and scroll position");
            window.lobbies.Remove(7);
            window.RenderRows();
            if (window.rows.Count != 0)
                throw new InvalidOperationException("Public lobby removal left a stale row");
        }
        finally { window.Close(); }
        Console.WriteLine("[PASS] 3.2.8 public lobby browser payload, sorting, join eligibility and WPF frame");
    }
}
