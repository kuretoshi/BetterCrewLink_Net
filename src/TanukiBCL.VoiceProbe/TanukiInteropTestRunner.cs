namespace TanukiBCL.VoiceProbe;

internal static class TanukiInteropTestRunner
{
    public static async Task<int> RunAsync(ProbeOptions baseOptions)
    {
        if (baseOptions.GameProcessId is null)
        {
            throw new ArgumentException("--tanuki-interop-test には --game-process-id が必要です。");
        }
        if (baseOptions.ExpectedTohRole is { } requestedRole &&
            string.IsNullOrWhiteSpace(requestedRole))
        {
            throw new ArgumentException("--expected-toh-role には役職名が必要です。");
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
        var dataReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toneSent = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcmReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tohRoleReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        long connectedAtMs = -1;
        long dataReadyAtMs = -1;
        bool IsExpectedPeer(int clientId) => options.ExpectedPeerClientId is not int expected || clientId == expected;

        using var cancellation = new CancellationTokenSource(timeout);
        await using var probe = new VoiceServerProbe(options, "interop");
        probe.PeerConnectionStatusChanged += (clientId, state) =>
        {
            if (IsExpectedPeer(clientId) &&
                (state.Equals("connected", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("data-ready", StringComparison.OrdinalIgnoreCase)))
            {
                Interlocked.CompareExchange(ref connectedAtMs, startedAt.ElapsedMilliseconds, -1);
                connected.TrySetResult(clientId);
            }
            if (IsExpectedPeer(clientId) && state.Equals("data-ready", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.CompareExchange(ref dataReadyAtMs, startedAt.ElapsedMilliseconds, -1);
                dataReady.TrySetResult(clientId);
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
        probe.TohRoleReportReceived += (clientId, role) =>
        {
            if (IsExpectedPeer(clientId) && role?.RoleName is { } name)
                tohRoleReceived.TrySetResult(name);
        };

        Console.WriteLine($"TanukiBCL相互接続テスト開始 pid={options.GameProcessId} timeout={timeout.TotalSeconds:0}s");
        var run = probe.RunAsync(cancellation.Token);
        try
        {
            var connectedClient = await connected.Task.WaitAsync(timeout, cancellation.Token);
            var toneClient = await toneSent.Task.WaitAsync(timeout, cancellation.Token);
            var dataClient = await dataReady.Task.WaitAsync(timeout, cancellation.Token);
            if (connectedClient != toneClient || connectedClient != dataClient)
            {
                Console.Error.WriteLine($"[FAIL] 接続先 {connectedClient}、Opus送信先 {toneClient}、データチャネル {dataClient} が異なります。");
                return 1;
            }

            if (options.ExpectedTohRole is { } expectedRole)
            {
                string receivedRole;
                try
                {
                    receivedRole = await tohRoleReceived.Task.WaitAsync(timeout, cancellation.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
                {
                    Console.Error.WriteLine($"[FAIL] TOH役職通知を受信できませんでした。期待={expectedRole}");
                    return 1;
                }
                if (!string.Equals(receivedRole, expectedRole, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"[FAIL] TOH役職が異なります。期待={expectedRole} 受信={receivedRole}");
                    return 1;
                }
                Console.WriteLine($"[PASS] TOHホストから役職通知を受信しました。role={receivedRole}");
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

            var dataDelayMs = dataReadyAtMs - connectedAtMs;
            Console.WriteLine($"[PASS] 起動中のTanukiBCLと接続しました。peerClient={connectedClient} " +
                $"opus送信={toneClient} opus受信={(pcmClient == connectedClient ? connectedClient.ToString() : "無音のため未検出")} " +
                $"dataReady={dataClient} dataDelay={dataDelayMs}ms");
            return 0;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("[FAIL] 起動中のTanukiBCLとのWebRTC接続、Opus送信、またはデータチャネル確立を確認できませんでした。");
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
