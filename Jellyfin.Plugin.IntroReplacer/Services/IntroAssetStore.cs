using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.IntroReplacer.Configuration;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Stores intro files (uploaded, or dropped into the configured folder) and resolves
/// them on disk.
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
        DefaultDirectory = Path.GetFullPath(Path.Combine(pluginConfigurationsPath, "IntroReplacer", "intros"));
        Directory = DefaultDirectory;
        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>Gets the plugin's own intro folder (used when no custom folder is set).</summary>
    public string DefaultDirectory { get; }

    /// <summary>Gets the folder that currently holds the intro files.</summary>
    public string Directory { get; private set; }

    /// <summary>Gets a value indicating whether a file extension is a supported video.</summary>
    /// <param name="fileName">The file name.</param>
    /// <returns><c>true</c> when the extension is allowed.</returns>
    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Points the store at the configured folder, or at the plugin's own folder when
    /// <paramref name="configured"/> is empty.
    /// </summary>
    /// <param name="configured">The configured folder, or <c>null</c>/empty for the default.</param>
    public void SetDirectory(string? configured)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : configured.Trim();
        var full = Path.GetFullPath(path);

        // Point at the configured folder even when it is not reachable right now
        // (unmounted drive): the hidden library keeps its location instead of being
        // silently re-pointed at the default folder.
        Directory = full;
        System.IO.Directory.CreateDirectory(full);
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
    /// Writes an uploaded intro file to disk and returns the stored file name. The
    /// original name is kept (sanitised) so Jellyfin shows a readable title while the
    /// intro plays; a numeric suffix is added when the name is already taken.
    /// </summary>
    /// <param name="originalFileName">The original file name.</param>
    /// <param name="content">The file content.</param>
    /// <returns>The stored (unique) file name.</returns>
    public string Save(string originalFileName, Stream content)
    {
        var ext = Path.GetExtension(originalFileName);
        var baseName = Sanitize(Path.GetFileNameWithoutExtension(originalFileName));
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = Guid.NewGuid().ToString("N");
        }

        var storedName = UniqueName(baseName + ext);
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

    /// <summary>
    /// Registers video files that were dropped into the folder but are not assets yet,
    /// and gives legacy hash-named uploads back their readable name.
    /// </summary>
    /// <param name="configuration">The plugin configuration to update.</param>
    /// <returns><c>true</c> when an asset was added or renamed.</returns>
    public bool ScanForNewFiles(PluginConfiguration configuration)
    {
        var changed = NormalizeLegacyNames(configuration);

        var known = new HashSet<string>(
            configuration.Assets
                .Where(a => !string.IsNullOrWhiteSpace(a.FileName))
                .Select(a => a.FileName),
            StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            // The folder is not reachable right now (unmounted drive, permissions);
            // keep the registered assets and try again on the next call.
            return changed;
        }

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            if (!IsSupported(fileName) || fileName.StartsWith('.') || !known.Add(fileName))
            {
                continue;
            }

            configuration.Assets.Add(new IntroAsset
            {
                Id = Guid.NewGuid(),
                Name = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName
            });
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Moves every supported intro file from the current folder to
    /// <paramref name="destination"/>, keeping asset file names in sync.
    /// </summary>
    /// <param name="destination">The new folder.</param>
    /// <param name="configuration">The plugin configuration to update on collisions.</param>
    /// <returns>The number of moved files.</returns>
    public int MoveAll(string destination, PluginConfiguration configuration)
    {
        destination = Path.GetFullPath(destination);
        System.IO.Directory.CreateDirectory(destination);

        if (PathsEqual(destination, Directory))
        {
            return 0;
        }

        var renames = new List<(string Source, string Original, string Target)>();
        try
        {
            foreach (var source in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(source);
                if (!IsSupported(fileName) || fileName.StartsWith('.'))
                {
                    continue;
                }

                var targetName = fileName;
                var targetPath = Path.Combine(destination, targetName);
                var suffix = 1;
                while (File.Exists(targetPath))
                {
                    targetName = $"{Path.GetFileNameWithoutExtension(fileName)} ({suffix}){Path.GetExtension(fileName)}";
                    targetPath = Path.Combine(destination, targetName);
                    suffix++;
                }

                MoveFile(source, targetPath);
                renames.Add((source, fileName, targetName));
            }
        }
        catch
        {
            // Roll back so a half-moved folder never leaves the config pointing at
            // files that are no longer there.
            foreach (var (source, _, target) in renames)
            {
                try
                {
                    MoveFile(Path.Combine(destination, target), source);
                }
                catch (Exception)
                {
                    // Best effort only.
                }
            }

            throw;
        }

        foreach (var (_, original, target) in renames)
        {
            foreach (var asset in configuration.Assets.Where(
                         a => string.Equals(a.FileName, original, StringComparison.OrdinalIgnoreCase)))
            {
                asset.FileName = target;
            }
        }

        return renames.Count;
    }

    /// <summary>Compares two folder paths the way the intro library does.</summary>
    /// <param name="a">First path.</param>
    /// <param name="b">Second path.</param>
    /// <returns><c>true</c> when they point at the same folder.</returns>
    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        static string Normalize(string p) => p.Replace('\\', '/').TrimEnd('/');

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private bool NormalizeLegacyNames(PluginConfiguration configuration)
    {
        var changed = false;
        foreach (var asset in configuration.Assets)
        {
            if (asset.LibraryItemId != Guid.Empty
                || string.IsNullOrWhiteSpace(asset.FileName)
                || string.IsNullOrWhiteSpace(asset.Name)
                || !IsHashName(asset.FileName))
            {
                continue;
            }

            var source = Path.Combine(Directory, asset.FileName);
            if (!File.Exists(source))
            {
                continue;
            }

            var ext = Path.GetExtension(asset.FileName);
            var targetName = UniqueName(Sanitize(asset.Name) + ext);
            try
            {
                File.Move(source, Path.Combine(Directory, targetName));
            }
            catch (Exception)
            {
                // Keep the old name; the next scan tries again.
                continue;
            }

            asset.FileName = targetName;
            changed = true;
        }

        return changed;
    }

    private string UniqueName(string fileName)
    {
        var name = fileName;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var suffix = 1;
        while (File.Exists(Path.Combine(Directory, name)))
        {
            name = $"{baseName} ({suffix}){ext}";
            suffix++;
        }

        return name;
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length > 120)
        {
            result = result[..120].Trim();
        }

        return result;
    }

    private static bool IsHashName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.Length == 32 && name.All(Uri.IsHexDigit);
    }

    private static void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            // Cross-device moves surface as an IOException on some platforms.
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
        }
    }
}
