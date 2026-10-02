namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class SnrLiveRoleReaderSelfTest
{
    public static (bool Role, bool Jumbo, bool TornSampleRejected) Verify()
    {
        const uint arraySlot = 0x10000, array = 0x20000, arrayType = 0x30000;
        const uint playerType = 0x40000, player = 0x50000;
        const uint abilityType = 0x60000, ability = 0x61000;
        const uint dataType = 0x70000, data = 0x71000;
        const uint listType = 0x80000, list = 0x81000;
        const uint itemsType = 0x90000, items = 0x91000;

        var memory = new Dictionary<uint, byte[]>
        {
            [arraySlot] = new byte[4], [array] = new byte[8 + 1024],
            [player] = new byte[32], [ability] = new byte[20],
            [data] = new byte[16], [list] = new byte[20], [items] = new byte[12]
        };
        void Put(uint block, int offset, uint value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        void PutFloat(uint block, int offset, float value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        Put(arraySlot, 0, array);
        Put(array, 0, arrayType);
        Put(array, 4, 256);
        Put(array, 8 + 5 * 4, player);
        Put(player, 0, playerType);
        memory[player][4] = 5;
        Put(player, 8, 7);
        Put(player, 12, 5);
        Put(player, 16, 0);
        Put(player, 20, list);
        Put(list, 0, listType);
        Put(list, 8, items);
        Put(list, 12, 1);
        Put(items, 0, itemsType);
        Put(items, 4, 1);
        Put(items, 8, ability);
        Put(ability, 0, abilityType);
        PutFloat(ability, 8, 2.5f);
        Put(ability, 12, data);
        Put(data, 0, dataType);
        PutFloat(data, 8, 5f);

        var layout = new SnrLiveLayout
        {
            Pid = 123, PointerSize = 4, ArraySlot = arraySlot, ArrayType = arrayType,
            PlayerType = playerType, ArrayLengthOffset = 4, ArrayDataOffset = 8,
            Fields = new SnrLiveFields
            {
                PlayerId = new SnrNumberField { Offset = 4, Size = 1 },
                Role = new SnrNumberField { Offset = 8, Size = 4,
                    Names = new Dictionary<string, string> { ["7"] = "Jackal", ["9"] = "Sidekick" } },
                Modifier = new SnrNumberField { Offset = 12, Size = 4,
                    Names = new Dictionary<string, string> { ["1"] = "Other", ["4"] = "JumboModifier" } },
                GhostRole = new SnrNumberField { Offset = 16, Size = 4 }
            },
            Jumbo = new SnrJumboLayout
            {
                AbilityType = abilityType, DataType = dataType, ListType = listType,
                ItemsType = itemsType, AbilitiesOffset = 20, ItemsOffset = 8,
                CountOffset = 12, CurrentOffset = 8, DataOffset = 12, MaxOffset = 8
            }
        };
        layout.Validate(123);
        byte[] Read(long address, int size)
        {
            foreach (var (start, bytes) in memory)
            {
                if (address < start || address + size > start + bytes.Length) continue;
                return bytes.AsSpan(checked((int)(address - start)), size).ToArray();
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
            if (address == array + 8 && size == 1024 && ++arrayReads == 2)
                Put(player, 8, 9);
            return bytes;
        }
        var rejected = false;
        try { SnrLiveRoleReader.ReadRoles(layout, TornRead); }
        catch (InvalidDataException) { rejected = true; }
        return (roleOk, jumboOk, rejected);
    }
}
