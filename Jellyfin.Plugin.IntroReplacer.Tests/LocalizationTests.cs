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
    public void UnknownCulturesFallBackToEnglish()
    {
        Assert.Equal("en", PluginLocalization.ResolveCulture(null));
        Assert.Equal("en", PluginLocalization.ResolveCulture("it-IT"));
        Assert.Equal("en", PluginLocalization.ResolveCulture("en-GB, en;q=0.9"));
    }

    private static Dictionary<string, string> English()
    {
        using var stream = PluginAssembly.GetManifestResourceStream(
            "Jellyfin.Plugin.IntroReplacer.Localization.en.json")
            ?? throw new InvalidOperationException("English localization resource not found.");
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
