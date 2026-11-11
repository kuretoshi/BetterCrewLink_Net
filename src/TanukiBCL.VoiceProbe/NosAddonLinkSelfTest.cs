using System.Diagnostics;
using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class NosAddonLinkSelfTest
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
    private sealed class Fixture : IDisposable
    {
        internal readonly Process Process;
        internal int Pid, Port;
        internal string Session = "";
        internal Fixture(string path)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(path); start.ArgumentList.Add("--server");
            Process = Process.Start(start)!;
            using var hello = JsonDocument.Parse(Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()!);
            var h = hello.RootElement; Pid = h.GetProperty("pid").GetInt32(); Port = h.GetProperty("port").GetInt32(); Session = h.GetProperty("session").GetString()!;
        }
        internal void Set(object channels) { Process.StandardInput.WriteLine(JsonSerializer.Serialize(new { channels })); Process.StandardInput.Flush(); }
        public void Dispose() { Process.StandardInput.Close(); if (!Process.WaitForExit(1000)) Process.Kill(); Process.Dispose(); }
    }
    private static object Channels(string kind, bool empty = false)
    {
        long now = Now;
        object[] heroes = empty ? [] : [new { playerId = 1, kind }];
        return new { heroes, parrots = new[] { new { ownerId = 1, birdId = 9, x = 0, y = 0, range = 4, seconds = 3, recordId = 0, sourceId = 2, started = 0L, clips = Array.Empty<int>(), playId = 0, playClip = 0, playStarted = 0L } }, ears = new[] { new { playerId = 1, x = 0, y = 0, range = 4 } }, portable = new[] { new { playerId = 1, extraRange = 4.5 } },
            megaphones = new[] { new { playerId = 2, energy = .5, range = 7, shotId = 3, phase = "charging" } },
            dying = new[] { new { playerId = 3, started = now - 1000, seconds = 5, range = 3, x = -2, y = 4, loop = true } },
            interviews = new[] { new { playerId = 4, started = now - 1000, seconds = 10, stopped = 0, visible = new[] { 1, 4 }, playStarted = 0 } },
            jamming = new[] { new { playerId = 2, muted = false, lowpass = true, pitch = 1.2 } },
            echoes = new[] { new { playerId = 5, unixMs = now } }, muted = new[] { 6 },
            speech = new[] { new { playerId = 1, kind = "hero", request = 7, expires = now + 5000, words = new[] { "変身" } } } };
    }
    private static async Task Until(Func<bool> condition, Action tick)
    {
        long deadline = Now + 5000;
        while (!condition()) { Check(Now < deadline, "addon link timeout"); tick(); await Task.Delay(20); }
    }
    internal static async Task<int> Run(string fixturePath)
    {
        using var a = new Fixture(fixturePath); using var b = new Fixture(fixturePath);
        using var first = NosAddonLink.Shared; using var second = new NosAddonLink();
        a.Set(Channels("super")); b.Set(Channels("evil"));
        void Tick() { first.Update(a.Pid, "round1", 0x54420000 | a.Port, "tasks", 1); second.Update(b.Pid, "round1", 0x54420000 | b.Port, "tasks", 1); }
        await Until(() => first.Current(a.Pid)?.Read<NosHeroTransformState>("heroes").Count == 1 && second.Current(b.Pid)?.Read<NosHeroTransformState>("heroes").Count == 1, Tick);
        Check(first.Current(a.Pid)!.Read<NosHeroTransformState>("heroes")[0].Kind == "super", "first PID data");
        Check(second.Current(b.Pid)!.Read<NosHeroTransformState>("heroes")[0].Kind == "evil", "second PID data");
        Check(first.Current(a.Pid)!.Read<NosWallEarState>("ears")[0].Range == 4, "ear state crosses actual addon TCP link");
        Check(first.Current(a.Pid)!.Read<NosParrotState>("parrots")[0].BirdId == 9, "parrot state crosses actual addon TCP");
        Check(first.Current(a.Pid)!.Read<NosParrotState>("parrots")[0].Seconds == 3, "recording duration crosses actual addon TCP");
        long firstSequence = first.Current(a.Pid)!.Sequence;
        await Task.Delay(550);
        Check(first.Current(a.Pid)?.Sequence > firstSequence, "TCP updates independently of the 500ms memory-reader cadence");
        Tick();
        Check(first.Current(b.Pid) == null && !first.Send(b.Pid, "ready", new() { ["players"] = new[] { 1 } }), "other process rejected");
        Check(first.Current(a.Pid)!.Read<NosInterviewState>("interviews").Count == 1 && first.Current(a.Pid)!.Read<NosMegaphoneState>("megaphones").Count == 1 &&
            first.Current(a.Pid)!.Read<NosPortableMegaphoneState>("portable").Count == 1 && first.Current(a.Pid)!.Read<NosDyingVoiceState>("dying").Count == 1 &&
            first.Current(a.Pid)!.Read<NosVoiceJammingState>("jamming").Count == 1 && first.Current(a.Pid)!.Read<NosBibiriEchoState>("echoes").Count == 1, "all typed role channels in one unified state");
        Check(first.Send(a.Pid, "sample", new() { ["playerId"] = 1, ["rms"] = .5, ["active"] = true, ["host"] = false }), "sample sent");
        Check(first.Send(a.Pid, "ready", new() { ["players"] = new[] { 1 } }), "ready sent");
        Check(first.Send(a.Pid, "word", new() { ["playerId"] = 1, ["request"] = 7, ["speechKind"] = "hero" }), "word sent");
        Check(first.Send(a.Pid, "parrot", new() { ["ownerId"] = 1, ["birdId"] = 9, ["sourceId"] = 2 }), "parrot command sent");
        foreach (string expected in new[] { "sample", "ready", "word", "parrot" })
        {
            string? line = await a.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            using var command = JsonDocument.Parse(line!);
            Check(command.RootElement.GetProperty("command").GetProperty("kind").GetString() == expected, "reply on expected server");
        }
        var empty = new[] { "heroes", "portable", "megaphones", "dying", "interviews", "jamming", "echoes", "muted", "speech" }.ToDictionary(k => k, _ => Array.Empty<object>());
        a.Set(empty);
        await Until(() => first.Current(a.Pid)?.Read<NosHeroTransformState>("heroes").Count == 0, Tick);
        Check(first.Current(a.Pid)!.Read<NosParrotState>("parrots").Count == 0, "older snapshot clears parrot state");
        Check(first.Current(a.Pid)!.Read<NosWallEarState>("ears").Count == 0, "older snapshot without ears clears ear state");
        Check(first.Current(a.Pid)!.Read<NosHeroTransformState>("heroes").Count == 0 && first.Current(a.Pid)!.Read<NosInterviewState>("interviews").Count == 0, "empty snapshot clears without file fallback");
        a.Set(new { heroes = Array.Empty<object>() });
        await Until(() => first.Current(a.Pid) == null, Tick);
        Check(!first.Send(a.Pid, "ready", new() { ["players"] = new[] { 1 } }), "partial/stale snapshot disables replies");
        first.Dispose(); first.Update(a.Pid, "other-endpoint", 0x54420000 | b.Port, "tasks", 1);
        await Task.Delay(200); Check(first.Current(a.Pid) == null, "wrong endpoint handshake rejected");
        Console.WriteLine("PASS .NET addon link: two actual addon servers, all typed role channels, sample/ready/word, empty clears, partial/stale state and PID isolation");
        return 0;
    }
}
