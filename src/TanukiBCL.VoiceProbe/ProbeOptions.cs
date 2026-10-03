namespace TanukiBCL.VoiceProbe;

internal sealed record ProbeOptions(
    Uri Server,
    string? LobbyCode,
    int PlayerId,
    int ClientId,
    bool IsHost,
    TimeSpan? Duration,
    bool SelfTest,
    bool TanukiInteropTest,
    bool LiveAudio,
    bool ListAudioDevices,
    bool ScanGame,
    bool GameAudioSelfTest,
    bool GameAudioTransitionTest,
    bool LiveGameAudioTest,
    bool GameAudioRecoveryTest,
    bool GameAudioServerRecoveryTest,
    int? GameProcessId,
    string? ExpectedGameState,
    int? ExpectedAlive,
    int? ExpectedDead,
    int? ExpectedImpostors,
    bool ExpectNearby,
    int InputDevice,
    int OutputDevice,
    bool AutoRadioTone,
    bool PolicySelfTest,
    int? ExpectedPeerClientId,
    bool VadSelfTest,
    bool NatFix,
    bool NosSnapshot,
    int ExpectedPlayers = 5,
    bool OldSampleDebug = false,
    bool DtlsTrace = false,
    bool TurnTcp = false,
    string? ExpectedTohRole = null)
{
    public static ProbeOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--host" or "--self-test" or "--quality-self-test" or "--mixed-nat-self-test" or "--tanuki-interop-test" or "--live-audio" or "--list-audio-devices" or "--scan-game" or "--game-audio-self-test" or "--game-audio-transition-test" or "--game-audio-recovery-test" or "--game-audio-server-recovery-test" or "--expect-nearby" or "--auto-radio-tone" or "--policy-self-test" or "--vad-self-test" or "--nat-fix" or "--nos-snapshot" or "--old-sample-debug" or "--capture-device-self-test" or "--dtls-trace" or "--turn-tcp")
            {
                switches.Add(argument);
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"引数を解釈できません: {argument}");
            }

            values[argument] = args[++index];
        }

        var serverText = values.GetValueOrDefault("--server") ?? "https://bettercrewl.ink";
        if (!Uri.TryCreate(serverText, UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"サーバーURLが不正です: {serverText}");
        }

        var lobby = values.GetValueOrDefault("--lobby")?.Trim().ToUpperInvariant();
        if (lobby is { Length: 0 })
        {
            lobby = null;
        }

        return new ProbeOptions(
            server,
            lobby,
            ParseInt(values, "--player-id", 0),
            ParseInt(values, "--client-id", 0),
            switches.Contains("--host"),
            values.TryGetValue("--seconds", out var secondsText)
                ? TimeSpan.FromSeconds(ParsePositiveInt(secondsText, "--seconds"))
                : null,
            switches.Contains("--self-test"),
            switches.Contains("--tanuki-interop-test"),
            switches.Contains("--live-audio"),
            switches.Contains("--list-audio-devices"),
            switches.Contains("--scan-game"),
            switches.Contains("--game-audio-self-test"),
            switches.Contains("--game-audio-transition-test"),
            switches.Contains("--live-game-audio-test"),
            switches.Contains("--game-audio-recovery-test"),
            switches.Contains("--game-audio-server-recovery-test"),
            values.TryGetValue("--game-process-id", out var processIdText)
                ? ParsePositiveInt(processIdText, "--game-process-id")
                : null,
            values.GetValueOrDefault("--expected-game-state"),
            ParseOptionalInt(values, "--expected-alive"),
            ParseOptionalInt(values, "--expected-dead"),
            ParseOptionalInt(values, "--expected-impostors"),
            switches.Contains("--expect-nearby"),
            ParseInt(values, "--input-device", 0),
            ParseInt(values, "--output-device", 0),
            switches.Contains("--auto-radio-tone"),
            switches.Contains("--policy-self-test"),
            values.TryGetValue("--expected-peer-client-id", out var peerClientIdText)
                ? ParsePositiveInt(peerClientIdText, "--expected-peer-client-id")
                : null,
            switches.Contains("--vad-self-test"),
            switches.Contains("--nat-fix"),
            switches.Contains("--nos-snapshot"),
            values.TryGetValue("--expected-players", out var playersText)
                ? ParsePositiveInt(playersText, "--expected-players") : 5,
            switches.Contains("--old-sample-debug"),
            switches.Contains("--dtls-trace"),
            switches.Contains("--turn-tcp"),
            values.GetValueOrDefault("--expected-toh-role"));
    }

    private static int? ParseOptionalInt(IReadOnlyDictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var text))
        {
            return null;
        }

        if (!int.TryParse(text, out var value) || value < 0)
        {
            throw new ArgumentException($"{name} には0以上の整数を指定してください。");
        }

        return value;
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string name, int fallback)
    {
        return values.TryGetValue(name, out var text) && int.TryParse(text, out var value)
            ? value
            : fallback;
    }

    private static int ParsePositiveInt(string text, string name)
    {
        if (!int.TryParse(text, out var value) || value <= 0)
        {
            throw new ArgumentException($"{name} には1以上の整数を指定してください。");
        }

        return value;
    }
}
