namespace TanukiBCL.VoiceProbe;

internal sealed record ProbeOptions(
    Uri Server,
    string? LobbyCode,
    int PlayerId,
    int ClientId,
    bool IsHost,
    TimeSpan? Duration)
{
    public static ProbeOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--host")
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
                : null);
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
