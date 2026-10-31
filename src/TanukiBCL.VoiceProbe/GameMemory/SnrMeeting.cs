using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed record SnrMeetingData(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("currentPage")] int CurrentPage,
    [property: JsonPropertyName("playerIds")] int[] PlayerIds)
{
    public int[] PageIds() => !Enabled || PlayerIds.Length > 256 ||
        PlayerIds.Any(id => id is < 0 or >= 255) || PlayerIds.Distinct().Count() != PlayerIds.Length ||
        CurrentPage < 1 || CurrentPage > (PlayerIds.Length + 14) / 15
        ? [] : PlayerIds.Skip((CurrentPage - 1) * 15).Take(15).ToArray();
}

internal sealed class SnrMeetingLayout
{
    public ulong EnabledSlot { get; set; }
    public ulong PageSlot { get; set; }
    public ulong AreasSlot { get; set; }
    public ulong ListType { get; set; }
    public ulong ItemsType { get; set; }
    public ulong VoteAreaType { get; set; }
    public ulong NativeClass { get; set; }
    public int ItemsOffset { get; set; }
    public int CountOffset { get; set; }
    public int VersionOffset { get; set; }
    public int PooledPtrOffset { get; set; }
    public int PlayerIdOffset { get; set; }

    public SnrMeetingData? Read(Func<long, int, byte[]> read)
    {
        try
        {
            static bool Valid(ulong value) => value >= 0x10000 && value <= long.MaxValue && value % 8 == 0;
            if (EnabledSlot < 0x10000 || PageSlot < 0x10000 ||
                !new[] { AreasSlot, ListType, ItemsType, VoteAreaType, NativeClass }.All(Valid) ||
                !new[] { ItemsOffset, CountOffset, VersionOffset, PooledPtrOffset, PlayerIdOffset }.All(offset => offset is >= 4 and <= 4096)) return null;
            byte[] Bytes(ulong address, int size) => read(checked((long)address), size);
            int Integer(ulong address) => BitConverter.ToInt32(Bytes(address, 4));
            ulong Pointer(ulong address)
            {
                return FromPointer(Bytes(address, 8));
            }
            static ulong FromPointer(byte[] bytes, int offset = 0)
            {
                var value = BitConverter.ToUInt64(bytes, offset);
                return Valid(value) ? value : throw new InvalidDataException("Invalid SNR meeting pointer");
            }
            SnrMeetingData Snapshot()
            {
                var enabled = Bytes(EnabledSlot, 1)[0];
                if (enabled == 0) return new(false, 1, []);
                if (enabled != 1) throw new InvalidDataException("Invalid SNR paging flag");
                var page = Integer(PageSlot);
                var list = Pointer(AreasSlot);
                if (Pointer(list) != ListType) throw new InvalidDataException("SNR meeting list type changed");
                var count = Integer(list + (ulong)CountOffset);
                var version = Integer(list + (ulong)VersionOffset);
                var items = Pointer(list + (ulong)ItemsOffset);
                var capacity = Integer(items + 8);
                if (Pointer(items) != ItemsType || count is < 1 or > 256 || capacity < count || capacity > 512 ||
                    page < 1 || page > (count + 14) / 15) throw new InvalidDataException("Invalid SNR meeting page");
                var ids = new int[count];
                var references = Bytes(items + 16, count * 8);
                for (var index = 0; index < count; index++)
                {
                    var wrapper = Bytes(FromPointer(references, index * 8), PooledPtrOffset + 8);
                    if (FromPointer(wrapper) != VoteAreaType) throw new InvalidDataException("SNR vote area type changed");
                    var native = Bytes(FromPointer(wrapper, PooledPtrOffset), PlayerIdOffset + 1);
                    if (FromPointer(native) != NativeClass) throw new InvalidDataException("SNR native vote area class changed");
                    ids[index] = native[PlayerIdOffset];
                }
                if (ids.Distinct().Count() != count || ids.Contains(255) || Integer(list + (ulong)VersionOffset) != version)
                    throw new InvalidDataException("SNR meeting order changed during read");
                return new(true, page, ids);
            }
            var first = Snapshot();
            var second = Snapshot();
            return first.Enabled == second.Enabled && first.CurrentPage == second.CurrentPage &&
                first.PlayerIds.SequenceEqual(second.PlayerIds) ? second : null;
        }
        catch { return null; }
    }
}
