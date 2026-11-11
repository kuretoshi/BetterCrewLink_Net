using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class ParrotSelfTest
{
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    internal static int Run()
    {
        foreach (double seconds in new[] { 1.5, .5, 3.0, 5.0 }) RunCase(seconds);
        return 0;
    }
    private static void RunCase(double seconds)
    {
        long clock = 1000, now = 100000;
        var audio = new NosParrotRecording(() => clock);
        var me = new Player { Id = 1, IsLocal = true, X = 12, Y = -15 };
        var other = new Player { Id = 2, X = 12, Y = -15 };
        var bird = new NosParrotState { OwnerId = 1, BirdId = 1, X = 12, Y = -15, Range = 4, SourceId = 2, Seconds = seconds };
        var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip, IsHost = true, GameState = GameState.Tasks,
            Map = MapType.Fungle, Players = [me, other], NosParrots = [bird] };
        audio.SetSource(2, true, 1); audio.Update(state, true, now);
        short[] pcm = Enumerable.Repeat((short)8192, 960).ToArray();
        clock += 20; audio.Record(2, pcm, 1);
        Check(audio.DrainRequests().Single() == (1, 1, 2), "host voice onset queues capture without network on audio callback");
        bird.RecordId = 1; bird.Started = now; audio.Update(state, true, now);
        for (int i = 0; i < (int)(seconds * 50); i++) { clock += 20; audio.Record(2, pcm, 1); }
        now += (long)(seconds * 1000); bird.Clips = [1]; bird.PlayId = 10; bird.PlayClip = 1; bird.PlayStarted = now;
        audio.Update(state, true, now);
        var buffer = new float[28800]; audio.Read(buffer, 0, buffer.Length);
        Check(buffer.Any(v => v > .24f), "recorded PCM audible from parrot");
        var tail = new float[(int)(seconds * 48000 * 2) - buffer.Length];
        audio.Read(tail, 0, tail.Length);
        Check(tail.Length == 0 || tail.TakeLast(600).Any(v => v > .24f), "PCM remains audible through configured end");
        bird.PlayId = 14; audio.Update(state, true, now); audio.Read(buffer, 0, buffer.Length);
        audio.SetSource(2, false, 0); audio.Read(buffer, 0, buffer.Length);
        Check(buffer.All(v => v == 0), "source mute silences old recording immediately");
        audio.SetSource(2, true, 1); me.X = 30; audio.Update(state, true, now); audio.Read(buffer, 0, buffer.Length);
        Check(buffer.All(v => v == 0), "outside parrot range silent");
        me.X = 12; state.GameState = GameState.Discussion; bird.PlayId = 0; bird.PlayClip = 0; bird.PlayStarted = 0;
        audio.Update(state, true, now); audio.Read(buffer, 0, buffer.Length);
        Check(buffer.All(v => v == 0), "meeting stops playback");
        state.GameState = GameState.Tasks; now += 2000; bird.PlayId = 11; bird.PlayClip = 1; bird.PlayStarted = now;
        audio.Update(state, true, now); audio.Read(buffer, 0, buffer.Length);
        Check(buffer.Any(v => v > .24f), "completed recording survives meeting");
        if (seconds > 1.5)
        {
            bird.PlayId = 13; bird.PlayStarted = now - 1600;
            audio.Update(state, true, now); audio.Read(buffer, 0, buffer.Length);
            Check(buffer.Any(v => v > .24f), "long clip playback accepted after 1.5-second delay");
        }
        audio.SetAudible(false); audio.Read(buffer, 0, buffer.Length);
        Check(buffer.All(v => v == 0), "deafen immediate");
        audio.Clear(); audio.Read(buffer, 0, buffer.Length); Check(buffer.All(v => v == 0), "cleanup");
        var wire = JsonSerializer.SerializeToElement(MobileGameStateWire.Create(state, new LobbySettings(), null));
        Check(wire.GetProperty("gameState").GetProperty("nosParrots")[0].GetProperty("birdId").GetInt32() == 1,
            "NET host forwards parrot recording and playback IDs to mobile");
        Check(wire.GetProperty("gameState").GetProperty("nosParrots")[0].GetProperty("seconds").GetDouble() == seconds, "recording duration reaches mobile");
        Check(bird.IsValid(now), "valid state");
        foreach (double invalid in new[] { .49, 5.01, double.NaN, double.PositiveInfinity })
        {
            bird.Seconds = invalid; Check(!bird.IsValid(now), "invalid recording duration rejected");
        }
        bird.Seconds = seconds; bird.Clips = [1, 2, 3, 4, 5, 6]; Check(!bird.IsValid(now), "bank bounded");
        Console.WriteLine($"PASS NET parrot {seconds}s: PCM capture/playback, host requests, range, mute, meeting, cleanup and mobile wire");
    }
}
