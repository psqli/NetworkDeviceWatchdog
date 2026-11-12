using System.Globalization;
using NetworkDeviceWatchdog.Localization;

namespace NetworkDeviceWatchdog.Tests;

public class LocalizerTests
{
    public static readonly string[] Expected = ["ar", "en", "es", "hi", "pt", "zh"];

    [Fact]
    public void All_requested_languages_are_built_in()
    {
        foreach (var code in Expected)
            Assert.Contains(code, Localizer.BuiltInLanguages);
    }

    [Fact]
    public void Every_locale_has_exactly_the_english_keys_and_placeholders()
    {
        var english = Localizer.LoadBuiltIn("en")!;
        foreach (var code in Localizer.BuiltInLanguages)
        {
            var locale = Localizer.LoadBuiltIn(code)!;
            var missing = english.Strings.Keys.Except(locale.Strings.Keys).ToArray();
            var extra = locale.Strings.Keys.Except(english.Strings.Keys).ToArray();
            Assert.True(missing.Length == 0, $"{code} is missing: {string.Join(", ", missing)}");
            Assert.True(extra.Length == 0, $"{code} has unknown keys: {string.Join(", ", extra)}");

            foreach (var (key, text) in english.Strings)
            {
                var expected = Localizer.PlaceholdersOf(text);
                var actual = Localizer.PlaceholdersOf(locale.Strings[key]);
                Assert.True(expected.SetEquals(actual),
                    $"{code}:{key} uses {{{string.Join("}, {", actual)}}} but English uses {{{string.Join("}, {", expected)}}}");
            }
        }
    }

    [Fact]
    public void Every_locale_declares_name_direction_and_font()
    {
        foreach (var code in Localizer.BuiltInLanguages)
        {
            var locale = Localizer.LoadBuiltIn(code)!;
            Assert.True(locale.HasDisplayName, $"{code}: language name missing");
            Assert.True(locale.HasDirection, $"{code}: direction missing");
            Assert.True(locale.HasFontFamily, $"{code}: fontFamily missing");
        }
    }

    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("pt", "pt")]
    [InlineData("es-MX", "es")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("hi-IN", "hi")]
    [InlineData("ar-SA", "ar")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("not-a-language", "en")]
    public void Language_tags_fall_back_to_the_best_locale(string tag, string expected)
    {
        Assert.Equal(expected, Localizer.Create(tag).Language);
    }

    [Fact]
    public void Auto_follows_the_ui_culture()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-AR");
            Assert.Equal("es", Localizer.Create("auto").Language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Arabic_is_right_to_left_and_others_are_not()
    {
        Assert.True(Localizer.Create("ar").IsRightToLeft);
        Assert.False(Localizer.Create("pt").IsRightToLeft);
        Assert.False(Localizer.Create("zh").IsRightToLeft);
    }

    [Fact]
    public void Formats_named_arguments_nested_messages_and_lists()
    {
        var l = Localizer.Create("en");
        var msg = Msg.Of("msg.deviceError", ("reasons", new MsgList([new Msg("reason.coverOpen"), new Msg("reason.paperEnd")])));
        Assert.Equal("The device reports: cover open, out of paper.", l.Format(msg));

        var zh = Localizer.Create("zh");
        Assert.Equal("设备报告：机盖打开、缺纸。", zh.Format(msg));
    }

    [Fact]
    public void Numbers_are_formatted_invariantly()
    {
        var l = Localizer.Create("pt");
        Assert.Equal("Configuração OK: 1234 dispositivo(s).", l.Format("app.configValid", ("count", 1234)));
    }

    [Fact]
    public void Unknown_keys_and_missing_arguments_are_visible_but_harmless()
    {
        var l = Localizer.Create("en");
        Assert.Equal("no.such.key", l["no.such.key"]);
        Assert.Equal("{device} is working again", l.Format("alert.title.recovered"));
    }

    [Fact]
    public void Override_directory_adds_languages_and_overrides_strings()
    {
        var dir = Directory.CreateTempSubdirectory("ndw-locales").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "pt.json"), """{ "strings": { "status.ok": "Tudo certo" } }""");
            File.WriteAllText(Path.Combine(dir, "de.json"),
                """{ "language": "Deutsch", "strings": { "status.ok": "In Ordnung" } }""");

            var pt = Localizer.Create("pt", dir);
            Assert.Equal("Tudo certo", pt["status.ok"]);
            Assert.Equal("Sem rede", pt["failure.unreachable"]);       // rest of the built-in locale kept

            var de = Localizer.Create("de-DE", dir);
            Assert.Equal("de", de.Language);
            Assert.Equal("In Ordnung", de["status.ok"]);
            Assert.Equal("No network", de["failure.unreachable"]);     // falls back to English
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Header_values_are_rfc2047_encoded_only_when_needed()
    {
        Assert.Equal("Kitchen: No network", TextEncoding.ToHeaderValue("Kitchen: No network"));
        var encoded = TextEncoding.ToHeaderValue("Cozinha: Não responde");
        Assert.StartsWith("=?UTF-8?B?", encoded);
        var payload = encoded["=?UTF-8?B?".Length..^2];
        Assert.Equal("Cozinha: Não responde", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
    }
}
