namespace BetterCrewLinkKai.DotNet.Models;

public sealed class AmongUsProcessInfo
{
    public int ProcessId { get; init; }

    public string ProcessPath { get; init; } = string.Empty;

    public string GameDirectory { get; init; } = string.Empty;

    public bool HasGameAssembly { get; init; }

    public bool Is64Bit { get; init; }

    public AmongUsMod InstalledMod { get; init; } = AmongUsMod.KnownMods[0];
}
