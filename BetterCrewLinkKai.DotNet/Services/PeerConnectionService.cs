using System.Text.Json;
using System.Text.RegularExpressions;
using BetterCrewLinkKai.DotNet.Models;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class PeerSignalPayloadEventArgs : EventArgs
{
    public PeerSignalPayloadEventArgs(string socketId, object payload)
    {
        SocketId = socketId;
        Payload = payload;
    }

    public string SocketId { get; }

    public object Payload { get; }
}

public sealed class PeerDataPayloadEventArgs : EventArgs
{
    public PeerDataPayloadEventArgs(string socketId, string payload)
    {
        SocketId = socketId;
        Payload = payload;
    }

    public string SocketId { get; }

    public string Payload { get; }
}

public sealed class PeerAudioFrameEventArgs : EventArgs
{
    public PeerAudioFrameEventArgs(string socketId, VoiceAudioFrame frame)
    {
        SocketId = socketId;
        Frame = frame;
    }

    public string SocketId { get; }

    public VoiceAudioFrame Frame { get; }
}

public sealed record PeerIceServer(string Urls, string? Username = null, string? Credential = null);

public sealed class PeerConnectionService
{
    private static readonly AudioFormat OpusFormat = new(AudioCodecsEnum.OPUS, 111, 48000, 2, "useinbandfec=1");
    private static readonly WaveFormat WebRtcPcmFormat = new(48000, 16, 1);
    private static readonly WaveFormat WebRtcStereoPcmFormat = new(48000, 16, 2);
    private const double WebRtcSendGain = 0.55d;
    private const int WebRtcSamplesPerChannelPerFrame = 960;
    private const int WebRtcChannels = 2;
    private const int WebRtcStereoSamplesPerFrame = WebRtcSamplesPerChannelPerFrame * WebRtcChannels;
    private static readonly IReadOnlyList<string> DefaultIceServers = ["stun:stun.l.google.com:19302"];
    private static readonly IReadOnlyList<string> NatFixIceServers =
    [
        "stun:stun.l.google.com:19302",
        "stun:global.stun.twilio.com:3478",
        "stun:stun.cloudflare.com:3478"
    ];

    private readonly Dictionary<string, VoicePeerConnection> peers = [];
    private readonly Dictionary<string, RTCPeerConnection> rtcPeers = [];
    private readonly Dictionary<string, RTCDataChannel> dataChannels = [];
    private readonly Dictionary<string, List<RTCIceCandidateInit>> pendingRemoteCandidates = [];
    private readonly Dictionary<string, List<short>> pendingSendPcm = [];
    private readonly Dictionary<string, int> pendingDisconnectChecks = [];
    private readonly AudioEncoder audioEncoder = new(true, true);
    private readonly object audioEncoderLock = new();
    private const int DisconnectGraceMilliseconds = 3500;
    private bool useNatFix;
    private bool forceRelayOnly;
    private IReadOnlyList<PeerIceServer>? serverIceServers;
    private IReadOnlyList<string> iceServers = DefaultIceServers;

    public IReadOnlyDictionary<string, VoicePeerConnection> Peers => peers;

    public bool UseNatFix => useNatFix;

    public IReadOnlyList<string> IceServers => iceServers;

    public bool ForceRelayOnly => forceRelayOnly;

    public event EventHandler? PeersChanged;

    public event EventHandler<PeerSignalPayloadEventArgs>? SignalPayloadQueued;

    public event EventHandler<PeerDataPayloadEventArgs>? DataPayloadQueued;

    public event EventHandler<PeerAudioFrameEventArgs>? AudioFrameQueued;

    public event EventHandler<PeerAudioFrameEventArgs>? AudioFrameReceived;

    public event EventHandler<string>? Error;

