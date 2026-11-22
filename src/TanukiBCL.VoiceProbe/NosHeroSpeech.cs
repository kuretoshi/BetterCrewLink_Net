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
    internal sealed record PlayerRequest(int PlayerId, string Kind, int Request, long Expires, string[] Words, int MaxCharacters = 0, string? Recognizer = null, long RecordUntil = 0, long RecordStarted = 0);
    internal sealed record Descriptor(int Protocol, int Pid, int Port, string Token, long Updated, PlayerRequest[] Players, bool Unified = false);
    private sealed record Chunk(int Player, string Signature, long Time, short[] Samples, int Channels);
    private sealed class Capture(PlayerRequest[] jobs, string signature, VoskRecognizer? recognizer) : IDisposable
    {
        public PlayerRequest[] Jobs = jobs;
        public readonly string Signature = signature;
        public readonly string InputKey = Guid.NewGuid().ToString("N");
        public readonly VoskRecognizer? Recognizer = recognizer;
        public long PartialAt;
        public readonly MemoryStream RecordedPcm = new();
        public bool Submitted;
        public readonly LiveSpeechAudio Live = new();
        public void Dispose() { Recognizer?.Dispose(); RecordedPcm.Dispose(); }
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<Chunk> chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(128)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<int, string> armed = new();
    private readonly ConcurrentDictionary<int, byte> mutedPlayers = new();
    private readonly Dictionary<int, Capture> captures = [];
    private readonly Dictionary<int, Queue<(long At, byte[] Bytes)>> warm = [];
    private readonly HashSet<string> handled = [];
    private readonly UdpClient udp = new();
    private readonly Task worker;
    private Model? model;
    private Descriptor? descriptor;
    private int pid;
    private int[] selectedPlayers = [];
    private bool host;
    private readonly Dictionary<string, CancellationTokenSource> pending = [];
    private readonly ConcurrentQueue<(string Session, PlayerRequest Job, string? Word)> results = new();
    private readonly Dictionary<int, (CancellationTokenSource Token, PlayerRequest[] Jobs, string InputKey)> livePending = [];
    private readonly ConcurrentQueue<(string Session, PlayerRequest[] Jobs, string InputKey, long At, string? Text)> liveResults = new();
    private long nextPoll, retryAt;
    private bool disposed;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public NosHeroSpeech() => worker = Task.Run(Work);
    public void Update(int gamePid, AmongUsState state, IEnumerable<int> webPlayers)
    {
        host = state.IsHost;
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
    private static bool Buffered(PlayerRequest p) => p.Recognizer == "local-neural";
    private static bool SharedLive(PlayerRequest p) => p.Kind != "stone" && LocalStoneSpeech.Enabled;
    private static bool BufferedOrWarm(PlayerRequest p) => Buffered(p) || SharedLive(p) || p.Kind == "stone" && p.Request == 0 && LocalStoneSpeech.Enabled;
    private static bool EnabledFor(PlayerRequest p) => p.Recognizer == "local-neural" && LocalStoneSpeech.Enabled;
    private void PollCore(long now)
    {
        LocalStoneSpeech.Idle();
        int currentPid = Volatile.Read(ref pid);
        if (currentPid <= 0) { Clear(); LocalStoneSpeech.Close(); return; }
        var current = ReadDescriptor(currentPid, now);
        if (!Valid(current, currentPid, now)) { Clear(); return; }
        if (descriptor?.Token != current!.Token) Clear();
        descriptor = current;
        var selected = Volatile.Read(ref selectedPlayers);
        var jobs = current.Players.Where(p => selected.Contains(p.PlayerId)).ToArray();
        foreach (var (id, entry) in livePending.ToArray())
            if (!LocalStoneSpeech.Enabled || mutedPlayers.ContainsKey(id) || !captures.TryGetValue(id, out var capture) || capture.InputKey != entry.InputKey || !entry.Jobs.Any(j => jobs.Any(p => SameLive(j, p, now))))
            { entry.Token.Cancel(); entry.Token.Dispose(); livePending.Remove(id); }
        while (liveResults.TryDequeue(out var result))
        {
            if (livePending.TryGetValue(result.Jobs[0].PlayerId, out var entry) && entry.InputKey == result.InputKey)
            { livePending.Remove(result.Jobs[0].PlayerId); entry.Token.Dispose(); }
            if (result.Text == null || result.Session != current.Token || now - result.At > 5000 || !LocalStoneSpeech.Enabled ||
                !captures.TryGetValue(result.Jobs[0].PlayerId, out var capture) || capture.InputKey != result.InputKey || mutedPlayers.ContainsKey(result.Jobs[0].PlayerId)) continue;
            foreach (var job in result.Jobs)
                if (!handled.Contains(Key(job)) && jobs.Any(p => SameLive(job, p, now)) &&
                    (job.Kind == "ng" ? HeardForbiddenWord(result.Text, job.Words, true) : HeardTransformation(result.Text, job.Words, true)))
                { handled.Add(Key(job)); SendWord(job); }
        }
        if (model != null && !jobs.Any(p => !BufferedOrWarm(p))) { foreach (int id in captures.Keys.ToArray()) Stop(id); model.Dispose(); model = null; }
        foreach (var (key, token) in pending.ToArray())
            if (!jobs.Any(j => Key(j) == key && j.Expires > now && EnabledFor(j))) { token.Cancel(); token.Dispose(); pending.Remove(key); }
        while (results.TryDequeue(out var result))
        {
            if (pending.Remove(Key(result.Job), out var token)) token.Dispose();
            if (result.Word != null && result.Session == current.Token && EnabledFor(result.Job) && jobs.Any(j => Key(j) == Key(result.Job) && j.Expires > now)) SendWord(result.Job, result.Word);
        }
        if (jobs.Any(p => !BufferedOrWarm(p)) && model == null && now >= retryAt) {
            try { LoadModel(); }
            catch (Exception ex) when (ex is IOException or DllNotFoundException or TypeInitializationException or InvalidOperationException) { retryAt = now + 10000; }
        }
        foreach (int id in jobs.Where(p => model != null || BufferedOrWarm(p)).Select(p => p.PlayerId).Distinct())
            SendReady(id, LocalStoneSpeech.Enabled && jobs.Any(p => p.PlayerId == id && p.Kind == "stone"));
        Reconcile(jobs, now);
    }
    internal static bool SameLive(PlayerRequest a, PlayerRequest b, long now) => Key(a) == Key(b) && a.Words.SequenceEqual(b.Words) && b.Request > 0 && b.Expires > now;
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
            now - value.Updated > 1500 || value.Updated - now > 100 || value.Players is not { Length: > 0 and <= 72 }) return false;
        var ids = new HashSet<string>();
        return value.Players.All(p => p is not null && p.PlayerId is >= 0 and <= 255 &&
            p.Kind is "hero" or "ng" or "stone" && ids.Add($"{p.PlayerId}|{p.Kind}") && p.Request >= 0 &&
            p.Words is { Length: <= 3 } && p.Words.All(w => w is { Length: >= 2 and <= 32 }) &&
            (p.Kind != "ng" || p.Request > 0 && p.Words.Length > 0) &&
            (p.Kind != "stone" || p.Words.Length == 0 && p.MaxCharacters is >= 1 and <= 12) &&
            (p.Recognizer is null or "local" || p.Kind == "stone" && Buffered(p) && p.RecordUntil >= 0 && p.RecordUntil <= now + 11000 && (p.Request == 0 || p.RecordUntil - p.RecordStarted is >= 1000 and <= 10000 && p.Expires - p.RecordUntil == 20000)) &&
            (p.Request == 0 || p.Expires > now - 100 && p.Expires <= now + (Buffered(p) ? 31000 : 16000)));
    }
    private static string Key(PlayerRequest job) => $"{job.PlayerId}|{job.Kind}|{job.Request}";
    private static string Signature(PlayerRequest[] jobs) => string.Join(';', jobs.Select(p => Key(p) + "|" + string.Join('|', p.Words) + "|" + p.MaxCharacters + "|" + p.Recognizer).Order());
    private void Reconcile(PlayerRequest[] jobs, long now)
    {
        var groups = jobs.Where(p => (BufferedOrWarm(p) || p.Request > 0 && p.Expires > now && !handled.Contains(Key(p))) && !mutedPlayers.ContainsKey(p.PlayerId))
            .GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var (id, capture) in captures.ToArray())
        {
            if (!groups.TryGetValue(id, out var current) || Signature(current) != capture.Signature) Stop(id);
            else capture.Jobs = current;
        }
        foreach (var (id, entries) in groups)
        {
            if (captures.ContainsKey(id)) continue;
            string signature = Signature(entries);
            if (model == null && !entries.Any(BufferedOrWarm)) continue;
            captures[id] = new(entries, signature, model != null && entries.Any(p => !BufferedOrWarm(p)) ? new VoskRecognizer(model, 16000) : null);
            if (entries.Any(p => p.Kind == "stone" && !BufferedOrWarm(p))) captures[id].Recognizer?.SetWords(true);
            if (warm.TryGetValue(id, out var buffered)) foreach (var part in buffered) AppendRecording(captures[id], part.Bytes, part.At);
            armed[id] = captures[id].InputKey;
        }
        foreach (var capture in captures.Values)
        {
            var liveJobs = capture.Jobs.Where(j => SharedLive(j) && j.Request > 0 && j.Expires > now && !handled.Contains(Key(j))).ToArray();
            if (liveJobs.Length > 0 && !livePending.ContainsKey(liveJobs[0].PlayerId) && pending.Count + livePending.Count < 4 && descriptor != null)
            {
                var livePcm = capture.Live.Take(now);
                if (livePcm != null)
                {
                    var liveToken = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    liveToken.CancelAfter(18000);
                    string liveSession = descriptor.Token, inputKey = capture.InputKey; var liveStopToken = liveToken.Token;
                    livePending[liveJobs[0].PlayerId] = (liveToken, liveJobs, inputKey);
                    _ = Task.Run(async () => {
                        string? text = null;
                        try { text = await LocalStoneSpeech.TranscribeText(livePcm, liveStopToken); }
                        catch (OperationCanceledException) when (liveStopToken.IsCancellationRequested) { }
                        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { Console.WriteLine("[HeroSpeech] 共有モデルの認識を完了できませんでした。"); }
                        if (!liveStopToken.IsCancellationRequested) liveResults.Enqueue((liveSession, liveJobs, inputKey, now, text));
                    });
                }
            }
            var job = capture.Jobs.FirstOrDefault(j => Buffered(j) && j.Request > 0);
            if (job == null || capture.Submitted || job.RecordUntil > now || descriptor == null) continue;
            capture.Submitted = true; handled.Add(Key(job));
            if (pending.Count >= 4) continue;
            var token = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            token.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(18000, job.Expires - now))));
            pending[Key(job)] = token;
            byte[] pcm = capture.RecordedPcm.ToArray(); string session = descriptor.Token; var stopToken = token.Token;
            _ = Task.Run(async () => {
                string? word = null;
                try { word = await LocalStoneSpeech.Transcribe(pcm, job.MaxCharacters, stopToken); }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
                catch (Exception ex) when (ex is OperationCanceledException or JsonException or IOException) { Console.WriteLine("[VoiceStone] ローカル認識を完了できませんでした。"); }
                if (!stopToken.IsCancellationRequested) results.Enqueue((session, job, word));
            });
        }
    }
    private void Process(Chunk chunk)
    {
        if (Volatile.Read(ref pid) <= 0 || mutedPlayers.ContainsKey(chunk.Player) || !Volatile.Read(ref selectedPlayers).Contains(chunk.Player) ||
            !captures.TryGetValue(chunk.Player, out var capture) || chunk.Signature != capture.InputKey || Now - chunk.Time > 250) return;
        var bytes = Downsample(chunk.Samples, chunk.Channels);
        long at = chunk.Time - bytes.Length / 32;
        if (capture.Jobs.Any(j => SharedLive(j) && j.Request > 0)) capture.Live.Append(bytes, at);
        if (capture.Jobs.Any(j => j.Kind == "stone" && BufferedOrWarm(j))) {
            if (!warm.TryGetValue(chunk.Player, out var buffer)) warm[chunk.Player] = buffer = new();
            buffer.Enqueue((at, bytes)); while (buffer.Count > 0 && buffer.Peek().At < at - 1000) buffer.Dequeue();
            AppendRecording(capture, bytes, at);
        }
        if (capture.Recognizer == null) return;
        bool final = capture.Recognizer.AcceptWaveform(bytes, bytes.Length);
        using var result = JsonDocument.Parse(final ? capture.Recognizer.Result() : capture.Recognizer.PartialResult());
        string text = result.RootElement.GetProperty(final ? "text" : "partial").GetString() ?? "";
        JsonElement? tokens = result.RootElement.TryGetProperty("result", out var words) ? words : null;
        foreach (var job in capture.Jobs) Accept(capture, job, text, final, tokens);
    }
    private static void AppendRecording(Capture capture, byte[] bytes, long at)
    {
        var job = capture.Jobs.FirstOrDefault(j => Buffered(j) && j.Request > 0);
        if (job == null || capture.Submitted) return;
        var window = VoiceStoneAudio.Window(bytes, at, job.RecordStarted, job.RecordUntil);
        int length = (int)Math.Min(window.Length, 320000 - capture.RecordedPcm.Length);
        if (length > 0) capture.RecordedPcm.Write(window[..length]);
    }
    private void Accept(Capture capture, PlayerRequest job, string text, bool final, JsonElement? tokens)
    {
        if (BufferedOrWarm(job) || job.Expires <= Now || handled.Contains(Key(job))) return;
        if (job.Kind == "stone") { AcceptStone(job, tokens, final); return; }
        bool matched = job.Kind == "ng" ? HeardForbiddenWord(text, job.Words, final) : HeardTransformation(text, job.Words, final);
        if (!matched) { if (job.Kind == "hero") capture.PartialAt = 0; return; }
        if (!final && capture.PartialAt == 0) { capture.PartialAt = Now; return; }
        if (!final && Now - capture.PartialAt < 180) return;
        handled.Add(Key(job));
        SendWord(job);
    }
    private void AcceptStone(PlayerRequest job, JsonElement? tokens, bool final)
    {
        string? word = VoiceStoneSpeech.Extract(tokens, final, job.MaxCharacters);
        if (word == null) return;
        handled.Add(Key(job));
        SendWord(job, word);
    }
    internal static bool RecogniseHere(bool local, bool host, string? platform) => local || host && platform is "web" or "mobile";
    private static string Normalise(string text) => new(Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\s、。！!？?・]", "")
        .Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());
    internal static bool HeardForbiddenWord(string text, string[] words, bool final) => final && text.Length <= 4096 &&
        words.Any(w => Normalise(w).Length >= 2 && Normalise(text).Contains(Normalise(w)));
    internal static bool HeardHenshin(string text, bool final = true)
    {
        string normalized = Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"[\s、。！!？?・]", "");
        return normalized.Contains("変身") || normalized.Contains("へんしん") || normalized.Contains("ヘンシン") || final && normalized is "返信" or "変信" or "返身";
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
    private static Descriptor? ReadDescriptor(int currentPid, long now)
    {
        var linked = NosAddonLink.Shared.Current(currentPid);
        if (linked != null) return new(2, currentPid, NosAddonLink.Shared.Port, linked.Session, linked.UnixMs,
            linked.Read<PlayerRequest>("speech").Where(p => p.Request == 0 || p.Expires > now).ToArray(), true);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TanukiBCL", "NoS", $"hero-speech-{currentPid}.json");
        return File.Exists(path) && new FileInfo(path).Length <= 32768 ? JsonSerializer.Deserialize<Descriptor>(File.ReadAllText(path), JsonOptions) : null;
    }
    private void SendReady(int player, bool neural = false)
    {
        if (descriptor == null) return;
        if (descriptor.Unified) { NosAddonLink.Shared.Send(descriptor.Pid, "ready", new() { ["players"] = new[] { player }, ["neural"] = neural ? new[] { player } : Array.Empty<int>() }); return; }
        Send($"TBCLHS2|{descriptor.Token}|ready|{Now}|{player}|1");
    }
    private void SendWord(PlayerRequest job, string? word = null)
    {
        if (descriptor == null) return;
        if (descriptor.Unified) { NosAddonLink.Shared.Send(descriptor.Pid, "word", new() { ["playerId"] = job.PlayerId, ["request"] = job.Request, ["speechKind"] = job.Kind, ["word"] = word }); return; }
        string payload = job.Kind == "stone" ? "stone|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(word!)) : job.Kind == "ng" ? "ng" : "henshin";
        Send($"TBCLHS2|{descriptor.Token}|word|{Now}|{job.PlayerId}|{job.Request}|{payload}");
    }
    private void Send(string text)
    {
        if (descriptor == null) return;
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, descriptor.Port));
    }
    private void Stop(int id) { armed.TryRemove(id, out _); if (captures.Remove(id, out var capture)) capture.Dispose(); }
    private void Clear() { foreach (var entry in livePending.Values) { entry.Token.Cancel(); entry.Token.Dispose(); } livePending.Clear(); while (liveResults.TryDequeue(out _)) { } warm.Clear(); foreach (var token in pending.Values) { token.Cancel(); token.Dispose(); } pending.Clear(); while (results.TryDequeue(out _)) { } armed.Clear(); foreach (var capture in captures.Values) capture.Dispose(); captures.Clear(); model?.Dispose(); model = null; handled.Clear(); descriptor = null; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; armed.Clear(); cancellation.Cancel(); LocalStoneSpeech.Close(); chunks.Writer.TryComplete();
    }
}
