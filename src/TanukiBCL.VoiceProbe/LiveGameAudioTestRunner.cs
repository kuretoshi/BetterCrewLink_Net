using System.Collections.Concurrent;
using System.Diagnostics;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class LiveGameAudioTestRunner
{
    public static async Task<int> RunAsync(ProbeOptions baseOptions)
    {
        var processIds = Process.GetProcessesByName("Among Us")
            .OrderBy(process => process.Id)
            .Select(process =>
            {
                var id = process.Id;
                process.Dispose();
                return id;
            })
            .ToArray();
        if (processIds.Length != 5)
        {
            Console.Error.WriteLine($"[FAIL] Among Usは5プロセス必要です。検出数={processIds.Length}");
            return 1;
        }

        var liveProcessId = baseOptions.GameProcessId ?? processIds[0];
        if (!processIds.Contains(liveProcessId))
        {
            Console.Error.WriteLine($"[FAIL] 指定PIDは現在のAmong Usプロセスではありません: {liveProcessId}");
            return 1;
        }

        var timeout = baseOptions.Duration ?? TimeSpan.FromSeconds(60);
        using var cancellation = new CancellationTokenSource(timeout);
        var nodes = processIds.Select(id => new TestNode(id, id == liveProcessId)).ToArray();
        Console.WriteLine(
            $"実音声ハイブリッドテスト開始: livePid={liveProcessId} input={baseOptions.InputDevice} " +
            $"output={baseOptions.OutputDevice} timeout={timeout.TotalSeconds:0}s");

        try
        {
            // 仮想側を先に参加させ、実音声側の開始直後に4方向をまとめて確認する。
            foreach (var node in nodes.OrderBy(node => node.IsLive))
            {
                var options = baseOptions with
                {
                    LobbyCode = null,
                    SelfTest = false,
                    GameAudioSelfTest = false,
                    GameAudioTransitionTest = false,
                    LiveGameAudioTest = false,
                    GameAudioRecoveryTest = false,
                    GameAudioServerRecoveryTest = false,
                    LiveAudio = node.IsLive,
                    GameProcessId = node.ProcessId,
                    Duration = null
                };
                node.Probe = new VoiceServerProbe(options, node.IsLive ? $"LIVE{node.ProcessId}" : $"SIM{node.ProcessId}");
                node.Probe.GameStateApplied += state => node.State = state;
                node.Probe.PeerMixChanged += (clientId, mix) => node.Mixes[clientId] = mix;
                node.Probe.PeerAudioVerified += (clientId, result) => node.Audio[clientId] = result;
                node.Probe.PeerPcmReceived += (clientId, _) => node.PcmFrames.AddOrUpdate(clientId, 1, (_, count) => count + 1);
                node.Probe.LocalVadChanged += talking => node.TalkingObserved |= talking;
                node.RunTask = node.Probe.RunAsync(cancellation.Token);
                await node.Probe.Connected.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
                await Task.Delay(250, cancellation.Token);
            }

            var liveNode = nodes.Single(node => node.IsLive);
            var waitStarted = Stopwatch.StartNew();
            var reconnectedClientIds = new HashSet<int>();
            while (!IsBidirectionalPathReady(nodes, liveNode))
            {
                if (waitStarted.Elapsed >= TimeSpan.FromSeconds(10))
                    await RetryMissingPathsAsync(nodes, liveNode, reconnectedClientIds);
                await Task.Delay(250, cancellation.Token);
            }

            var state = liveNode.State!;
            var me = state.Players.Single(player => player.IsLocal);
            var passed = true;
            foreach (var other in state.Players.Where(player => !player.IsLocal))
            {
                var expected = SpatialVoicePolicy.Calculate(state, me, other, new SpatialVoiceSettings());
                var mixPassed = liveNode.Mixes.TryGetValue(other.ClientId, out var actual) && actual == expected;
                var audioPassed = liveNode.Audio.TryGetValue(other.ClientId, out var audio) && audio.Frames >= 10 && audio.Rms > 0.01d;
                passed &= mixPassed && audioPassed;
                Console.WriteLine(
                    $"LIVE PID {liveProcessId} <- client {other.ClientId}: audio={(audioPassed ? "OK" : "NG")} " +
                    $"mix={(mixPassed ? "OK" : "NG")} gain={actual?.Gain ?? 0:0.000} " +
                    $"pan={actual?.Pan ?? 0:+0.00;-0.00;0.00} reason={actual?.Reason ?? "missing"}");
            }

            foreach (var simulatedNode in nodes.Where(node => !node.IsLive))
            {
                var frames = simulatedNode.PcmFrames.GetValueOrDefault(me.ClientId);
                var sent = frames >= 10;
                passed &= sent;
                Console.WriteLine(
                    $"MIC client {me.ClientId} -> PID {simulatedNode.ProcessId}: " +
                    $"audio={(sent ? "OK" : "NG")} pcmFrames={frames}");
            }

            passed &= liveNode.TalkingObserved;
            Console.WriteLine($"VAD talking=true: {(liveNode.TalkingObserved ? "OK" : "NG")}");

            Console.WriteLine(passed
                ? "[PASS] 4方向の実再生と、実マイク/VADから4仮想視点への送信を確認しました。"
                : "[FAIL] 実音声の受信、送信、VAD、またはミックスに不一致があります。");
            return passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("[FAIL] 制限時間内に実音声視点の4方向受信を確認できませんでした。");
            var liveNode = nodes.Single(node => node.IsLive);
            var liveClientId = liveNode.State?.Players.SingleOrDefault(player => player.IsLocal)?.ClientId;
            Console.WriteLine(
                $"LIVE PID {liveNode.ProcessId}: state={liveNode.State?.GameState.ToString() ?? "未取得"} " +
                $"vad={liveNode.TalkingObserved} incomingAudio={liveNode.Audio.Count}/4 mixes={liveNode.Mixes.Count}/4 client={liveClientId}");
            foreach (var node in nodes.Where(node => !node.IsLive))
            {
                Console.WriteLine(
                    $"SIM PID {node.ProcessId}: micFrames=" +
                    (liveClientId is { } id ? node.PcmFrames.GetValueOrDefault(id) : 0));
            }
            return 1;
        }
        finally
        {
            cancellation.Cancel();
            foreach (var node in nodes.Reverse())
            {
                if (node.Probe is not null)
                {
                    await node.Probe.DisposeAsync();
                }
            }

            await Task.WhenAll(nodes.Where(node => node.RunTask is not null).Select(node => IgnoreCancellationAsync(node.RunTask!)));
        }
    }

    private static async Task RetryMissingPathsAsync(IReadOnlyCollection<TestNode> nodes, TestNode liveNode,
        HashSet<int> reconnectedClientIds)
    {
        if (liveNode.State is not { } currentState) return;
        var liveClientId = currentState.Players.Single(player => player.IsLocal).ClientId;
        foreach (var simulatedNode in nodes.Where(node => !node.IsLive))
        {
            var remoteClientId = simulatedNode.State?.Players.SingleOrDefault(player => player.IsLocal)?.ClientId;
            if (remoteClientId is not { } id ||
                simulatedNode.PcmFrames.GetValueOrDefault(liveClientId) >= 10 ||
                !reconnectedClientIds.Add(id)) continue;
            Console.WriteLine($"[RETRY] client {id}への片方向メディアを再接続します。");
            await liveNode.Probe!.ReconnectClientAsync(id);
        }
    }

    private static bool IsBidirectionalPathReady(IReadOnlyCollection<TestNode> nodes, TestNode liveNode)
    {
        if (liveNode.State is not { Players.Count: 5 } state ||
            !liveNode.TalkingObserved ||
            liveNode.Mixes.Keys.Intersect(OtherClientIds(state)).Count() != 4 ||
            liveNode.Audio.Keys.Intersect(OtherClientIds(state)).Count() != 4)
        {
            return false;
        }

        var local = state.Players.Single(player => player.IsLocal);
        return nodes.Where(node => !node.IsLive).All(node => node.PcmFrames.GetValueOrDefault(local.ClientId) >= 10);
    }

    private static IEnumerable<int> OtherClientIds(AmongUsState state) =>
        state.Players.Where(player => !player.IsLocal).Select(player => player.ClientId);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class TestNode(int processId, bool isLive)
    {
        public int ProcessId { get; } = processId;
        public bool IsLive { get; } = isLive;
        public VoiceServerProbe? Probe { get; set; }
        public Task? RunTask { get; set; }
        public AmongUsState? State { get; set; }
        public ConcurrentDictionary<int, PeerVoiceMix> Mixes { get; } = [];
        public ConcurrentDictionary<int, AudioTestResult> Audio { get; } = [];
        public ConcurrentDictionary<int, int> PcmFrames { get; } = [];
        public bool TalkingObserved { get; set; }
    }
}
