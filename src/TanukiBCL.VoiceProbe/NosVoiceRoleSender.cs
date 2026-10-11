using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed class NosVoiceRoleSender : IDisposable
{
    private const int DiscoveryPort = 57741;
    private static readonly Regex TokenPattern = new("^[a-f0-9]{32}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly object gate = new();
    private readonly string descriptorPath;
    private UdpClient? socket;
    private CancellationTokenSource? receiveCancellation;
    private string nonce = NewNonce();
    private NosVoiceRoleDescriptor? descriptor;
    private NosVoiceRoleDescriptor? discovered;
    private long discoveredAt;
    private long lastRead;
    private long lastDiscovery;
    private long sequence;
    private string token = string.Empty;
    private int currentPid;
    private int currentPlayer = -1;

    public NosVoiceRoleSender(string? path = null) => descriptorPath = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TanukiBCL", "NoS", "voice-role.json");

    public void Send(double rms, bool active, int pid, AmongUsState? state)
    {
        if (!double.IsFinite(rms) || rms is < 0d or > 1d ||
            state?.Mod != AmongUsModType.NebulaOnTheShip ||
            state.GameState is not (GameState.Tasks or GameState.Discussion or GameState.Lobby)) return;
        var player = state.Players.FirstOrDefault(item => item.IsLocal);
        if (player is null || player.IsDead || player.Disconnected) return;
        lock (gate) SendCore(rms, active, pid, player.Id, state.GameState);
    }

    private void SendCore(double rms, bool active, int pid, int playerId, GameState phase)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        currentPid = pid;
        currentPlayer = playerId;
        EnsureSocket();
        if (socket is null) return;
        if (now - lastDiscovery >= 250)
        {
            lastDiscovery = now;
            SendUdp($"TBCLD1|{pid}|{nonce}", DiscoveryPort);
        }
        if (now - lastRead >= 250)
        {
            lastRead = now;
            descriptor = ReadDescriptor(descriptorPath);
        }
        var selected = discovered is { } live && now - discoveredAt < 1000 &&
            live.Pid == pid && live.PlayerId == playerId ? live : descriptor;
        if (selected is null || selected.Pid != pid || selected.PlayerId != playerId) return;
        if (selected.Role == "tanukiMicTest" ? phase != GameState.Lobby : phase == GameState.Lobby) return;
        if (phase == GameState.Discussion && selected.Role is not
            ("tanukiTalkative" or "tanukiBclBibiri" or "tanukiVoiceRoles")) return;
        if (token != selected.Token)
        {
            token = selected.Token;
            sequence = 0;
        }
        sequence = Math.Max(sequence + 1, now * 1000);
        SendUdp(FormattableString.Invariant(
            $"TBCL1|{token}|{sequence}|{now}|{rms}|{(active ? 1 : 0)}"), selected.Port);
    }

    private void EnsureSocket()
    {
        if (socket is not null) return;
        try
        {
            var created = new UdpClient(AddressFamily.InterNetwork);
            socket = created;
            receiveCancellation = new CancellationTokenSource();
            _ = ReceiveDiscoveryAsync(created, receiveCancellation.Token);
        }
        catch (SocketException) { CloseCore(); }
    }

    private async Task ReceiveDiscoveryAsync(UdpClient source, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var response = await source.ReceiveAsync(cancellation);
                AcceptDiscovery(response);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A new sample will open a fresh socket if needed.
        }
    }

    private void AcceptDiscovery(UdpReceiveResult response)
    {
        if (!response.RemoteEndPoint.Address.Equals(IPAddress.Loopback) ||
            response.RemoteEndPoint.Port != DiscoveryPort || response.Buffer.Length > 4096) return;
        lock (gate)
        {
            var prefix = $"TBCLD1|{nonce}|";
            var text = Encoding.UTF8.GetString(response.Buffer);
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return;
            try
            {
                var candidate = JsonSerializer.Deserialize<NosVoiceRoleDescriptor>(text[prefix.Length..],
                    JsonOptions);
                if (candidate is null || !Valid(candidate) ||
                    candidate.Pid != currentPid || candidate.PlayerId != currentPlayer) return;
                discovered = candidate;
                discoveredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (JsonException) { }
        }
    }

    private void SendUdp(string payload, int port)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            socket?.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException) { CloseCore(); }
    }

    internal static NosVoiceRoleDescriptor? ReadDescriptor(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 4096) return null;
            var value = JsonSerializer.Deserialize<NosVoiceRoleDescriptor>(File.ReadAllText(path),
                JsonOptions);
            return Valid(value) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static bool Valid(NosVoiceRoleDescriptor? value) => value is
    { Protocol: 1, PlayerId: >= 0 and <= 255, Pid: > 0, Port: > 0 and <= 65535,
      Role: "tanukiMegaphone" or "tanukiBclBibiri" or "tanukiTalkative" or "tanukiMicTest" or "tanukiVoiceRoles" } &&
        value.Token is { } token && TokenPattern.IsMatch(token);

    public void Dispose()
    {
        lock (gate) CloseCore();
    }

    private void CloseCore()
    {
        receiveCancellation?.Cancel();
        socket?.Dispose();
        receiveCancellation?.Dispose();
        receiveCancellation = null;
        socket = null;
        discovered = null;
        nonce = NewNonce();
    }

    private static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

internal sealed class NosVoiceRoleDescriptor
{
    public int Protocol { get; set; }
    public string Role { get; set; } = string.Empty;
    public int PlayerId { get; set; }
    public int Pid { get; set; }
    public int Port { get; set; }
    public string Token { get; set; } = string.Empty;
}
