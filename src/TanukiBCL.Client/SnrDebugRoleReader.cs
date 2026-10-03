using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TanukiBCL.Client;

internal static class SnrDebugRoleReader
{
    internal static async Task<string> ReadAsync(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        var helper = FindHelper() ?? throw new FileNotFoundException("SNR役職読み取りツールが見つかりません");
        using var game = Process.GetProcessById(processId);
        if (game.ProcessName != "Among Us") throw new InvalidOperationException("Among Usプロセスを選択してください");
        var started = game.StartTime;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        if (!process.Start()) throw new InvalidOperationException("SNR役職読み取りツールを起動できません");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            using var result = JsonDocument.Parse(output);
            if (!result.RootElement.TryGetProperty("status", out var status) ||
                status.GetString() != "ok" || process.ExitCode != 0 ||
                !result.RootElement.TryGetProperty("pid", out var pid) || pid.GetInt32() != processId)
            {
                var message = result.RootElement.TryGetProperty("message", out var detail)
                    ? detail.GetString() : error.Trim();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                    ? "SNR役職の取得に失敗しました" : message);
            }
            using var current = Process.GetProcessById(processId);
            if (current.StartTime != started)
                throw new InvalidOperationException("取得中にAmong Usプロセスが切り替わりました");
            return output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("SNR役職読み取りツールが時間内に応答しませんでした");
        }
    }

    private static string? FindHelper()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "RoleReaders", "SnrRoleReader.exe");
        if (File.Exists(packaged)) return packaged;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var development = Path.Combine(directory.FullName, "tools", "SnrRoleReader", "bin",
                "Release", "net8.0", "win-x64", "SnrRoleReader.exe");
            if (File.Exists(development)) return development;
        }
        return null;
    }
}
