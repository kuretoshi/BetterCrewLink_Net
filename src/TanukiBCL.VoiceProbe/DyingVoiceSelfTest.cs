using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class DyingVoiceSelfTest
{
    internal static void Verify(Action<string, bool, bool> check)
    {
        var entry = new NosDyingVoiceState { PlayerId = 1, Started = 10000, Seconds = 1, Range = 2.5, X = 1, Y = 2 };
        var listener = new Player { X = 1, Y = 2 };
        check("Dying voice: living listener enters range", true, NosDyingVoiceRecording.InRange(entry, listener, GameState.Tasks));
        listener.IsDead = true;
        check("Dying voice: ghosts cannot trigger", false, NosDyingVoiceRecording.InRange(entry, listener, GameState.Tasks));
        listener.IsDead = false;
        check("Dying voice: meeting cannot trigger", false, NosDyingVoiceRecording.InRange(entry, listener, GameState.Discussion));
        listener.X = 10;
        check("Dying voice: distant listener cannot trigger", false, NosDyingVoiceRecording.InRange(entry, listener, GameState.Tasks));
        long clock = 0;
        var audio = new NosDyingVoiceRecording(() => clock);
        audio.Update(entry, true, false, 1, 10000);
        audio.RecordStereoPcm16([10000, 10000, -10000, -10000]);
        check("Dying voice: record before proximity attenuation", true, audio.RecordedSamples == 2 && !audio.IsPlaying);
        clock = 2000;
        audio.RecordStereoPcm16([30000, 30000]);
        check("Dying voice: stalled state cannot record later ghost speech", true, audio.RecordedSamples == 2);
        audio.Update(entry, true, true, 1, 11000);
        check("Dying voice: ready message starts for nearby survivor", true, audio.IsPlaying);
        var output = new float[4]; audio.Read(output, 0, output.Length);
        check("Dying voice: radio output is finite and audible", true, output.All(float.IsFinite) && output.Any(value => value != 0));
        audio.Update(entry, true, true, 1, 11001);
        check("Dying voice: standing still does not replay", false, audio.IsPlaying);
        audio.Update(entry, true, false, 1, 11002);
        audio.Update(entry, true, true, 1, 11003);
        check("Dying voice: re-entry replays stored message", true, audio.IsPlaying);
        audio.Update(entry, true, true, 0, 11004);
        check("Dying voice: mute stops playback", false, audio.IsPlaying);
        audio.Cancel(); audio.Update(entry, true, true, 1, 12000);
        audio.RecordStereoPcm16([30000, 30000]);
        check("Dying voice: late listener cannot capture later conversation", true, audio.RecordedSamples == 0 && !audio.IsPlaying);
        entry.Started = 20000; clock = 3000;
        audio.Update(entry, true, false, 1, 20000);
        audio.RecordStereoPcm16(new short[48000 * 4]);
        check("Dying voice: memory bounded by recording duration", true, audio.RecordedSamples == 48000);
        audio.Update(null, false, false, 0, 21000);
        check("Dying voice: game end clears buffer", true, audio.RecordedSamples == 0 && !audio.IsPlaying);
        entry.Started = 30000; entry.Loop = true; clock = 4000;
        audio.Update(entry, true, false, 1, 30000);
        audio.RecordStereoPcm16([10000, 10000, -10000, -10000]);
        audio.Update(entry, true, true, 1, 31000);
        var repeated = new float[20]; audio.Read(repeated, 0, repeated.Length);
        check("Dying voice: loop continues past recording end", true, audio.IsPlaying && repeated.Skip(4).Any(value => value != 0));
        audio.Update(entry, true, false, 1, 31001);
        check("Dying voice: leaving stops loop", false, audio.IsPlaying);
        audio.Update(entry, true, true, 1, 31002);
        audio.Update(entry, true, true, 0, 31003);
        check("Dying voice: mute stops loop", false, audio.IsPlaying);
        audio.Cancel();
        check("Dying voice: loop buffer cleared at game end", true, audio.RecordedSamples == 0);
        VerifyFiles(check);
    }

    private static void VerifyFiles(Action<string, bool, bool> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "dying-voice-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"protocol\":1,\"pid\":123,\"unixMs\":10000,\"players\":[{\"playerId\":1,\"started\":10000,\"seconds\":5,\"range\":2.5,\"x\":1,\"y\":2}]}");
            check("Dying voice: state reader accepts valid role", true, NosVoiceRoleStateReader.ReadDyingVoices(123, 10000, path).Count == 1);
            check("Dying voice: state reader rejects other game process", true, NosVoiceRoleStateReader.ReadDyingVoices(124, 10000, path).Count == 0);
            check("Dying voice: state reader rejects stale role", true, NosVoiceRoleStateReader.ReadDyingVoices(123, 11001, path).Count == 0);
            var state = new AmongUsState { Mod = AmongUsModType.NebulaOnTheShip,
                NosDyingVoices = NosVoiceRoleStateReader.ReadDyingVoices(123, 10000, path) };
            var wire = JsonSerializer.SerializeToElement(MobileGameStateWire.Create(state, new LobbySettings(), null));
            check("Dying voice: NET host forwards role and clock to mobile", true,
                wire.GetProperty("gameState").GetProperty("nosDyingVoices").GetArrayLength() == 1 &&
                wire.GetProperty("nosVoiceFrameTime").GetInt64() > 0);
        }
        finally { File.Delete(path); }
    }
}
