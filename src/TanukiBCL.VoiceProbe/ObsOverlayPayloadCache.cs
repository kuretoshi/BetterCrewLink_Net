using System.Buffers;
using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed class ObsOverlayPayloadCache
{
    private readonly object gate = new();
    private readonly ArrayBufferWriter<byte> buffer = new();
    private byte[] lastPayload = [];
    private string lastSecret = string.Empty;
    private DateTimeOffset lastSentAt;

    public JsonElement? BuildIfChanged(string secret, AmongUsState state,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool localTalking, bool localUsingRadio,
        IReadOnlyList<ObsPlayerAppearance>? appearances = null, string? assetSession = null,
        DateTimeOffset? now = null)
    {
        lock (gate)
        {
            buffer.Clear();
            using (var writer = new Utf8JsonWriter(buffer))
                ObsOverlayWire.Write(writer, state, peers, localTalking, localUsingRadio, appearances, assetSession);
            var current = now ?? DateTimeOffset.UtcNow;
            if (secret == lastSecret && buffer.WrittenSpan.SequenceEqual(lastPayload) &&
                current - lastSentAt < TimeSpan.FromSeconds(5)) return null;
            lastPayload = buffer.WrittenSpan.ToArray();
            lastSecret = secret;
            lastSentAt = current;
            return JsonSerializer.Deserialize<JsonElement>(buffer.WrittenSpan);
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            lastPayload = [];
            lastSecret = string.Empty;
            lastSentAt = default;
        }
    }
}
