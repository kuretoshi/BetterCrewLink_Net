using System.Diagnostics;
using SIPSorcery.Net;

namespace TanukiBCL.VoiceProbe;

// RFC 3550 LSR/DLSR gives a peer RTT without comparing clocks on two machines.
// It is an RTCP audio-path sample, not the ICE candidate-pair RTT exposed by browsers.
internal sealed class RtcpRoundTripEstimator
{
    private const int MaxSenderReports = 16;
    private const double MaxPlausibleRttMs = 60_000d;
    private readonly object gate = new();
    private readonly Dictionary<(uint Ssrc, uint Lsr), long> sentAt = [];
    private readonly Queue<(uint Ssrc, uint Lsr)> sentOrder = new();
    private double? rttMs;
    private long measuredAt;

    public double? RttMs
    {
        get
        {
            lock (gate)
            {
                return rttMs is not null &&
                    Stopwatch.GetElapsedTime(measuredAt) <= TimeSpan.FromSeconds(30)
                    ? rttMs : null;
            }
        }
    }

    public void ObserveSent(RTCPSenderReport? senderReport, long timestamp)
    {
        if (senderReport is null) return;
        var key = (senderReport.SSRC, (uint)(senderReport.NtpTimestamp >> 16));
        lock (gate)
        {
            if (!sentAt.ContainsKey(key)) sentOrder.Enqueue(key);
            sentAt[key] = timestamp;
            while (sentOrder.Count > MaxSenderReports)
                sentAt.Remove(sentOrder.Dequeue());
        }
    }

    public void ObserveReceived(ReceptionReportSample sample, long timestamp)
    {
        if (sample.LastSenderReportTimestamp == 0) return;
        lock (gate)
        {
            if (!sentAt.TryGetValue((sample.SSRC, sample.LastSenderReportTimestamp), out var sent) ||
                timestamp < sent) return;
            var elapsedMs = (timestamp - sent) * 1000d / Stopwatch.Frequency;
            var remoteDelayMs = sample.DelaySinceLastSenderReport * 1000d / 65_536d;
            var measuredMs = elapsedMs - remoteDelayMs;
            if (measuredMs < 0d || measuredMs > MaxPlausibleRttMs) return;
            rttMs = measuredMs;
            measuredAt = timestamp;
        }
    }
}
