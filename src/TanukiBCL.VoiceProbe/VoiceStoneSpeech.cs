using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe;

internal static class VoiceStoneSpeech
{
    private static readonly HashSet<string> FunctionWords = new([
        "えー", "ええ", "えっと", "あの", "その", "この", "それ", "これ", "はい", "うん",
        "は", "が", "の", "を", "に", "へ", "と", "で", "も", "ね", "よ", "です", "ます", "だ"
    ]);
    internal static bool ValidWord(string? value, int maximum)
    {
        if (value is null || value.Length > 64 || maximum is < 1 or > 12 || value != value.Normalize(NormalizationForm.FormKC)) return false;
        int length = new StringInfo(value).LengthInTextElements;
        if (length < 1 || length > maximum) return false;
        return value.EnumerateRunes().All(r => Rune.IsLetterOrDigit(r) || r.Value is 'ー' or '々');
    }
    internal static string? Extract(JsonElement? tokens, bool final, int maximum)
    {
        if (!final || tokens is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() > 64) return null;
        foreach (var token in array.EnumerateArray())
        {
            if (token.ValueKind != JsonValueKind.Object || !token.TryGetProperty("word", out var text) || text.ValueKind != JsonValueKind.String) continue;
            if (token.TryGetProperty("conf", out var confidence) && (confidence.ValueKind != JsonValueKind.Number || !confidence.TryGetDouble(out double c) || !double.IsFinite(c) || c < .55)) continue;
            string? raw = text.GetString();
            if (raw is null || raw.Length > 64) continue;
            string word = raw.Normalize(NormalizationForm.FormKC);
            if (ValidWord(word, maximum) && !FunctionWords.Contains(word)) return word;
        }
        return null;
    }
}
