using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class InterviewerSelfTest
{
    internal static void Verify(Action<string, bool, bool> check)
    {
        long clock = 0;
        var audio = new NosInterviewerRecording(() => clock);
        var entry = new NosInterviewState { PlayerId = 7, Started = 10000, Seconds = 1, Visible = [1, 2] };
        audio.Update([entry], true, false, true, 10000);
        clock = 10;
        audio.Record(1, Enumerable.Repeat((short)8192, 480).ToArray(), 1);
        audio.Record(2, Enumerable.Repeat((short)16384, 960).ToArray(), 2);
        audio.Record(3, Enumerable.Repeat((short)30000, 480).ToArray(), 1);
        entry.Visible = [1];
        audio.Update([entry], true, false, true, 10010);
        clock = 20;
        audio.Record(1, Enumerable.Repeat((short)8192, 480).ToArray(), 1);
        audio.Record(2, Enumerable.Repeat((short)30000, 960).ToArray(), 2);
        entry.Stopped = 10015; entry.Visible = []; entry.PlayStarted = 11000;
        audio.Update([entry], true, true, true, 11000);
        var buffer = new float[1442]; audio.Read(buffer, 0, buffer.Length);
        check("Interviewer: own mic and visible voices are mixed", true, buffer.Take(960).All(v => v == .75f));
        check("Interviewer: visibility changes exclude departed players", true, buffer.Skip(960).Take(480).All(v => v == .25f));
        check("Interviewer: meeting trims recording at its exact start", true, buffer[^1] == 0 && buffer[^2] == 0);
        audio.Update([entry], true, true, true, 11000); audio.Read(buffer, 0, buffer.Length);
        check("Interviewer: heartbeat never replays a completed clip", true, buffer.All(v => v == 0));
        entry.PlayStarted = 12000;
        audio.Update([entry], true, true, true, 12000); audio.Read(buffer, 0, 2);
        check("Interviewer: another button request replays", true, buffer[0] == .75f);
        audio.Update([entry], true, false, true, 12001); audio.Read(buffer, 0, buffer.Length);
        check("Interviewer: meeting end stops playback", true, buffer.All(v => v == 0));
        var next = new NosInterviewState { PlayerId = 7, Started = 20000, Seconds = 1, Visible = [1] };
        clock = 2000; audio.Update([next], true, false, true, 20000);
        clock = 2010; audio.Record(1, Enumerable.Repeat((short)4096, 480).ToArray(), 1);
        clock = 4000; audio.Record(1, Enumerable.Repeat((short)30000, 480).ToArray(), 1);
        next.Stopped = 21000; next.Visible = []; next.PlayStarted = 22000;
        audio.Update([next], true, true, true, 22000);
        audio.Read(buffer, 0, buffer.Length);
        check("Interviewer: overwrite clears all previous audio", true, buffer.Take(960).All(v => v == .125f));
        check("Interviewer: audio clock excludes later conversation", true, buffer.Skip(960).All(v => v == 0));
        next.PlayStarted = 23000; audio.Update([next], true, true, false, 23000);
        audio.Read(buffer, 0, buffer.Length);
        check("Interviewer: deafen prevents playback", true, buffer.All(v => v == 0));
        audio.Clear(); audio.Read(buffer, 0, buffer.Length);
        check("Interviewer: game end clears recordings", true, buffer.All(v => v == 0));
        VerifyReader(check);
    }
    private static void VerifyReader(Action<string, bool, bool> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "tanuki-interview-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var player = new { playerId = 7, started = 10000, seconds = 1, stopped = 0, visible = new[] { 1, 2 }, playStarted = 0 };
            File.WriteAllText(path, JsonSerializer.Serialize(new { protocol = 1, pid = 123, unixMs = 10000, players = new[] { player } }));
            check("Interviewer: IPC accepts integer visibility mask", true, NosVoiceRoleStateReader.ReadInterviews(123, 10000, path).Count == 1);
            check("Interviewer: IPC rejects other process", true, NosVoiceRoleStateReader.ReadInterviews(124, 10000, path).Count == 0);
            check("Interviewer: IPC rejects stale state", true, NosVoiceRoleStateReader.ReadInterviews(123, 11001, path).Count == 0);
            File.WriteAllText(path, JsonSerializer.Serialize(new { protocol = 1, pid = 123, unixMs = 10000, players = new[] { player, player } }));
            check("Interviewer: IPC rejects duplicate recorders", true, NosVoiceRoleStateReader.ReadInterviews(123, 10000, path).Count == 0);
            var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip,
                NosInterviews = [new NosInterviewState { PlayerId = 7, Started = 10000, Seconds = 1, Visible = [1, 2] }] };
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(MobileGameStateWire.Create(state, new LobbySettings(), null)));
            check("Interviewer: host relays recording and visibility to web/mobile", true,
                wire.RootElement.GetProperty("gameState").GetProperty("nosInterviews")[0].GetProperty("visible").GetArrayLength() == 2);
        }
        finally { File.Delete(path); }
    }
}