    public void ConfigureNatFix(bool enabled)
    {
        if (useNatFix == enabled)
        {
            return;
        }

        useNatFix = enabled;
        RefreshIceServers();
        UpdatePeerConfigurationSnapshots();
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ConfigureServerPeerConfig(IEnumerable<PeerIceServer> nextIceServers, bool nextForceRelayOnly)
    {
        var normalizedIceServers = nextIceServers
            .Where(static server => !string.IsNullOrWhiteSpace(server.Urls))
            .Select(static server => new PeerIceServer(
                server.Urls.Trim(),
                string.IsNullOrWhiteSpace(server.Username) ? null : server.Username.Trim(),
                string.IsNullOrWhiteSpace(server.Credential) ? null : server.Credential))
            .Distinct()
            .ToArray();

        var changed = forceRelayOnly != nextForceRelayOnly ||
            !IceServerListsEqual(serverIceServers ?? [], normalizedIceServers);
        if (!changed)
        {
            return;
        }

        forceRelayOnly = nextForceRelayOnly;
        serverIceServers = normalizedIceServers.Length == 0 ? null : normalizedIceServers;
        RefreshIceServers();
        UpdatePeerConfigurationSnapshots();
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshIceServers()
    {
        iceServers = serverIceServers is { Count: > 0 }
            ? serverIceServers.Select(static server => server.Urls).ToArray()
            : useNatFix
                ? NatFixIceServers
                : DefaultIceServers;
    }

    private void UpdatePeerConfigurationSnapshots()
    {
        foreach (var peer in peers.Values)
        {
            peer.UseNatFix = useNatFix;
            peer.IceServers = iceServers;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public VoicePeerConnection EnsurePeer(string socketId, VoiceClient client, bool initiator)
    {
        if (string.IsNullOrWhiteSpace(socketId))
        {
            throw new ArgumentException("Socket id is empty.", nameof(socketId));
        }

        if (!peers.TryGetValue(socketId, out var peer))
        {
            peer = new VoicePeerConnection
            {
                SocketId = socketId,
                PlayerId = client.PlayerId,
                ClientId = client.ClientId,
                Initiator = initiator,
                UseNatFix = useNatFix,
                IceServers = iceServers,
                State = VoicePeerConnectionState.Connecting
            };
            peers[socketId] = peer;
        }
        else if (peer.State is VoicePeerConnectionState.Disconnected or VoicePeerConnectionState.Failed)
        {
            peer.State = VoicePeerConnectionState.Connecting;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
        }

        PeersChanged?.Invoke(this, EventArgs.Empty);
        return peer;
    }

    public async Task InitiatePeerAsync(string socketId, VoiceClient client)
    {
        try
        {
            EnsurePeer(socketId, client, initiator: true);
            CloseRtcPeer(socketId);
            var rtcPeer = await EnsureRtcPeerAsync(socketId, client, initiator: true).ConfigureAwait(false);
            var offer = rtcPeer.createOffer(null);
            offer.sdp = AddBrowserMediaStreamSdpAttributes(offer.sdp, socketId);
            DiagnosticLog.WebRtc($"local offer > {socketId}\n{ExtractAudioSdpSection(offer.sdp)}");
            await rtcPeer.setLocalDescription(offer).ConfigureAwait(false);
            SignalPayloadQueued?.Invoke(this, new PeerSignalPayloadEventArgs(socketId, new
            {
                type = "offer",
                sdp = offer.sdp
            }));
        }
        catch (Exception ex)
        {
            MarkFailed(socketId);
            Error?.Invoke(this, $"WebRTC offer failed: {ex.Message}");
        }
    }

    public async Task ApplySignalAsync(string socketId, VoiceClient client, JsonElement signal)
    {
        try
        {
            var typeText = signal.TryGetProperty("type", out var type)
                ? type.GetString() ?? string.Empty
                : string.Empty;
            DiagnosticLog.WebRtc($"apply signal < {socketId} type={typeText}\n{SummarizeSignal(signal)}");
            var isOffer = string.Equals(typeText, "offer", StringComparison.OrdinalIgnoreCase);
            var peer = EnsurePeer(socketId, client, initiator: false);
            if (isOffer || peer.LastSignal is null)
            {
                peer.LastSignal = signal.Clone();
            }

            if (isOffer)
            {
                CloseRtcPeer(socketId);
            }

            peer.State = VoicePeerConnectionState.Connecting;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
            PeersChanged?.Invoke(this, EventArgs.Empty);

            var rtcPeer = await EnsureRtcPeerAsync(socketId, client, initiator: false).ConfigureAwait(false);
            if (string.Equals(typeText, "candidate", StringComparison.OrdinalIgnoreCase))
            {
                QueueOrApplyIceCandidate(socketId, rtcPeer, signal);
                return;
            }

            if (!TryReadDescription(signal, out var description))
            {
                Error?.Invoke(this, $"Unsupported WebRTC signal: {typeText}");
                return;
            }

            var result = rtcPeer.setRemoteDescription(description);
            DiagnosticLog.WebRtc($"remote description {socketId} type={description.type} result={result}\n{ExtractAudioSdpSection(description.sdp)}");
            if (result != SetDescriptionResultEnum.OK)
            {
                MarkFailed(socketId);
                Error?.Invoke(this, $"WebRTC remote description failed: {result}");
                return;
            }

            ApplyQueuedIceCandidates(socketId, rtcPeer);

            if (description.type == RTCSdpType.offer)
            {
                var answer = rtcPeer.createAnswer(null);
                answer.sdp = AddBrowserMediaStreamSdpAttributes(answer.sdp, socketId);
                DiagnosticLog.WebRtc($"local answer > {socketId}\n{ExtractAudioSdpSection(answer.sdp)}");
                await rtcPeer.setLocalDescription(answer).ConfigureAwait(false);
                SignalPayloadQueued?.Invoke(this, new PeerSignalPayloadEventArgs(socketId, new
                {
                    type = "answer",
                    sdp = answer.sdp
                }));
            }
        }
        catch (Exception ex)
        {
            MarkFailed(socketId);
            Error?.Invoke(this, $"WebRTC signal failed: {ex.Message}");
        }
    }

    public void MarkConnected(string socketId)
    {
        pendingDisconnectChecks.Remove(socketId);
        DiagnosticLog.WebRtc($"peer connected {socketId}");
        if (peers.TryGetValue(socketId, out var peer))
        {
            peer.State = VoicePeerConnectionState.Connected;
            peer.ReconnectAttempts = 0;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
            PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void MarkTemporarilyDisconnected(string socketId)
    {
        DiagnosticLog.WebRtc($"peer temporarily disconnected {socketId}");
        if (!peers.TryGetValue(socketId, out var peer))
        {
            return;
        }

        peer.UpdatedAt = DateTimeOffset.UtcNow;
        PeersChanged?.Invoke(this, EventArgs.Empty);

        var checkId = pendingDisconnectChecks.TryGetValue(socketId, out var current) ? current + 1 : 1;
        pendingDisconnectChecks[socketId] = checkId;
        _ = Task.Delay(DisconnectGraceMilliseconds).ContinueWith(_ =>
        {
            if (!pendingDisconnectChecks.TryGetValue(socketId, out var latest) || latest != checkId)
            {
                return;
            }

            if (rtcPeers.TryGetValue(socketId, out var rtcPeer) &&
                rtcPeer.connectionState is RTCPeerConnectionState.connected or RTCPeerConnectionState.connecting)
            {
                return;
            }

            pendingDisconnectChecks.Remove(socketId);
            MarkFailed(socketId);
        });
    }

    public void MarkFailed(string socketId)
    {
        pendingDisconnectChecks.Remove(socketId);
        DiagnosticLog.WebRtc($"peer failed {socketId}");
        if (peers.TryGetValue(socketId, out var peer))
        {
            peer.State = VoicePeerConnectionState.Failed;
            peer.ReconnectAttempts++;
            peer.UpdatedAt = DateTimeOffset.UtcNow;
            PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemovePeer(string socketId)
    {
        CloseRtcPeer(socketId);
        if (peers.Remove(socketId))
        {
            PeersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        foreach (var socketId in rtcPeers.Keys.ToList())
        {
            CloseRtcPeer(socketId);
        }

        if (peers.Count == 0)
        {
            return;
        }

        peers.Clear();
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void QueueDataPayload(string socketId, string payload)
    {
        if (dataChannels.TryGetValue(socketId, out var dataChannel) && dataChannel.IsOpened)
        {
            dataChannel.send(payload);
            return;
        }

        if (peers.ContainsKey(socketId))
        {
            DataPayloadQueued?.Invoke(this, new PeerDataPayloadEventArgs(socketId, payload));
        }
    }

    public void BroadcastDataPayload(string payload)
    {
        foreach (var socketId in peers.Keys.ToList())
        {
            QueueDataPayload(socketId, payload);
        }
    }

    public void QueueAudioFrame(string socketId, VoiceAudioFrame frame)
    {
        SendWebRtcAudioFrame(socketId, frame);
        if (peers.TryGetValue(socketId, out var peer) &&
            peer.State is VoicePeerConnectionState.Connecting or VoicePeerConnectionState.Connected)
        {
            AudioFrameQueued?.Invoke(this, new PeerAudioFrameEventArgs(socketId, frame));
        }
    }

    public void BroadcastAudioFrame(VoiceAudioFrame frame)
    {
        foreach (var socketId in peers.Keys.ToList())
        {
            QueueAudioFrame(socketId, frame);
        }
    }

    private Task<RTCPeerConnection> EnsureRtcPeerAsync(string socketId, VoiceClient client, bool initiator)
    {
        if (rtcPeers.TryGetValue(socketId, out var existingPeer))
        {
            return Task.FromResult(existingPeer);
        }

        var rtcPeer = new RTCPeerConnection(CreateRtcConfiguration());
        rtcPeers[socketId] = rtcPeer;
        rtcPeer.addTrack(new MediaStreamTrack([OpusFormat], MediaStreamStatusEnum.SendRecv));

        rtcPeer.onicecandidate += candidate =>
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.candidate))
            {
                return;
            }

            DiagnosticLog.WebRtc($"ice candidate > {socketId} {candidate.candidate}");
            SignalPayloadQueued?.Invoke(this, new PeerSignalPayloadEventArgs(socketId, new
            {
                type = "candidate",
                candidate = new
                {
                    candidate = candidate.candidate,
                    sdpMLineIndex = candidate.sdpMLineIndex,
                    sdpMid = candidate.sdpMid
                }
            }));
        };

        rtcPeer.onconnectionstatechange += state =>
        {
            DiagnosticLog.WebRtc($"rtc connection state {socketId} {state}");
            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    MarkConnected(socketId);
                    break;
                case RTCPeerConnectionState.disconnected:
                    MarkTemporarilyDisconnected(socketId);
                    break;
                case RTCPeerConnectionState.failed:
                case RTCPeerConnectionState.closed:
                    MarkFailed(socketId);
                    break;
            }
        };

        rtcPeer.OnAudioFrameReceived += encodedFrame => ApplyWebRtcAudioFrame(socketId, encodedFrame);
        rtcPeer.ondatachannel += dataChannel =>
        {
            DiagnosticLog.WebRtc($"data channel < {socketId}");
            ConfigureDataChannel(socketId, dataChannel);
        };

        if (initiator)
        {
            return rtcPeer.createDataChannel("data", null).ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    DiagnosticLog.WebRtc($"data channel created > {socketId}");
                    ConfigureDataChannel(socketId, task.Result);
                }

                return rtcPeer;
            });
        }

        return Task.FromResult(rtcPeer);
    }

    private static string AddBrowserMediaStreamSdpAttributes(string sdp, string socketId)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return sdp;
        }

        var newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var streamId = $"bclkai-{SanitizeSdpToken(socketId)}";
        var trackId = $"{streamId}-audio";
        var lines = sdp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        EnsureMediaStreamSemantic(lines, streamId);

        var audioStart = lines.FindIndex(static line => line.StartsWith("m=audio ", StringComparison.Ordinal));
        if (audioStart < 0)
        {
            return string.Join(newline, lines);
        }

        var audioEnd = lines.FindIndex(audioStart + 1, static line => line.StartsWith("m=", StringComparison.Ordinal));
        if (audioEnd < 0)
        {
            audioEnd = lines.Count;
        }

        var directionIndex = FindAudioLine(lines, audioStart, audioEnd, static line =>
            line is "a=sendrecv" or "a=sendonly" or "a=recvonly" or "a=inactive");
        if (directionIndex >= 0)
        {
            lines[directionIndex] = "a=sendrecv";
        }
        else
        {
            lines.Insert(audioStart + 1, "a=sendrecv");
            audioEnd++;
            directionIndex = audioStart + 1;
        }

        var hasLocalMsid = lines
            .Skip(audioStart)
            .Take(audioEnd - audioStart)
            .Any(line => string.Equals(line, $"a=msid:{streamId} {trackId}", StringComparison.OrdinalIgnoreCase));
        if (!hasLocalMsid)
        {
            lines.Insert(directionIndex + 1, $"a=msid:{streamId} {trackId}");
            audioEnd++;
        }

        var ssrcLine = lines
            .Skip(audioStart)
            .Take(audioEnd - audioStart)
            .FirstOrDefault(static line => line.StartsWith("a=ssrc:", StringComparison.OrdinalIgnoreCase));
        var ssrcMatch = ssrcLine is null ? null : Regex.Match(ssrcLine, @"^a=ssrc:(\d+)\s");
        if (ssrcMatch?.Success == true)
        {
            var ssrc = ssrcMatch.Groups[1].Value;
            EnsureSsrcAttribute(lines, audioStart, ref audioEnd, ssrc, "cname", streamId);
            EnsureSsrcAttribute(lines, audioStart, ref audioEnd, ssrc, "msid", $"{streamId} {trackId}");
            EnsureSsrcAttribute(lines, audioStart, ref audioEnd, ssrc, "mslabel", streamId);
            EnsureSsrcAttribute(lines, audioStart, ref audioEnd, ssrc, "label", trackId);
        }

        var fixedSdp = string.Join(newline, lines);
        DiagnosticLog.WebRtc($"sdp audio fixed {socketId}\n{ExtractAudioSdpSection(fixedSdp)}");
        return fixedSdp;
    }

    private static void EnsureSsrcAttribute(List<string> lines, int audioStart, ref int audioEnd, string ssrc, string attribute, string value)
    {
        var prefix = $"a=ssrc:{ssrc} {attribute}:";
        if (lines
            .Skip(audioStart)
            .Take(audioEnd - audioStart)
            .Any(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var insertIndex = FindAudioLine(lines, audioStart, audioEnd, line =>
            line.StartsWith($"a=ssrc:{ssrc} ", StringComparison.OrdinalIgnoreCase));
        lines.Insert(insertIndex >= 0 ? insertIndex + 1 : audioEnd, $"{prefix}{value}");
        audioEnd++;
    }

    private static void EnsureMediaStreamSemantic(List<string> lines, string streamId)
    {
        var semanticIndex = lines.FindIndex(static line => line.StartsWith("a=msid-semantic:", StringComparison.OrdinalIgnoreCase));
        if (semanticIndex < 0)
        {
            var firstMediaIndex = lines.FindIndex(static line => line.StartsWith("m=", StringComparison.Ordinal));
            lines.Insert(firstMediaIndex >= 0 ? firstMediaIndex : lines.Count, $"a=msid-semantic: WMS {streamId}");
            return;
        }

        var semantic = lines[semanticIndex];
        if (semantic.Contains(" *", StringComparison.Ordinal) ||
            semantic.EndsWith(" *", StringComparison.Ordinal) ||
            semantic.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(streamId, StringComparer.Ordinal))
        {
            return;
        }

        lines[semanticIndex] = $"{semantic} {streamId}";
    }

    private static int FindAudioLine(List<string> lines, int start, int end, Predicate<string> match)
    {
        for (var index = start; index < end; index++)
        {
            if (match(lines[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static string SanitizeSdpToken(string value)
    {
        var sanitized = Regex.Replace(value, "[^A-Za-z0-9_-]", "_");
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private static string ExtractAudioSdpSection(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return string.Empty;
        }

        var lines = sdp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var audioStart = Array.FindIndex(lines, static line => line.StartsWith("m=audio ", StringComparison.Ordinal));
        if (audioStart < 0)
        {
            return sdp.Length <= 2000 ? sdp : sdp[..2000] + "...";
        }

        var audioEnd = Array.FindIndex(lines, audioStart + 1, static line => line.StartsWith("m=", StringComparison.Ordinal));
        if (audioEnd < 0)
        {
            audioEnd = lines.Length;
        }

        return string.Join(Environment.NewLine, lines.Skip(audioStart).Take(audioEnd - audioStart));
    }

    private static string SummarizeSignal(JsonElement signal)
    {
        if (signal.TryGetProperty("sdp", out var sdpElement))
        {
            return ExtractAudioSdpSection(sdpElement.GetString() ?? string.Empty);
        }

        return signal.GetRawText();
    }

    private RTCConfiguration CreateRtcConfiguration()
    {
        return new RTCConfiguration
        {
            iceServers = CreateRtcIceServers(),
            iceTransportPolicy = forceRelayOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all
        };
    }

    private List<RTCIceServer> CreateRtcIceServers()
    {
        if (serverIceServers is { Count: > 0 })
        {
            return serverIceServers
                .Select(static server => new RTCIceServer
                {
                    urls = server.Urls,
                    username = server.Username,
                    credential = server.Credential
                })
                .ToList();
        }

        return iceServers
            .Select(static server => new RTCIceServer { urls = server })
            .ToList();
    }

    private static bool IceServerListsEqual(IReadOnlyList<PeerIceServer> left, IReadOnlyList<PeerIceServer> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Urls, right[i].Urls, StringComparison.Ordinal) ||
                !string.Equals(left[i].Username, right[i].Username, StringComparison.Ordinal) ||
                !string.Equals(left[i].Credential, right[i].Credential, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void SendWebRtcAudioFrame(string socketId, VoiceAudioFrame frame)
    {
        if (!rtcPeers.TryGetValue(socketId, out var rtcPeer) ||
            rtcPeer.connectionState is not RTCPeerConnectionState.connected)
        {
            return;
        }

        var pcm = ToPcm16Stereo48k(frame);
        if (pcm.Length == 0)
        {
            return;
        }

        if (!frame.IsTransmitting)
        {
            Array.Clear(pcm);
        }

        if (!pendingSendPcm.TryGetValue(socketId, out var pendingPcm))
        {
            pendingPcm = [];
            pendingSendPcm[socketId] = pendingPcm;
        }

        pendingPcm.AddRange(pcm);
        while (pendingPcm.Count >= WebRtcStereoSamplesPerFrame)
        {
            var opusPcm = pendingPcm.Take(WebRtcStereoSamplesPerFrame).ToArray();
            pendingPcm.RemoveRange(0, WebRtcStereoSamplesPerFrame);

            byte[] encoded;
            try
            {
                lock (audioEncoderLock)
                {
                    encoded = audioEncoder.EncodeAudio(opusPcm, OpusFormat);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"WebRTC audio encode failed: {ex.Message}");
                return;
            }

            if (encoded.Length == 0)
            {
                continue;
            }

            try
            {
                rtcPeer.SendAudio(WebRtcSamplesPerChannelPerFrame, encoded);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"WebRTC audio send failed: {ex.Message}");
            }
        }
    }

    private void ApplyWebRtcAudioFrame(string socketId, EncodedAudioFrame encodedFrame)
    {
        if (encodedFrame.EncodedAudio.Length == 0)
        {
            return;
        }

        var audioFormat = encodedFrame.AudioFormat;
        if (audioFormat.Codec == AudioCodecsEnum.Unknown)
        {
            audioFormat = OpusFormat;
        }

        short[] pcm;
        try
        {
            lock (audioEncoderLock)
            {
                pcm = audioEncoder.DecodeAudio(encodedFrame.EncodedAudio, audioFormat);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"WebRTC audio decode failed: {ex.Message}");
            return;
        }

        if (pcm.Length == 0)
        {
            return;
        }

        var channels = InferDecodedPcmChannels(pcm, audioFormat.ChannelCount);
        var buffer = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, buffer, 0, buffer.Length);
        var level = CalculateLevel(pcm);
        AudioFrameReceived?.Invoke(this, new PeerAudioFrameEventArgs(
            socketId,
            new VoiceAudioFrame(buffer, channels == 2 ? WebRtcStereoPcmFormat : WebRtcPcmFormat, level, isTransmitting: true)));
    }

    private static short[] ToPcm16Stereo48k(VoiceAudioFrame frame)
    {
        if (frame.Format.Encoding != WaveFormatEncoding.Pcm ||
            frame.Format.BitsPerSample != 16 ||
            frame.Format.SampleRate != 48000 ||
            frame.Format.Channels != 1)
        {
            return [];
        }

        var mono = new short[frame.Buffer.Length / 2];
        Buffer.BlockCopy(frame.Buffer, 0, mono, 0, mono.Length * 2);
        var stereo = new short[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            var sample = (short)Math.Clamp(mono[i] * WebRtcSendGain, short.MinValue, short.MaxValue);
            var outputIndex = i * 2;
            stereo[outputIndex] = sample;
            stereo[outputIndex + 1] = sample;
        }

        return stereo;
    }

    private static double CalculateLevel(short[] pcm)
    {
        if (pcm.Length == 0)
        {
            return 0;
        }

        double total = 0;
        foreach (var sample in pcm)
        {
            var normalized = sample / 32768d;
            total += normalized * normalized;
        }

        return Math.Clamp(Math.Sqrt(total / pcm.Length) * 220, 0, 100);
    }

    private static int InferDecodedPcmChannels(short[] pcm, int advertisedChannels)
    {
        var channels = Math.Clamp(advertisedChannels, 1, 2);
        if (pcm.Length == 0)
        {
            return channels;
        }

        var monoRemainder = pcm.Length % WebRtcSamplesPerChannelPerFrame;
        var stereoRemainder = pcm.Length % WebRtcStereoSamplesPerFrame;
        if (monoRemainder == 0 && stereoRemainder != 0)
        {
            return 1;
        }

        if (stereoRemainder == 0 && channels == 2)
        {
            return 2;
        }

        return channels;
    }

    private void ConfigureDataChannel(string socketId, RTCDataChannel dataChannel)
    {
        dataChannels[socketId] = dataChannel;
        dataChannel.onmessage += (_, _, data) =>
        {
            if (data is null || data.Length == 0)
            {
                return;
            }

            var payload = System.Text.Encoding.UTF8.GetString(data);
            DataPayloadQueued?.Invoke(this, new PeerDataPayloadEventArgs(socketId, payload));
        };
        dataChannel.onclose += () =>
        {
            dataChannels.Remove(socketId);
            MarkTemporarilyDisconnected(socketId);
        };
    }

    private static bool TryReadDescription(JsonElement signal, out RTCSessionDescriptionInit description)
    {
        description = new RTCSessionDescriptionInit();
        if (!signal.TryGetProperty("type", out var typeElement) ||
            !signal.TryGetProperty("sdp", out var sdpElement))
        {
            return false;
        }

        var typeText = typeElement.GetString();
        var sdp = sdpElement.GetString();
        if (string.IsNullOrWhiteSpace(typeText) || string.IsNullOrWhiteSpace(sdp) ||
            !Enum.TryParse<RTCSdpType>(typeText, ignoreCase: true, out var type))
        {
            return false;
        }

        description.type = type;
        description.sdp = sdp;
        return true;
    }

    private static void ApplyIceCandidate(RTCPeerConnection rtcPeer, JsonElement signal)
    {
        var candidate = ReadIceCandidate(signal);
        if (candidate is not null)
        {
            DiagnosticLog.WebRtc($"ice candidate apply {candidate.candidate}");
            rtcPeer.addIceCandidate(candidate);
        }
    }

    private void QueueOrApplyIceCandidate(string socketId, RTCPeerConnection rtcPeer, JsonElement signal)
    {
        var candidate = ReadIceCandidate(signal);
        if (candidate is null)
        {
            return;
        }

        if (rtcPeer.remoteDescription is null)
        {
            DiagnosticLog.WebRtc($"ice candidate queued {socketId} {candidate.candidate}");
            if (!pendingRemoteCandidates.TryGetValue(socketId, out var candidates))
            {
                candidates = [];
                pendingRemoteCandidates[socketId] = candidates;
            }

            candidates.Add(candidate);
            return;
        }

        DiagnosticLog.WebRtc($"ice candidate apply {socketId} {candidate.candidate}");
        rtcPeer.addIceCandidate(candidate);
    }

    private void ApplyQueuedIceCandidates(string socketId, RTCPeerConnection rtcPeer)
    {
        if (!pendingRemoteCandidates.Remove(socketId, out var candidates))
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            DiagnosticLog.WebRtc($"ice candidate apply queued {socketId} {candidate.candidate}");
            rtcPeer.addIceCandidate(candidate);
        }
    }

    private static RTCIceCandidateInit? ReadIceCandidate(JsonElement signal)
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
            candidate = GetString(candidateElement, "candidate"),
            sdpMid = GetString(candidateElement, "sdpMid")
        };

        if (candidateElement.TryGetProperty("sdpMLineIndex", out var indexElement) &&
            indexElement.TryGetUInt16(out var index))
        {
            candidate.sdpMLineIndex = index;
        }

        return string.IsNullOrWhiteSpace(candidate.candidate) ? null : candidate;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) ? property.GetString() ?? string.Empty : string.Empty;
    }

    private void CloseRtcPeer(string socketId)
    {
        pendingDisconnectChecks.Remove(socketId);
        dataChannels.Remove(socketId);
        pendingRemoteCandidates.Remove(socketId);
        pendingSendPcm.Remove(socketId);
        if (rtcPeers.Remove(socketId, out var rtcPeer))
        {
            rtcPeer.close();
            rtcPeer.Dispose();
        }
    }
}
