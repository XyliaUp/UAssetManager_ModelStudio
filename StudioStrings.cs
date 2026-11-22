using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Markup;

namespace UAssetManager.ModelStudio;

internal static class StudioStrings
{
    private const string Prefix = "UAssetManager.ModelStudio.Localization.";
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Catalogs = new(Load);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("zh-CN");

    public static string Language => _culture.IetfLanguageTag;

    public static void UseLanguage(string? language)
    {
        _culture = ResolveCulture(language);
    }

    public static string Text(string key, string fallbackChinese = "")
    {
        return Lookup(key, fallbackChinese, _culture);
    }

    public static string ForLanguage(string? language, string key, string fallbackChinese)
    {
        return Lookup(key, fallbackChinese, ResolveCulture(language));
    }

    public static string Format(string key, string fallbackChinese, params object?[] values)
    {
        var culture = _culture;
        return string.Format(culture, Lookup(key, fallbackChinese, culture), values);
    }

    private static string Lookup(string key, string fallback, CultureInfo culture)
    {
        for (var current = culture; current.Name.Length > 0; current = current.Parent)
        {
            if (Catalogs.Value.TryGetValue(current.Name, out var strings) && strings.TryGetValue(key, out var value))
                return value;
        }
        if (Catalogs.Value.TryGetValue("zh-CN", out var defaultStrings) && defaultStrings.TryGetValue(key, out var defaultValue))
            return defaultValue;

        return fallback.Length > 0 ? fallback : key;
    }

    private static CultureInfo ResolveCulture(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return CultureInfo.GetCultureInfo("zh-CN");

        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("zh-CN");
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(StudioStrings).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var language = name[Prefix.Length..^5].Split('.')[^1];
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                continue;

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (entries is null)
                continue;

            if (!result.TryGetValue(language, out var catalog))
            {
                catalog = new Dictionary<string, string>(StringComparer.Ordinal);
                result.Add(language, catalog);
            }
            foreach (var (key, text) in entries)
                catalog.Add(key, text);
        }
        return result;
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class StudioTextExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;
    public string Fallback { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return StudioStrings.Text(Key, Fallback);
    }
}
