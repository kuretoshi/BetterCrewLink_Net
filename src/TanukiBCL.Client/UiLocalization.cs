using System.IO;
using System.Text.Json;

namespace TanukiBCL.Client;

internal sealed record UiLanguage(string Code, string Name);

// Translations are copied byte-for-byte from released TanukiBCL 3.2.7.
internal static class UiLocalization
{
    internal static readonly IReadOnlyList<UiLanguage> Languages =
    [
        new("en", "English"), new("af", "Afrikaans"), new("ar", "العربية"),
        new("az", "Azərbaycan"), new("ca", "Català"), new("zh_CN", "简体中文"),
        new("zh_TW", "繁體中文"), new("cs", "Čeština"), new("da", "Dansk"),
        new("nl", "Nederlands"), new("eo", "Esperanto"), new("fi", "Suomi"),
        new("fr", "Français"), new("de", "Deutsch"), new("el", "Ελληνικά"),
        new("he", "עברית"), new("hu", "Magyar"), new("id", "Bahasa Indonesia"),
        new("it", "Italiano"), new("ja", "日本語"), new("ko", "한국인"),
        new("no", "Norsk"), new("fa", "فارسی"), new("pl", "Polski"),
        new("pt", "Português (Portugal)"), new("pt_BR", "Português (Brasil)"),
        new("ro", "Română"), new("ru", "Русский"), new("sr", "Српски"),
        new("sk", "Slovenčina"), new("sl", "Slovenščina"), new("es", "Español"),
        new("sv", "Svenska"), new("tt", "Татар"), new("tr", "Türkçe"),
        new("uk", "Українська"), new("vi", "Tiếng Việt")
    ];

    private static readonly Dictionary<string, JsonDocument> Catalogs = new(StringComparer.Ordinal);
    private static Dictionary<string, string>? japaneseKeys;

    internal static string Normalize(string? code) =>
        Languages.Any(language => language.Code == code) ? code! : "ja";

    internal static string Translate(string? code, string key)
    {
        var selected = Normalize(code);
        return TryTranslate(selected, key) ?? (selected == "ja" ? null : TryTranslate("ja", key)) ?? key;
    }

    internal static string? KeyForJapanese(string text)
    {
        japaneseKeys ??= BuildJapaneseKeys();
        return japaneseKeys.TryGetValue(text, out var key) ? key : null;
    }

    private static Dictionary<string, string> BuildJapaneseKeys()
    {
        _ = TryTranslate("ja", "settings.title");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(JsonElement element, string prefix)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value)) values.TryAdd(value, prefix);
            }
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                    Visit(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}");
        }
        Visit(Catalogs["ja"].RootElement, string.Empty);
        // .NET uses the shorter label while the released shortcut page uses the
        // full Japanese title. Keep the localized meaning of that category.
        values["キー割り当て"] = "settings.keyboard.title";
        return values;
    }

    private static string? TryTranslate(string code, string key)
    {
        if (!Catalogs.TryGetValue(code, out var document))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Locales", code, "translation.json");
            if (!File.Exists(path)) return null;
            document = JsonDocument.Parse(File.ReadAllText(path));
            Catalogs.Add(code, document);
        }
        var element = document.RootElement;
        foreach (var segment in key.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    internal static void Verify()
    {
        if (Languages.Count != 37 || Languages.Select(language => language.Code).Distinct().Count() != 37)
            throw new InvalidOperationException("Released language list is incomplete");
        foreach (var language in Languages)
            if (Translate(language.Code, "settings.title") is { Length: 0 } or "settings.title")
                throw new InvalidOperationException($"Language catalog missing settings.title: {language.Code}");
        if (Translate("en", "settings.title") != "Settings" ||
            Translate("ja", "settings.title") != "設定" || Normalize("unknown") != "ja")
            throw new InvalidOperationException("Language selection or fallback is incorrect");
        Console.WriteLine("[PASS] All 37 released language catalogs load with Japanese fallback");
    }
}
