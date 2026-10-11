using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed record NosHostVoiceSample(int PlayerId, double Rms, bool Active);

/// <summary>Admits a mobile sample only under the signaling server's player identity.</summary>
internal sealed class NosHostVoiceAdmission
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Session, long Sequence, long At)> peers = [];
    private string scope = string.Empty;

    public bool TryAccept(string from, JsonElement client, JsonElement data, AmongUsState? state,
        int knownClientId, out NosHostVoiceSample? sample, long? timestamp = null)
    {
        lock (gate)
            return TryAcceptCore(from, client, data, state, knownClientId, out sample, timestamp);
    }

    private bool TryAcceptCore(string from, JsonElement client, JsonElement data, AmongUsState? state,
        int knownClientId, out NosHostVoiceSample? sample, long? timestamp)
    {
        sample = null;
        if (state is not { IsHost: true, Mod: AmongUsModType.NebulaOnTheShip } ||
            client.ValueKind != JsonValueKind.Object || data.ValueKind != JsonValueKind.Object ||
            !Int(client, "playerId", out var playerId) || !Int(client, "clientId", out var clientId) ||
            clientId != knownClientId || !Int(data, "playerId", out var dataPlayer) ||
            !Int(data, "clientId", out var dataClient) ||
            playerId != dataPlayer || clientId != dataClient ||
            !String(data, "lobbyCode", out var lobby) || lobby != state.LobbyCode ||
            !Double(data, "rms", out var rms) || rms is < 0d or > 1d ||
            !Boolean(data, "active", out var active) ||
            !String(data, "session", out var session) ||
            !Regex.IsMatch(session, "^[a-f0-9-]{36}$") ||
            !Long(data, "sequence", out var sequence) || sequence <= 0)
            return false;
        if (!state.Players.Any(player => player.Id == playerId && player.ClientId == clientId &&
            !player.IsDead && !player.Disconnected)) return false;
        var now = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (scope != state.LobbyCode)
        {
            scope = state.LobbyCode;
            peers.Clear();
        }
        var known = peers.TryGetValue(from, out var previous);
        if (known && (now - previous.At < 80 ||
            previous.Session == session && sequence <= previous.Sequence)) return false;
        if (!known && peers.Count >= 256) return false;
        peers[from] = (session, sequence, now);
        sample = new NosHostVoiceSample(playerId, rms, active);
        return true;
    }

    private static bool Int(JsonElement value, string name, out int result)
    {
        result = 0;
        return value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetInt32(out result);
    }

    private static bool Long(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetInt64(out result);
    }

    private static bool Double(JsonElement value, string name, out double result)
    {
        result = 0;
        return value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetDouble(out result) &&
            double.IsFinite(result);
    }

    private static bool String(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
        result = field.GetString() ?? string.Empty;
        return true;
    }

    private static bool Boolean(JsonElement value, string name, out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out var field) ||
            field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = field.GetBoolean();
        return true;
    }
}

internal sealed class NosHostVoiceRelay : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly UdpClient socket = new(AddressFamily.InterNetwork);
    private readonly Func<int, string> descriptorPath;
    private long sequence;
    private long lastRead;
    private int knownPid;
    private HostDescriptor? descriptor;

    public NosHostVoiceRelay(Func<int, string>? path = null) => descriptorPath = path ?? DefaultPath;

    public void Send(NosHostVoiceSample sample, int pid, AmongUsState? state)
    {
        if (state is not { IsHost: true, Mod: AmongUsModType.NebulaOnTheShip } || pid <= 0 ||
            !state.Players.Any(player => player.Id == sample.PlayerId &&
                !player.IsDead && !player.Disconnected)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (pid != knownPid || now - lastRead >= 250)
        {
            knownPid = pid;
            lastRead = now;
            descriptor = ReadDescriptor(pid);
        }
        if (descriptor is null) return;
        sequence = Math.Max(sequence + 1, now * 1000);
        var payload = FormattableString.Invariant(
            $"TBCLH1|{descriptor.Token}|{sample.PlayerId}|{sequence}|{now}|{sample.Rms}|{(sample.Active ? 1 : 0)}");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            socket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, descriptor.Port));
        }
        catch (SocketException) { }
    }

    private HostDescriptor? ReadDescriptor(int pid)
    {
        var path = descriptorPath(pid);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 4096) return null;
            var value = JsonSerializer.Deserialize<HostDescriptor>(File.ReadAllText(path), JsonOptions);
            return value is { Protocol: 1, Pid: > 0, Port: > 0 and <= 65535 } && value.Pid == pid &&
                value.Token is { } token && Regex.IsMatch(token, "^[a-f0-9]{32}$") ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => socket.Dispose();

    private static string DefaultPath(int pid) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TanukiBCL", "NoS", $"host-voice-{pid}.json");

    private sealed class HostDescriptor
    {
        public int Protocol { get; set; }
        public int Pid { get; set; }
        public int Port { get; set; }
        public string Token { get; set; } = string.Empty;
    }
}
