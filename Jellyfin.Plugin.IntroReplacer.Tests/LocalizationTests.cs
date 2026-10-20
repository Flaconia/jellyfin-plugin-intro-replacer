using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.IntroReplacer.Services;
using Xunit;

namespace Jellyfin.Plugin.IntroReplacer.Tests;

public class LocalizationTests
{
    private static readonly Assembly PluginAssembly = typeof(PluginLocalization).Assembly;

    [Fact]
    public void EnglishStringsLoad()
    {
        Assert.Equal("Intro Replacer", PluginLocalization.Get("page.title", "en"));
        Assert.Equal("Intro Replacer", PluginLocalization.Get("page.title", "en-US"));
        Assert.NotEmpty(PluginLocalization.GetAll("en"));
    }

    [Fact]
    public void EveryConfigTranslationKeyExists()
    {
        var english = English();
        var html = ConfigHtml();

        var keys = Regex.Matches(html, @"\b(?:t|tf|tn)\('([^']+)'")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(html, @"countPart\([^,]+,\s*'([^']+)'")
                .Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(html, @"data-i18n(?:-html|-label|-title)?=""([^""]+)""")
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        var missing = keys
            .Where(key => !english.ContainsKey(key)
                && !(english.ContainsKey(key + ".one") && english.ContainsKey(key + ".other")))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void PluralKeysComeInPairs()
    {
        var singular = English().Keys
            .Where(k => k.EndsWith(".one", StringComparison.Ordinal))
            .Select(k => k[..^4])
            .ToList();

        foreach (var key in singular)
        {
            Assert.True(English().ContainsKey(key + ".other"), $"Missing plural form for {key}");
        }
    }

    [Fact]
    public void CulturesResolveToAvailableFiles()
    {
        Assert.Equal("en", PluginLocalization.ResolveCulture(null));
        Assert.Equal("it", PluginLocalization.ResolveCulture("it-IT"));
        Assert.Equal("it", PluginLocalization.ResolveCulture("it-CH, it;q=0.9"));
        Assert.Equal("en", PluginLocalization.ResolveCulture("en-GB, en;q=0.9"));
        Assert.Equal("en", PluginLocalization.ResolveCulture("xx-YY"));
    }

    [Fact]
    public void ItalianStringsLoad()
    {
        Assert.Equal("Impostazioni", PluginLocalization.Get("settings.title", "it-IT"));
        Assert.Equal("Riproduci le intro solo per i film",
            PluginLocalization.Get("settings.moviesOnly", "it"));
    }

    [Fact]
    public void EveryTranslationMatchesTheSourceKeysAndPlaceholders()
    {
        var source = English();
        var cultures = PluginAssembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Jellyfin.Plugin.IntroReplacer.Localization.", StringComparison.Ordinal)
                && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(n => n["Jellyfin.Plugin.IntroReplacer.Localization.".Length..^".json".Length])
            .Where(c => c != "en")
            .ToList();

        Assert.NotEmpty(cultures);

        foreach (var culture in cultures)
        {
            var values = Load(culture);
            Assert.Equal(
                source.Keys.OrderBy(k => k, StringComparer.Ordinal),
                values.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (var key in source.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(values[key]), $"{culture}: empty value for {key}");
                Assert.Equal(Placeholders(source[key]), Placeholders(values[key]));
            }
        }
    }

    private static string[] Placeholders(string text)
        => Regex.Matches(text, @"\{[^}]+\}")
            .Select(m => m.Value)
            .Distinct()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

    private static Dictionary<string, string> English() => Load("en");

    private static Dictionary<string, string> Load(string culture)
    {
        using var stream = PluginAssembly.GetManifestResourceStream(
            $"Jellyfin.Plugin.IntroReplacer.Localization.{culture}.json")
            ?? throw new InvalidOperationException($"Localization resource '{culture}' not found.");
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
            ?? new Dictionary<string, string>();
    }

    private static string ConfigHtml()
    {
        using var stream = PluginAssembly.GetManifestResourceStream(
            "Jellyfin.Plugin.IntroReplacer.Configuration.config.html")
            ?? throw new InvalidOperationException("Config page resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
