using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

public partial class App : Application
{
    private SupportLog? supportLog;
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
            e.Args.Contains("--input-processing-self-test") || e.Args.Contains("--inquiry-self-test") ||
            e.Args.Contains("--inquiry-preview") ||
            e.Args.Contains("--support-log-self-test") || e.Args.Contains("--update-catalog-self-test") ||
            e.Args.Contains("--update-catalog-live-test") ||
            e.Args.Contains("--update-package-live-test") ||
            e.Args.Contains("--update-package-self-test") || e.Args.Contains("--update-package-file-test") ||
            e.Args.Contains("--registration-self-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Contains("--nos-avatar-self-test")) AvatarImageFactory.VerifyNosColors();
                if (e.Args.Contains("--voice-view-self-test"))
                {
                    TanukiBCL.Client.MainWindow.VerifyAutomaticProcessSelection();
                    TanukiBCL.Client.MainWindow.VerifyPeerConnectionRecovery();
                    RecentPcmLevelTracker.Verify();
                    VoiceView.VerifyNameLayout();
                    VoiceView.VerifyPeerQualityFallback();
                    VoiceView.VerifyDuplicateClientAvatars();
                    VoiceView.VerifyRemoteDeathPresentation();
                    GameLauncher.Verify();
                    VoiceView.VerifyLaunchControls();
                    CustomPlatformWindow.Verify();
                }
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
                    DeveloperDebugAuth.VerifyParity();
                    DebugInfoWindow.VerifyUi();
                    PublicLobbyBrowserWindow.VerifyUi();
                    SettingsWindow.VerifyModControls();
                    SettingsWindow.VerifyLocalization();
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
                if (e.Args.Contains("--inquiry-self-test"))
                {
                    InquiryWindow.VerifyForm();
                    Task.Run(InquirySubmission.VerifyAsync).GetAwaiter().GetResult();
                }
                var inquiryPreviewIndex = Array.IndexOf(e.Args, "--inquiry-preview");
                if (inquiryPreviewIndex >= 0)
                {
                    if (inquiryPreviewIndex + 1 >= e.Args.Length)
                        throw new ArgumentException("--inquiry-preview needs a PNG path");
                    InquiryWindow.RenderPreview(e.Args[inquiryPreviewIndex + 1]);
                }
                if (e.Args.Contains("--support-log-self-test")) SupportLog.Verify();
                if (e.Args.Contains("--update-catalog-self-test"))
                    Task.Run(UpdateCatalog.VerifyAsync).GetAwaiter().GetResult();
                var liveCatalogIndex = Array.IndexOf(e.Args, "--update-catalog-live-test");
                if (liveCatalogIndex >= 0)
                {
                    if (liveCatalogIndex + 2 >= e.Args.Length)
                        throw new ArgumentException("--update-catalog-live-test needs a current version and expected tag");
                    Task.Run(() => UpdateCatalog.VerifyLiveAsync(
                        e.Args[liveCatalogIndex + 1], e.Args[liveCatalogIndex + 2])).GetAwaiter().GetResult();
                }
                if (e.Args.Contains("--update-package-self-test"))
                    Task.Run(UpdatePackage.VerifyAsync).GetAwaiter().GetResult();
                if (e.Args.Contains("--registration-self-test")) InstalledAppRegistration.Verify();
                var livePackageIndex = Array.IndexOf(e.Args, "--update-package-live-test");
                if (livePackageIndex >= 0)
                {
                    if (livePackageIndex + 3 >= e.Args.Length)
                        throw new ArgumentException("--update-package-live-test needs a current version, expected tag and staging directory");
                    Task.Run(() => UpdatePackage.VerifyLiveAsync(
                        e.Args[livePackageIndex + 1], e.Args[livePackageIndex + 2],
                        e.Args[livePackageIndex + 3])).GetAwaiter().GetResult();
                }
                var packageFileIndex = Array.IndexOf(e.Args, "--update-package-file-test");
                if (packageFileIndex >= 0)
                {
                    if (packageFileIndex + 1 >= e.Args.Length)
                        throw new ArgumentException("--update-package-file-test needs a ZIP path");
                    Task.Run(() => UpdatePackage.VerifyPublishedArchiveAsync(e.Args[packageFileIndex + 1]))
                        .GetAwaiter().GetResult();
                }
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
        try { supportLog = SupportLog.Install(e.Args); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Support log could not be opened: {error.Message}");
        }
        try { InstalledAppRegistration.Refresh(UpdateCatalog.CurrentVersion, AppContext.BaseDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            Trace.TraceWarning($"Installed app registration could not be refreshed: {error.Message}");
        }
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        supportLog?.Dispose();
        base.OnExit(e);
    }
}
