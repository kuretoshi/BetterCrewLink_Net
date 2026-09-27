using System.Text.Json;

namespace BetterCrewLinkKai.DotNet.Models;

public enum VoicePeerConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Failed
}

public sealed class VoicePeerConnection
{
    public string SocketId { get; init; } = string.Empty;

    public int PlayerId { get; init; }

    public int ClientId { get; init; }

    public bool Initiator { get; init; }

    public VoicePeerConnectionState State { get; set; } = VoicePeerConnectionState.New;

    public int ReconnectAttempts { get; set; }

    public bool UseNatFix { get; set; }

    public IReadOnlyList<string> IceServers { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public JsonElement? LastSignal { get; set; }
}
