namespace TanukiBCL.VoiceProbe;

internal sealed record ObsPlayerAppearance(string HatId, string SkinId, string VisorId,
    string? SkinSource = null, string? BodyMaskSource = null);
