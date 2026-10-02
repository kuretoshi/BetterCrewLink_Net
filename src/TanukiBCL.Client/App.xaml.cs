using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

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
    }
}
