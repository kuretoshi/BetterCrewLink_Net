using System.IO;
using System.Text.RegularExpressions;

namespace TanukiBCL.Client;

internal static class SnrLocalCosmetics
{
    // v3.2.7 main/snrCosmetics.ts. Search only the selected game's cosmetics folder.
    internal static CosmeticAsset? Resolve(string executable, string id, CosmeticPart part)
    {
        if (!Path.IsPathFullyQualified(executable) || !id.StartsWith("Modded_", StringComparison.Ordinal)) return null;
        var root = Path.Combine(Path.GetDirectoryName(executable)!, "SuperNewRolesNext", "CustomCosmetics");
        if (!Directory.Exists(root)) return null;
        var plain = Regex.Replace(id, "<[^>]*>", "");
        string[] suffixes = part switch {
            CosmeticPart.HatBack => ["_back.png"], CosmeticPart.Visor => ["_idle.png", "_front.png"],
            CosmeticPart.Skin => ["_front.png", "_idle.png"], _ => ["_front.png"] };
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var package = Path.GetFileName(directory);
            if (package.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = $"Modded_{package}_";
            if (!plain.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var name = Sanitize(plain[prefix.Length..]);
            foreach (var suffix in suffixes)
            {
                var path = Path.GetFullPath(Path.Combine(directory, name + suffix));
                if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(path)) continue;
                var adaptive = part is CosmeticPart.Hat or CosmeticPart.HatBack &&
                    plain.StartsWith("Modded_Multiverse Costume SEL_", StringComparison.Ordinal);
                return new CosmeticAsset(new Uri(path), adaptive, "-52%", "-18px", "140%");
            }
        }
        return null;
    }

    internal static string Sanitize(string value) => new(value.Replace("...", ".", StringComparison.Ordinal)
        .Where(c => c >= 32 && !"<>:\"/\\|?*".Contains(c)).ToArray());

    internal static void Verify()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var fixture = Directory.CreateTempSubdirectory("tanuki-snr-local-test-");
        try
        {
            var executable = Path.Combine(fixture.FullName, "Among Us.exe");
            var root = Path.Combine(fixture.FullName, "SuperNewRolesNext", "CustomCosmetics");
            var pack = Directory.CreateDirectory(Path.Combine(root, "Pack"));
            var adaptive = Directory.CreateDirectory(Path.Combine(root, "Multiverse Costume SEL"));
            var bundle = Directory.CreateDirectory(Path.Combine(root, "Skip.bundle"));
            // A generated 1px PNG tests the same local decode path used by the UI.
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            foreach (var path in new[] { Path.Combine(pack.FullName, "Name_front.png"), Path.Combine(pack.FullName, "Name_back.png"),
                Path.Combine(pack.FullName, "Name_idle.png"), Path.Combine(adaptive.FullName, "Name_front.png"),
                Path.Combine(bundle.FullName, "Name_front.png") })
            { File.WriteAllBytes(path, encoded.ToArray()); }
            var front = Resolve(executable, "Modded_Pack_<b>Name</b>", CosmeticPart.Hat)!;
            Require(front.Url.LocalPath == Path.Combine(pack.FullName, "Name_front.png"), "Tagged local hat resolution failed");
            Require(Resolve(executable, "Modded_Pack_Name", CosmeticPart.HatBack)!.Url.LocalPath.EndsWith("Name_back.png"), "Back image selection failed");
            Require(Resolve(executable, "Modded_Pack_Name", CosmeticPart.Visor)!.Url.LocalPath.EndsWith("Name_idle.png"), "Visor idle priority failed");
            Require(Resolve(executable, "Modded_Pack_Name", CosmeticPart.Skin)!.Url == front.Url, "Skin front priority failed");
            Require(Resolve(executable, "Modded_Multiverse Costume SEL_Name", CosmeticPart.Hat)!.Adaptive &&
                !Resolve(executable, "Modded_Multiverse Costume SEL_Name", CosmeticPart.Visor)!.Adaptive, "Local adaptive hat scope failed");
            Require(Resolve(executable, "Modded_Skip.bundle_Name", CosmeticPart.Hat) is null &&
                Resolve("", "Modded_Pack_Name", CosmeticPart.Hat) is null &&
                Resolve(Path.Combine(fixture.FullName, "other", "Among Us.exe"), "Modded_Pack_Name", CosmeticPart.Hat) is null,
                "Local cosmetic root isolation failed");
            Require(Sanitize("a...b/\\:\"|?*\u0001") == "a.b", "Filename sanitization differs from upstream");
            var decoded = Task.Run(() => CosmeticImages.GetImageAsync(front.Url)).GetAwaiter().GetResult();
            Require(decoded.IsFrozen && decoded.PixelWidth == 1, "Local image decoding failed");
            PlayerAvatar.VerifyLocalCosmetics(executable);
            Console.WriteLine("[PASS] SNR local root isolation, tags, suffix priority, adaptive scope, sanitization and PNG decode");
        }
        finally { fixture.Delete(recursive: true); }
    }
}
