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
    private const int Channels = 2;
    private readonly string owner;
    private readonly bool sendTestTone;
    private readonly Func<string, object, Task> sendSignal;
    private readonly Dictionary<string, Peer> peers = [];
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

    public event Action<string>? PeerConnectionFailed;

    public event Action<string, RTCPeerConnectionState>? PeerConnectionStateChanged;

    public void BroadcastMonoPcm48k(ReadOnlySpan<byte> pcm16Mono)
    {
        if (pcm16Mono.Length < sizeof(short))
        {
            return;
        }

        var monoCount = Math.Min(pcm16Mono.Length / sizeof(short), SamplesPerChannel);
        var stereo = new short[SamplesPerChannel * Channels];
        for (var index = 0; index < monoCount; index++)
        {
            var sample = BitConverter.ToInt16(pcm16Mono.Slice(index * sizeof(short), sizeof(short)));
            stereo[index * 2] = sample;
            stereo[index * 2 + 1] = sample;
        }

        byte[] encoded;
        lock (audioCodecLock)
        {
            encoded = audioEncoder.EncodeAudio(stereo, OpusFormat);
        }

        foreach (var peer in peers.Values.ToArray())
        {
            if (peer.Connection.connectionState == RTCPeerConnectionState.connected)
            {
                peer.Connection.SendAudio(SamplesPerChannel, encoded);
            }
        }
    }

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
        connection.addTrack(new MediaStreamTrack([OpusFormat], MediaStreamStatusEnum.SendRecv));

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
        connection.onconnectionstatechange += state =>
        {
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

            var pcm = new short[SamplesPerChannel * Channels];
            for (var sampleIndex = 0; sampleIndex < SamplesPerChannel; sampleIndex++)
            {
                var absoluteSample = frameIndex * SamplesPerChannel + sampleIndex;
                var sample = (short)(Math.Sin(2d * Math.PI * frequency * absoluteSample / 48_000d) * amplitude);
                pcm[sampleIndex * 2] = sample;
                pcm[sampleIndex * 2 + 1] = sample;
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
    }

    private void ReceiveAudio(Peer peer, EncodedAudioFrame frame)
    {
        short[] pcm;
        lock (audioCodecLock)
        {
            pcm = audioEncoder.DecodeAudio(frame.EncodedAudio, frame.AudioFormat.Codec == AudioCodecsEnum.Unknown
                ? OpusFormat
                : frame.AudioFormat);
        }

        if (pcm.Length == 0)
        {
            return;
        }

        PcmReceived?.Invoke(peer.RemoteSocketId, EnsureStereo(pcm, frame.AudioFormat.ChannelCount));

        AudioTestResult? result;
        lock (peer.AudioGate)
        {
            if (peer.AudioReported)
            {
                return;
            }

            peer.AudioFrames++;
            for (var index = 0; index < pcm.Length; index += Channels)
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

    private static short[] EnsureStereo(short[] pcm, int advertisedChannels)
    {
        if (advertisedChannels == 2)
        {
            return pcm;
        }

        var stereo = new short[pcm.Length * 2];
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
        public RTCDataChannel? Channel { get; set; }
        public List<RTCIceCandidateInit> PendingCandidates { get; } = [];
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
