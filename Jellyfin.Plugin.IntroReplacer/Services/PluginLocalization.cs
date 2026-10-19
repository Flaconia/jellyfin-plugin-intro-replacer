using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Lightweight JSON localization for the plugin UI and API messages.
/// Translation files live in <c>Localization/{culture}.json</c> and are embedded
/// in the assembly. The flat key/value layout is intentionally compatible with
/// Weblate's JSON file format; <c>en.json</c> is the source language.
/// </summary>
public static class PluginLocalization
{
    private const string DefaultCulture = "en";
    private const string ResourceMarker = ".Localization.";

    private static readonly Assembly Assembly = typeof(PluginLocalization).Assembly;
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    // culture code -> embedded resource name
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Resources = new(DiscoverResources);

    /// <summary>
    /// Gets every localized string for a culture, falling back to English.
    /// </summary>
    /// <param name="culture">A locale name or an <c>Accept-Language</c> header.</param>
    /// <returns>The key/value dictionary.</returns>
    public static IReadOnlyDictionary<string, string> GetAll(string? culture)
    {
        var resolved = ResolveCulture(culture);
        return Cache.GetOrAdd(resolved, Load);
    }

    /// <summary>
    /// Gets one localized string, returning the key when it is missing.
    /// </summary>
    /// <param name="key">The translation key.</param>
    /// <param name="culture">A locale name or an <c>Accept-Language</c> header.</param>
    /// <returns>The localized value, or the key itself.</returns>
    public static string Get(string key, string? culture)
        => GetAll(culture).TryGetValue(key, out var value) ? value : key;

    /// <summary>
    /// Gets one localized string and applies <see cref="string.Format(string, object?[])"/>.
    /// </summary>
    /// <param name="key">The translation key.</param>
    /// <param name="culture">A locale name or an <c>Accept-Language</c> header.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted localized value.</returns>
    public static string Format(string key, string? culture, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, Get(key, culture), args);

    /// <summary>
    /// Resolves the best available culture for a locale name or <c>Accept-Language</c>
    /// header, falling back to <c>en</c>.
    /// </summary>
    /// <param name="culture">Locale name or header value.</param>
    /// <returns>The culture code that has an embedded translation file.</returns>
    public static string ResolveCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return DefaultCulture;
        }

        foreach (var part in culture.Split(','))
        {
            var tag = part.Split(';')[0].Trim().Replace('_', '-');
            if (tag.Length == 0)
            {
                continue;
            }

            var exact = FindCulture(tag);
            if (exact is not null)
            {
                return exact;
            }

            var language = tag.Split('-')[0];
            var languageMatch = FindCulture(language)
                ?? Resources.Value.Keys.FirstOrDefault(k =>
                    k.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));

            if (languageMatch is not null)
            {
                return languageMatch;
            }
        }

        return DefaultCulture;
    }

    private static string? FindCulture(string tag)
        => Resources.Value.Keys.FirstOrDefault(k => k.Equals(tag, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> DiscoverResources()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in Assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var marker = name.LastIndexOf(ResourceMarker, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                continue;
            }

            var start = marker + ResourceMarker.Length;
            var culture = name[start..^".json".Length];
            if (culture.Length > 0 && !culture.Contains('.'))
            {
                result[culture] = name;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> Load(string culture)
    {
        if (!Resources.Value.TryGetValue(culture, out var resource))
        {
            Resources.Value.TryGetValue(DefaultCulture, out resource);
        }

        if (resource is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var stream = Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
