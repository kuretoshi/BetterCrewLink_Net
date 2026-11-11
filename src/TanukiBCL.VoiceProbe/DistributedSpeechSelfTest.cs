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
        Console.WriteLine("PASS distributed speech routing, NG matching and protocol validation");
        return 0;
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
