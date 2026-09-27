using System.Collections.Concurrent;
using System.Diagnostics;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal static class GameAudioSelfTestRunner
{
    public static Task<int> RunAsync(ProbeOptions baseOptions) => RunAsync(baseOptions, TestMode.Snapshot);

    public static Task<int> RunTransitionAsync(ProbeOptions baseOptions) => RunAsync(baseOptions, TestMode.Transition);

    public static Task<int> RunRecoveryAsync(ProbeOptions baseOptions) => RunAsync(baseOptions, TestMode.Recovery);

    public static Task<int> RunServerRecoveryAsync(ProbeOptions baseOptions) => RunAsync(baseOptions, TestMode.ServerRecovery);

    private static async Task<int> RunAsync(ProbeOptions baseOptions, TestMode mode)
    {
        var processes = Process.GetProcessesByName("Among Us")
            .OrderBy(process => process.Id)
            .ToArray();
        if (processes.Length != 5)
        {
            Console.Error.WriteLine($"[FAIL] Among Usは5プロセス必要です。検出数={processes.Length}");
            DisposeProcesses(processes);
            return 1;
        }

        var timeout = baseOptions.Duration ?? TimeSpan.FromSeconds(mode == TestMode.Transition ? 180 : 60);
        using var cancellation = new CancellationTokenSource(timeout);
        var nodes = processes.Select(process => new TestNode(process.Id)).ToArray();
        DisposeProcesses(processes);

        var testName = mode switch
        {
            TestMode.Transition => "状態遷移",
            TestMode.Recovery => "再参加復旧",
            TestMode.ServerRecovery => "サーバー再接続復旧",
            _ => "統合"
        };
        Console.WriteLine($"ゲーム音声{testName}テスト開始: pids={string.Join(',', nodes.Select(node => node.ProcessId))} timeout={timeout.TotalSeconds:0}s");
        try
        {
            foreach (var node in nodes)
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
                    LiveAudio = false,
                    GameProcessId = node.ProcessId,
                    Duration = null
                };
                node.Probe = new VoiceServerProbe(options, $"G{node.ProcessId}");
                node.Probe.GameStateApplied += state => node.State = state;
                node.Probe.PeerMixChanged += (clientId, mix) => node.Mixes[clientId] = mix;
                node.Probe.PeerAudioVerified += (clientId, result) => node.Audio[clientId] = result;
                node.RunTask = node.Probe.RunAsync(cancellation.Token);
                await node.Probe.Connected.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
                await Task.Delay(250, cancellation.Token);
            }

            if (mode == TestMode.Transition)
            {
                return await WaitForTransitionsAsync(nodes, cancellation.Token);
            }

            while (!cancellation.IsCancellationRequested && !IsComplete(nodes))
            {
                await Task.Delay(250, cancellation.Token);
            }

            if (mode is TestMode.Recovery or TestMode.ServerRecovery)
            {
                return await RunRecoveryStageAsync(nodes, mode, cancellation.Token);
            }

            var passed = Validate(nodes);
            Console.WriteLine(passed
                ? "[PASS] 5視点×4相手のWebRTC/Opus受信とゲーム状態別音声ミックスが一致しました。"
                : "[FAIL] 音声受信またはゲーム状態別ミックスに不一致があります。");
            return passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("[FAIL] 制限時間内に5クライアントの音声マトリクスを確認できませんでした。");
            PrintProgress(nodes);
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

    private static async Task<int> RunRecoveryStageAsync(
        IReadOnlyCollection<TestNode> nodes,
        TestMode mode,
        CancellationToken cancellationToken)
    {
        if (!Validate(nodes, printDetails: false))
        {
            Console.Error.WriteLine("[FAIL] 再参加前の音声メッシュが不正です。");
            return 1;
        }

        Console.WriteLine("[PASS] 再参加前の20方向音声メッシュを確認しました。");
        var target = nodes.OrderBy(node => node.ProcessId).First();
        var targetClientId = target.State!.Players.Single(player => player.IsLocal).ClientId;
        target.Audio.Clear();
        target.Mixes.Clear();
        foreach (var node in nodes.Where(node => node != target))
        {
            node.Audio.TryRemove(targetClientId, out _);
            node.Mixes.TryRemove(targetClientId, out _);
        }

        if (mode == TestMode.ServerRecovery)
        {
            Console.WriteLine($"[TEST] PID {target.ProcessId} / client {targetClientId}のサーバー接続を切断・再接続させます。");
            await target.Probe!.RestartServerConnectionAsync(cancellationToken);
        }
        else
        {
            Console.WriteLine($"[TEST] PID {target.ProcessId} / client {targetClientId}をロビーから退出・再参加させます。");
            await target.Probe!.RejoinCurrentGameLobbyAsync();
        }
        while (!IsComplete(nodes))
        {
            await Task.Delay(250, cancellationToken);
        }

        var passed = Validate(nodes, printDetails: false);
        var recoveryName = mode == TestMode.ServerRecovery ? "サーバー再接続" : "ロビー再参加";
        Console.WriteLine(passed
            ? $"[PASS] {recoveryName}後に対象8方向が復旧し、全20方向の音声メッシュへ戻りました。"
            : $"[FAIL] {recoveryName}後の音声メッシュに不一致があります。");
        return passed ? 0 : 1;
    }

    private static async Task<int> WaitForTransitionsAsync(
        IReadOnlyCollection<TestNode> nodes,
        CancellationToken cancellationToken)
    {
        var sequence = new[] { GameState.Tasks, GameState.Discussion, GameState.Tasks };
        for (var index = 0; index < sequence.Length; index++)
        {
            var expected = sequence[index];
            Console.WriteLine(index switch
            {
                0 => "[WAIT] まずTasks状態と20方向の音声接続を確認します。",
                1 => "[WAIT] 会議を開始してください。Discussionへの切り替えを待っています。",
                _ => "[WAIT] 会議を終了してください。Tasksへの復帰を待っています。"
            });

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsComplete(nodes) &&
                    nodes.All(node => node.State?.GameState == expected) &&
                    Validate(nodes, printDetails: false))
                {
                    Console.WriteLine($"[PASS] {expected}: 全20方向の音声ミックスが一致しました。");
                    break;
                }

                await Task.Delay(250, cancellationToken);
            }
        }

        Console.WriteLine("[PASS] WebRTC接続を維持したままTasks→Discussion→Tasksへ音声ルールが追従しました。");
        return 0;
    }

    private static bool IsComplete(IEnumerable<TestNode> nodes) => nodes.All(node =>
        node.State is { Players.Count: 5 } state &&
        node.Mixes.Keys.Intersect(OtherClientIds(state)).Count() == 4 &&
        node.Audio.Keys.Intersect(OtherClientIds(state)).Count() == 4);

    private static bool Validate(IReadOnlyCollection<TestNode> nodes, bool printDetails = true)
    {
        if (nodes.Any(node => node.State is null) ||
            nodes.Select(node => node.State!.LobbyCode).Distinct(StringComparer.Ordinal).Count() != 1 ||
            nodes.Select(node => node.State!.GameState).Distinct().Count() != 1)
        {
            if (printDetails)
            {
                PrintProgress(nodes);
            }
            return false;
        }

        var passed = true;
        foreach (var node in nodes)
        {
            var state = node.State!;
            var me = state.Players.Single(player => player.IsLocal);
            foreach (var other in state.Players.Where(player => !player.IsLocal))
            {
                var expected = SpatialVoicePolicy.Calculate(state, me, other, new SpatialVoiceSettings());
                var hasMix = node.Mixes.TryGetValue(other.ClientId, out var actual) && actual == expected;
                var hasAudio = node.Audio.TryGetValue(other.ClientId, out var audio) &&
                               audio.Frames >= 10 && audio.Rms > 0.01d;
                passed &= hasMix && hasAudio;
                if (printDetails)
                {
                    Console.WriteLine(
                        $"PID {node.ProcessId} client={me.ClientId} <- {other.ClientId}: " +
                        $"audio={(hasAudio ? "OK" : "NG")} mix={(hasMix ? "OK" : "NG")} " +
                        $"gain={actual?.Gain ?? 0:0.000} pan={actual?.Pan ?? 0:+0.00;-0.00;0.00} reason={actual?.Reason ?? "missing"}");
                }
            }
        }

        return passed;
    }

    private static IEnumerable<int> OtherClientIds(AmongUsState state) =>
        state.Players.Where(player => !player.IsLocal).Select(player => player.ClientId);

    private static void PrintProgress(IEnumerable<TestNode> nodes)
    {
        foreach (var node in nodes)
        {
            Console.WriteLine(
                $"PID {node.ProcessId}: state={node.State?.GameState.ToString() ?? "未取得"} " +
                $"mixes={node.Mixes.Count}/4 audio={node.Audio.Count}/4");
        }
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

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

    private sealed class TestNode(int processId)
    {
        public int ProcessId { get; } = processId;
        public VoiceServerProbe? Probe { get; set; }
        public Task? RunTask { get; set; }
        public AmongUsState? State { get; set; }
        public ConcurrentDictionary<int, PeerVoiceMix> Mixes { get; } = [];
        public ConcurrentDictionary<int, AudioTestResult> Audio { get; } = [];
    }

    private enum TestMode
    {
        Snapshot,
        Transition,
        Recovery,
        ServerRecovery
    }
}
