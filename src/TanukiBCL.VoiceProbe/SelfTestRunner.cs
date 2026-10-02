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
        var hostSettings = new LobbySettings { MaxDistance = 7.4d, ImpostorRadioEnabled = true };
        var settingsVerified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updatedSettings = hostSettings with { MaxDistance = 3.6d, Haunting = true };
        var updateVerified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.SetOwnLobbySettings(hostSettings);
        second.SetExpectedHostClientId(seed);
        second.LobbySettingsChanged += settings =>
        {
            if (settings == hostSettings)
            {
                settingsVerified.TrySetResult();
            }
            if (settings == updatedSettings)
            {
                updateVerified.TrySetResult();
            }
        };

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
                second.AudioVerified.WaitAsync(timeout, cancellation.Token),
                settingsVerified.Task.WaitAsync(timeout, cancellation.Token));

            first.SetOwnLobbySettings(updatedSettings);
            await updateVerified.Task.WaitAsync(timeout, cancellation.Token);

            Console.WriteLine("[PASS] Socket.IO、WebRTCデータチャネル、Opus音声トラック、ホストの3.2.7ロビー設定配信と変更反映を検証しました。");
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
