namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class NosRadioRules
{
    public const int ImpostorKind = 0;
    public const int JackalKind = 1;

    public static bool IsEnabled(int kind, LobbySettings settings) => kind switch
    {
        ImpostorKind => settings.ImpostorRadioEnabled || settings.ImpostorRadioOnlyMode,
        JackalKind => settings.JackalRadioEnabled && !settings.ImpostorRadioOnlyMode,
        _ => false
    };

    public static bool HasEnabledChannel(IReadOnlyList<NosRadioData>? radios, LobbySettings settings) =>
        radios?.Any(radio => IsEnabled(radio.Kind, settings)) == true;

    public static bool HasChannel(IReadOnlyList<NosRadioData>? radios, int kind) =>
        radios?.Any(radio => radio.Kind == kind) == true;

    public static int? ResolveKind(IReadOnlyList<NosRadioData>? radios, int? selected)
    {
        if (selected is ImpostorKind or JackalKind) return selected;
        var kinds = radios?.Where(radio => radio.Kind is ImpostorKind or JackalKind)
            .Select(radio => radio.Kind).Distinct().Take(2).ToArray();
        return kinds is { Length: 1 } ? kinds[0] : null;
    }

    public static bool CanHearChannel(IReadOnlyList<NosRadioData>? radios, int listenerId, int kind) =>
        listenerId is >= 0 and < 32 &&
        radios?.Any(radio => radio.Kind == kind &&
            ((uint)radio.HearableMask & (1u << listenerId)) != 0) == true;

    public static bool HasJackalChannel(IReadOnlyList<NosRadioData>? radios) =>
        radios?.Any(radio => radio.Kind == JackalKind) == true;

    public static bool CanHearJackalChannel(IReadOnlyList<NosRadioData>? radios, int listenerId) =>
        CanHearChannel(radios, listenerId, JackalKind);
}
