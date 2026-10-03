using System.Collections.Concurrent;
using System.Diagnostics;
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
    private static readonly IReadOnlyList<IceServer> NatFixIceServers =
    [
        new("turn:turn.bettercrewl.ink:3478", "M9DRVaByiujoXeuYAAAG", "TpHR9HQNZ8taxjb3")
    ];
    private const int SamplesPerChannel = 960;
    private const int PlaybackChannels = 2;
    private readonly string owner;
    private readonly bool sendTestTone;
    private readonly Func<string, bool>? testToneTarget;
    private bool natFix;
    private readonly Func<string, object, Task> sendSignal;
    private readonly ConcurrentDictionary<string, Peer> peers = new();
    private readonly AudioEncoder audioEncoder = new(true, true);
    private readonly object audioCodecLock = new();
    private IReadOnlyList<IceServer> iceServers = [new("stun:stun.l.google.com:19302", null, null)];
    private bool forceRelayOnly;

    public WebRtcPeerManager(string owner, Func<string, object, Task> sendSignal, bool sendTestTone,
        bool natFix = false, Func<string, bool>? testToneTarget = null)
    {
        this.owner = owner;
        this.sendSignal = sendSignal;
        this.sendTestTone = sendTestTone;
        this.testToneTarget = testToneTarget;
        this.natFix = natFix;
    }

    public event Action<string>? PeerVerified;

    public event Action<string, AudioTestResult>? AudioVerified;

    public event Action<string, short[]>? PcmReceived;

    public event Action<string, string>? PeerDataReceived;

    public event Action<string, Guid>? PeerConnectionFailed;

    public event Action<string, Guid>? PeerDataChannelStalled;

    public event Action<string>? PeerDataChannelOpened;

    public event Action<string, RTCPeerConnectionState>? PeerConnectionStateChanged;

    public event Action<string, ConnectionQuality>? PeerQualityChanged;

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
            if (CanSendAudio(peer) &&
                (canSendToPeer is null || canSendToPeer(peer.RemoteSocketId)))
            {
                try
                {
                    peer.Connection.SendAudio(SamplesPerChannel, encoded);
                    sentPeers++;
                }
                catch (Exception exception)
                {
                    if (Interlocked.Exchange(ref peer.AudioSendFailureLogged, 1) == 0)
                    {
                        Log($"Opus send failed peer={Short(peer.RemoteSocketId)}: {exception.Message}");
                    }
                }
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

        // v3.2.7 ConnectionController uses relay only when the server explicitly
        // requests it. Merely advertising a TURN server must not disable direct ICE.
        forceRelayOnly = serverRequiresRelay;
        Log($"ICE設定受信: servers={iceServers.Count} relayOnly={forceRelayOnly} natFix={natFix}");
    }

    public void SetNatFix(bool enabled) => Volatile.Write(ref natFix, enabled);

    public async Task InitiateAsync(string remoteSocketId)
    {
        if (peers.ContainsKey(remoteSocketId))
        {
            return;
        }

        var peer = CreatePeer(remoteSocketId, initiator: true);
        var channel = await peer.Connection.createDataChannel("tanuki-probe", null);
        ConfigureDataChannel(peer, channel, "local");

        var offer = peer.Connection.createOffer(null);
        await peer.Connection.setLocalDescription(offer);
        var offerSdp = LocalSdpForRemote(peer, offer.sdp);
        Log($"offer > {Short(remoteSocketId)} sctp={HasSctpMedia(offerSdp)}");
        await SendSignalAsync(peer, new { type = "offer", sdp = offerSdp });
        FlushLocalCandidates(peer);
    }

    public bool IsInitiating(string remoteSocketId) =>
        peers.TryGetValue(remoteSocketId, out var peer) && peer.Initiator;

    public bool ShouldDeferIncomingOffer(string remoteSocketId)
    {
        if (!peers.TryGetValue(remoteSocketId, out var peer))
        {
            // Only an actual local offer can glare with an incoming one.
            // The joining client waits for offers from existing v3.2.7 peers.
            return false;
        }

        return peer.Initiator &&
            peer.Connection.connectionState is RTCPeerConnectionState.@new or RTCPeerConnectionState.connecting;
    }

    public bool HasOpenDataChannel(string remoteSocketId) =>
        peers.TryGetValue(remoteSocketId, out var peer) && Volatile.Read(ref peer.DataChannelOpen) == 1;

    public bool HasPeer(string remoteSocketId) => peers.ContainsKey(remoteSocketId);

    public bool IsCurrentPeer(string remoteSocketId, Guid peerInstanceId) =>
        peers.TryGetValue(remoteSocketId, out var peer) && peer.InstanceId == peerInstanceId;

    public bool IsFailedPeer(string remoteSocketId, Guid peerInstanceId) =>
        peers.TryGetValue(remoteSocketId, out var peer) && peer.InstanceId == peerInstanceId &&
        peer.Connection.connectionState == RTCPeerConnectionState.failed;

    private static bool CanSendAudio(Peer peer) =>
        peer.Connection.connectionState == RTCPeerConnectionState.connected ||
        Volatile.Read(ref peer.DataChannelOpen) == 1;

    private bool ShouldSendTestTone(Peer peer) =>
        sendTestTone && (testToneTarget?.Invoke(peer.RemoteSocketId) ?? true);

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
            // A candidate (or answer) arriving before its offer must not create
            // a competing connection that prevents our own offer from starting.
            if (type != "offer")
            {
                return;
            }
            peer = CreatePeer(remoteSocketId, initiator: false, incomingConnectionId);
        }

        if (type == "candidate")
        {
            var candidate = ReadCandidate(data);
            if (candidate is null)
            {
                return;
            }
            if (!IsRelayCandidate(candidate.candidate)) MarkRemoteNonRelayCandidate(peer);
            if (peer.RelayOnly && !IsRelayCandidate(candidate.candidate))
            {
                DeferNonRelayCandidate(peer, candidate);
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
            sdp = peer.RelayOnly ? PreferRelayCandidates(peer, sdpElement.GetString()!) : sdpElement.GetString()!
        };
        if (!peer.RelayOnly && ContainsNonRelayCandidate(description.sdp))
            MarkRemoteNonRelayCandidate(peer);
        var result = peer.Connection.setRemoteDescription(description);
        Log($"{type} < {Short(remoteSocketId)} result={result} sctp={HasSctpMedia(description.sdp)}");
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
            // SIPSorcery needs an answerer-side channel for reliable SCTP
            // establishment with the .NET offerer. Create it after applying
            // remote SDP so the DTLS role has been established.
            if (HasSctpMedia(description.sdp))
            {
                var channel = await peer.Connection.createDataChannel("tanuki-probe", null);
                ConfigureDataChannel(peer, channel, "local");
            }
            var answer = peer.Connection.createAnswer(null);
            await peer.Connection.setLocalDescription(answer);
            var answerSdp = LocalSdpForRemote(peer, answer.sdp);
            Log($"answer > {Short(remoteSocketId)} sctp={HasSctpMedia(answerSdp)}");
            await SendSignalAsync(peer, new { type = "answer", sdp = answerSdp });
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
        // Match v3.2.7: NAT fix selects its static TURN config at peer creation.
        // Existing peers keep their ICE configuration until they reconnect.
        var useNatFix = Volatile.Read(ref natFix);
        var configuration = new RTCConfiguration
        {
            iceTransportPolicy = useNatFix || forceRelayOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
            iceServers = (useNatFix ? NatFixIceServers : iceServers).Select(server => new RTCIceServer
            {
                urls = server.Url,
                username = server.Username,
                credential = server.Credential
            }).ToList()
        };
        var connection = new RTCPeerConnection(configuration);
        var peer = new Peer(remoteSocketId, connectionId ?? Guid.NewGuid().ToString("N"), connection,
            initiator, useNatFix || forceRelayOnly);
        peers[remoteSocketId] = peer;
        _ = WatchHandshakeAsync(peer);
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
            var localCandidate = new LocalCandidateSignal(signal, IsRelayCandidate(candidateText));
            lock (peer.LocalCandidateGate)
            {
                if (!peer.LocalDescriptionSent)
                {
                    peer.PendingLocalCandidates.Add(localCandidate);
                    return;
                }
            }
            SendOrDeferLocalCandidate(peer, localCandidate);
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
                ShouldSendTestTone(peer) &&
                Interlocked.Exchange(ref peer.TestToneStarted, 1) == 0)
            {
                _ = SendTestToneAsync(peer);
            }
            if (state == RTCPeerConnectionState.connected &&
                Interlocked.Exchange(ref peer.DataChannelWatchdogStarted, 1) == 0)
            {
                _ = WatchDataChannelAsync(peer);
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                PeerConnectionFailed?.Invoke(remoteSocketId, peer.InstanceId);
            }
        };
        connection.oniceconnectionstatechange += state =>
        {
            if (!peers.TryGetValue(remoteSocketId, out var currentPeer) || !ReferenceEquals(currentPeer, peer))
                return;
            Log($"peer {Short(remoteSocketId)} ice={state} dtlsRole={connection.IceRole}");
            if (state == RTCIceConnectionState.connected &&
                Interlocked.Exchange(ref peer.DtlsHandshakeWatchdogStarted, 1) == 0)
                _ = WatchDtlsHandshakeAsync(peer);
        };
        connection.onicecandidateerror += (_, error) => Log($"peer {Short(remoteSocketId)} ice-candidate-error={error}");
        connection.sctp.OnStateChanged += state =>
        {
            if (IsCurrentPeer(remoteSocketId, peer.InstanceId))
            {
                Log($"peer {Short(remoteSocketId)} sctp={state} association={connection.sctp.RTCSctpAssociation?.State}");
                if (ShouldRecoverClosedSctp(connection.connectionState, state,
                        Volatile.Read(ref peer.DataChannelOpen) == 1) &&
                    Interlocked.Exchange(ref peer.SctpClosedRecoveryStarted, 1) == 0)
                    _ = RecoverClosedSctpAsync(peer);
            }
        };
        connection.OnRtpClosed += reason =>
        {
            if (IsCurrentPeer(remoteSocketId, peer.InstanceId))
                Log($"peer {Short(remoteSocketId)} transport-closed={reason}");
        };
        connection.ondatachannel += channel => ConfigureDataChannel(peer, channel, "remote");
        connection.OnRtpPacketReceived += (_, mediaType, packet) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio)
                peer.JitterEstimator.Observe(packet.Header.SyncSource, packet.Header.Timestamp, Stopwatch.GetTimestamp());
        };
        connection.OnSendReport += (mediaType, report) =>
        {
            if (mediaType != SDPMediaTypesEnum.audio ||
                !peers.TryGetValue(remoteSocketId, out var current) || !ReferenceEquals(current, peer)) return;
            peer.RoundTripEstimator.ObserveSent(report.SenderReport, Stopwatch.GetTimestamp());
            // Our outgoing RTCP reception report describes the audio we received
            // from this peer. The remote's report would describe our outbound path.
            var sample = report.ReceiverReport?.ReceptionReports?.FirstOrDefault()
                ?? report.SenderReport?.ReceptionReports?.FirstOrDefault();
            if (sample is not null)
                PeerQualityChanged?.Invoke(remoteSocketId, FromReceptionReport(
                    sample, peer.JitterEstimator.JitterMs, IsDirectHostPair(connection.GetRtpChannel().NominatedEntry),
                    peer.RoundTripEstimator.RttMs));
        };
        connection.OnReceiveReport += (_, mediaType, report) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio &&
                peers.TryGetValue(remoteSocketId, out var current) && ReferenceEquals(current, peer))
            {
                var samples = report.ReceiverReport?.ReceptionReports
                    ?? report.SenderReport?.ReceptionReports;
                if (samples is not null)
                {
                    var receivedAt = Stopwatch.GetTimestamp();
                    foreach (var receivedSample in samples)
                        peer.RoundTripEstimator.ObserveReceived(receivedSample, receivedAt);
                }
            }
        };
        connection.OnAudioFrameReceived += frame => ReceiveAudio(peer, frame);
        return peer;
    }

    internal static bool IsDirectHostPair(ChecklistEntry? nominatedEntry) =>
        nominatedEntry?.LocalCandidate.type == RTCIceCandidateType.host &&
        nominatedEntry.RemoteCandidate.type == RTCIceCandidateType.host;

    internal static ConnectionQuality FromReceptionReport(
        ReceptionReportSample sample, double? observedJitterMs, bool direct = false, double? rttMs = null) =>
        new(RttMs: rttMs, JitterMs: observedJitterMs,
            LossPercent: sample.FractionLost * 100d / 256d, Direct: direct);

    private void ConfigureDataChannel(Peer peer, RTCDataChannel channel, string origin)
    {
        lock (peer.DataChannelGate)
        {
            peer.Channel ??= channel;
            peer.AllChannels.Add(channel);
        }
        Log($"data channel configured: {Short(peer.RemoteSocketId)} origin={origin} label={channel.label} id={channel.id} state={channel.readyState}");
        void HandleOpen()
        {
            if (!peers.TryGetValue(peer.RemoteSocketId, out var current) || !ReferenceEquals(current, peer))
            {
                return;
            }
            if (channel.readyState != RTCDataChannelState.open) return;
            var wasOpen = MarkDataChannelOpen(peer, channel);
            if (wasOpen) return;
            Log($"data channel open: {Short(peer.RemoteSocketId)} origin={origin} id={channel.id}");
            OnDataChannelReady(peer);
            if (ShouldSendTestTone(peer))
            {
                channel.send($"tanuki-probe:{owner}:{Guid.NewGuid():N}");
            }
        }
        channel.onopen += HandleOpen;
        channel.onmessage += (_, _, data) =>
        {
            var isCurrent = peers.TryGetValue(peer.RemoteSocketId, out var current) &&
                ReferenceEquals(current, peer);
            if (isCurrent && !MarkDataChannelOpen(peer, channel))
            {
                Log($"data channel ready via message: {Short(peer.RemoteSocketId)}");
                OnDataChannelReady(peer);
            }
            else if (!isCurrent)
            {
                Log($"data from superseded peer: {Short(peer.RemoteSocketId)} connection={peer.ConnectionId}");
            }
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
        channel.onclose += () =>
        {
            lock (peer.DataChannelGate)
            {
                peer.OpenChannels.Remove(channel);
                if (ReferenceEquals(peer.Channel, channel))
                    peer.Channel = peer.OpenChannels.FirstOrDefault();
                Volatile.Write(ref peer.DataChannelOpen, peer.OpenChannels.Count > 0 ? 1 : 0);
            }
            Log($"data channel closed: {Short(peer.RemoteSocketId)}");
        };
        // An incoming channel can already be open when ondatachannel fires.
        // Its onopen event may have been missed before these handlers existed.
        if (channel.readyState == RTCDataChannelState.open) HandleOpen();
    }

    private static bool MarkDataChannelOpen(Peer peer, RTCDataChannel channel)
    {
        lock (peer.DataChannelGate)
        {
            var wasOpen = peer.OpenChannels.Count > 0;
            peer.OpenChannels.Add(channel);
            peer.Channel = channel;
            Volatile.Write(ref peer.DataChannelOpen, 1);
            return wasOpen;
        }
    }

    private void OnDataChannelReady(Peer peer)
    {
        PeerDataChannelOpened?.Invoke(peer.RemoteSocketId);
        if (ShouldSendTestTone(peer) && Interlocked.Exchange(ref peer.TestToneStarted, 1) == 0)
        {
            _ = SendTestToneAsync(peer);
        }
    }

    private async Task WatchDataChannelAsync(Peer peer)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (!peers.TryGetValue(peer.RemoteSocketId, out var current) ||
            !ReferenceEquals(current, peer) ||
            peer.Connection.connectionState != RTCPeerConnectionState.connected ||
            Volatile.Read(ref peer.DataChannelOpen) == 1)
        {
            return;
        }

        string channels;
        lock (peer.DataChannelGate)
            channels = string.Join(",", peer.AllChannels.Select(channel =>
                $"{channel.label}:{channel.id}:{channel.readyState}"));
        Log($"data channel stalled: {Short(peer.RemoteSocketId)} sctp={peer.Connection.sctp.state} " +
            $"association={peer.Connection.sctp.RTCSctpAssociation?.State} channels=[{channels}]");
        PeerDataChannelStalled?.Invoke(peer.RemoteSocketId, peer.InstanceId);
    }

    private async Task RecoverClosedSctpAsync(Peer peer)
    {
        // SIPSorcery 10.0.17 can give up on SCTP association after two seconds.
        // A closed SCTP transport cannot open the pending data channel, so do
        // not wait for the eight-second generic data-channel watchdog.
        await Task.Delay(200);
        if (!IsCurrentPeer(peer.RemoteSocketId, peer.InstanceId) ||
            !ShouldRecoverClosedSctp(peer.Connection.connectionState, peer.Connection.sctp.state,
                Volatile.Read(ref peer.DataChannelOpen) == 1)) return;

        Log($"SCTP association closed before data channel opened: {Short(peer.RemoteSocketId)}");
        PeerDataChannelStalled?.Invoke(peer.RemoteSocketId, peer.InstanceId);
    }

    internal static bool ShouldRecoverClosedSctp(RTCPeerConnectionState connectionState,
        RTCSctpTransportState sctpState, bool dataChannelOpen) =>
        connectionState == RTCPeerConnectionState.connected &&
        sctpState == RTCSctpTransportState.Closed && !dataChannelOpen;

    private async Task WatchDtlsHandshakeAsync(Peer peer)
    {
        // ICE can connect while DTLS remains stuck in "connecting". The
        // generic 30-second watchdog is too late to recover that state promptly.
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (!peers.TryGetValue(peer.RemoteSocketId, out var current) ||
            !ReferenceEquals(current, peer) ||
            peer.Connection.connectionState != RTCPeerConnectionState.connecting)
            return;

        Log($"DTLS handshake stalled: {Short(peer.RemoteSocketId)} ice={peer.Connection.iceConnectionState}");
        PeerDataChannelStalled?.Invoke(peer.RemoteSocketId, peer.InstanceId);
    }

    private async Task WatchHandshakeAsync(Peer peer)
    {
        // The data-channel watchdog starts only after the overall connection
        // (including DTLS) reaches connected.
        // A lost offer/answer can otherwise remain in new/connecting forever.
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (!peers.TryGetValue(peer.RemoteSocketId, out var current) ||
            !ReferenceEquals(current, peer) ||
            Volatile.Read(ref peer.DataChannelOpen) == 1)
        {
            return;
        }

        Log($"peer handshake stalled: {Short(peer.RemoteSocketId)} state={peer.Connection.connectionState}");
        PeerDataChannelStalled?.Invoke(peer.RemoteSocketId, peer.InstanceId);
    }

    private async Task SendTestToneAsync(Peer peer)
    {
        // 440Hz、48kHz、stereo、20ms x 30フレーム（600ms）。
        const double frequency = 440d;
        const double amplitude = short.MaxValue * 0.25d;
        for (var frameIndex = 0; frameIndex < 30; frameIndex++)
        {
            if (!CanSendAudio(peer))
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

            try
            {
                peer.Connection.SendAudio(SamplesPerChannel, encoded);
            }
            catch (Exception exception)
            {
                Log($"Opus test tone failed peer={Short(peer.RemoteSocketId)}: {exception.Message}");
                return;
            }
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
        LocalCandidateSignal[] pending;
        lock (peer.LocalCandidateGate)
        {
            peer.LocalDescriptionSent = true;
            pending = peer.PendingLocalCandidates.ToArray();
            peer.PendingLocalCandidates.Clear();
        }
        foreach (var signal in pending)
            SendOrDeferLocalCandidate(peer, signal);
    }

    private static string LocalSdpForRemote(Peer peer, string sdp)
    {
        if (peer.Initiator || peer.RelayOnly || Volatile.Read(ref peer.RemoteNonRelayCandidateSeen) == 1 ||
            Volatile.Read(ref peer.LocalCandidateFallbackElapsed) == 1) return sdp;
        var result = new StringBuilder(sdp.Length);
        var lines = sdp.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase) &&
                !IsRelayCandidate(line)) continue;
            result.Append(lines[index]);
            if (index < lines.Length - 1) result.Append('\n');
        }
        return result.ToString();
    }

    private static bool ContainsNonRelayCandidate(string sdp) =>
        sdp.Split('\n').Any(rawLine =>
            rawLine.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase) &&
            !IsRelayCandidate(rawLine));

    private void MarkRemoteNonRelayCandidate(Peer peer)
    {
        if (peer.RelayOnly || Interlocked.Exchange(ref peer.RemoteNonRelayCandidateSeen, 1) == 1) return;
        FlushDeferredLocalCandidates(peer);
    }

    private void SendOrDeferLocalCandidate(Peer peer, LocalCandidateSignal candidate)
    {
        if (peer.Initiator || peer.RelayOnly || candidate.Relay ||
            Volatile.Read(ref peer.RemoteNonRelayCandidateSeen) == 1 ||
            Volatile.Read(ref peer.LocalCandidateFallbackElapsed) == 1)
        {
            _ = SendSignalAsync(peer, candidate.Payload);
            return;
        }
        lock (peer.LocalCandidateGate)
        {
            if (Volatile.Read(ref peer.RemoteNonRelayCandidateSeen) == 0)
            {
                peer.DeferredLocalCandidates.Add(candidate.Payload);
                if (Interlocked.CompareExchange(ref peer.LocalCandidateFallbackStarted, 1, 0) == 0)
                    _ = FlushDeferredLocalCandidatesAsync(peer);
                return;
            }
        }
        _ = SendSignalAsync(peer, candidate.Payload);
    }

    private async Task FlushDeferredLocalCandidatesAsync(Peer peer)
    {
        // Relay-only browsers can abandon ICE before the later relay candidate
        // arrives if they first receive this peer's host candidates.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Volatile.Write(ref peer.LocalCandidateFallbackElapsed, 1);
        if (IsCurrentPeer(peer.RemoteSocketId, peer.InstanceId) &&
            peer.Connection.iceConnectionState != RTCIceConnectionState.connected)
            FlushDeferredLocalCandidates(peer);
    }

    private void FlushDeferredLocalCandidates(Peer peer)
    {
        object[] pending;
        lock (peer.LocalCandidateGate)
        {
            if (!peer.LocalDescriptionSent) return;
            pending = [.. peer.DeferredLocalCandidates];
            peer.DeferredLocalCandidates.Clear();
        }
        foreach (var signal in pending)
            _ = SendSignalAsync(peer, signal);
        if (pending.Length > 0)
            Log($"peer {Short(peer.RemoteSocketId)} released non-relay local candidates={pending.Length}");
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

    private static bool IsRelayCandidate(string candidate) =>
        candidate.Contains(" typ relay", StringComparison.OrdinalIgnoreCase);

    private string PreferRelayCandidates(Peer peer, string sdp)
    {
        var result = new StringBuilder(sdp.Length);
        var mediaIndex = -1;
        var lines = sdp.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase)) mediaIndex++;
            if (line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase) &&
                !IsRelayCandidate(line))
            {
                DeferNonRelayCandidate(peer, new RTCIceCandidateInit
                {
                    candidate = line[2..],
                    sdpMLineIndex = (ushort)Math.Max(mediaIndex, 0)
                });
            }
            else
            {
                result.Append(rawLine);
                if (index < lines.Length - 1) result.Append('\n');
            }
        }
        return result.ToString();
    }

    private void DeferNonRelayCandidate(Peer peer, RTCIceCandidateInit candidate)
    {
        lock (peer.DeferredCandidates)
            peer.DeferredCandidates.Add(candidate);
        if (Interlocked.CompareExchange(ref peer.DeferredCandidateFallbackStarted, 1, 0) == 0)
            _ = FlushDeferredCandidatesAsync(peer);
    }

    private async Task FlushDeferredCandidatesAsync(Peer peer)
    {
        // Prefer a relay pair first, but retain host-only interoperability.
        // A relay-only peer can otherwise fail its initial checklist before
        // the remote's trickled relay candidate arrives.
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (IsCurrentPeer(peer.RemoteSocketId, peer.InstanceId) &&
            peer.Connection.iceConnectionState == RTCIceConnectionState.checking &&
            peer.Connection.remoteDescription is not null)
        {
            RTCIceCandidateInit[] candidates;
            lock (peer.DeferredCandidates)
            {
                candidates = [.. peer.DeferredCandidates];
                peer.DeferredCandidates.Clear();
            }
            foreach (var candidate in candidates)
            {
                try { peer.Connection.addIceCandidate(candidate); }
                catch (Exception exception)
                {
                    if (IsCurrentPeer(peer.RemoteSocketId, peer.InstanceId))
                        Log($"non-relay ICE fallback failed: {Short(peer.RemoteSocketId)} {exception.GetType().Name}");
                    continue;
                }
            }
            if (candidates.Length > 0)
                Log($"peer {Short(peer.RemoteSocketId)} non-relay ICE fallback candidates={candidates.Length}");
        }
        Interlocked.Exchange(ref peer.DeferredCandidateFallbackStarted, 0);
        lock (peer.DeferredCandidates)
        {
            if (peer.DeferredCandidates.Count > 0 &&
                IsCurrentPeer(peer.RemoteSocketId, peer.InstanceId) &&
                peer.Connection.iceConnectionState == RTCIceConnectionState.checking &&
                Interlocked.CompareExchange(ref peer.DeferredCandidateFallbackStarted, 1, 0) == 0)
                _ = FlushDeferredCandidatesAsync(peer);
        }
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
    private static bool HasSctpMedia(string? sdp) =>
        sdp?.Contains("m=application ", StringComparison.Ordinal) == true;
    private static string Short(string socketId) => socketId.Length <= 8 ? socketId : socketId[..8];

    private sealed record LocalCandidateSignal(object Payload, bool Relay);

    private sealed record Peer(
        string RemoteSocketId,
        string ConnectionId,
        RTCPeerConnection Connection,
        bool Initiator,
        bool RelayOnly)
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
        public AudioEncoder Decoder { get; } = new(true, true);
        public RTCDataChannel? Channel { get; set; }
        public object DataChannelGate { get; } = new();
        public HashSet<RTCDataChannel> OpenChannels { get; } = [];
        public List<RTCDataChannel> AllChannels { get; } = [];
        public List<RTCIceCandidateInit> PendingCandidates { get; } = [];
        public List<RTCIceCandidateInit> DeferredCandidates { get; } = [];
        public int DeferredCandidateFallbackStarted;
        public object LocalCandidateGate { get; } = new();
        public List<LocalCandidateSignal> PendingLocalCandidates { get; } = [];
        public List<object> DeferredLocalCandidates { get; } = [];
        public int RemoteNonRelayCandidateSeen;
        public int LocalCandidateFallbackStarted;
        public int LocalCandidateFallbackElapsed;
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
        public int DataChannelWatchdogStarted;
        public int DtlsHandshakeWatchdogStarted;
        public int SctpClosedRecoveryStarted;
        public int DataChannelOpen;
        public int AudioSendFailureLogged;
        public RtpAudioJitterEstimator JitterEstimator { get; } = new();
        public RtcpRoundTripEstimator RoundTripEstimator { get; } = new();
    }
}

