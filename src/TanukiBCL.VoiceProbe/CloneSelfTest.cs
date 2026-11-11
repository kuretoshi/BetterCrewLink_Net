using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class CloneSelfTest
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static int Run()
    {
        long clock = 1000, now = 100000;
        var audio = new NosParrotRecording(() => clock);
        var me = new Player { Id = 1, IsLocal = true, X = 12, Y = -15 };
        var row = new NosParrotState { Mode = "clone", SpoofTalking = true, OwnerId = 1, SourceId = 1, BirdId = 1000001,
            X = 12, Y = -15, Range = 4, Seconds = 10, RecordId = 1, Started = now };
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip, IsHost = true,
            GameState = GameState.Tasks, Map = MapType.Fungle, Players = [me], NosParrots = [row] };
        audio.SetSource(1, true, 1); audio.Update(state, true, now);
        short[] pcm = Enumerable.Repeat((short)8192, 960).ToArray();
        for (int i = 0; i < 500; i++) { clock += 20; audio.Record(1, pcm, 1); }
        Check(audio.DrainRequests().Length == 0, "clone never requests automatic parrot capture");
        Check(!audio.IsCloneTalking(1), "recording alone cannot spoof");
        now += 10000; row.Clips = [1]; row.PlayId = 1; row.PlayClip = 1; row.PlayStarted = now;
        audio.Update(state, true, now);
        var buffer = new float[48000 * 2 * 35]; audio.Read(buffer, 0, buffer.Length);
        Check(buffer.Skip(48000 * 2 * 30).Any(v => v > .24f), "ten-second clone clip still audible after three loops");
        Check(audio.IsCloneTalking(1), "audible saved PCM spoofs owner");
        row.SpoofTalking = false; audio.Update(state, true, now);
        Check(!audio.IsCloneTalking(1), "off clears spoof without stopping audio");
        row.SpoofTalking = null; audio.Update(state, true, now);
        Check(!audio.IsCloneTalking(1), "legacy packets do not enable spoof");
        row.SpoofTalking = true; audio.Update(state, true, now);
        Check(audio.IsCloneTalking(1), "on affects ongoing playback");
        Check(!audio.IsCloneTalking(2), "only owner spoofed");
        var remoteOwner = new Player { Id = 2, ClientId = 42, InVent = true, ShiftedColor = 2 };
        state.Players.Add(remoteOwner);
        var selected = OverlaySelection.Select(state,
            new Dictionary<int, OverlayPeerStatus> { [42] = new(true, false, false, false, true) }, false, true, true);
        Check(selected.Any(p => p.Player.Id == 2 && p.Talking && p.CloneTalking), "clone overlay ignores owner vent/disguise and live-audio range");
        state.Players.Remove(remoteOwner);
        clock += 251; Check(!audio.IsCloneTalking(1), "stopped output cannot leave indicator lit");
        me.X = 30; audio.Update(state, true, now + 35000);
        var chunk = new float[960 * 2]; audio.Read(chunk, 0, chunk.Length);
        Check(chunk.All(v => v == 0), "listener outside clone range is silent");
        Check(!audio.IsCloneTalking(1), "out of range clears spoof");
        row.X = 30; audio.Update(state, true, now + 35000); audio.Read(chunk, 0, chunk.Length);
        Check(chunk.Any(v => v > .24f), "sound follows moving clone, not owner position");
        Check(audio.IsCloneTalking(1), "nearby clone spoofs independently of owner position");
        audio.SetSource(1, true, 0); audio.Read(chunk, 0, chunk.Length);
        Check(chunk.All(v => v == 0), "player volume mutes saved clone voice");
        Check(!audio.IsCloneTalking(1), "source volume zero clears spoof");
        audio.SetSource(1, true, 1); audio.SetAudible(false); audio.Read(chunk, 0, chunk.Length);
        Check(chunk.All(v => v == 0), "deafen stops clone immediately");
        Check(!audio.IsCloneTalking(1), "deafen clears spoof");
        audio.Update(state, true, now + 35000); audio.Read(chunk, 0, chunk.Length);
        Check(chunk.Any(v => v > .24f), "undeafen resumes clone without a new play ID");
        var wire = JsonSerializer.SerializeToElement(MobileGameStateWire.Create(state, new LobbySettings(), null));
        Check(wire.GetProperty("gameState").GetProperty("nosParrots")[0].GetProperty("mode").GetString() == "clone", "clone mode reaches Web/mobile");
        Check(wire.GetProperty("gameState").GetProperty("nosParrots")[0].GetProperty("spoofTalking").GetBoolean(), "toggle reaches Web/mobile");
        state.GameState = GameState.Discussion; row.PlayId = 0; row.PlayClip = 0; row.PlayStarted = 0;
        audio.Update(state, true, now + 35000); audio.Read(chunk, 0, chunk.Length);
        Check(chunk.All(v => v == 0), "meeting stops loop");
        Check(!audio.IsCloneTalking(1), "meeting clears spoof");
        state.GameState = GameState.Tasks; row.RecordId = 2; row.Started = now + 36000; row.Clips = [];
        row.X = me.X = 12; audio.Update(state, true, row.Started);
        short[] replacement = Enumerable.Repeat((short)16384, 960).ToArray();
        for (int i = 0; i < 500; i++) { clock += 20; audio.Record(1, i < 200 ? new short[960] : replacement, 1); }
        row.Clips = [2]; row.PlayId = 2; row.PlayClip = 2; row.PlayStarted = row.Started + 10000;
        audio.Update(state, true, row.PlayStarted); audio.Read(chunk, 0, chunk.Length);
        Check(chunk.All(v => v == 0) && !audio.IsCloneTalking(1), "silent recording section stays dark");
        var fiveSeconds = new float[48000 * 2 * 5]; audio.Read(fiveSeconds, 0, fiveSeconds.Length);
        Check(fiveSeconds.Any(v => v > .49f) && audio.IsCloneTalking(1), "voice after silence lights up");
        state.NosParrots.Clear(); audio.Update(state, true, row.PlayStarted); audio.Read(chunk, 0, chunk.Length);
        Check(chunk.All(v => v == 0), "removal releases clone playback");
        Check(!audio.IsCloneTalking(1), "removal clears spoof");
        Check(row.IsValid(row.PlayStarted), "valid clone mode");
        row.Mode = null; Check(!row.IsValid(row.PlayStarted), "ordinary parrots cannot request ten seconds");
        row.Mode = "clone"; row.SourceId = 2; Check(!row.IsValid(row.PlayStarted), "clone cannot capture another player's voice");
        row.SourceId = 1; row.Clips = [1, 2]; Check(!row.IsValid(row.PlayStarted), "clone has only one clip");
        audio.Clear();
        Console.WriteLine("PASS NET clone: 10s PCM, loop, moving origin, volume, deafen resume, meeting, overwrite, Web/mobile and cleanup");
        return 0;
    }
}
