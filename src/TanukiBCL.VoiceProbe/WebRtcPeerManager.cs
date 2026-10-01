using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace TanukiBCL.VoiceProbe;

internal sealed record IceServer(string Url, string? Username, string? Credential);

internal sealed class WebRtcPeerManager : IDisposable
{
    private static readonly AudioFormat OpusFormat = new(AudioCodecsEnum.OPUS, 111, 48_000, 2, "useinbandfec=1");
    private const int SamplesPerChannel = 960;
    private const int PlaybackChannels = 2;
    private readonly string owner;
    private readonly bool sendTestTone;
    private readonly Func<string, object, Task> sendSignal;
    private readonly ConcurrentDictionary<string, Peer> peers = new();
    private readonly AudioEncoder audioEncoder = new(true, true);
    private readonly object audioCodecLock = new();
    private IReadOnlyList<IceServer> iceServers = [new("stun:stun.l.google.com:19302", null, null)];
    private bool forceRelayOnly;

    public WebRtcPeerManager(string owner, Func<string, object, Task> sendSignal, bool sendTestTone)
    {
        this.owner = owner;
        this.sendSignal = sendSignal;
        this.sendTestTone = sendTestTone;
    }

    public event Action<string>? PeerVerified;

    public event Action<string, AudioTestResult>? AudioVerified;

    public event Action<string, short[]>? PcmReceived;

    public event Action<string, string>? PeerDataReceived;

    public event Action<string>? PeerConnectionFailed;

    public event Action<string, RTCPeerConnectionState>? PeerConnectionStateChanged;

    public event Action<string>? TestToneSent;

    public int BroadcastMonoPcm48k(ReadOnlySpan<byte> pcm16Mono, Func<string, bool>? canSendToPeer = null)
    {
        if (pcm16Mono.Length < sizeof(short))
        {
            return 0;
        }

        var monoCount = Math.Min(pcm16Mono.Length / sizeof(short), SamplesPerChannel);
        var mono = new short[SamplesPerChannel];
        for (var index = 0; index < monoCount; index++)
        {
            mono[index] = BitConverter.ToInt16(pcm16Mono.Slice(index * sizeof(short), sizeof(short)));
        }

        byte[] encoded;
        lock (audioCodecLock)
        {
            encoded = audioEncoder.EncodeAudio(mono, OpusFormat);
        }

        var sentPeers = 0;
        foreach (var peer in peers.Values.ToArray())
        {
            if (peer.Connection.connectionState == RTCPeerConnectionState.connected &&
                (canSendToPeer is null || canSendToPeer(peer.RemoteSocketId)))
            {
                peer.Connection.SendAudio(SamplesPerChannel, encoded);
                sentPeers++;
            }
        }
        return sentPeers;
    }

