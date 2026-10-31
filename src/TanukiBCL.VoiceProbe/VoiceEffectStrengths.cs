namespace TanukiBCL.VoiceProbe;

// Strength is chosen by each listener. The lobby still supplies effect enable switches.
internal sealed record VoiceEffectStrengths
{
    public RokuroVoiceSettings RokuroVoice { get; init; } = new();
    public IReadOnlyDictionary<string, VoiceEffectTuningOverride>? Tunings { get; init; }
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

    public VoiceEffectTuning Tuning(string id, int disguiseStrength = 100) => VoiceEffectTunings.Resolve(Tunings, id,
        id switch
        {
            "disguise" => disguiseStrength, "snrJumbo" => SnrJumboVoiceEffectStrength,
            "snrPush" => SnrPushReverbStrength, "nosSize" => NosSizeVoiceEffectStrength,
            "nosRokurokubi" => NosRokurokubiVoiceEffectStrength, "nosBerserker" => NosBerserkerVoiceEffectStrength,
            "nosCitrus" => NosCitrusVoiceEffectStrength, "nosRainbowStar" => NosRainbowStarEchoStrength,
            "nosVoiceJamming" => NosVoiceJammingEffectStrength, "nosMegaphone" => NosMegaphoneEffectStrength,
            "nosBibiri" => NosBibiriEchoStrength, _ => 100
        });

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
