namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class TohLiveRoleReaderSelfTest
{
    public static (bool Role, bool Killer, bool TornSampleRejected) Verify()
    {
        const uint dictionarySlot = 0x10000, dictionary = 0x20000, dictionaryType = 0x30000;
        const uint entries = 0x40000, entriesType = 0x50000;
        const uint player = 0x60000, playerType = 0x70000;
        const uint killerSlot = 0x80000, killerDictionary = 0x90000;
        const uint killerDictionaryType = 0xA0000, killerEntries = 0xB0000;
        const uint killerEntriesType = 0xC0000, killerRole = 0xD0000;
        const uint killerRoleType = 0xE0000, canKillSlot = 0xF0000;
        var memory = new Dictionary<uint, byte[]>
        {
            [dictionarySlot] = new byte[4], [dictionary] = new byte[20],
            [entries] = new byte[24], [player] = new byte[12],
            [killerSlot] = new byte[4], [killerDictionary] = new byte[20],
            [killerEntries] = new byte[24], [killerRole] = new byte[8],
            [canKillSlot] = [1]
        };
        void Put(uint block, int offset, uint value) =>
            BitConverter.GetBytes(value).CopyTo(memory[block], offset);
        void InitializeDictionary(uint slot, uint dictionary, uint dictionaryType,
            uint entries, uint entriesType, uint value)
        {
            Put(slot, 0, dictionary);
            Put(dictionary, 0, dictionaryType);
            Put(dictionary, 8, entries);
            Put(dictionary, 12, 1);
            Put(dictionary, 16, 1);
            Put(entries, 0, entriesType);
            Put(entries, 4, 1);
            Put(entries, 12, 0xffffffff);
            memory[entries][16] = 5;
            Put(entries, 20, value);
        }
        InitializeDictionary(dictionarySlot, dictionary, dictionaryType, entries, entriesType, player);
        InitializeDictionary(killerSlot, killerDictionary, killerDictionaryType,
            killerEntries, killerEntriesType, killerRole);
        Put(player, 0, playerType);
        memory[player][4] = 5;
        Put(player, 8, 19);
        Put(killerRole, 0, killerRoleType);
        Put(killerRole, 4, player);
        var layout = new TohLayout
        {
            Pid = 123, PointerSize = 4, DictionarySlot = dictionarySlot,
            DictionaryType = dictionaryType, PlayerType = playerType,
            EntriesType = entriesType, EntriesOffset = 8, CountOffset = 12,
            VersionOffset = 16, DataOffset = 8, Stride = 16,
            NextOffset = 4, KeyOffset = 8, ValueOffset = 12,
            IdOffset = 4, RoleOffset = 8, OpportunistCanKillSlot = canKillSlot,
            Names = new Dictionary<string, string> { ["19"] = "Opportunist", ["20"] = "Jackal" },
            KillerLayout = new TohKillerLayout
            {
                DictionarySlot = killerSlot, DictionaryType = killerDictionaryType,
                EntriesType = killerEntriesType, EntriesOffset = 8, CountOffset = 12,
                VersionOffset = 16, DataOffset = 8, Stride = 16,
                NextOffset = 4, KeyOffset = 8, ValueOffset = 12,
                Types = new Dictionary<string, TohKillerType>
                {
                    [killerRoleType.ToString()] = new() { IsKiller = true, StateOffset = 4 }
                }
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
            if (address == entries + 8 && size == 16 && ++entriesReads == 2)
                Put(player, 8, 20);
            return bytes;
        }
        var rejected = false;
        try { TohLiveRoleReader.ReadRoles(layout, TornRead); }
        catch (InvalidDataException) { rejected = true; }
        return (roleOk, killerOk, rejected);
    }
}
