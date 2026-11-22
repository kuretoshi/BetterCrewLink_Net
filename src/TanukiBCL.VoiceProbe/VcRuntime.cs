namespace TanukiBCL.VoiceProbe;

public static class VcRuntime
{
    public const string DownloadPage = "https://learn.microsoft.com/ja-jp/cpp/windows/latest-supported-vc-redist";
    public const string ModelInstaller = "https://github.com/kuretoshi/TanukiBCL/releases/download/v3.2.69/TanukiBCL-SharedSpeech-Setup-2.0.2.1.exe";
    private static readonly string[] Required = ["msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
    private static readonly object Gate = new();
    private static long nextCheck;
    private static bool installed;
    internal static bool HasFiles(string[] directories, Func<string, bool> isFile) =>
        Required.All(dll => directories.Any(directory => isFile(Path.Combine(directory, dll))));
    public static string SetupUrl(bool runtimeInstalled) => runtimeInstalled ? ModelInstaller : DownloadPage;
    public static bool Installed(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && Environment.TickCount64 < nextCheck) return installed;
            string? windows = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetEnvironmentVariable("WINDIR");
            string system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            string local = Path.Combine(LocalStoneSpeech.Root, "runtime");
            // Check files without loading a native library or recognition model.
            installed = OperatingSystem.IsWindows() && HasFiles(windows == null ? [local] : [local, Path.Combine(windows, system)], File.Exists);
            nextCheck = Environment.TickCount64 + 5000;
            return installed;
        }
    }
}
