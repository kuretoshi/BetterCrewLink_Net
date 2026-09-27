using System.Text;
using System.Text.Json;
using SIPSorcery.Net;

namespace TanukiBCL.VoiceProbe;

internal sealed record IceServer(string Url, string? Username, string? Credential);

internal sealed class WebRtcPeerManager : IDisposable
{
    private readonly string owner;
    private readonly Func<string, object, Task> sendSignal;
    private readonly Dictionary<string, Peer> peers = [];
    private IReadOnlyList<IceServer> iceServers = [new("stun:stun.l.google.com:19302", null, null)];
    private bool forceRelayOnly;

    public WebRtcPeerManager(string owner, Func<string, object, Task> sendSignal)
    {
        this.owner = owner;
        this.sendSignal = sendSignal;
    }

    public event Action<string>? PeerVerified;

    public void Configure(JsonElement configuration)
    {
        forceRelayOnly = configuration.TryGetProperty("forceRelayOnly", out var relay) && relay.GetBoolean();
        if (!configuration.TryGetProperty("iceServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var parsed = new List<IceServer>();
        foreach (var server in servers.EnumerateArray())
        {
            if (!server.TryGetProperty("urls", out var urls))
            {
                continue;
            }

            foreach (var url in ReadUrls(urls))
            {
                parsed.Add(new IceServer(
                    url,
                    server.TryGetProperty("username", out var username) ? username.GetString() : null,
                    server.TryGetProperty("credential", out var credential) ? credential.GetString() : null));
            }
        }

        if (parsed.Count > 0)
        {
            iceServers = parsed;
        }

        Log($"ICE設定受信: servers={iceServers.Count} relayOnly={forceRelayOnly}");
    }

    public async Task InitiateAsync(string remoteSocketId)
    {
        if (peers.ContainsKey(remoteSocketId))
        {
            return;
        }

        var peer = CreatePeer(remoteSocketId, initiator: true);
        var channel = await peer.Connection.createDataChannel("tanuki-probe", null);
        ConfigureDataChannel(peer, channel);

        var offer = peer.Connection.createOffer(null);
        await peer.Connection.setLocalDescription(offer);
        Log($"offer > {Short(remoteSocketId)}");
        await SendSignalAsync(peer, new { type = "offer", sdp = offer.sdp });
    }

    public async Task ApplySignalAsync(string remoteSocketId, JsonElement data)
    {
        var type = data.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        if (type == "offer")
        {
            RemovePeer(remoteSocketId);
        }

        if (!peers.TryGetValue(remoteSocketId, out var peer))
        {
            peer = CreatePeer(remoteSocketId, initiator: false);
        }

        if (type == "candidate")
        {
            var candidate = ReadCandidate(data);
            if (candidate is null)
            {
                return;
            }

            if (peer.Connection.remoteDescription is null)
            {
                peer.PendingCandidates.Add(candidate);
            }
            else
            {
                peer.Connection.addIceCandidate(candidate);
            }

            return;
        }

        if (!data.TryGetProperty("sdp", out var sdpElement) || string.IsNullOrWhiteSpace(sdpElement.GetString()))
        {
            return;
        }

        var description = new RTCSessionDescriptionInit
        {
            type = type.Equals("offer", StringComparison.OrdinalIgnoreCase) ? RTCSdpType.offer : RTCSdpType.answer,
            sdp = sdpElement.GetString()!
        };
        var result = peer.Connection.setRemoteDescription(description);
        Log($"{type} < {Short(remoteSocketId)} result={result}");
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"リモートSDPを適用できません: {result}");
        }

        foreach (var candidate in peer.PendingCandidates)
        {
            peer.Connection.addIceCandidate(candidate);
        }
        peer.PendingCandidates.Clear();

        if (description.type == RTCSdpType.offer)
        {
            var answer = peer.Connection.createAnswer(null);
            await peer.Connection.setLocalDescription(answer);
            Log($"answer > {Short(remoteSocketId)}");
            await SendSignalAsync(peer, new { type = "answer", sdp = answer.sdp });
        }
    }

    public void RemovePeer(string remoteSocketId)
    {
        if (!peers.Remove(remoteSocketId, out var peer))
        {
            return;
        }

        peer.Connection.close();
        peer.Connection.Dispose();
    }

