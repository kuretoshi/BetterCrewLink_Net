namespace TanukiBCL.VoiceProbe.GameMemory;

internal sealed class SnrSecondaryLayout
{
    public int Pid { get; set; }
    public int PointerSize { get; set; }
    public ulong ControlClass { get; set; }
    public ulong CosmeticsClass { get; set; }
    public int PlayerIdOffset { get; set; }
    public int CosmeticsOffset { get; set; }
    public List<SnrSecondaryLayer> Layers { get; set; } = [];
    private Dictionary<ulong, SnrSecondaryLayer> layers = [];
    private static bool Pointer(ulong value) => value >= 0x10000 && value <= long.MaxValue && value % 8 == 0;
    public void Validate(int pid)
    {
        if (Pid != pid || PointerSize != 8 || !Pointer(ControlClass) || !Pointer(CosmeticsClass) ||
            PlayerIdOffset is < 16 or > 4096 || CosmeticsOffset is < 16 or > 4096 || Layers.Count > 4096)
            throw new InvalidDataException("Invalid SNR secondary cosmetic layout");
        var next = new Dictionary<ulong, SnrSecondaryLayer>();
        foreach (var layer in Layers)
        {
            static bool Id(string? id) => id is null || id.Length is > 0 and <= 1024;
            if (!Pointer(layer.Address) || layer.Class != CosmeticsClass || !Id(layer.Hat2Id) || !Id(layer.Visor2Id) ||
                !next.TryAdd(layer.Address, layer)) throw new InvalidDataException("Invalid SNR secondary cosmetic layer");
        }
        layers = next;
    }
    internal void Apply(Player player, Func<long, int, byte[]> read)
    {
        player.SnrHat2Id = player.SnrVisor2Id = null;
        if (player.Disconnected || player.Id is < 0 or > 254 || player.NativeControlAddress < 0x10000) return;
        try
        {
            var control = checked((ulong)player.NativeControlAddress);
            ulong Ptr(ulong at) => BitConverter.ToUInt64(read(checked((long)at), 8));
            ulong Sample()
            {
                if (Ptr(control) != ControlClass || read(checked((long)(control + (ulong)PlayerIdOffset)), 1)[0] != player.Id)
                    throw new InvalidDataException("SNR secondary cosmetic owner changed");
                var cosmetics = Ptr(control + (ulong)CosmeticsOffset);
                if (!Pointer(cosmetics) || Ptr(cosmetics) != CosmeticsClass)
                    throw new InvalidDataException("SNR secondary cosmetic class changed");
                return cosmetics;
            }
            var first = Sample();
            layers.TryGetValue(first, out var layer);
            if (Sample() != first) return;
            player.SnrHat2Id = layer?.Hat2Id;
            player.SnrVisor2Id = layer?.Visor2Id;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or OverflowException) { }
    }
    internal static void Verify()
    {
        var layout = new SnrSecondaryLayout { Pid = 42, PointerSize = 8, ControlClass = 0x20000,
            CosmeticsClass = 0x40000, PlayerIdOffset = 16, CosmeticsOffset = 24,
            Layers = [new() { Address = 0x30000, Class = 0x40000, Hat2Id = "Modded_SecondHat", Visor2Id = "Modded_SecondVisor" }] };
        layout.Validate(42);
        var memory = new byte[40];
        BitConverter.GetBytes(0x20000UL).CopyTo(memory, 0); memory[16] = 3;
        BitConverter.GetBytes(0x30000UL).CopyTo(memory, 24);
        byte[] Read(long at, int count) => at == 0x30000 ? BitConverter.GetBytes(0x40000UL) : memory.AsSpan((int)(at - 0x10000), count).ToArray();
        var player = new Player { Id = 3, NativeControlAddress = 0x10000 };
        layout.Apply(player, Read);
        if (player.SnrHat2Id != "Modded_SecondHat" || player.SnrVisor2Id != "Modded_SecondVisor" || player.SnrRole is not null)
            throw new InvalidOperationException("Lobby cosmetics depend on role assignment");
        memory[16] = 4; layout.Apply(player, Read);
        if (player.SnrHat2Id is not null || player.SnrVisor2Id is not null) throw new InvalidOperationException("Wrong player kept cosmetics");
        memory[16] = 3; player.Disconnected = true; layout.Apply(player, Read);
        if (player.SnrHat2Id is not null) throw new InvalidOperationException("Disconnected player kept cosmetics");
        player.Disconnected = false;
        var samples = 0;
        layout.Apply(player, (at, count) => at == 0x10018 && ++samples > 1 ? BitConverter.GetBytes(0x50000UL) : Read(at, count));
        if (player.SnrHat2Id is not null) throw new InvalidOperationException("Moving cosmetic pointer was accepted");
    }
}
internal sealed class SnrSecondaryLayer
{
    public ulong Address { get; set; }
    public ulong Class { get; set; }
    public string? Hat2Id { get; set; }
    public string? Visor2Id { get; set; }
}
