namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class NosSnapshotSelfTest
{
    internal static int Run()
    {
        const long slot = 0x1_0001_0000;
        const long snapshotAddress = 0x1_0004_0000;
        const long playersAddress = 0x1_0005_0000;
        const long radiosAddress = 0x1_0006_0000;
        var layout = new NosLayout
        {
            Pid = 42, PointerSize = 8, SchemaVersion = 20260918,
            LatestSlotAddress = slot,
            Snapshot = new NosSnapshotLayout
            {
                LocalMicPositionX = 0, LocalMicPositionY = 4,
                PlayersLength = 8, Players = 16, RadiosLength = 24, Radios = 32
            },
            PlayerData = new NosPlayerLayout
            {
                Size = 128, PlayerId = 0, IsKiller = 1, IsImpostor = 2,
                IsCrewmate = 3, IsNeutral = 4, IsImpostorlike = 5, IsJammed = 6,
                NameLength = 7, Name = 8, SpeakerPositionX = 72, SpeakerPositionY = 76,
                BodyRateX = 80, BodyRateY = 84, ColorR = 88, ColorG = 92, ColorB = 96
            },
            RadioData = new NosRadioLayout
                { Size = 80, Kind = 0, HearableMask = 4, NameLength = 8, Name = 12 }
        };
        layout.Validate(42);
        var slotBytes = BitConverter.GetBytes((ulong)snapshotAddress);
        var header = new byte[40];
        BitConverter.GetBytes(1.5f).CopyTo(header, 0);
        BitConverter.GetBytes(2.5f).CopyTo(header, 4);
        BitConverter.GetBytes(1).CopyTo(header, 8);
        BitConverter.GetBytes((ulong)playersAddress).CopyTo(header, 16);
        BitConverter.GetBytes(1).CopyTo(header, 24);
        BitConverter.GetBytes((ulong)radiosAddress).CopyTo(header, 32);
        var player = new byte[128];
        player[0] = 7;
        player[1] = 1;
        player[2] = 1;
        player[7] = 3;
        System.Text.Encoding.Unicode.GetBytes("NoS").CopyTo(player, 8);
        BitConverter.GetBytes(3f).CopyTo(player, 72);
        BitConverter.GetBytes(4f).CopyTo(player, 76);
        BitConverter.GetBytes(1f).CopyTo(player, 80);
        BitConverter.GetBytes(1f).CopyTo(player, 84);
        BitConverter.GetBytes(0.1f).CopyTo(player, 88);
        BitConverter.GetBytes(0.2f).CopyTo(player, 92);
        BitConverter.GetBytes(0.3f).CopyTo(player, 96);
        var radio = new byte[80];
        BitConverter.GetBytes(1).CopyTo(radio, 0);
        BitConverter.GetBytes(5).CopyTo(radio, 4);
        radio[8] = 2;
        System.Text.Encoding.Unicode.GetBytes("RX").CopyTo(radio, 12);
        var torn = false;
        var playerReads = 0;
        byte[] Read(long address, int size)
        {
            var bytes = address switch
            {
                slot => slotBytes.ToArray(),
                snapshotAddress => header.ToArray(),
                playersAddress => player.ToArray(),
                radiosAddress => radio.ToArray(),
                _ => throw new InvalidOperationException($"Unexpected address {address:X}")
            };
            if (bytes.Length != size) throw new InvalidOperationException("Unexpected NoS read size");
            if (address == playersAddress && torn && ++playerReads == 2) bytes[0] ^= 1;
            return bytes;
        }
        var snapshot = NosSnapshotReader.ReadSnapshot(layout, Read);
        if (snapshot.Publication != (ulong)snapshotAddress || snapshot.LocalMicPosition.X != 1.5f ||
            !snapshot.Players.TryGetValue(7, out var published) || published.Name != "NoS" ||
            !published.IsImpostor || snapshot.Radios.Count != 1 || snapshot.Radios[0].Name != "RX")
            throw new InvalidOperationException("64-bit NoS player/radio snapshot changed");
        torn = true;
        try
        {
            NosSnapshotReader.ReadSnapshot(layout, Read);
            throw new InvalidOperationException("Torn NoS snapshot accepted");
        }
        catch (InvalidDataException) { }
        layout.PointerSize = 4;
        try
        {
            layout.Validate(42);
            throw new InvalidOperationException("32-bit NoS layout accepted");
        }
        catch (InvalidDataException) { }
        Console.WriteLine("[PASS] 64-bit NoS player/radio pointers, publication and torn-read rejection");
        return 0;
    }
}
