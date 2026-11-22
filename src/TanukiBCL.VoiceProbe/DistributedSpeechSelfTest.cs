namespace TanukiBCL.VoiceProbe;

internal static class DistributedSpeechSelfTest
{
    public static int Run()
    {
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); }
        foreach (bool host in new[] { false, true })
        foreach (var platform in new string?[] { null, "desktop", "web", "mobile" })
        {
            Check(NosHeroSpeech.RecogniseHere(true, host, platform), "Local microphone stays local");
            Check(NosHeroSpeech.RecogniseHere(false, host, platform) == (host && platform is "web" or "mobile"), "Remote routing");
        }
        Check(NosHeroSpeech.HeardForbiddenWord("イン ポスターだ", ["いんぽすたー"], true), "Kana and spaces");
        Check(!NosHeroSpeech.HeardForbiddenWord("タスク", ["タスク"], false), "Partial text cannot kill");
        Check(!NosHeroSpeech.HeardForbiddenWord("タスク", ["投票"], true), "Other player's word does not match");
        Check(NosHeroSpeech.HeardTransformation("蒸着", ["蒸着", "じょうちゃく"], true), "Custom hero keyword");
        Check(NosHeroSpeech.HeardTransformation("ジョウ チャク", ["蒸着", "じょうちゃく"], false), "Custom reading and partial recognition");
        Check(!NosHeroSpeech.HeardTransformation("変身", ["蒸着", "じょうちゃく"], true), "Custom word replaces default");
        Check(!NosHeroSpeech.HeardTransformation("返信", ["蒸着"], true), "Default correction stays scoped");
        Check(NosHeroSpeech.HeardTransformation("へんしん", ["変身"], false), "Default kana compatible");
        Check(NosHeroSpeech.HeardTransformation("返信", ["変身"], true), "Default final correction compatible");
        Check(!NosHeroSpeech.HeardTransformation("返信", ["変身"], false), "No partial homophone correction");
        Check(NosHeroSpeech.HeardTransformation("変身", [], true), "Older addon compatible");
        var hero = new NosHeroSpeech.PlayerRequest(1, "hero", 5, 11000, []);
        var ng = new NosHeroSpeech.PlayerRequest(1, "ng", 6, 11000, ["タスク", "たすく"]);
        var descriptor = new NosHeroSpeech.Descriptor(2, 99, 12345, new string('a', 32), 10000, [hero, ng]);
        Check(NosHeroSpeech.Valid(descriptor, 99, 10000), "Hero and NG can coexist");
        Check(NosHeroSpeech.Valid(descriptor with { Players = [hero with { Words = ["蒸着", "じょうちゃく"] }, ng] }, 99, 10000), "Custom words pass protocol validation");
        Check(!NosHeroSpeech.Valid(descriptor with { Protocol = 1 }, 99, 10000), "Old protocol rejected");
        Check(!NosHeroSpeech.Valid(descriptor with { Players = [hero, hero] }, 99, 10000), "Duplicate jobs rejected");
        Check(!NosHeroSpeech.Valid(descriptor, 98, 10000), "Different PID rejected");
        Check(!NosHeroSpeech.Valid(descriptor, 99, 12000), "Stale file rejected");
        CheckVoiceStone();
        CheckLiveSpeech();
        Console.WriteLine("PASS distributed speech routing, NG matching and protocol validation");
        return 0;
    }
    private static void CheckLiveSpeech()
    {
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); }
        byte[] tone = new byte[3200];
        for (int i = 0; i < tone.Length; i += 2) { tone[i] = 208; tone[i + 1] = 7; }
        var audio = new LiveSpeechAudio();
        for (int i = 0; i < 20; i++) audio.Append(new byte[3200], 1000 + i * 100);
        Check(audio.Take(3500) == null, "Silence never submits");
        for (int i = 0; i < 12; i++) audio.Append(tone, 4000 + i * 100);
        Check(audio.Take(5200) != null && audio.Take(5201) == null, "Bounded intermediate utterance");
        audio.Append(tone, 5200);
        Check(audio.Take(5650)!.Length > 40000 && audio.Take(5800) == null, "Overlapping word and single final flush");
        for (int i = 0; i < 300; i++) audio.Append(tone, 6000 + i * 100);
        Check(audio.Take(36000)!.Length <= 16000 * 8 * 2, "Continuous speech memory bound");
        audio.Append(tone, 37000); audio.Reset(); Check(audio.Take(38000) == null, "Mute drops PCM");
        var ng = new NosHeroSpeech.PlayerRequest(1, "ng", 1, 1400, ["タスク"]);
        Check(NosHeroSpeech.SameLive(ng, ng with { Expires = 5000 }, 3000), "NG heartbeat renewals preserve request identity");
        Check(!NosHeroSpeech.SameLive(ng, ng with { Expires = 5000, Words = ["会議"] }, 3000), "Changed word rejects stale result");
        Check(!NosHeroSpeech.SameLive(ng, ng with { Expires = 5000, Request = 2 }, 3000), "Changed request rejects stale result");
        Check(NosHeroSpeech.HeardTransformation("変信", ["変身"], true), "SenseVoice default homophone");
        Check(!NosHeroSpeech.HeardTransformation("Hey Siri", ["変身"], true), "Unrelated phrase cannot transform");
        Check(!NosHeroSpeech.HeardForbiddenWord("変信", ["変身"], true), "No fuzzy correction for NG deaths");
    }
    private static void CheckVoiceStone()
    {
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); }
        using var tokens = System.Text.Json.JsonDocument.Parse("""[{"word":"えっと","conf":0.9},{"word":"猫","conf":0.95},{"word":"です","conf":1}]""");
        Check(VoiceStoneSpeech.Extract(tokens.RootElement, true, 8) == "猫", "One token, not the whole sentence");
        Check(VoiceStoneSpeech.Extract(tokens.RootElement, false, 8) == null, "Partial speech never petrifies");
        Check(VoiceStoneSpeech.Extract(null, true, 8) == null, "No fallback to transcript");
        Check(!VoiceStoneSpeech.ValidWord("猫 犬", 8) && !VoiceStoneSpeech.ValidWord("<猫>", 8), "No multiword or markup");
        Check(VoiceStoneSpeech.ValidWord("インポスター", 6) && !VoiceStoneSpeech.ValidWord("インポスター", 5), "Word length bounds");
        using var bad = System.Text.Json.JsonDocument.Parse("""[{"word":"猫","conf":"bad"},{"word":"犬","conf":0.1}]""");
        Check(VoiceStoneSpeech.Extract(bad.RootElement, true, 8) == null, "Malformed and low-confidence results ignored");
        var stone = new NosHeroSpeech.PlayerRequest(1, "stone", 9, 11000, [], 8);
        var descriptor = new NosHeroSpeech.Descriptor(2, 99, 12345, new string('a', 32), 10000, [stone]);
        Check(NosHeroSpeech.Valid(descriptor, 99, 10000), "Stone protocol accepted");
        Check(!NosHeroSpeech.Valid(descriptor with { Players = [stone with { MaxCharacters = 13 }] }, 99, 10000), "Unsafe size rejected");
    }
}
