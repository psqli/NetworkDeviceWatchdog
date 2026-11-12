using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetworkDeviceWatchdog.Localization;

/// <summary>
/// Looks up and formats user-facing text. Locale files are JSON documents
/// embedded in this assembly (<c>Localization/Locales/*.json</c>); a directory
/// of additional or overriding locale files can be supplied at runtime.
/// </summary>
/// <remarks>
/// Lookup order for a key: the selected locale, then English. Missing keys never
/// throw: the key itself is shown, so a gap in a translation is visible but harmless.
/// </remarks>
public sealed partial class Localizer
{
    /// <summary>The language used when nothing else matches.</summary>
    public const string DefaultLanguage = "en";

    private const string ResourcePrefix = "locales/";

    private readonly IReadOnlyDictionary<string, string> _strings;
    private readonly IReadOnlyDictionary<string, string> _fallback;

    private Localizer(LocaleFile locale, LocaleFile fallback)
    {
        Language = locale.Code;
        DisplayName = locale.DisplayName;
        IsRightToLeft = locale.IsRightToLeft;
        FontFamily = locale.FontFamily;
        _strings = locale.Strings;
        _fallback = fallback.Strings;
    }

    /// <summary>Code of the locale in use, e.g. <c>pt</c> or <c>zh</c>.</summary>
    public string Language { get; }

    /// <summary>Name of the language in that language, e.g. <c>Português (Brasil)</c>.</summary>
    public string DisplayName { get; }

    /// <summary>True for right-to-left scripts such as Arabic.</summary>
    public bool IsRightToLeft { get; }

    /// <summary>Preferred UI font for this script (used by the Windows alert window).</summary>
    public string FontFamily { get; }