    public void Configure(JsonElement configuration)
    {
        var serverRequiresRelay = configuration.TryGetProperty("forceRelayOnly", out var relay) && relay.GetBoolean();
        if (!configuration.TryGetProperty("iceServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
        {
            forceRelayOnly = serverRequiresRelay;
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

        // The TanukiBCL desktop client can use TURN-only NAT mode. With mixed
        // relay/direct ICE in this interop path, peers may repeatedly time out.
        // Prefer the server-provided TURN when available; keep STUN-only servers usable.
        forceRelayOnly = serverRequiresRelay || iceServers.Any(server => server.Url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                                                                          server.Url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));
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
        FlushLocalCandidates(peer);
    }

    public bool IsInitiating(string remoteSocketId) =>
        peers.TryGetValue(remoteSocketId, out var peer) && peer.Initiator;

    public bool TrySendPeerData(string remoteSocketId, string message)
    {
        if (!peers.TryGetValue(remoteSocketId, out var peer) || peer.Channel is null)
        {
            return false;
        }

        try
        {
            peer.Channel.send(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task ReconnectAsync(string remoteSocketId)
    {
        RemovePeer(remoteSocketId);
        await InitiateAsync(remoteSocketId);
    }

    public void RemoveAllPeers()
    {
        foreach (var remoteSocketId in peers.Keys.ToArray())
        {
            RemovePeer(remoteSocketId);
        }
    }

    public async Task ApplySignalAsync(string remoteSocketId, JsonElement data)
    {
        var type = data.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var incomingConnectionId = data.TryGetProperty("connectionId", out var connectionIdElement)
            ? connectionIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        if (type == "offer")
        {
            if (peers.TryGetValue(remoteSocketId, out var existingPeer) &&
                !existingPeer.Initiator &&
                !string.IsNullOrWhiteSpace(incomingConnectionId) &&
                incomingConnectionId == existingPeer.ConnectionId)
            {
                return;
            }
            RemovePeer(remoteSocketId);
        }
        else if (peers.TryGetValue(remoteSocketId, out var existingPeer) &&
                 !string.IsNullOrWhiteSpace(incomingConnectionId) &&
                 incomingConnectionId != existingPeer.ConnectionId)
        {
            Log($"stale {type} ignored < {Short(remoteSocketId)}");
            return;
        }

        if (!peers.TryGetValue(remoteSocketId, out var peer))
        {
            peer = CreatePeer(remoteSocketId, initiator: false, incomingConnectionId);
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
            FlushLocalCandidates(peer);
        }
    }

    public void RemovePeer(string remoteSocketId)
    {
        if (!peers.TryRemove(remoteSocketId, out var peer))
        {
            return;
        }

        peer.Connection.close();
        peer.Connection.Dispose();
        peer.Decoder.Dispose();
    }

    private Peer CreatePeer(string remoteSocketId, bool initiator, string? connectionId = null)
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
        var peer = new Peer(remoteSocketId, connectionId ?? Guid.NewGuid().ToString("N"), connection, initiator);
        peers[remoteSocketId] = peer;
        connection.addTrack(new MediaStreamTrack([OpusFormat], MediaStreamStatusEnum.SendRecv));

        connection.onicecandidate += candidate =>
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.candidate))
            {
                return;
            }

            var candidateText = candidate.candidate.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase)
                ? candidate.candidate
                : $"candidate:{candidate.candidate}";
            var signal = new
            {
                type = "candidate",
                candidate = new
                {
                    candidate = candidateText,
                    sdpMLineIndex = candidate.sdpMLineIndex,
                    sdpMid = candidate.sdpMid ?? "0"
                }
            };
            lock (peer.LocalCandidateGate)
            {
                if (!peer.LocalDescriptionSent)
                {
                    peer.PendingLocalCandidates.Add(signal);
                    return;
                }
            }
            _ = SendSignalAsync(peer, signal);
        };
        connection.onconnectionstatechange += state =>
        {
            if (!peers.TryGetValue(remoteSocketId, out var currentPeer) || !ReferenceEquals(currentPeer, peer))
            {
                return;
            }
            Log($"peer {Short(remoteSocketId)} state={state}");
            PeerConnectionStateChanged?.Invoke(remoteSocketId, state);
            if (state == RTCPeerConnectionState.connected &&
                sendTestTone &&
                Interlocked.Exchange(ref peer.TestToneStarted, 1) == 0)
            {
                _ = SendTestToneAsync(peer);
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                PeerConnectionFailed?.Invoke(remoteSocketId);
            }
        };
        connection.oniceconnectionstatechange += state => Log($"peer {Short(remoteSocketId)} ice={state}");
        connection.onicecandidateerror += (_, error) => Log($"peer {Short(remoteSocketId)} ice-candidate-error={error}");
        connection.ondatachannel += channel => ConfigureDataChannel(peer, channel);
        connection.OnAudioFrameReceived += frame => ReceiveAudio(peer, frame);
        return peer;
    }

