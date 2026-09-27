namespace BetterCrewLinkKai.DotNet.Models;

public enum GamePlatform
{
    Steam,
    Epic,
    Microsoft,
    Custom
}

public enum PlatformRunType
{
    Uri,
    Exe
}

public sealed class GamePlatformInstance
{
    public bool Default { get; set; }

    public string Key { get; set; } = string.Empty;

    public PlatformRunType LaunchType { get; set; }

    public string RunPath { get; set; } = string.Empty;

    public List<string> Execute { get; set; } = [];

    public string TranslateKey { get; set; } = string.Empty;
}
