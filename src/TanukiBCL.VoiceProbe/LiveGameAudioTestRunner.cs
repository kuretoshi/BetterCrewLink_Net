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
                    LiveAudio = node.IsLive,
                    GameProcessId = node.ProcessId,
                    Duration = null
                };
                node.Probe = new VoiceServerProbe(options, node.IsLive ? $"LIVE{node.ProcessId}" : $"SIM{node.ProcessId}");
                node.Probe.GameStateApplied += state => node.State = state;
                node.Probe.PeerMixChanged += (clientId, mix) => node.Mixes[clientId] = mix;
                node.Probe.PeerAudioVerified += (clientId, result) => node.Audio[clientId] = result;
                node.RunTask = node.Probe.RunAsync(cancellation.Token);
                await node.Probe.Connected.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
                await Task.Delay(250, cancellation.Token);
            }

            var liveNode = nodes.Single(node => node.IsLive);
            while (!IsLivePathReady(liveNode))
            {
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

            Console.WriteLine(passed
                ? "[PASS] 4仮想クライアントのOpus音声が実音声視点の再生ミキサーまで到達しました。"
                : "[FAIL] 実音声視点の受信またはミックスに不一致があります。");
            return passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("[FAIL] 制限時間内に実音声視点の4方向受信を確認できませんでした。");
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

    private static bool IsLivePathReady(TestNode node) =>
        node.State is { Players.Count: 5 } state &&
        node.Mixes.Keys.Intersect(OtherClientIds(state)).Count() == 4 &&
        node.Audio.Keys.Intersect(OtherClientIds(state)).Count() == 4;

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
    }
}
