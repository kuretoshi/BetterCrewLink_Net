using System.Diagnostics;
using System.IO;

namespace TanukiBCL.Client;

internal static class ApplicationRelaunch
{
    internal static void Verify()
    {
        var app = Create(@"C:\App With Spaces\TanukiBCL.Net.exe", @"C:\App With Spaces\TanukiBCL.Net.dll", 60232);
        if (app.UseShellExecute || !app.ArgumentList.SequenceEqual(new[] { "--game-process-id", "60232" }))
            throw new InvalidOperationException("App restart loses selected game process");
        var dotnet = Create(@"C:\Runtime\dotnet.exe", @"C:\App With Spaces\TanukiBCL.Net.dll", null);
        if (!dotnet.ArgumentList.SequenceEqual(new[] { @"C:\App With Spaces\TanukiBCL.Net.dll" }))
            throw new InvalidOperationException("Framework-host restart arguments invalid");
    }
    internal static ProcessStartInfo Create(string executable, string assembly, int? gamePid)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(assembly);
        if (gamePid is > 0)
        {
            start.ArgumentList.Add("--game-process-id");
            start.ArgumentList.Add(gamePid.Value.ToString());
        }
        return start;
    }
}
