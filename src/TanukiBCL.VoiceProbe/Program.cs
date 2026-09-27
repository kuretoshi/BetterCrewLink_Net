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
            if (options.SelfTest)
            {
                return await SelfTestRunner.RunAsync(options);
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
            TanukiBCL v3.2.5 ボイスサーバー疎通確認

            dotnet run --project src/TanukiBCL.VoiceProbe -- [options]

              --server <url>       接続先（既定: https://bettercrewl.ink）
              --lobby <code>       指定時のみロビーへ参加
              --player-id <number> プレイヤーID（既定: 0）
              --client-id <number> クライアントID（既定: 0）
              --host               ホストとして参加
              --seconds <number>   指定秒数後に自動終了
              --self-test         2クライアントでP2Pデータチャネルを自動検証
              --help, -h           このヘルプを表示
            """);
    }
}
