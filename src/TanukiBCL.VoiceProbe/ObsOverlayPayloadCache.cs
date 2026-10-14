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

    public JsonElement? BuildIfChanged(string secret, AmongUsState state,
        IReadOnlyDictionary<int, ObsPeerState> peers, bool localTalking, bool localUsingRadio)
    {
        lock (gate)
        {
            buffer.Clear();
            using (var writer = new Utf8JsonWriter(buffer))
                ObsOverlayWire.Write(writer, state, peers, localTalking, localUsingRadio);
            if (secret == lastSecret && buffer.WrittenSpan.SequenceEqual(lastPayload)) return null;
            lastPayload = buffer.WrittenSpan.ToArray();
            lastSecret = secret;
            return JsonSerializer.Deserialize<JsonElement>(buffer.WrittenSpan);
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            lastPayload = [];
            lastSecret = string.Empty;
        }
    }
}
