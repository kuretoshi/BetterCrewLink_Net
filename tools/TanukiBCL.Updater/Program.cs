using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private const string AppId = "TanukiBCL.Net";
    private const string ManifestName = "update-manifest.json";
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test")) { Verify(); return 0; }
            if (args.Length == 2 && args[0] == "--package-self-test")
            {
                VerifyPublishedPackage(args[1]);
                return 0;
            }
            if (args.Length is not (6 or 8) || args[0] != "--parent-pid" ||
                !int.TryParse(args[1], out var parentPid) || parentPid <= 0 ||
                args[2] != "--install-dir" || args[4] != "--stage-root" ||
                args.Length == 8 && (args[6] != "--game-process-id" ||
                    !int.TryParse(args[7], out _)))
                throw new ArgumentException("Invalid updater arguments");
            var installDir = Path.GetFullPath(args[3]);
            var stageRoot = Path.GetFullPath(args[5]);
            var gamePid = args.Length == 8 ? int.Parse(args[7]) : (int?)null;
            ValidatePaths(installDir, stageRoot);
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (!parent.WaitForExit(120_000))
                    throw new TimeoutException("The running client did not exit in time");
            }
            catch (ArgumentException) { /* The client already exited. */ }
            var backup = InstallCore(installDir, Path.Combine(stageRoot, "payload"), executable =>
            {
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = installDir
                };
                if (gamePid is > 0)
                {
                    start.ArgumentList.Add("--game-process-id");
                    start.ArgumentList.Add(gamePid.Value.ToString());
                }
                using var started = Process.Start(start)
                    ?? throw new InvalidOperationException("Updated client could not be started");
            });
            Console.WriteLine($"Updated {installDir}; previous version retained at {backup}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Update failed: {error}");
            return 1;
        }
    }

    private static void ValidatePaths(string installDir, string stageRoot)
    {
        var install = Path.TrimEndingDirectorySeparator(installDir);
        var stage = Path.TrimEndingDirectorySeparator(stageRoot);
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TanukiBCL.Net", "updates"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetPathRoot(install)?.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(install, PathComparison) == true ||
            !stage.StartsWith(tempRoot, PathComparison) ||
            Path.GetDirectoryName(stage)?.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(tempRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison) != true ||
            install.StartsWith(stage + Path.DirectorySeparatorChar, PathComparison) ||
            stage.StartsWith(install + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("Unsafe updater paths");
        VerifyManifest(install);
        VerifyManifest(Path.Combine(stage, "payload"));
    }

    private static void VerifyManifest(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, ManifestName)));
        if (document.RootElement.GetProperty("appId").GetString() != AppId ||
            document.RootElement.GetProperty("formatVersion").GetInt32() != 1 ||
            !File.Exists(Path.Combine(directory, "TanukiBCL.Net.exe")))
            throw new InvalidDataException("Package is not a TanukiBCL.Net installation");
    }

    private static string InstallCore(string installDir, string payload, Action<string> launch)
    {
        VerifyManifest(installDir);
        VerifyManifest(payload);
        var parent = Directory.GetParent(installDir)?.FullName ??
            throw new InvalidOperationException("Installation has no parent directory");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(installDir));
        var suffix = Guid.NewGuid().ToString("N");
        var incoming = Path.Combine(parent, $"{name}.incoming-{suffix}");
        var backup = Path.Combine(parent, $"{name}.backup-{suffix}");
        var failed = Path.Combine(parent, $"{name}.failed-{suffix}");
        CopyDirectory(payload, incoming);
        try
        {
            Directory.Move(installDir, backup);
            try
            {
                Directory.Move(incoming, installDir);
                launch(Path.Combine(installDir, "TanukiBCL.Net.exe"));
                return backup;
            }
            catch
            {
                if (Directory.Exists(installDir)) Directory.Move(installDir, failed);
                Directory.Move(backup, installDir);
                throw;
            }
        }
        catch
        {
            // If the first rename fails, preserve the original installation.
            // The copied incoming directory is left for inspection/recovery.
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination)) throw new IOException("Incoming directory already exists");
        Directory.CreateDirectory(destination);
        var sourcePrefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Package contains a reparse point");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Package contains a reparse point");
            var relative = Path.GetRelativePath(source, file);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!Path.GetFullPath(file).StartsWith(sourcePrefix, PathComparison) ||
                !target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("Package path escaped staging directory");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        VerifyManifest(destination);
    }

    private static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tanukibcl-updater-test-{Guid.NewGuid():N}");
        var install = Path.Combine(root, "app");
        var payload = Path.Combine(root, "payload");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        try
        {
            void MakePackage(string directory, string content)
            {
                File.WriteAllText(Path.Combine(directory, ManifestName),
                    "{\"appId\":\"TanukiBCL.Net\",\"formatVersion\":1}");
                File.WriteAllText(Path.Combine(directory, "TanukiBCL.Net.exe"), content);
            }
            MakePackage(install, "old");
            MakePackage(payload, "new");
            var backup = InstallCore(install, payload, _ => { });
            if (File.ReadAllText(Path.Combine(install, "TanukiBCL.Net.exe")) != "new" ||
                File.ReadAllText(Path.Combine(backup, "TanukiBCL.Net.exe")) != "old")
                throw new InvalidOperationException("Updater did not preserve the old version");
            try
            {
                InstallCore(install, payload, _ => throw new IOException("Launch failed"));
                throw new InvalidOperationException("Launch failure was accepted");
            }
            catch (IOException error) when (error.Message == "Launch failed") { }
            if (File.ReadAllText(Path.Combine(install, "TanukiBCL.Net.exe")) != "new")
                throw new InvalidOperationException("Updater did not roll back after launch failure");
            Console.WriteLine("[PASS] Updater stages, swaps, preserves backup and rolls back launch failure");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyPublishedPackage(string packageDirectory)
    {
        var payload = Path.GetFullPath(packageDirectory);
        VerifyManifest(payload);
        var root = Path.Combine(Path.GetTempPath(), $"tanukibcl-real-updater-test-{Guid.NewGuid():N}");
        var install = Path.Combine(root, "app");
        Directory.CreateDirectory(install);
        try
        {
            File.WriteAllText(Path.Combine(install, ManifestName),
                "{\"appId\":\"TanukiBCL.Net\",\"formatVersion\":1}");
            File.WriteAllText(Path.Combine(install, "TanukiBCL.Net.exe"), "previous-version");
            var launched = false;
            var backup = InstallCore(install, payload, executable =>
            {
                launched = true;
                if (executable != Path.Combine(install, "TanukiBCL.Net.exe") ||
                    !File.Exists(executable) ||
                    !File.Exists(Path.Combine(install, "Updater", "TanukiBCL.Updater.exe")) ||
                    !File.Exists(Path.Combine(install, "NoSReader", "TbclSnapshotReader.exe")) ||
                    !File.Exists(Path.Combine(install, "RoleReaders", "SnrRoleReader.exe")) ||
                    !File.Exists(Path.Combine(install, "README.md")) ||
                    !File.Exists(Path.Combine(install, "LICENSE")))
                    throw new InvalidOperationException("Swapped package is incomplete");
            });
            var sourceFiles = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(payload, path)).Order().ToArray();
            var installedFiles = Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(install, path)).Order().ToArray();
            if (!launched || !sourceFiles.SequenceEqual(installedFiles) ||
                File.ReadAllText(Path.Combine(backup, "TanukiBCL.Net.exe")) != "previous-version")
                throw new InvalidOperationException("Full package swap or backup verification failed");
            foreach (var relative in sourceFiles)
            {
                var source = Path.Combine(payload, relative);
                var installed = Path.Combine(install, relative);
                if (new FileInfo(source).Length != new FileInfo(installed).Length)
                    throw new InvalidOperationException($"Swapped package file length differs: {relative}");
            }
            var sourceDigest = SHA256.HashData(File.ReadAllBytes(Path.Combine(payload, "TanukiBCL.Net.exe")));
            var installedDigest = SHA256.HashData(File.ReadAllBytes(Path.Combine(install, "TanukiBCL.Net.exe")));
            if (!CryptographicOperations.FixedTimeEquals(sourceDigest, installedDigest))
                throw new InvalidOperationException("Swapped application executable digest differs");
            Console.WriteLine($"[PASS] Full published package swapped with {sourceFiles.Length} files; previous version retained");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
