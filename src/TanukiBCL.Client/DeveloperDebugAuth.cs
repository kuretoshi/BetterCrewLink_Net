using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TanukiBCL.Client;

internal static partial class DeveloperDebugAuth
{
    private const int Iterations = 100_000;

    internal static bool Verify(string? password, string? configuration = null)
    {
        configuration ??= Environment.GetEnvironmentVariable("TANUKI_DEBUG_AUTH");
        if (string.IsNullOrEmpty(password) || password.Length > 1_024 || string.IsNullOrEmpty(configuration))
            return false;

        try
        {
            using var document = JsonDocument.Parse(configuration);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("salt", out var saltValue) ||
                !root.TryGetProperty("hash", out var hashValue) ||
                saltValue.ValueKind != JsonValueKind.String ||
                hashValue.ValueKind != JsonValueKind.String)
                return false;
            var saltHex = saltValue.GetString();
            var hashHex = hashValue.GetString();
            if (saltHex is null || hashHex is null ||
                !SaltPattern().IsMatch(saltHex) || !HashPattern().IsMatch(hashHex))
                return false;

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(saltHex),
                Iterations, HashAlgorithmName.SHA256, 32);
            var expected = Convert.FromHexString(hashHex);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static void VerifyParity()
    {
        // Node.js pbkdf2Sync vector from the released implementation's parameters.
        const string config = "{\"salt\":\"000102030405060708090a0b0c0d0e0f\",\"hash\":\"fc870efa2ecf82f10ed215e1fa0b2eeb1a49150cd539ca1d9dfc0e82faea2047\"}";
        if (!Verify("test-pass", config) || Verify("wrong-pass", config) ||
            Verify("test-pass", "{\"salt\":\"00\",\"hash\":\"ff\"}") ||
            Verify(new string('x', 1_025), config) || Verify("test-pass", "{}"))
            throw new InvalidOperationException("Developer debug authentication differs from TanukiBCL 3.2.8");
        Console.WriteLine("[PASS] Developer debug PBKDF2 authentication matches released parameters");
    }

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaltPattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
