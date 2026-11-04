using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TanukiBCL.Link;

namespace TanukiBCL.VoiceProbe;

// Shared by the memory reader, microphone meter and speech worker. All socket
// lifetime and sequence transitions are serialized under gate.
internal sealed class NosAddonLink : IDisposable
{
    internal static readonly NosAddonLink Shared = new();
    private readonly object gate = new();
    private TcpClient? client;
    private CancellationTokenSource? cancellation;
    private NosAddonProfile? profile;
    private string scope = "", session = "", nonce = "";
    private string? phase;
    private int pid, port;
    private int? local;
    private long sequence, nextAttempt, nextDiscovery, lastUpdate;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static int? Endpoint(int? value) => value is int data && (data & unchecked((int)0xFFFF0000)) == 0x54420000 &&
        (data & 65535) > 0 ? data & 65535 : null;
    internal NosAddonProfile? Update(int targetPid, string targetScope, int? marker, string? targetPhase, int? localPlayer)
    {
        lock (gate) return UpdateCore(targetPid, targetScope, marker, targetPhase, localPlayer);
    }
    private NosAddonProfile? UpdateCore(int targetPid, string targetScope, int? marker, string? targetPhase, int? localPlayer)
    {
        if (pid != targetPid || scope != targetScope) { Reset(); pid = targetPid; scope = targetScope; }
        phase = targetPhase; local = localPlayer;
        if (pid <= 0) return null;
        long now = Now;
        lastUpdate = now;
        int? endpoint = Endpoint(marker) ?? Discover(now);
        if (endpoint.HasValue && port != endpoint.Value) { Disconnect(); port = endpoint.Value; nextAttempt = 0; }
        if (port > 0 && client == null && now >= nextAttempt) Connect();
        if (profile != null && now - profile.UnixMs > 1500) Disconnect();
        return CurrentCore(targetPid);
    }
    internal NosAddonProfile? Current(int targetPid) { lock (gate) return CurrentCore(targetPid); }
    private NosAddonProfile? CurrentCore(int targetPid) => profile is { } p && targetPid == pid && Now - p.UnixMs <= 700 &&
        (phase == null || p.Phase == phase) && (!local.HasValue || p.LocalPlayer == local) ? p : null;
    internal int Port { get { lock (gate) return port; } }
    internal bool Send(int targetPid, string kind, Dictionary<string, object?> fields)
    {
        lock (gate) return CurrentCore(targetPid) != null && SendCore(kind, fields);
    }
    private int? Discover(long now)
    {
        if (now < nextDiscovery) return port > 0 ? port : null;
        nextDiscovery = now + 500;
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TanukiBCL", "NoS", $"addon-link-{pid}.json");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 4096) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var d = doc.RootElement;
            int candidate = d.GetProperty("port").GetInt32(); long time = d.GetProperty("unixMs").GetInt64();
            return d.GetProperty("protocol").GetInt32() == 1 && d.GetProperty("pid").GetInt32() == pid && candidate is > 0 and <= 65535 &&
                now - time <= 2500 && time <= now + 100 ? candidate : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
    private void Connect()
    {
        nextAttempt = Now + 500;
        var created = new TcpClient { NoDelay = true };
        client = created; nonce = Guid.NewGuid().ToString("N");
        cancellation = new();
        _ = Run(created, pid, nonce, port, cancellation.Token);
    }
    private async Task Run(TcpClient source, int targetPid, string clientNonce, int targetPort, CancellationToken stop)
    {
        try
        {
            await source.ConnectAsync(IPAddress.Loopback, targetPort, stop).AsTask().WaitAsync(TimeSpan.FromSeconds(2), stop);
            var stream = source.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind = "hello", protocol = 1, pid = targetPid, nonce = clientNonce }) + "\n"), stop);
            _ = Poll(source, stop);
            var buffer = new byte[4096]; var framer = new AddonLinkFramer();
            while (!stop.IsCancellationRequested)
            {
                int size = await stream.ReadAsync(buffer, stop).AsTask().WaitAsync(TimeSpan.FromSeconds(2), stop);
                if (size == 0) break;
                foreach (string frame in framer.Push(buffer.AsSpan(0, size))) Accept(source, frame);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or
            TimeoutException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException) { }
        finally { lock (gate) { if (ReferenceEquals(source, client)) Disconnect(); } source.Dispose(); }
    }
    private async Task Poll(TcpClient source, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(100, stop);
                lock (gate)
                {
                    if (!ReferenceEquals(source, client)) return;
                    if (Now - lastUpdate > 1500) { Disconnect(); return; }
                    SendCore("poll", new() { ["accepted"] = CurrentCore(pid)?.Sequence ?? 0 });
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Accept(TcpClient source, string frame)
    {
        using var doc = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 16 });
        lock (gate)
        {
            if (!ReferenceEquals(client, source)) return;
            var m = doc.RootElement;
            if (session.Length == 0) { Hello(m); return; }
            var candidate = NosAddonProfile.Parse(m, pid, session, profile?.Sequence ?? 0, Now);
            if (candidate != null && (phase == null || candidate.Phase == phase) && (!local.HasValue || candidate.LocalPlayer == local)) profile = candidate;
        }
    }
    private void Hello(JsonElement m)
    {
        string? token = m.GetProperty("session").GetString();
        if (m.GetProperty("kind").GetString() != "hello" || m.GetProperty("protocol").GetInt32() != 1 ||
            m.GetProperty("pid").GetInt32() != pid || m.GetProperty("nonce").GetString() != nonce || !AddonLinkWire.Hex(token))
            throw new InvalidOperationException("Invalid addon link hello");
        session = token!;
    }
    private bool SendCore(string kind, Dictionary<string, object?> fields)
    {
        if (session.Length == 0 || client is not { Connected: true }) return false;
        fields["kind"] = kind; fields["protocol"] = 1; fields["pid"] = pid; fields["session"] = session;
        fields["sequence"] = ++sequence; fields["unixMs"] = Now;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields) + "\n");
            client.SendTimeout = 100;
            client.GetStream().Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { Disconnect(); return false; }
    }
    private void Disconnect()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        client?.Dispose(); client = null; session = ""; sequence = 0; profile = null;
    }
    private void Reset() { Disconnect(); pid = port = 0; scope = ""; nextDiscovery = 0; }
    public void Dispose() { lock (gate) Reset(); }
}
