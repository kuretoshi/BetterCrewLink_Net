using System.IO;
using Microsoft.Win32;

namespace TanukiBCL.Client;

internal static class InstalledAppRegistration
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TanukiBCL.Net";

    internal static void Refresh(string version, string installDirectory) =>
        RefreshCore(Registry.CurrentUser, UninstallKey, version, installDirectory);

    private static bool RefreshCore(RegistryKey root, string keyPath, string version, string installDirectory)
    {
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key is null || key.GetValue("InstallLocation") is not string registeredDirectory ||
            key.GetValue("UninstallString") is not string registeredUninstaller)
            return false;

        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredDirectory));
        var uninstaller = Path.Combine(installed, "Uninstall.exe");
        if (!string.Equals(installed, registered, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(registeredUninstaller, $"\"{uninstaller}\"", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(uninstaller))
            return false;

        var displayName = $"TanukiBCL.Net {version}";
        var changed = false;
        if (key.GetValue("DisplayVersion") as string != version)
        {
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            changed = true;
        }
        if (key.GetValue("DisplayName") as string != displayName)
        {
            key.SetValue("DisplayName", displayName, RegistryValueKind.String);
            changed = true;
        }
        return changed;
    }

    internal static void Verify()
    {
        var id = Guid.NewGuid().ToString("N");
        var keyPath = $@"Software\TanukiBCL.Net-RegistrationSelfTest-{id}";
        var install = Path.Combine(Path.GetTempPath(), $"tanukibcl-registration-{id}");
        Directory.CreateDirectory(install);
        var uninstaller = Path.Combine(install, "Uninstall.exe");
        try
        {
            File.WriteAllBytes(uninstaller, [0]);
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("InstallLocation", install, RegistryValueKind.String);
                key.SetValue("UninstallString", $"\"{uninstaller}\"", RegistryValueKind.String);
                key.SetValue("DisplayVersion", "3.2.8-net-beta.1", RegistryValueKind.String);
            }
            if (!RefreshCore(Registry.CurrentUser, keyPath, "3.2.8-net-beta.2", install) ||
                RefreshCore(Registry.CurrentUser, keyPath, "3.2.8-net-beta.2", install))
                throw new InvalidOperationException("Installed app version did not refresh exactly once");
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
            {
                if (key.GetValue("DisplayVersion") as string != "3.2.8-net-beta.2" ||
                    key.GetValue("DisplayName") as string != "TanukiBCL.Net 3.2.8-net-beta.2")
                    throw new InvalidOperationException("Installed app registration has the wrong display version");
                key.SetValue("InstallLocation", install + "-other", RegistryValueKind.String);
                key.SetValue("DisplayVersion", "untouched", RegistryValueKind.String);
            }
            if (RefreshCore(Registry.CurrentUser, keyPath, "3.2.8-net-beta.2", install))
                throw new InvalidOperationException("An unrelated installation was modified");
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
            {
                if (key.GetValue("DisplayVersion") as string != "untouched")
                    throw new InvalidOperationException("An unrelated installation's version changed");
                key.SetValue("InstallLocation", install, RegistryValueKind.String);
            }
            File.Delete(uninstaller);
            if (RefreshCore(Registry.CurrentUser, keyPath, "3.2.8-net-beta.2", install))
                throw new InvalidOperationException("A portable build claimed an installer registration");
            Console.WriteLine("[PASS] Installer registration refreshes only its matching installation");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Directory.Delete(install, recursive: true);
        }
    }
}
