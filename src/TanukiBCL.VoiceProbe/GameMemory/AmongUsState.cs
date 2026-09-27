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

    public int ColorId { get; set; }

    public string HatId { get; set; } = string.Empty;

    public string SkinId { get; set; } = string.Empty;

    public string NormalSkinId { get; set; } = string.Empty;

    public string VisorId { get; set; } = string.Empty;

    public string AppearanceId { get; set; } = string.Empty;

    public bool Disconnected { get; set; }

    public bool IsImpostor { get; set; }

    public bool IsThirdParty { get; set; }

    public bool IsDead { get; set; }

    public bool IsLocal { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public bool InVent { get; set; }
}

public sealed class AmongUsState
{
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

    public MapType Map { get; set; } = MapType.Unknown;

    public CameraLocation CurrentCamera { get; set; } = CameraLocation.None;

    public List<int> ClosedDoors { get; set; } = [];
}

public sealed class PlayerColorPair
{
    public uint Main { get; set; }

    public uint Shadow { get; set; }
}
