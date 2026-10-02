namespace TanukiBCL.VoiceProbe;

internal static class TanukiInteropTestRunner
{
    public static async Task<int> RunAsync(ProbeOptions baseOptions)
    {
        if (baseOptions.GameProcessId is null)
        {
            throw new ArgumentException("--tanuki-interop-test には --game-process-id が必要です。");
        }

        var timeout = baseOptions.Duration ?? TimeSpan.FromSeconds(30);
        var options = baseOptions with
        {
            Duration = null,
            SelfTest = false,
            TanukiInteropTest = false,
            LiveAudio = false
        };
        var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toneSent = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcmReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool IsExpectedPeer(int clientId) => options.ExpectedPeerClientId is not int expected || clientId == expected;

        using var cancellation = new CancellationTokenSource(timeout);
        await using var probe = new VoiceServerProbe(options, "interop");
        probe.PeerConnectionStatusChanged += (clientId, state) =>
        {
            if (IsExpectedPeer(clientId) &&
                (state.Equals("connected", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("data-ready", StringComparison.OrdinalIgnoreCase)))
            {
                connected.TrySetResult(clientId);
            }
        };
        probe.PeerTestToneSent += clientId =>
        {
            if (IsExpectedPeer(clientId)) toneSent.TrySetResult(clientId);
        };
        probe.PeerPcmReceived += (clientId, _) =>
        {
            if (IsExpectedPeer(clientId)) pcmReceived.TrySetResult(clientId);
        };

        Console.WriteLine($"TanukiBCL相互接続テスト開始 pid={options.GameProcessId} timeout={timeout.TotalSeconds:0}s");
        var run = probe.RunAsync(cancellation.Token);
        try
        {
            var connectedClient = await connected.Task.WaitAsync(timeout, cancellation.Token);
            var toneClient = await toneSent.Task.WaitAsync(timeout, cancellation.Token);
            if (connectedClient != toneClient)
            {
                Console.Error.WriteLine($"[FAIL] 接続先 {connectedClient} とOpus送信先 {toneClient} が異なります。");
                return 1;
            }

            int? pcmClient = null;
            try
            {
                pcmClient = await pcmReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation.Token);
            }
            catch (TimeoutException)
            {
                // TanukiBCL側が無音時に音声送信を抑止していても、接続と送信確認は有効。
            }

            Console.WriteLine($"[PASS] 起動中のTanukiBCLと接続しました。peerClient={connectedClient} opus送信={toneClient} opus受信={(pcmClient == connectedClient ? connectedClient.ToString() : "無音のため未検出")}");
            return 0;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("[FAIL] 起動中のTanukiBCLとのWebRTC接続またはOpus送信を確認できませんでした。");
            return 1;
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