internal sealed record AudioTestResult(int Frames, double Rms, double FrequencyHz);

// RFC 3550 interarrival jitter for the 48 kHz Opus RTP clock. SIPSorcery's
// RTCP jitter figure has been observed to be implausible on a healthy call.
internal sealed class RtpAudioJitterEstimator
{
    private const double ClockRate = 48_000d;
    private readonly object gate = new();
    private uint? source;
    private uint previousTimestamp;
    private long previousArrival;
    private double jitterTicks;
    private int samples;

    public void Observe(uint ssrc, uint timestamp, long arrivalTicks)
    {
        lock (gate)
        {
            if (source != ssrc)
            {
                source = ssrc;
                previousTimestamp = timestamp;
                previousArrival = arrivalTicks;
                jitterTicks = 0d;
                samples = 0;
                return;
            }

            var rtpDelta = unchecked(timestamp - previousTimestamp);
            var arrivalDelta = arrivalTicks - previousArrival;
            previousTimestamp = timestamp;
            previousArrival = arrivalTicks;
            if (rtpDelta == 0 || rtpDelta > 10 * ClockRate || arrivalDelta < 0)
            {
                jitterTicks = 0d;
                samples = 0;
                return;
            }

            var arrivalRtpTicks = arrivalDelta * ClockRate / Stopwatch.Frequency;
            jitterTicks += (Math.Abs(arrivalRtpTicks - rtpDelta) - jitterTicks) / 16d;
            samples++;
        }
    }

    public double? JitterMs
    {
        get
        {
            lock (gate) return samples == 0 ? null : jitterTicks * 1000d / ClockRate;
        }
    }

    internal static void Verify()
    {
        var estimator = new RtpAudioJitterEstimator();
        estimator.Observe(7, uint.MaxValue - 959, 0);
        estimator.Observe(7, 0, Stopwatch.Frequency / 50);
        if (estimator.JitterMs is not double wrapped || wrapped > 0.1d)
            throw new InvalidOperationException("RTP timestamp wrap introduced false jitter.");
        estimator.Observe(7, 960, Stopwatch.Frequency * 41 / 1000);
        if (estimator.JitterMs is not double varied || varied <= 0d || varied >= 1d)
            throw new InvalidOperationException("RTP arrival variation was not sampled.");
        estimator.Observe(8, 1, Stopwatch.Frequency * 42 / 1000);
        if (estimator.JitterMs is not null)
            throw new InvalidOperationException("A new RTP source inherited stale jitter.");
    }
}
