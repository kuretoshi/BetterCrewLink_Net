namespace TanukiBCL.VoiceProbe;

// Strength is chosen by each listener. The lobby still supplies effect enable switches.
internal sealed record VoiceEffectStrengths
{
    public int SnrPushReverbStrength { get; init; } = 100;
    public int SnrJumboVoiceEffectStrength { get; init; } = 100;
    public int NosSizeVoiceEffectStrength { get; init; } = 100;
    public int NosRokurokubiVoiceEffectStrength { get; init; } = 100;
    public int NosBerserkerVoiceEffectStrength { get; init; } = 100;
    public int NosCitrusVoiceEffectStrength { get; init; } = 100;
    public int NosRainbowStarEchoStrength { get; init; } = 100;
    public int NosVoiceJammingEffectStrength { get; init; } = 100;
    public int NosMegaphoneEffectStrength { get; init; } = 100;
    public int NosBibiriEchoStrength { get; init; } = 100;

    public LobbySettings ApplyTo(LobbySettings lobby) => lobby with
    {
        SnrJumboVoiceEffectStrength = SnrJumboVoiceEffectStrength,
        NosSizeVoiceEffectStrength = NosSizeVoiceEffectStrength,
        NosRokurokubiVoiceEffectStrength = NosRokurokubiVoiceEffectStrength,
        NosBerserkerVoiceEffectStrength = NosBerserkerVoiceEffectStrength,
        NosCitrusVoiceEffectStrength = NosCitrusVoiceEffectStrength,
        NosRainbowStarEchoStrength = NosRainbowStarEchoStrength,
        NosVoiceJammingEffectStrength = NosVoiceJammingEffectStrength,
        NosMegaphoneEffectStrength = NosMegaphoneEffectStrength,
        NosBibiriEchoStrength = NosBibiriEchoStrength
    };
}
