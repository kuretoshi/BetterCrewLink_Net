using System.Diagnostics;
using System.Text;
using SIPSorcery.Net;

namespace TanukiBCL.VoiceProbe;

// Browser RTCStats uses currentRoundTripTime from the selected ICE candidate pair.
// SIPSorcery does not expose that statistic, but it does expose the STUN checks
// and their responses. Only the nominated pair's transaction is sampled.
internal sealed class IceRoundTripEstimator
{
    private const double MaxRttMs = 60_000d;
    private const double SampleAgeMs = 30_000d;
    private readonly object gate = new();
    private ChecklistEntry? pendingPair;
    private string? pendingTransactionId;
    private long pendingSentAt;
    private ChecklistEntry? samplePair;
    private double? rttMs;
    private long sampleAt;

    public double? GetRttMs(ChecklistEntry? nominatedPair)
    {
        lock (gate)
        {
            if (rttMs is null || nominatedPair is null || !ReferenceEquals(samplePair, nominatedPair) ||
                Stopwatch.GetElapsedTime(sampleAt).TotalMilliseconds > SampleAgeMs)
                return null;
            return rttMs;
        }
    }

    public void ObserveSent(STUNMessage message, ChecklistEntry? nominatedPair, long now)
    {
        if (nominatedPair?.RequestTransactionID is not { Length: > 0 } nominatedTransactionId) return;
        var bindingRequest = UnwrapBindingRequest(message);
        if (bindingRequest is null ||
            TransactionId(bindingRequest) != nominatedTransactionId) return;
        lock (gate)
        {
            pendingPair = nominatedPair;
            pendingTransactionId = nominatedTransactionId;
            pendingSentAt = now;
        }
    }

    public void ObserveReceived(STUNMessage message, ChecklistEntry? nominatedPair, long now)
    {
        if (message.Header.MessageType != STUNMessageTypesEnum.BindingSuccessResponse ||
            nominatedPair is null) return;
        var responseTransactionId = TransactionId(message);
        lock (gate)
        {
            if (!ReferenceEquals(pendingPair, nominatedPair) ||
                pendingTransactionId != responseTransactionId || now < pendingSentAt) return;
            var measured = (now - pendingSentAt) * 1_000d / Stopwatch.Frequency;
            if (!double.IsFinite(measured) || measured < 0d || measured > MaxRttMs) return;
            rttMs = measured;
            sampleAt = now;
            samplePair = nominatedPair;
            pendingPair = null;
            pendingTransactionId = null;
        }
    }

    private static string TransactionId(STUNMessage message) => Encoding.ASCII.GetString(message.Header.TransactionId);

    private static STUNMessage? UnwrapBindingRequest(STUNMessage message)
    {
        if (message.Header.MessageType == STUNMessageTypesEnum.BindingRequest) return message;
        if (message.Header.MessageType != STUNMessageTypesEnum.SendIndication) return null;
        var data = message.GetFirstAttribute(STUNAttributeTypesEnum.Data)?.Value;
        // Avoid parsing the much more frequent relayed RTP/DTLS datagrams.
        if (data is not { Length: >= 20 } || (data[0] & 0xC0) != 0 ||
            data[4] != 0x21 || data[5] != 0x12 || data[6] != 0xA4 || data[7] != 0x42)
            return null;
        try
        {
            var nested = STUNMessage.ParseSTUNMessage(data, data.Length);
            return nested?.Header.MessageType == STUNMessageTypesEnum.BindingRequest ? nested : null;
        }
        catch (ApplicationException)
        {
            return null;
        }
    }
}
