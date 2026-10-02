namespace TanukiBCL.VoiceProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var options = ProbeOptions.Parse(args);
            if (options.PolicySelfTest)
            {
                return SpatialVoicePolicySelfTest.Run();
            }

            if (options.VadSelfTest)
            {
                return TanukiVoiceActivityDetectorSelfTest.Run();
            }
            if (options.ListAudioDevices)
            {
                AudioDeviceSession.PrintDevices();
                return 0;
            }

            if (options.ScanGame)
            {
                return await GameProcessScanner.RunAsync(
                    TimeSpan.FromSeconds(20),
                    new GameScanExpectation(
                        options.ExpectedGameState,
                        options.ExpectedAlive,
                        options.ExpectedDead,
                        options.ExpectedImpostors,
                        options.ExpectNearby));
            }

            if (options.GameAudioSelfTest)
            {
                return await GameAudioSelfTestRunner.RunAsync(options);
            }

            if (options.GameAudioTransitionTest)
            {
                return await GameAudioSelfTestRunner.RunTransitionAsync(options);
            }

            if (options.LiveGameAudioTest)
            {
                return await LiveGameAudioTestRunner.RunAsync(options);
            }

            if (options.GameAudioRecoveryTest)
            {
                return await GameAudioSelfTestRunner.RunRecoveryAsync(options);
            }

            if (options.GameAudioServerRecoveryTest)
            {
                return await GameAudioSelfTestRunner.RunServerRecoveryAsync(options);
            }

            if (options.SelfTest)
            {
                return await SelfTestRunner.RunAsync(options);
            }

            if (options.TanukiInteropTest)
            {
                return await TanukiInteropTestRunner.RunAsync(options);
            }

            if (options.LiveAudio && options.LobbyCode is null && options.GameProcessId is null)
            {
                throw new ArgumentException("--live-audio には --lobby または --game-process-id の指定が必要です。");
            }

            using var cancellation = new CancellationTokenSource();
            if (options.Duration is { } duration)
            {
                cancellation.CancelAfter(duration);
            }

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            await using var probe = new VoiceServerProbe(options);
            try
            {
                await probe.RunAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Console.WriteLine("疎通確認を終了します。");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"疎通確認に失敗しました: {exception.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            TanukiBCL v3.2.7 互換性・ボイスサーバー疎通確認

            dotnet run --project src/TanukiBCL.VoiceProbe -- [options]

              --server <url>       接続先（既定: https://bettercrewl.ink）
              --lobby <code>       指定時のみロビーへ参加
              --player-id <number> プレイヤーID（既定: 0）
              --client-id <number> クライアントID（既定: 0）
              --host               ホストとして参加
              --seconds <number>   指定秒数後に自動終了
              --self-test         2クライアントでP2Pデータチャネルを自動検証
              --policy-self-test  3.2.7の音声ポリシーをローカルで検証
              --vad-self-test     3.2.7の周波数帯VADを合成音で検証
              --tanuki-interop-test 起動中のTanukiBCLとのWebRTC・Opus相互接続を検証
              --expected-peer-client-id <id> 相互接続テストの対象client IDを固定
              --live-audio        マイク入力を送信し、受信音声をスピーカー再生
              --list-audio-devices 入出力デバイスの番号と名前を表示
              --scan-game         起動中の全Among Usプロセスを読み取り検証
              --game-audio-self-test 5プロセスと仮想音声クライアントの統合検証
              --game-audio-transition-test Tasks→会議→Tasksの連続追従検証
              --live-game-audio-test 1視点を実音声、残り4視点を仮想音声で検証
              --game-audio-recovery-test ロビー退出・再参加後の音声復旧を検証
              --game-audio-server-recovery-test サーバー再接続後の音声復旧を検証
              --expected-game-state <state> 期待するTasks / Discussion等
              --expected-alive <n> 期待する生存者数
              --expected-dead <n> 期待する死亡者数
              --expected-impostors <n> 期待するインポスター数
              --expect-nearby     生存者同士が距離内であることも検証
              --game-process-id <pid> ゲーム状態を追跡してロビー・音量を自動更新
              --auto-radio-tone   ラジオON時に440Hzの検証音を送信（実マイク不要）
              --input-device <n>  マイク番号（既定: 0）
              --output-device <n> スピーカー番号（既定: 0）
              --help, -h           このヘルプを表示
            """);
    }
}
