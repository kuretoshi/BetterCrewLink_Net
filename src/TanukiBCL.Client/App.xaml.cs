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
        System.Windows.Media.RenderOptions.ProcessRenderMode = ClientSettingsStore.Load().HardwareAcceleration
            ? System.Windows.Interop.RenderMode.Default : System.Windows.Interop.RenderMode.SoftwareOnly;
        if (e.Args.Contains("--cosmetics-self-test") || e.Args.Contains("--voice-view-self-test") || e.Args.Contains("--nos-avatar-self-test") || e.Args.Contains("--overlay-self-test") || e.Args.Contains("--settings-self-test") ||
            e.Args.Contains("--session-lifecycle-self-test") ||
            e.Args.Contains("--settings-application-self-test") || e.Args.Contains("--settings-transaction-self-test") ||
            e.Args.Contains("--audio-preview-self-test") ||
            e.Args.Contains("--input-processing-self-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Contains("--nos-avatar-self-test")) AvatarImageFactory.VerifyNosColors();
                if (e.Args.Contains("--voice-view-self-test")) VoiceView.VerifyNameLayout();
                if (e.Args.Contains("--cosmetics-self-test"))
                {
                    CosmeticCatalog.Verify();
                    SnrCosmeticCatalog.Verify();
                    SnrLocalCosmetics.Verify();
                    PlayerAvatar.VerifyCosmeticLayers();
                }
                if (e.Args.Contains("--cosmetics-self-test") && e.Args.Contains("--download-cosmetics-catalog"))
                {
                    Task.Run(CosmeticCatalog.VerifyDownloadAsync).GetAwaiter().GetResult();
                    Task.Run(SnrCosmeticCatalog.VerifyDownloadAsync).GetAwaiter().GetResult();
                }
                if (e.Args.Contains("--overlay-self-test")) OverlayWindow.VerifyRender();
                if (e.Args.Contains("--settings-self-test"))
                {
                    SettingsWindow.VerifyModControls();
                    var previewIndex = Array.IndexOf(e.Args, "--server-dialog-preview");
                    if (previewIndex >= 0 && previewIndex + 1 < e.Args.Length)
                        SettingsWindow.RenderServerDialogPreview(e.Args[previewIndex + 1]);
                }
                if (e.Args.Contains("--session-lifecycle-self-test")) ClientSessionLifecycleSelfTest.Run();
                if (e.Args.Contains("--settings-application-self-test")) ClientSettingsApplicationSelfTest.Run();
                if (e.Args.Contains("--settings-transaction-self-test") &&
                    ClientSettingsTransactionSelfTest.Run(e.Args.Contains("--settings-transaction-failure-exit")) != 0)
                    throw new InvalidOperationException("Settings transaction self-test failed.");
                if (e.Args.Contains("--audio-preview-self-test")) AudioPreviewSelfTest.VerifyDevices();
                if (e.Args.Contains("--input-processing-self-test") && MicrophoneProcessorSelfTest.Run() != 0)
                    throw new InvalidOperationException("Input processing self-test failed.");
                if (e.Args.Contains("--self-test-failure-exit"))
                    throw new InvalidOperationException("Deliberate self-test exit-code verification.");
                Shutdown(0);
            }
            catch (Exception error)
            {
                Trace.TraceError($"Client self-test failed: {error}");
                Console.Error.WriteLine($"Client self-test failed: {error}");
                Shutdown(1);
            }
            return;
        }
        new MainWindow().Show();
    }
}
