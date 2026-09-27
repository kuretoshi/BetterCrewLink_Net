namespace BetterCrewLinkKai.DotNet.Models;

public sealed class CosmeticImageSet
{
    public string HatFront { get; init; } = string.Empty;

    public string HatBack { get; init; } = string.Empty;

    public string Skin { get; init; } = string.Empty;

    public string Visor { get; init; } = string.Empty;

    public CosmeticPlacement Hat { get; init; } = CosmeticPlacement.Empty;

    public CosmeticPlacement SkinPlacement { get; init; } = CosmeticPlacement.Empty;

    public CosmeticPlacement VisorPlacement { get; init; } = CosmeticPlacement.Empty;
}

public sealed class CosmeticPlacement
{
    public static CosmeticPlacement Empty { get; } = new();

    public double TopPercent { get; init; }

    public double LeftPercent { get; init; }

    public double WidthPercent { get; init; }
}
