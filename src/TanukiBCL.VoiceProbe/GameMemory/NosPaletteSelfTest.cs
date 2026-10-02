namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class NosPaletteSelfTest
{
    public static int Run()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        var layout = new NosPaletteReader.Layout { Pid = 42, PointerSize = 4, ArraySlot = 0x10000,
            ArrayType = 0x20000, ArrayLengthOffset = 4, ArrayDataOffset = 8, Stride = 12, R = 0, G = 4, B = 8 };
        var payload = new byte[32 * 12];
        for (var i = 0; i < 32; i++)
        {
            BitConverter.GetBytes(i / 31f).CopyTo(payload, i * 12);
            BitConverter.GetBytes(0.5f).CopyTo(payload, i * 12 + 4);
            BitConverter.GetBytes(1f).CopyTo(payload, i * 12 + 8);
        }
        var torn = false;
        var reads = 0;
        byte[] Read(long address, int size)
        {
            var data = address switch
            {
                0x10000 => BitConverter.GetBytes(0x30000u),
                0x30000 => BitConverter.GetBytes(0x20000u),
                0x30004 => BitConverter.GetBytes(32u),
                0x30008 => payload.ToArray(),
                _ => throw new InvalidOperationException("Unexpected address")
            };
            Require(data.Length == size, "Wrong read length");
            if (address == 0x30008 && torn && ++reads == 2) data[0] ^= 1;
            return data;
        }
        var colors = NosPaletteReader.Read(layout, Read);
        Require(colors.Length == 32 && colors[0] == "#0080ff" && colors[31] == "#ff80ff", "Palette rounding/index");
        Require(NosColor.ToHex(-1, 0.5, 2) == "#0080ff" && NosColor.ToHex(double.NaN, 0, 0) is null,
            "Snapshot color clamp/nonfinite");
        Require(NosColor.For(new Player { NosLobbyColor = "#123456", NosPlayer = new() { ColorR = 1 } }) == "#123456",
            "Lobby color precedence");
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid palette accepted");
        }
        torn = true;
        Reject(() => NosPaletteReader.Read(layout, Read));
        torn = false;
        BitConverter.GetBytes(float.NaN).CopyTo(payload, 0);
        Reject(() => NosPaletteReader.Read(layout, Read));
        Reject(() => layout.Validate(43));
        layout.R = int.MaxValue;
        Reject(() => layout.Validate(42));
        Console.WriteLine("[PASS] NoS palette layout, RGB rounding, invalid floats, torn reads and precedence");
        return 0;
    }
}
