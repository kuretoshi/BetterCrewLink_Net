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
        Console.WriteLine("PASS distributed speech routing, NG matching and protocol validation");
        return 0;
    }
}
