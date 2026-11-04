namespace TanukiBCL.VoiceProbe.GameMemory;

internal sealed record TanukiAddonState(List<NosHeroTransformState> Heroes, List<NosPortableMegaphoneState> Portable);

internal static class TanukiAddonData
{
    internal static TanukiAddonState? Read(NosSnapshot? snapshot)
    {
        bool v2 = NosAddonLink.Endpoint(snapshot?.AddonData).HasValue;
        if (snapshot == null || (!v2 && snapshot.AddonData != 0x544B0001) || snapshot.Players.Count > 24) return null;
        var result = new TanukiAddonState([], []);
        var ids = new HashSet<int>();
        foreach (var player in snapshot.Players.Values)
        {
            if (player.PlayerId is < 0 or >= 24 || !ids.Add(player.PlayerId) || player.AddonData is not int data ||
                (v2 ? (data & unchecked((int)0xFFFF0000)) != 0x544C0000 : (data & unchecked((int)0xFFFFFE00)) != 0x544B0000)) return null;
            int hero = data & 3, range = (data >> 2) & 63;
            bool active = (data & 0x100) != 0;
            if (hero == 3 || range > 60 || (!active && range != 0)) return null;
            if (hero != 0) result.Heroes.Add(new() { PlayerId = player.PlayerId, Kind = hero == 1 ? "super" : "evil" });
            if (active) result.Portable.Add(new() { PlayerId = player.PlayerId, ExtraRange = range / 2d });
        }
        return result;
    }
}
