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
            if (args.Contains("--dtls-trace"))
                SIPSorcery.LogFactory.Set(new DtlsTraceLoggerFactory());

            if (args.Contains("--nos-palette-self-test"))
                return GameMemory.NosPaletteSelfTest.Run();
            if (args.Contains("--nos-snapshot-self-test"))
                return GameMemory.NosSnapshotSelfTest.Run();
            if (args.Contains("--game-scan-self-test"))
            {
                return GameProcessScannerSelfTest.Run();
            }
            if (args.Contains("--audio-processing-self-test"))
            {
                return MicrophoneProcessorSelfTest.Run();
            }
            if (args.Contains("--mobile-host-self-test"))
            {
                return MobileHostBeaconSelfTest.Run();
            }
            if (args.Contains("--server-reconnect-self-test"))
            {
                return await ServerReconnectSelfTest.RunAsync();
            }
            if (args.Contains("--public-lobby-live-test"))
                return await PublicLobbyLiveSelfTest.RunAsync(
                    ProbeOptions.Parse(args.Where(arg => arg != "--public-lobby-live-test").ToArray()));
            if (args.Contains("--nos-palette"))
                return await NosSnapshotDiagnostic.RunAsync(
                    ProbeOptions.Parse(args.Where(arg => arg != "--nos-palette").ToArray()).GameProcessId, palette: true);
            if (args.Contains("--speaker-loopback-test"))
                return await SpeakerLoopbackTestRunner.RunAsync(args);
            if (args.Contains("--microphone-tone-test"))
                return await MicrophoneToneTestRunner.RunAsync(args);
            if (args.Contains("--speaker-loopback-self-test"))
                return SpeakerLoopbackTestRunner.RunSelfTest();
            if (args.Contains("--peer-pcm-tone-test"))
                return await PeerPcmToneTestRunner.RunAsync(ProbeOptions.Parse(
                    args.Where(argument => argument != "--peer-pcm-tone-test").ToArray()));
            var options = ProbeOptions.Parse(args);
            if (args.Contains("--radio-enabled-test") &&
                (!options.AutoRadioTone || !options.IsHost || options.GameProcessId is null || options.Duration is null))
                throw new ArgumentException("--radio-enabled-test requires --auto-radio-tone --host --game-process-id and --seconds.");
            if (args.Contains("--play-test-tone"))
                return await TonePlaybackTestRunner.RunAsync(options, args.Contains("--output-device"),
                    args.Contains("--vad-tone"), args.Contains("--speech-like"));
            if (args.Contains("--capture-device-self-test"))
                return await AudioCaptureDeviceSelfTest.RunAsync(options);
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
                        options.ExpectNearby,
                        options.ExpectedPlayers));
            }

            if (options.NosSnapshot)
            {
                return await NosSnapshotDiagnostic.RunAsync(options.GameProcessId);
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

            if (options.SelfTest || args.Contains("--quality-self-test") || args.Contains("--mixed-nat-self-test"))
            {
                return await SelfTestRunner.RunAsync(options,
                    expectPeerQuality: args.Contains("--quality-self-test"),
                    mixedNat: args.Contains("--mixed-nat-self-test"),
                    failOnRecovery: args.Contains("--fail-on-recovery"));
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
            if (args.Contains("--radio-enabled-test"))
                probe.SetOwnLobbySettings(new LobbySettings { ImpostorRadioEnabled = true });
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
            TanukiBCL v3.2.8 互換性・ボイスサーバー疎通確認

            dotnet run --project src/TanukiBCL.VoiceProbe -- [options]

              --server <url>       接続先（既定: https://bettercrewl.ink）
              --lobby <code>       指定時のみロビーへ参加
              --player-id <number> プレイヤーID（既定: 0）
              --client-id <number> クライアントID（既定: 0）
              --host               ホストとして参加
              --seconds <number>   指定秒数後に自動終了
              --self-test         2クライアントでP2Pデータチャネルを自動検証
              --mixed-nat-self-test  片側だけNAT修正ONでP2P接続を検証（--nat-fixでON側を逆転）
              --fail-on-recovery  P2Pセルフテストで自動再接続が発生したら失敗にする
              --public-lobby-live-test  公開ロビー購読と無効IDのコード照会を実サーバーで検証
              --nat-fix          TURNリレーのみを使用
              --turn-tcp         CLI試験中だけNAT修正TURNへの接続をTCPに変更
              --active-sctp-answer  CLI比較試験中だけanswer側もデータチャネルを作る（旧方式）
              --dtls-trace       CLI試験中のDTLSイベントとヘッダー順序を匿名表示
              --policy-self-test  3.2.7の音声ポリシーをローカルで検証
              --vad-self-test     3.2.7の周波数帯VADとマイク操作モードを検証
              --audio-processing-self-test  エコーキャンセル・ノイズ抑制・自動ゲイン処理を検証
              --capture-device-self-test  選択マイクの既定/強制48 kHz録音を各750msローカル検証
              --mobile-host-self-test  3.2.7モバイルホスト通知を検証
              --server-reconnect-self-test  ローカルサーバーの切断・再接続とロビー再参加を検証
              --tanuki-interop-test 起動中のTanukiBCLとのWebRTC・Opus相互接続を検証
              --expected-peer-client-id <id> 相互接続テストの対象client IDを固定
              --expected-toh-role <name> TOHホストから届く役職名を相互接続テストで検証
              --live-audio        マイク入力を送信し、受信音声をスピーカー再生
              --list-audio-devices 入出力デバイスの番号と名前を表示
              --play-test-tone --output-device <n> --seconds <1-15> [--vad-tone]  選択した出力先へ440Hz検証音を流す。--vad-toneは150Hz VAD用成分を追加（保存なし）
              --play-test-tone --output-device <n> --seconds <1-15> --speech-like  音量と基本周波数が変動する440Hz付き診断音を流す（実音声ではない）
              --speaker-loopback-test [--speaker-name name] [--seconds n]  出力デバイスの440Hz成分だけを測定（音声は保存しない）
              --microphone-tone-test [--microphone-name name] [--seconds n]  入力デバイスの440Hz成分だけを測定（音声は保存しない）
              --peer-pcm-tone-test --game-process-id PID --expected-peer-client-id ID --seconds N  相手から復号したPCMの440Hz成分を測定（保存なし）
              --speaker-loopback-self-test 440Hz検出と別周波数の除外を合成音で検証
              --scan-game         起動中の全Among Usプロセスを読み取り検証
              --expected-players  検証するプロセス数・ロビー人数（既定5）
              --game-scan-self-test ゲーム読取検証コマンドの回帰テスト
              --nos-palette       NoSロビー色を取得（--game-process-id 必須）
              --nos-palette-self-test NoS色パレットの読取回帰テスト
              --nos-snapshot-self-test 64bit NoSプレイヤー・ラジオ読取の回帰テスト
              --nos-snapshot --game-process-id PID  NoS公開スナップショットを実機検証
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
              --radio-enabled-test  --auto-radio-toneと併用し、CLIホストのラジオを試験中だけ有効化
              --input-device <n>  マイク番号（既定: 0）
              --output-device <n> スピーカー番号（既定: 0）
              --old-sample-debug マイクに48 kHz入力を要求（既定はデバイスの既定レート）
              --help, -h           このヘルプを表示
            """);
    }
}
