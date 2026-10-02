using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

public partial class App : Application
{
    // Give this process a distinct shell identity before WPF creates a window.
    // It must not be grouped with the official Electron release during interop tests.
    private const string AppUserModelId = "TanukiBCL.Net.Client";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (result < 0)
        {
            Trace.TraceWarning($"Could not set TanukiBCL AppUserModelID: 0x{result:X8}");
        }
        base.OnStartup(e);
        if (e.Args.Contains("--overlay-self-test") || e.Args.Contains("--settings-self-test") ||
            e.Args.Contains("--audio-preview-self-test") ||
            e.Args.Contains("--input-processing-self-test"))
        {
            try
            {
                if (e.Args.Contains("--overlay-self-test")) OverlayWindow.VerifyRender();
                if (e.Args.Contains("--settings-self-test")) SettingsWindow.VerifyModControls();
                if (e.Args.Contains("--audio-preview-self-test")) AudioPreviewSelfTest.VerifyDevices();
                if (e.Args.Contains("--input-processing-self-test") && MicrophoneProcessorSelfTest.Run() != 0)
                    throw new InvalidOperationException("Input processing self-test failed.");
                Shutdown(0);
            }
            catch (Exception error)
            {
                Trace.TraceError($"Client self-test failed: {error}");
                Shutdown(1);
            }
            return;
        }
        new MainWindow().Show();
    }
}
