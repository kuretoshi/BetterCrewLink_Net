using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace BetterCrewLinkKai.DotNet;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly object LogLock = new();

    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterCrewLinkKai.DotNet",
        "logs");

    private static string CrashLogPath => Path.Combine(LogDirectory, "crash.log");

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog("AppDomain.CurrentDomain.UnhandledException", args.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        WriteLog("Application starting.");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WriteLog($"Application exiting. code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Application.DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"アプリでエラーが発生しました。\n\nログ: {CrashLogPath}\n\n{e.Exception.Message}",
            "BetterCrewLinkKai.net crash",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void WriteCrashLog(string source, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine("==== Crash ====");
        builder.AppendLine($"Time: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($".NET: {Environment.Version}");
        builder.AppendLine($"Process: {Environment.ProcessPath}");
        builder.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        builder.AppendLine(exception?.ToString() ?? "No exception object.");
        WriteLog(builder.ToString());
    }

    private static void WriteLog(string message)
    {
        lock (LogLock)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                CrashLogPath,
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
    }
}
