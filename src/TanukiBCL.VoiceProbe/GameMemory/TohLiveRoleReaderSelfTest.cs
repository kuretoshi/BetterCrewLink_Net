namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class TohLiveRoleReaderSelfTest
{
    public static (bool Role, bool Killer, bool TornSampleRejected) Verify()
    {
        const ulong baseAddress = 0x1_0000_0000;
        const ulong dictionarySlot = baseAddress + 0x10000, dictionary = baseAddress + 0x20000,
            dictionaryType = baseAddress + 0x30000;
        const ulong entries = baseAddress + 0x40000, entriesType = baseAddress + 0x50000;
        const ulong player = baseAddress + 0x60000, playerType = baseAddress + 0x70000;
        const ulong killerSlot = baseAddress + 0x80000, killerDictionary = baseAddress + 0x90000;
        const ulong killerDictionaryType = baseAddress + 0xA0000, killerEntries = baseAddress + 0xB0000;
        const ulong killerEntriesType = baseAddress + 0xC0000, killerRole = baseAddress + 0xD0000;
        const ulong killerRoleType = baseAddress + 0xE0000, canKillSlot = baseAddress + 0xF0000;
        var memory = new Dictionary<ulong, byte[]>
        {
            [dictionarySlot] = new byte[8], [dictionary] = new byte[32],
            [entries] = new byte[40], [player] = new byte[16],
            [killerSlot] = new byte[8], [killerDictionary] = new byte[32],
            [killerEntries] = new byte[40], [killerRole] = new byte[16],
            [canKillSlot] = [1]
        };
        void Put(ulong block, int offset, ulong value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        void PutInt(ulong block, int offset, int value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        void InitializeDictionary(ulong slot, ulong dictionary, ulong dictionaryType,
            ulong entries, ulong entriesType, ulong value)
        {
            Put(slot, 0, dictionary);
            Put(dictionary, 0, dictionaryType);
            Put(dictionary, 8, entries);
            PutInt(dictionary, 16, 1);
            PutInt(dictionary, 20, 1);
            Put(entries, 0, entriesType);
            PutInt(entries, 8, 1);
            PutInt(entries, 20, -1);
            memory[entries][24] = 5;
            Put(entries, 32, value);
        }
        InitializeDictionary(dictionarySlot, dictionary, dictionaryType, entries, entriesType, player);
        InitializeDictionary(killerSlot, killerDictionary, killerDictionaryType,
            killerEntries, killerEntriesType, killerRole);
        Put(player, 0, playerType);
        memory[player][8] = 5;
        PutInt(player, 12, 19);
        Put(killerRole, 0, killerRoleType);
        Put(killerRole, 8, player);
        var layout = new TohLayout
        {
            Pid = 123, PointerSize = 8, DictionarySlot = dictionarySlot,
            DictionaryType = dictionaryType, PlayerType = playerType,
            EntriesType = entriesType, EntriesOffset = 8, CountOffset = 16,
            VersionOffset = 20, DataOffset = 16, Stride = 24,
            NextOffset = 4, KeyOffset = 8, ValueOffset = 16,
            IdOffset = 8, RoleOffset = 12, OpportunistCanKillSlot = canKillSlot,
            Names = new Dictionary<string, string> { ["19"] = "Opportunist", ["20"] = "Jackal" },
            KillerLayout = new TohKillerLayout
            {
                DictionarySlot = killerSlot, DictionaryType = killerDictionaryType,
                EntriesType = killerEntriesType, EntriesOffset = 8, CountOffset = 16,
                VersionOffset = 20, DataOffset = 16, Stride = 24,
                NextOffset = 4, KeyOffset = 8, ValueOffset = 16,
                Types = new Dictionary<string, TohKillerType>
                {
                    [killerRoleType.ToString()] = new() { IsKiller = true, StateOffset = 8 }
                }
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
            throw new InvalidDataException($"Unmapped TOH fixture address 0x{address:X}");
        }
        var roles = TohLiveRoleReader.ReadRoles(layout, Read);
        roles.TryGetValue(5, out var role);
        var roleOk = roles.Count == 1 && role is { RoleId: 19, RoleName: "Opportunist",
            IsNeutralKiller: true, OpportunistCanKill: true };
        var killerOk = role?.IsKiller == true;

        var entriesReads = 0;
        byte[] TornRead(long address, int size)
        {
            var bytes = Read(address, size);
            if (address == checked((long)(entries + 16)) && size == 24 && ++entriesReads == 2)
                PutInt(player, 12, 20);
            return bytes;
        }
        var rejected = false;
        try { TohLiveRoleReader.ReadRoles(layout, TornRead); }
        catch (InvalidDataException) { rejected = true; }
        return (roleOk, killerOk, rejected);
    }
}
