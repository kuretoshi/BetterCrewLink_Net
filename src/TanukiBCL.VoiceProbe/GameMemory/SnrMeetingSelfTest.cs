using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

internal static class SnrMeetingSelfTest
{
    public static void Verify()
    {
        const ulong start = 0x100000000;
        var bytes = new byte[0x40000];
        void Ptr(int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        void Int(int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        var layout = new SnrMeetingLayout { EnabledSlot = start + 1, PageSlot = start + 4,
            AreasSlot = start + 8, ListType = start + 0x1000, ItemsType = start + 0x2000,
            VoteAreaType = start + 0x3000, NativeClass = start + 0x4000,
            ItemsOffset = 8, CountOffset = 16, VersionOffset = 20, PooledPtrOffset = 8, PlayerIdOffset = 16 };
        bytes[1] = 1; Int(4, 1); Ptr(8, start + 0x5000);
        Ptr(0x5000, layout.ListType); Ptr(0x5008, start + 0x6000); Int(0x5010, 45); Int(0x5014, 13);
        Ptr(0x6000, layout.ItemsType); Int(0x6008, 45);
        var ids = Enumerable.Range(0, 45).Reverse().ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            var wrapper = 0x10000 + index * 0x100;
            var native = 0x20000 + index * 0x100;
            Ptr(0x6010 + index * 8, start + (ulong)wrapper);
            Ptr(wrapper, layout.VoteAreaType); Ptr(wrapper + 8, start + (ulong)native);
            Ptr(native, layout.NativeClass); bytes[native + 16] = (byte)ids[index];
        }
        byte[] Read(long address, int size) => bytes.AsSpan(checked((int)(address - (long)start)), size).ToArray();
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("SNR meeting memory/wire self-test failed"); }
        foreach (var page in new[] { 1, 2, 3, 1 })
        {
            Int(4, page);
            var result = layout.Read(Read);
            Check(result is not null && result.CurrentPage == page && result.PlayerIds.SequenceEqual(ids) &&
                result.PageIds().SequenceEqual(ids.Skip((page - 1) * 15).Take(15)));
        }
        var reads = 0;
        Check(layout.Read((address, size) => {
            if (address == (long)layout.PageSlot && ++reads == 2) Int(4, 2);
            return Read(address, size);
        }) is null);
        reads = 0;
        Check(layout.Read((address, size) => {
            if (address == (long)start + 0x5014 && ++reads == 2) Int(0x5014, 14);
            return Read(address, size);
        }) is null);
        Int(4, 4); Check(layout.Read(Read) is null); Int(4, 1);
        bytes[0x20010] = 255; Check(layout.Read(Read) is null);
        bytes[0x20010] = 43; Check(layout.Read(Read) is null);
        bytes[0x20010] = 44;
        bytes[1] = 0; Check(layout.Read(Read) is { Enabled: false }); bytes[1] = 1;
        var state = new AmongUsState { GameState = GameState.Discussion, Mod = AmongUsModType.SuperNewRoles,
            SnrMeeting = layout.Read(Read) };
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) ObsOverlayWire.Write(writer, state, new Dictionary<int, ObsPeerState>(), false, false);
        using var document = JsonDocument.Parse(output.ToArray());
        var meeting = document.RootElement.GetProperty("snrMeeting");
        Check(meeting.GetProperty("currentPage").GetInt32() == 1 &&
            meeting.GetProperty("playerIds").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual(ids));
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(ObsOverlayWire.Build(state, new Dictionary<int, ObsPeerState>(), false, false).GetRawText())));
        Console.WriteLine("[PASS] SNR meeting memory: actual IDs, pages, torn sample/invalid data rejection and OBS wire parity");
    }
}