    /// <summary>Codes of the locales embedded in the application.</summary>
    public static IReadOnlyList<string> BuiltInLanguages { get; } = typeof(Localizer).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^".json".Length])
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Creates a localizer.
    /// </summary>
    /// <param name="language">
    /// <c>auto</c> (or null/empty) to follow the operating system's UI language,
    /// or a language tag such as <c>pt-BR</c>, <c>es</c>, <c>zh-CN</c>.
    /// </param>
    /// <param name="overrideDirectory">
    /// Optional directory with <c>&lt;code&gt;.json</c> files that add languages or
    /// override individual strings of a built-in one.
    /// </param>
    public static Localizer Create(string? language, string? overrideDirectory = null)
    {
        var overrides = LoadOverrides(overrideDirectory);
        var english = Merge(LoadBuiltIn(DefaultLanguage)!, overrides.GetValueOrDefault(DefaultLanguage));

        foreach (var code in Candidates(language))
        {
            var builtIn = LoadBuiltIn(code);
            overrides.TryGetValue(code, out var custom);
            if (builtIn is null && custom is null)
                continue;
            var merged = builtIn is null ? custom! : Merge(builtIn, custom);
            return new Localizer(merged, english);
        }
        return new Localizer(english, english);
    }

    /// <summary>Returns the raw localized text for a key (placeholders not substituted).</summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var s) ? s :
        _fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>True if the key exists in the selected locale or in English.</summary>
    public bool Contains(string key) => _strings.ContainsKey(key) || _fallback.ContainsKey(key);

    /// <summary>Localizes a message, substituting its named arguments.</summary>
    public string Format(Msg message) => Substitute(this[message.Key], message.Args);

    /// <summary>Localizes a key with named arguments.</summary>
    public string Format(string key, params (string Name, object? Value)[] args) => Format(Msg.Of(key, args));

    /// <summary>Formats any argument value: messages are localized, lists joined, numbers invariant.</summary>
    public string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        Msg m => Format(m),
        MsgList list => string.Join(this["list.separator"], list.Items.Select(FormatValue)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private string Substitute(string template, IReadOnlyDictionary<string, object?> args)
    {
        if (args.Count == 0 || template.IndexOf('{', StringComparison.Ordinal) < 0)
            return template;
        return PlaceholderRegex().Replace(template, m =>
            args.TryGetValue(m.Groups[1].Value, out var v) ? FormatValue(v) : m.Value);
    }

    /// <summary>Placeholder names used in a template, e.g. <c>{host}</c> → <c>host</c>.</summary>
    public static IReadOnlySet<string> PlaceholdersOf(string template) =>
        PlaceholderRegex().Matches(template).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderRegex();

    // ------------------------------------------------------------------ loading

    /// <summary>Language codes to try, most specific first: <c>pt-BR</c> → <c>pt-br</c>, <c>pt</c>.</summary>
    internal static IEnumerable<string> Candidates(string? language)
    {
        CultureInfo culture;
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            culture = CultureInfo.CurrentUICulture;
        }
        else
        {
            try { culture = CultureInfo.GetCultureInfo(language); }
            catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
            // Accept the tag verbatim too (custom locale files may use any name).
            yield return language.ToLowerInvariant();
        }

        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            yield return c.Name.ToLowerInvariant();
            if (!string.IsNullOrEmpty(c.TwoLetterISOLanguageName) && c.TwoLetterISOLanguageName != "iv")
                yield return c.TwoLetterISOLanguageName.ToLowerInvariant();
        }
        yield return DefaultLanguage;
    }

    internal static LocaleFile? LoadBuiltIn(string code)
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream(ResourcePrefix + code + ".json");
        return stream is null ? null : LocaleFile.Parse(code, stream);
    }

    private static Dictionary<string, LocaleFile> LoadOverrides(string? directory)
    {
        var result = new Dictionary<string, LocaleFile>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return result;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            using var stream = File.OpenRead(file);
            result[code] = LocaleFile.Parse(code, stream);
        }
        return result;
    }

    private static LocaleFile Merge(LocaleFile baseFile, LocaleFile? custom)
    {
        if (custom is null)
            return baseFile;
        var strings = new Dictionary<string, string>(baseFile.Strings, StringComparer.Ordinal);
        foreach (var (k, v) in custom.Strings)
            strings[k] = v;
        return baseFile with
        {
            DisplayName = custom.HasDisplayName ? custom.DisplayName : baseFile.DisplayName,
            IsRightToLeft = custom.HasDirection ? custom.IsRightToLeft : baseFile.IsRightToLeft,
            FontFamily = custom.HasFontFamily ? custom.FontFamily : baseFile.FontFamily,
            Strings = strings,
        };
    }

    /// <summary>A parsed locale file.</summary>
    internal sealed record LocaleFile(
        string Code,
        string DisplayName,
        bool IsRightToLeft,
        string FontFamily,
        IReadOnlyDictionary<string, string> Strings,
        bool HasDisplayName,
        bool HasDirection,
        bool HasFontFamily)
    {
        public static LocaleFile Parse(string code, Stream utf8Json)
        {
            using var doc = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            var name = Str("language");
            var direction = Str("direction");
            var font = Str("fontFamily");
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("strings", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in s.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                        strings[p.Name] = p.Value.GetString()!;
                }
            }
            return new LocaleFile(
                code,
                name ?? code,
                string.Equals(direction, "rtl", StringComparison.OrdinalIgnoreCase),
                font ?? "Segoe UI",
                strings,
                name is not null,
                direction is not null,
                font is not null);
        }
    }
}

/// <summary>Helpers for headers and other ASCII-only transports.</summary>
public static class TextEncoding
{
    /// <summary>
    /// Encodes text for an HTTP header as an RFC 2047 encoded word when it is not
    /// plain ASCII (HTTP headers cannot carry UTF-8 directly).
    /// </summary>
    public static string ToHeaderValue(string text)
    {
        foreach (var ch in text)
        {
            if (ch > 0x7E || ch < 0x20)
                return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
        }
        return text;
    }
}
