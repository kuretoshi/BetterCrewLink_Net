namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class NosRadioRules
{
    public const int JackalKind = 1;

    public static bool HasJackalChannel(IReadOnlyList<NosRadioData>? radios) =>
        radios?.Any(radio => radio.Kind == JackalKind) == true;

    public static bool CanHearJackalChannel(IReadOnlyList<NosRadioData>? radios, int listenerId) =>
        listenerId is >= 0 and < 32 &&
        radios?.Any(radio => radio.Kind == JackalKind &&
            ((uint)radio.HearableMask & (1u << listenerId)) != 0) == true;
}
