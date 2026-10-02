namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class SnrLiveRoleReaderSelfTest
{
    public static (bool Role, bool Jumbo, bool TornSampleRejected) Verify()
    {
        const ulong baseAddress = 0x1_0000_0000;
        const ulong arraySlot = baseAddress + 0x10000, array = baseAddress + 0x20000,
            arrayType = baseAddress + 0x30000;
        const ulong playerType = baseAddress + 0x40000, player = baseAddress + 0x50000;
        const ulong abilityType = baseAddress + 0x60000, ability = baseAddress + 0x61000;
        const ulong dataType = baseAddress + 0x70000, data = baseAddress + 0x71000;
        const ulong listType = baseAddress + 0x80000, list = baseAddress + 0x81000;
        const ulong itemsType = baseAddress + 0x90000, items = baseAddress + 0x91000;

        var memory = new Dictionary<ulong, byte[]>
        {
            [arraySlot] = new byte[8], [array] = new byte[16 + 256 * 8],
            [player] = new byte[48], [ability] = new byte[32],
            [data] = new byte[24], [list] = new byte[32], [items] = new byte[24]
        };
        void Put(ulong block, int offset, ulong value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        void PutInt(ulong block, int offset, int value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        void PutFloat(ulong block, int offset, float value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        Put(arraySlot, 0, array);
        Put(array, 0, arrayType);
        PutInt(array, 8, 256);
        Put(array, 16 + 5 * 8, player);
        Put(player, 0, playerType);
        memory[player][8] = 5;
        PutInt(player, 12, 7);
        PutInt(player, 16, 5);
        PutInt(player, 20, 0);
        Put(player, 24, list);
        Put(list, 0, listType);
        Put(list, 8, items);
        PutInt(list, 16, 1);
        Put(items, 0, itemsType);
        PutInt(items, 8, 1);
        Put(items, 16, ability);
        Put(ability, 0, abilityType);
        PutFloat(ability, 8, 2.5f);
        Put(ability, 16, data);
        Put(data, 0, dataType);
        PutFloat(data, 8, 5f);

        var layout = new SnrLiveLayout
        {
            Pid = 123, PointerSize = 8, ArraySlot = arraySlot, ArrayType = arrayType,
            PlayerType = playerType, ArrayLengthOffset = 8, ArrayDataOffset = 16,
            Fields = new SnrLiveFields
            {
                PlayerId = new SnrNumberField { Offset = 8, Size = 1 },
                Role = new SnrNumberField { Offset = 12, Size = 4,
                    Names = new Dictionary<string, string> { ["7"] = "Jackal", ["9"] = "Sidekick" } },
                Modifier = new SnrNumberField { Offset = 16, Size = 4,
                    Names = new Dictionary<string, string> { ["1"] = "Other", ["4"] = "JumboModifier" } },
                GhostRole = new SnrNumberField { Offset = 20, Size = 4 }
            },
            Jumbo = new SnrJumboLayout
            {
                AbilityType = abilityType, DataType = dataType, ListType = listType,
                ItemsType = itemsType, AbilitiesOffset = 24, ItemsOffset = 8,
                CountOffset = 16, CurrentOffset = 8, DataOffset = 16, MaxOffset = 8
            }
        };
        layout.Validate(123);
        byte[] Read(long address, int size)
        {
            foreach (var (start, bytes) in memory)
            {
                if (address < checked((long)start) || address + size > checked((long)start) + bytes.Length) continue;
                return bytes.AsSpan(checked((int)(address - checked((long)start))), size).ToArray();
            }
            throw new InvalidDataException($"Unmapped fixture address 0x{address:X}");
        }

        var roles = SnrLiveRoleReader.ReadRoles(layout, Read);
        roles.TryGetValue(5, out var result);
        var roleOk = roles.Count == 1 && result is not null &&
            result.RoleId == 7 && result.RoleName == "Jackal" &&
            result.ModifierId == 5 && result.HasJumbo;
        var jumboOk = result?.JumboCurrentSize == 2.5d && result.JumboMaxSize == 5d;

        var arrayReads = 0;
        byte[] TornRead(long address, int size)
        {
            var bytes = Read(address, size);
            if (address == checked((long)(array + 16)) && size == 256 * 8 && ++arrayReads == 2)
                PutInt(player, 12, 9);
            return bytes;
        }
        var rejected = false;
        try { SnrLiveRoleReader.ReadRoles(layout, TornRead); }
        catch (InvalidDataException) { rejected = true; }
        return (roleOk, jumboOk, rejected);
    }
}
