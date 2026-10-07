using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class NosRadioStatusWire
{
    // Official 3.2.16 rejects null nosRadioKind; omit it on release.
    public static string Build(bool active, long version, int? kind) => active && kind.HasValue
        ? JsonSerializer.Serialize(new { impostorRadio = true, impostorRadioVersion = version,
            nosRadioKind = kind.Value })
        : JsonSerializer.Serialize(new { impostorRadio = active, impostorRadioVersion = version });
}
