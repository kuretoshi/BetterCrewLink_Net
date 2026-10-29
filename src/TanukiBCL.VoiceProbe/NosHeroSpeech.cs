using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TanukiBCL.VoiceProbe.GameMemory;
using Vosk;

namespace TanukiBCL.VoiceProbe;

// One model and one recognizer per selected speaker. A PC selects its own mic;
// the host additionally selects positively identified Web/mobile input streams.
internal sealed class NosHeroSpeech : IDisposable
{
    internal sealed record PlayerRequest(int PlayerId, string Kind, int Request, long Expires, string[] Words);
    internal sealed record Descriptor(int Protocol, int Pid, int Port, string Token, long Updated, PlayerRequest[] Players);
    private sealed record Chunk(int Player, string Signature, long Time, short[] Samples, int Channels);
    private sealed class Capture(PlayerRequest[] jobs, string signature, VoskRecognizer recognizer) : IDisposable
    {
        public PlayerRequest[] Jobs = jobs;
        public readonly string Signature = signature;
        public readonly string InputKey = Guid.NewGuid().ToString("N");
        public readonly VoskRecognizer Recognizer = recognizer;
        public long PartialAt;
        public void Dispose() => Recognizer.Dispose();
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<Chunk> chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(128)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<int, string> armed = new();
    private readonly ConcurrentDictionary<int, byte> mutedPlayers = new();
    private readonly Dictionary<int, Capture> captures = [];
    private readonly HashSet<string> handled = [];
    private readonly UdpClient udp = new();
    private readonly Task worker;
    private Model? model;
    private Descriptor? descriptor;
    private int pid;
    private int[] selectedPlayers = [];
    private long nextPoll, retryAt;
    private bool disposed;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public NosHeroSpeech() => worker = Task.Run(Work);
    public void Update(int gamePid, AmongUsState state, IEnumerable<int> webPlayers)
    {
        var local = state.Players.FirstOrDefault(p => p.IsLocal && !p.IsDead && !p.Disconnected);
        bool enabled = TanukiAddon.IsEnabled(state) && state.GameState is GameState.Tasks or GameState.Discussion;
        var selected = local is null ? Array.Empty<int>() : new[] { local.Id };
        if (state.IsHost) selected = selected.Concat(webPlayers).Distinct().ToArray();
        Volatile.Write(ref selectedPlayers, enabled ? selected : []);
        Volatile.Write(ref pid, enabled ? gamePid : 0);
        if (!enabled) armed.Clear();
    }
    public void Record(int player, ReadOnlySpan<short> pcm, int channels, bool active = true)
    {
        if (!active) { mutedPlayers[player] = 1; armed.TryRemove(player, out _); return; }
        mutedPlayers.TryRemove(player, out _);
        if (disposed || channels is not (1 or 2) || pcm.Length is 0 or > 8192 ||
            !Volatile.Read(ref selectedPlayers).Contains(player) || !armed.TryGetValue(player, out var signature)) return;
        chunks.Writer.TryWrite(new(player, signature, Now, pcm.ToArray(), channels));
    }
    private async Task Work()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                Poll();
                for (int i = 0; i < 32 && chunks.Reader.TryRead(out var chunk); i++) Process(chunk);
                await Task.Delay(10, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { Clear(); model?.Dispose(); udp.Dispose(); }
    }
    private void Poll()
    {
        long now = Now;
        if (now < nextPoll) return;
        nextPoll = now + 200;
        try { PollCore(now); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            DllNotFoundException or TypeInitializationException or InvalidOperationException or SocketException)
        { Clear(); retryAt = now + 10000; Console.WriteLine($"[HeroSpeech] 日本語認識を準備できません: {ex.GetType().Name}"); }
    }
    private void PollCore(long now)
    {
        int currentPid = Volatile.Read(ref pid);
        if (currentPid <= 0) { Clear(); return; }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TanukiBCL", "NoS", $"hero-speech-{currentPid}.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 32768) { Clear(); return; }
        var current = JsonSerializer.Deserialize<Descriptor>(File.ReadAllText(path), JsonOptions);
        if (!Valid(current, currentPid, now)) { Clear(); return; }
        if (descriptor?.Token != current!.Token) Clear();
        descriptor = current;
        var selected = Volatile.Read(ref selectedPlayers);
        var jobs = current.Players.Where(p => selected.Contains(p.PlayerId)).ToArray();
        if (jobs.Length > 0 && model == null && now >= retryAt) LoadModel();
        if (model != null)
            foreach (int id in jobs.Select(p => p.PlayerId).Distinct()) Send($"TBCLHS2|{current.Token}|ready|{Now}|{id}|1");
        Reconcile(jobs, now);
    }
    private void LoadModel()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Speech", "model");
        if (!File.Exists(Path.Combine(path, "am", "final.mdl"))) throw new IOException("The bundled Japanese speech model is missing.");
        Vosk.Vosk.SetLogLevel(-1); model = new Model(path);
    }
    internal static bool Valid(Descriptor? value, int pid, long now)
    {
        if (value is null || value.Protocol != 2 || value.Pid != pid || value.Port is < 1 or > 65535 ||
            value.Token is null || !Regex.IsMatch(value.Token, "^[a-f0-9]{32}$") ||
            now - value.Updated > 1500 || value.Updated - now > 100 || value.Players is not { Length: > 0 and <= 48 }) return false;
        var ids = new HashSet<string>();
        return value.Players.All(p => p is not null && p.PlayerId is >= 0 and <= 255 &&
            p.Kind is "hero" or "ng" && ids.Add($"{p.PlayerId}|{p.Kind}") && p.Request >= 0 &&
            p.Words is { Length: <= 3 } && p.Words.All(w => w is { Length: >= 2 and <= 32 }) &&
            (p.Kind != "ng" || p.Request > 0 && p.Words.Length > 0) &&
            (p.Request == 0 || p.Expires > now - 100 && p.Expires <= now + 16000));
    }
    private static string Key(PlayerRequest job) => $"{job.PlayerId}|{job.Kind}|{job.Request}";
    private static string Signature(PlayerRequest[] jobs) => string.Join(';', jobs.Select(p => Key(p) + "|" + string.Join('|', p.Words)).Order());
    private void Reconcile(PlayerRequest[] jobs, long now)
    {
        var groups = jobs.Where(p => p.Request > 0 && p.Expires > now && !handled.Contains(Key(p)) && !mutedPlayers.ContainsKey(p.PlayerId))
            .GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var (id, capture) in captures.ToArray())
        {
            if (!groups.TryGetValue(id, out var current) || Signature(current) != capture.Signature) Stop(id);
            else capture.Jobs = current;
        }
        if (model == null) return;
        foreach (var (id, entries) in groups)
        {
            if (captures.ContainsKey(id)) continue;
            string signature = Signature(entries);
            captures[id] = new(entries, signature, new VoskRecognizer(model, 16000));
            armed[id] = captures[id].InputKey;
        }
    }
    private void Process(Chunk chunk)
    {
        if (Volatile.Read(ref pid) <= 0 || mutedPlayers.ContainsKey(chunk.Player) || !Volatile.Read(ref selectedPlayers).Contains(chunk.Player) ||
            !captures.TryGetValue(chunk.Player, out var capture) || chunk.Signature != capture.InputKey || Now - chunk.Time > 250) return;
        var bytes = Downsample(chunk.Samples, chunk.Channels);
        bool final = capture.Recognizer.AcceptWaveform(bytes, bytes.Length);
        using var result = JsonDocument.Parse(final ? capture.Recognizer.Result() : capture.Recognizer.PartialResult());
        string text = result.RootElement.GetProperty(final ? "text" : "partial").GetString() ?? "";
        foreach (var job in capture.Jobs) Accept(capture, job, text, final);
    }
    private void Accept(Capture capture, PlayerRequest job, string text, bool final)
    {
        if (job.Expires <= Now || handled.Contains(Key(job))) return;
        bool matched = job.Kind == "ng" ? HeardForbiddenWord(text, job.Words, final) : HeardTransformation(text, job.Words, final);
        if (!matched) { if (job.Kind == "hero") capture.PartialAt = 0; return; }
        if (!final && capture.PartialAt == 0) { capture.PartialAt = Now; return; }
        if (!final && Now - capture.PartialAt < 180) return;
        handled.Add(Key(job));
        Send($"TBCLHS2|{descriptor!.Token}|word|{Now}|{job.PlayerId}|{job.Request}|{(job.Kind == "ng" ? "ng" : "henshin")}");
    }
    internal static bool RecogniseHere(bool local, bool host, string? platform) => local || host && platform is "web" or "mobile";
    private static string Normalise(string text) => new(Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\s、。！!？?・]", "")
        .Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());
    internal static bool HeardForbiddenWord(string text, string[] words, bool final) => final && text.Length <= 4096 &&
        words.Any(w => Normalise(w).Length >= 2 && Normalise(text).Contains(Normalise(w)));
    internal static bool HeardHenshin(string text, bool final = true)
    {
        string normalized = Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"[\s、。！!？?・]", "");
        return normalized.Contains("変身") || normalized.Contains("へんしん") || normalized.Contains("ヘンシン") || final && normalized == "返信";
    }
    internal static bool HeardTransformation(string text, string[] words, bool final = true)
    {
        if (words.Length == 0) return HeardHenshin(text, final);
        if (text.Length > 4096) return false;
        if (words[0] == "変身" && HeardHenshin(text, final)) return true;
        string normal = Normalise(text);
        if (words.Any(w => Normalise(w).Length >= 2 && normal.Contains(Normalise(w)))) return true;
        return false;
    }
    internal static byte[] Downsample(short[] pcm, int channels)
    {
        int stride = 3 * channels;
        var result = new byte[pcm.Length / stride * 2];
        for (int i = 0; i < result.Length / 2; i++)
        {
            int sum = 0;
            for (int channel = 0; channel < stride; channel++) sum += pcm[i * stride + channel];
            short sample = (short)(sum / stride);
            result[i * 2] = (byte)sample; result[i * 2 + 1] = (byte)(sample >> 8);
        }
        return result;
    }
    private void Send(string text)
    {
        if (descriptor == null) return;
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, descriptor.Port));
    }
    private void Stop(int id) { armed.TryRemove(id, out _); if (captures.Remove(id, out var capture)) capture.Dispose(); }
    private void Clear() { armed.Clear(); foreach (var capture in captures.Values) capture.Dispose(); captures.Clear(); handled.Clear(); descriptor = null; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; armed.Clear(); cancellation.Cancel(); chunks.Writer.TryComplete();
    }
}