    private void ConfigureDataChannel(Peer peer, RTCDataChannel channel)
    {
        peer.Channel = channel;
        channel.onopen += () =>
        {
            Log($"data channel open: {Short(peer.RemoteSocketId)}");
            if (sendTestTone)
            {
                channel.send($"tanuki-probe:{owner}:{Guid.NewGuid():N}");
            }
        };
        channel.onmessage += (_, _, data) =>
        {
            var message = Encoding.UTF8.GetString(data);
            Log($"data < {Short(peer.RemoteSocketId)} {message}");
            PeerDataReceived?.Invoke(peer.RemoteSocketId, message);
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

    private async Task SendTestToneAsync(Peer peer)
    {
        // 440Hz、48kHz、stereo、20ms x 30フレーム（600ms）。
        const double frequency = 440d;
        const double amplitude = short.MaxValue * 0.25d;
        for (var frameIndex = 0; frameIndex < 30; frameIndex++)
        {
            if (peer.Connection.connectionState != RTCPeerConnectionState.connected)
            {
                return;
            }

            var pcm = new short[SamplesPerChannel];
            for (var sampleIndex = 0; sampleIndex < SamplesPerChannel; sampleIndex++)
            {
                var absoluteSample = frameIndex * SamplesPerChannel + sampleIndex;
                pcm[sampleIndex] = (short)(Math.Sin(2d * Math.PI * frequency * absoluteSample / 48_000d) * amplitude);
            }

            byte[] encoded;
            lock (audioCodecLock)
            {
                encoded = audioEncoder.EncodeAudio(pcm, OpusFormat);
            }

            peer.Connection.SendAudio(SamplesPerChannel, encoded);
            await Task.Delay(20);
        }

        Log($"Opus test tone > {Short(peer.RemoteSocketId)} frames=30 frequency={frequency:0}Hz");
        TestToneSent?.Invoke(peer.RemoteSocketId);
    }

    private void ReceiveAudio(Peer peer, EncodedAudioFrame frame)
    {
        short[] pcm;
        lock (peer.AudioGate)
        {
            pcm = peer.Decoder.DecodeAudio(frame.EncodedAudio, frame.AudioFormat.Codec == AudioCodecsEnum.Unknown
                ? OpusFormat
                : frame.AudioFormat);
        }

        if (pcm.Length == 0)
        {
            return;
        }

        PcmReceived?.Invoke(peer.RemoteSocketId, MonoToStereo(pcm));

        AudioTestResult? result;
        lock (peer.AudioGate)
        {
            if (peer.AudioReported)
            {
                return;
            }

            peer.AudioFrames++;
            for (var index = 0; index < pcm.Length; index++)
            {
                var sample = pcm[index];
                var normalized = sample / 32768d;
                peer.SumSquares += normalized * normalized;
                peer.SampleCount++;

                if (peer.HasPreviousSample && ((peer.PreviousSample < 0 && sample >= 0) || (peer.PreviousSample >= 0 && sample < 0)))
                {
                    peer.ZeroCrossings++;
                }
                peer.PreviousSample = sample;
                peer.HasPreviousSample = true;
            }

            if (peer.AudioFrames < 10 || peer.SampleCount == 0)
            {
                return;
            }

            var durationSeconds = peer.SampleCount / 48_000d;
            var frequency = peer.ZeroCrossings / (2d * durationSeconds);
            var rms = Math.Sqrt(peer.SumSquares / peer.SampleCount);
            if (rms < 0.02d || frequency is < 350d or > 550d)
            {
                return;
            }

            peer.AudioReported = true;
            result = new AudioTestResult(peer.AudioFrames, rms, frequency);
        }

        Log($"Opus audio < {Short(peer.RemoteSocketId)} frames={result.Frames} rms={result.Rms:0.000} frequency={result.FrequencyHz:0.0}Hz");
        AudioVerified?.Invoke(peer.RemoteSocketId, result);
    }

    private static short[] MonoToStereo(short[] pcm)
    {
        var stereo = new short[pcm.Length * PlaybackChannels];
        for (var index = 0; index < pcm.Length; index++)
        {
            stereo[index * 2] = pcm[index];
            stereo[index * 2 + 1] = pcm[index];
        }
        return stereo;
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

    private void FlushLocalCandidates(Peer peer)
    {
        object[] pending;
        lock (peer.LocalCandidateGate)
        {
            peer.LocalDescriptionSent = true;
            pending = peer.PendingLocalCandidates.ToArray();
            peer.PendingLocalCandidates.Clear();
        }
        foreach (var signal in pending)
        {
            _ = SendSignalAsync(peer, signal);
        }
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
        RemoveAllPeers();
    }

    private void Log(string message) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} [{owner}/RTC] {message}");
    private static string Short(string socketId) => socketId.Length <= 8 ? socketId : socketId[..8];

    private sealed record Peer(
        string RemoteSocketId,
        string ConnectionId,
        RTCPeerConnection Connection,
        bool Initiator)
    {
        public AudioEncoder Decoder { get; } = new(true, true);
        public RTCDataChannel? Channel { get; set; }
        public List<RTCIceCandidateInit> PendingCandidates { get; } = [];
        public object LocalCandidateGate { get; } = new();
        public List<object> PendingLocalCandidates { get; } = [];
        public bool LocalDescriptionSent { get; set; }
        public object AudioGate { get; } = new();
        public int AudioFrames { get; set; }
        public long SampleCount { get; set; }
        public double SumSquares { get; set; }
        public long ZeroCrossings { get; set; }
        public short PreviousSample { get; set; }
        public bool HasPreviousSample { get; set; }
        public bool AudioReported { get; set; }
        public int TestToneStarted;
    }
}

internal sealed record AudioTestResult(int Frames, double Rms, double FrequencyHz);
