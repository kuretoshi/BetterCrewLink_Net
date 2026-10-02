namespace TanukiBCL.VoiceProbe.GameMemory;

public enum AmongUsModType
{
    None,
    TownOfHostForE,
    SuperNewRoles,
    TownOfUsMira,
    TownOfUs,
    TheOtherRoles,
    LasMonjas,
    NebulaOnTheShip,
    Other
}

public sealed class AmongUsMod
{
    public AmongUsModType Id { get; init; }

    public string Label { get; init; } = string.Empty;

    public string? DllStartsWith { get; init; }

    public static IReadOnlyList<AmongUsMod> KnownMods { get; } =
    [
        new() { Id = AmongUsModType.None, Label = "None" },
        new() { Id = AmongUsModType.TownOfHostForE, Label = "TOH4E / TOH4E_EM", DllStartsWith = "TownOfHost_ForE" },
        new() { Id = AmongUsModType.SuperNewRoles, Label = "SuperNewRoles", DllStartsWith = "SuperNewRoles" },
        new() { Id = AmongUsModType.TownOfUsMira, Label = "Town of Us: Mira", DllStartsWith = "TownOfUsMira" },
        new() { Id = AmongUsModType.TownOfUs, Label = "Town of Us: Reactivated", DllStartsWith = "TownOfUs" },
        new() { Id = AmongUsModType.TheOtherRoles, Label = "The Other Roles", DllStartsWith = "TheOtherRoles" },
        new() { Id = AmongUsModType.LasMonjas, Label = "Las Monjas", DllStartsWith = "LasMonjas" },
        new() { Id = AmongUsModType.NebulaOnTheShip, Label = "Nebula on the Ship", DllStartsWith = "Nebula" },
        new() { Id = AmongUsModType.Other, Label = "Other" }
    ];

    public static AmongUsMod For(AmongUsModType id) =>
        KnownMods.FirstOrDefault(mod => mod.Id == id) ?? KnownMods[0];
}
