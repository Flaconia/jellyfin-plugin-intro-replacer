using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.IntroReplacer.Configuration;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Stores intro files in the plugin's data folder and resolves them on disk.
/// </summary>
public sealed class IntroAssetStore
{
    private static readonly string[] AllowedExtensions =
    {
        ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".avi", ".ts", ".mpg", ".mpeg", ".wmv"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroAssetStore"/> class.
    /// </summary>
    /// <param name="pluginConfigurationsPath">The Jellyfin plugin configuration directory.</param>
    public IntroAssetStore(string pluginConfigurationsPath)
    {
        Directory = Path.Combine(pluginConfigurationsPath, "IntroReplacer", "intros");
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>Gets the folder that holds the intro files.</summary>
    public string Directory { get; }

    /// <summary>Gets a value indicating whether a file extension is a supported video.</summary>
    /// <param name="fileName">The file name.</param>
    /// <returns><c>true</c> when the extension is allowed.</returns>
    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the absolute path of an asset, or <c>null</c> when the file is missing.
    /// </summary>
    /// <param name="asset">The asset.</param>
    /// <returns>The absolute path, or <c>null</c>.</returns>
    public string? ResolvePath(IntroAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.FileName))
        {
            return null;
        }

        var path = Path.Combine(Directory, asset.FileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Writes an uploaded intro file to disk and returns the stored file name.
    /// </summary>
    /// <param name="originalFileName">The original file name (used for the extension).</param>
    /// <param name="content">The file content.</param>
    /// <returns>The stored (unique) file name.</returns>
    public string Save(string originalFileName, Stream content)
    {
        var ext = Path.GetExtension(originalFileName);
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var path = Path.Combine(Directory, storedName);

        using var target = File.Create(path);
        content.CopyTo(target);
        return storedName;
    }

    /// <summary>
    /// Deletes an asset's file from disk.
    /// </summary>
    /// <param name="asset">The asset.</param>
    public void Delete(IntroAsset asset)
    {
        var path = ResolvePath(asset);
        if (path is not null)
        {
            File.Delete(path);
        }
    }
}
