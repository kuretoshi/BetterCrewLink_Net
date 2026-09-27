namespace TanukiBCL.VoiceProbe;

internal static class SelfTestRunner
{
    public static async Task<int> RunAsync(ProbeOptions baseOptions)
    {
        var lobby = baseOptions.LobbyCode ?? CreateLobbyCode();
        var timeout = baseOptions.Duration ?? TimeSpan.FromSeconds(30);
        var seed = Random.Shared.Next(100_000, 900_000);

        var firstOptions = baseOptions with
        {
            LobbyCode = lobby,
            PlayerId = 0,
            ClientId = seed,
            IsHost = true,
            Duration = null,
            SelfTest = false,
            LiveAudio = false,
            GameProcessId = null
        };
        var secondOptions = baseOptions with
        {
            LobbyCode = lobby,
            PlayerId = 1,
            ClientId = seed + 1,
            IsHost = false,
            Duration = null,
            SelfTest = false,
            LiveAudio = false,
            GameProcessId = null
        };

        Console.WriteLine($"P2Pセルフテスト開始: lobby={lobby} timeout={timeout.TotalSeconds:0}s");
        using var cancellation = new CancellationTokenSource(timeout);
        await using var first = new VoiceServerProbe(firstOptions, "A");
        await using var second = new VoiceServerProbe(secondOptions, "B");

        try
        {
            var firstRun = first.RunAsync(cancellation.Token);
            await first.Connected.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);

            // 先行クライアントがロビーに登録されてから2台目を接続する。
            await Task.Delay(500, cancellation.Token);
            var secondRun = second.RunAsync(cancellation.Token);

            await Task.WhenAll(
                first.PeerVerified.WaitAsync(timeout, cancellation.Token),
                second.PeerVerified.WaitAsync(timeout, cancellation.Token),
                second.AudioVerified.WaitAsync(timeout, cancellation.Token));

            Console.WriteLine("[PASS] Socket.IO、WebRTCデータチャネル、Opus音声トラックの検証に成功しました。");
            cancellation.Cancel();
            await IgnoreCancellationAsync(firstRun);
            await IgnoreCancellationAsync(secondRun);
            return 0;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("[FAIL] 制限時間内にP2P接続を確認できませんでした。");
            return 1;
        }
    }

    private static string CreateLobbyCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));
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
}
