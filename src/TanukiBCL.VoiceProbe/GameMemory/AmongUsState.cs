namespace TanukiBCL.VoiceProbe.GameMemory;

public enum GameState
{
    Lobby,
    Tasks,
    Discussion,
    Menu,
    Unknown
}

public enum MapType
{
    TheSkeld = 0,
    MiraHq = 1,
    Polus = 2,
    TheSkeldApril = 3,
    Airship = 4,
    Fungle = 5,
    Unknown = 6,
    Submerged = 105
}

public enum CameraLocation
{
    East = 0,
    Central = 1,
    Northeast = 2,
    South = 3,
    SouthWest = 4,
    NorthWest = 5,
    Skeld = 6,
    None = 7
}

public sealed class Player
{
    public int Id { get; set; }

    public int ClientId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int NameHash { get; set; }

    public int PlayerConfigId { get; set; }

    public int ColorId { get; set; }

    public int CurrentOutfit { get; set; }

    public string AppearanceName { get; set; } = string.Empty;

    public int AppearanceColorId { get; set; } = -1;

    public string AppearanceHatId { get; set; } = string.Empty;

    public string AppearanceSkinId { get; set; } = string.Empty;

    public string AppearanceVisorId { get; set; } = string.Empty;

    public int ShiftedColor { get; set; } = -1;

    public string HatId { get; set; } = string.Empty;

    public int PetId { get; set; }

    public string SkinId { get; set; } = string.Empty;

    public string NormalSkinId { get; set; } = string.Empty;

    public string VisorId { get; set; } = string.Empty;

    public string AppearanceId { get; set; } = string.Empty;

    public bool Disconnected { get; set; }

    public bool IsImpostor { get; set; }

    public bool? TohImpostor { get; set; }

    public uint RoleTeam { get; set; }

    public bool IsThirdParty { get; set; }

    public bool IsDead { get; set; }

    public bool IsLocal { get; set; }

    public bool IsDummy { get; set; }

    public bool Bugged { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public bool InVent { get; set; }

    public double LightRadius { get; set; } = 1d;

    public NosPlayerData? NosPlayer { get; set; }
    public NosRoleData? NosRole { get; set; }
    public string? NosLobbyColor { get; set; }

    public SnrRoleData? SnrRole { get; set; }

    public TohRoleData? TohRole { get; set; }

    public bool HasVisibleAppearanceChanged()
    {
        if (CurrentOutfit is <= 0 or > 10) return false;
        return ColorId != AppearanceColorId ||
            Normalize(HatId, "hat_NoHat") != Normalize(AppearanceHatId, "hat_NoHat") ||
            Normalize(SkinId, "skin_None") != Normalize(AppearanceSkinId, "skin_None") ||
            Normalize(VisorId, "visor_EmptyVisor") != Normalize(AppearanceVisorId, "visor_EmptyVisor");
    }

    private static string Normalize(string id, string emptyId) => id == emptyId ? string.Empty : id;
}

public sealed class NosPlayerData
{
    public int PlayerId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsKiller { get; set; }

    public bool IsImpostor { get; set; }

    public bool IsCrewmate { get; set; }

    public bool IsNeutral { get; set; }

    public bool IsImpostorlike { get; set; }

    public bool? IsJammed { get; set; }

    public double SpeakerPositionX { get; set; }

    public double SpeakerPositionY { get; set; }

    public double? BodyRateX { get; set; }

    public double? BodyRateY { get; set; }

    public int? BodyType { get; set; }

    public double? NeckLength { get; set; }

    public double ColorR { get; set; }

    public double ColorG { get; set; }

    public double ColorB { get; set; }

    // TBCLFields 20261005 costume names. Null when the published schema has no costume data.
    public NosCostumeData? Skin { get; set; }

    public NosCostumeData? Hat { get; set; }

    public NosCostumeData? Visor { get; set; }
}

public sealed record NosCostumeData(string Name);

/// <summary>3.2.9 nosReadStatus: drives the dark-red MOD badge while a match has no NoS data.</summary>
public sealed record NosReadStatus(bool Failed, string Message, int? SchemaVersion);

public sealed record VoicePosition(double X, double Y);

public sealed record NosRadioData(int Kind, int HearableMask, string Name);

public sealed class AmongUsState
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string GameExecutablePath { get; set; } = string.Empty;

    public AmongUsModType Mod { get; set; } = AmongUsModType.None;

    // The local installation before a host-only MOD is inferred from peer reports.
    // The effective Mod above may change for a vanilla client in a TOH4E lobby.
    public AmongUsModType? InstalledMod { get; set; }

    public GameState GameState { get; set; } = GameState.Unknown;

    public GameState OldGameState { get; set; } = GameState.Unknown;

    public int LobbyCodeInt { get; set; } = -1;

    public string LobbyCode { get; set; } = string.Empty;

    public List<Player> Players { get; set; } = [];

    public List<PlayerColorPair> PlayerColors { get; set; } = [];

    public bool IsHost { get; set; }

    public int ClientId { get; set; }

    public int HostId { get; set; }

    public bool CommsSabotaged { get; set; }

    public string CurrentServer { get; set; } = string.Empty;

    public int MaxPlayers { get; set; } = 15;

    public double LightRadius { get; set; } = 1d;

    public MapType Map { get; set; } = MapType.Unknown;

    public bool AirshipMeetingByOutfit { get; set; }

    // Released GameReader uses one valid visibly disguised player as the
    // threshold, and resets the flag outside Tasks. Camouflaged is always false there.
    public bool MixupSabotaged => GameState == GameState.Tasks &&
        Players.Any(player => !player.Disconnected && !player.Bugged && player.HasVisibleAppearanceChanged());

    public bool OldMeetingHud { get; set; }

    public CameraLocation CurrentCamera { get; set; } = CameraLocation.None;

    public NosReadStatus? NosReadStatus { get; set; }

    /// <summary>Automatic SNR/TOH4E role reader status for the debug window.</summary>
    public string? RoleReaderStatus { get; set; }

    public string? NosRoleStatus { get; set; }

    public List<string> NosAddonIds { get; set; } = [];

    public List<TohRoleDefinition> TohRoleCatalog { get; set; } = [];

    public VoicePosition? NosLocalMicPosition { get; set; }

    public List<NosRadioData> NosRadios { get; set; } = [];

    public List<int> ClosedDoors { get; set; } = [];
}

public sealed class PlayerColorPair
{
    public uint Main { get; set; }

    public uint Shadow { get; set; }
}