    private Peer CreatePeer(string remoteSocketId, bool initiator)
    {
        var configuration = new RTCConfiguration
        {
            iceTransportPolicy = forceRelayOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
            iceServers = iceServers.Select(server => new RTCIceServer
            {
                urls = server.Url,
                username = server.Username,
                credential = server.Credential
            }).ToList()
        };
        var connection = new RTCPeerConnection(configuration);
        var peer = new Peer(remoteSocketId, Guid.NewGuid().ToString("N"), connection, initiator);
        peers[remoteSocketId] = peer;

        connection.onicecandidate += candidate =>
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.candidate))
            {
                return;
            }

            Log($"candidate > {Short(remoteSocketId)}");
            _ = SendSignalAsync(peer, new
            {
                type = "candidate",
                candidate = new
                {
                    candidate = candidate.candidate,
                    sdpMLineIndex = candidate.sdpMLineIndex,
                    sdpMid = candidate.sdpMid
                }
            });
        };
        connection.onconnectionstatechange += state => Log($"peer {Short(remoteSocketId)} state={state}");
        connection.ondatachannel += channel => ConfigureDataChannel(peer, channel);
        return peer;
    }

    private void ConfigureDataChannel(Peer peer, RTCDataChannel channel)
    {
        peer.Channel = channel;
        channel.onopen += () =>
        {
            Log($"data channel open: {Short(peer.RemoteSocketId)}");
            channel.send($"tanuki-probe:{owner}:{Guid.NewGuid():N}");
        };
        channel.onmessage += (_, _, data) =>
        {
            var message = Encoding.UTF8.GetString(data);
            Log($"data < {Short(peer.RemoteSocketId)} {message}");
            if (message.StartsWith("tanuki-probe:", StringComparison.Ordinal))
            {
                channel.send($"tanuki-ack:{owner}");
                // 相手からの受信と、ACKの送信まで成功。相手側はACK受信で検証する。
                PeerVerified?.Invoke(peer.RemoteSocketId);
            }
            else if (message.StartsWith("tanuki-ack:", StringComparison.Ordinal))
            {
                PeerVerified?.Invoke(peer.RemoteSocketId);
            }
        };
        channel.onclose += () => Log($"data channel closed: {Short(peer.RemoteSocketId)}");
    }

    private Task SendSignalAsync(Peer peer, object signal)
    {
        var withConnectionId = JsonSerializer.SerializeToElement(signal);
        var payload = new Dictionary<string, object?>();
        foreach (var property in withConnectionId.EnumerateObject())
        {
            payload[property.Name] = property.Value.Clone();
        }
        payload["connectionId"] = peer.ConnectionId;
        return sendSignal(peer.RemoteSocketId, payload);
    }

    private static RTCIceCandidateInit? ReadCandidate(JsonElement signal)
    {
        if (!signal.TryGetProperty("candidate", out var candidateElement))
        {
            return null;
        }

        if (candidateElement.ValueKind == JsonValueKind.String)
        {
            candidateElement = signal;
        }

        var candidate = new RTCIceCandidateInit
        {
            candidate = candidateElement.TryGetProperty("candidate", out var value) ? value.GetString() ?? string.Empty : string.Empty,
            sdpMid = candidateElement.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null
        };
        if (candidateElement.TryGetProperty("sdpMLineIndex", out var index) && index.TryGetUInt16(out var lineIndex))
        {
            candidate.sdpMLineIndex = lineIndex;
        }

        return string.IsNullOrWhiteSpace(candidate.candidate) ? null : candidate;
    }

    private static IEnumerable<string> ReadUrls(JsonElement urls)
    {
        if (urls.ValueKind == JsonValueKind.String && urls.GetString() is { Length: > 0 } url)
        {
            yield return url;
        }
        else if (urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in urls.EnumerateArray())
            {
                if (item.GetString() is { Length: > 0 } itemUrl)
                {
                    yield return itemUrl;
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var socketId in peers.Keys.ToArray())
        {
            RemovePeer(socketId);
        }
    }

    private void Log(string message) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{owner}/RTC] {message}");
    private static string Short(string socketId) => socketId.Length <= 8 ? socketId : socketId[..8];

    private sealed record Peer(
        string RemoteSocketId,
        string ConnectionId,
        RTCPeerConnection Connection,
        bool Initiator)
    {
        public RTCDataChannel? Channel { get; set; }
        public List<RTCIceCandidateInit> PendingCandidates { get; } = [];
    }
}
