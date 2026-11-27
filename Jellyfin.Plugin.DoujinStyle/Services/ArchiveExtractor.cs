using System.Text;
using Jellyfin.Plugin.DoujinStyle.Models;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Jellyfin.Plugin.DoujinStyle.Services;

/// <summary>
/// Extracts downloaded archives (.rar, .zip, .7z, .tar) into {MusicRoot}/{Artist}/{Album},
/// flattens redundant single root directories, writes cover.jpg if missing, and fills missing audio tags.
/// </summary>
public sealed class ArchiveExtractor
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".ogg", ".opus", ".m4a", ".wav", ".tta", ".aac", ".wma", ".alac", ".ape"
    };

    private static readonly HashSet<string> AllowedCompanionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".cue", ".lrc", ".txt"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    private readonly ILogger<ArchiveExtractor> _logger;

    public ArchiveExtractor(ILogger<ArchiveExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExtractAndOrganizeAsync(
        IReadOnlyList<string> downloadedFiles,
        string musicRootDirectory,
        DoujinAlbum album,
        byte[]? fallbackCoverBytes,
        bool writeMissingTags,
        CancellationToken cancellationToken)
    {
        var artistDirName = SanitizeDirectorySegment(album.Artist, "Doujin Artist");
        var albumDirName = SanitizeDirectorySegment(album.Title, $"Album {album.Id}");
        var finalAlbumDir = Path.GetFullPath(Path.Combine(musicRootDirectory, artistDirName, albumDirName));

        var stagingExtractDir = Path.Combine(
            Path.GetTempPath(),
            "jellyfin-doujinstyle-extract",
            $"{album.Id}_{Guid.NewGuid():N}");

        Directory.CreateDirectory(stagingExtractDir);

        try
        {
            foreach (var sourceFile in downloadedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(sourceFile);
                if (AudioExtensions.Contains(ext) || AllowedCompanionExtensions.Contains(ext))
                {
                    var dest = Path.Combine(stagingExtractDir, Path.GetFileName(sourceFile));
                    File.Copy(sourceFile, dest, overwrite: true);
                }
                else
                {
                    ExtractArchiveSafe(sourceFile, stagingExtractDir, cancellationToken);
                }
            }

            var effectiveRoot = UnwrapRedundantSingleDirectories(stagingExtractDir);
            var extractedAudioFiles = Directory.EnumerateFiles(effectiveRoot, "*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            if (extractedAudioFiles.Count == 0)
            {
                throw new InvalidOperationException("Aucun fichier audio (.flac, .mp3, .ogg, .opus, .m4a, .wav...) n'a été trouvé dans l'archive téléchargée.");
            }

            Directory.CreateDirectory(finalAlbumDir);

            // Copy allowed audio + companion files into finalAlbumDir preserving multi-disc subdirectories
            var placedAudioFiles = new List<string>(extractedAudioFiles.Count);
            bool hasCoverImage = false;

            foreach (var file in Directory.EnumerateFiles(effectiveRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                if (!AudioExtensions.Contains(ext) && !AllowedCompanionExtensions.Contains(ext))
                {
                    continue;
                }

                var relPath = Path.GetRelativePath(effectiveRoot, file);
                var safeDest = Path.GetFullPath(Path.Combine(finalAlbumDir, relPath));
                if (!safeDest.StartsWith(finalAlbumDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !string.Equals(safeDest, finalAlbumDir, StringComparison.Ordinal))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(safeDest)!);
                File.Copy(file, safeDest, overwrite: true);

                if (AudioExtensions.Contains(ext))
                {
                    placedAudioFiles.Add(safeDest);
                }
                else if (ImageExtensions.Contains(ext))
                {
                    hasCoverImage = true;
                }
            }

            if (!hasCoverImage && fallbackCoverBytes is { Length: > 0 })
            {
                var coverPath = Path.Combine(finalAlbumDir, "cover.jpg");
                await File.WriteAllBytesAsync(coverPath, fallbackCoverBytes, cancellationToken).ConfigureAwait(false);
            }

            if (writeMissingTags)
            {
                EnsureAudioMetadataTags(placedAudioFiles, album);
            }

            _logger.LogInformation(
                "[DoujinStyle] Extracted {Count} tracks into {Destination}",
                placedAudioFiles.Count,
                finalAlbumDir);

            return finalAlbumDir;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingExtractDir))
                {
                    Directory.Delete(stagingExtractDir, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    public static string UnwrapRedundantSingleDirectories(string rootDir)
    {
        var current = rootDir;
        while (true)
        {
            var files = Directory.GetFiles(current);
            var subDirs = Directory.GetDirectories(current)
                .Where(d => !Path.GetFileName(d).Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (files.Length == 0 && subDirs.Length == 1)
            {
                var subName = Path.GetFileName(subDirs[0]);
                // Keep multi-disc folder names only when there are multiple discs; if there's a single wrapper folder, unwrap it
                if (!subName.StartsWith("Disc ", StringComparison.OrdinalIgnoreCase)
                    && !subName.StartsWith("CD", StringComparison.OrdinalIgnoreCase))
                {
                    current = subDirs[0];
                    continue;
                }
            }

            break;
        }

        return current;
    }

    public static string SanitizeDirectorySegment(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var cleaned = sb.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static void ExtractArchiveSafe(string archivePath, string destinationRoot, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(destinationRoot);
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var normalizedKey = entry.Key.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (normalizedKey.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(fullRoot, normalizedKey));
            if (!targetPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Entrée d'archive non sécurisée bloquée (Zip-Slip) : {entry.Key}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.WriteToFile(targetPath, new ExtractionOptions
            {
                ExtractFullPath = false,
                Overwrite = true
            });
        }
    }

    private void EnsureAudioMetadataTags(IReadOnlyList<string> audioFiles, DoujinAlbum album)
    {
        var ordered = audioFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var filePath = ordered[i];
            try
            {
                using var tfile = TagLib.File.Create(filePath);
                bool modified = false;

                if (string.IsNullOrWhiteSpace(tfile.Tag.Album))
                {
                    tfile.Tag.Album = album.Title;
                    modified = true;
                }

                if (tfile.Tag.AlbumArtists is null || tfile.Tag.AlbumArtists.Length == 0 || string.IsNullOrWhiteSpace(tfile.Tag.AlbumArtists[0]))
                {
                    tfile.Tag.AlbumArtists = [album.Artist];
                    modified = true;
                }

                if (tfile.Tag.Performers is null || tfile.Tag.Performers.Length == 0 || string.IsNullOrWhiteSpace(tfile.Tag.Performers[0]))
                {
                    tfile.Tag.Performers = [album.Artist];
                    modified = true;
                }

                if (string.IsNullOrWhiteSpace(tfile.Tag.Title))
                {
                    tfile.Tag.Title = Path.GetFileNameWithoutExtension(filePath);
                    modified = true;
                }

                if (tfile.Tag.Track == 0 && ordered.Count > 1)
                {
                    tfile.Tag.Track = (uint)(i + 1);
                    modified = true;
                }

                if ((tfile.Tag.Genres is null || tfile.Tag.Genres.Length == 0) && album.Tags.Count > 0)
                {
                    tfile.Tag.Genres = album.Tags.Take(5).ToArray();
                    modified = true;
                }

                if (modified)
                {
                    tfile.Save();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DoujinStyle] Could not update audio tags for {File}", filePath);
            }
        }
    }
}
