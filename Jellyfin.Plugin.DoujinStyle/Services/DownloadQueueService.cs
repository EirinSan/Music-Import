using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Plugin.DoujinStyle.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoujinStyle.Services;

/// <summary>
/// Background download and import queue for DoujinStyle albums.
/// </summary>
public sealed class DownloadQueueService : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly DoujinStyleClient _doujinClient;
    private readonly FileHostResolver _fileHostResolver;
    private readonly ArchiveExtractor _archiveExtractor;
    private readonly StubCache _stubCache;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<DownloadQueueService> _logger;

    public DownloadQueueService(
        DoujinStyleClient doujinClient,
        FileHostResolver fileHostResolver,
        ArchiveExtractor archiveExtractor,
        StubCache stubCache,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        ILogger<DownloadQueueService> logger)
    {
        _doujinClient = doujinClient;
        _fileHostResolver = fileHostResolver;
        _archiveExtractor = archiveExtractor;
        _stubCache = stubCache;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public IReadOnlyList<DownloadJob> GetAllJobs()
    {
        return _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .Take(100)
            .ToList();
    }

    public DownloadJob? GetJob(string albumId)
    {
        return _jobs.TryGetValue(albumId, out var job) ? job : null;
    }

    public IReadOnlyList<string> DetectMusicLibraryFolders()
    {
        var folders = new List<string>();
        try
        {
            foreach (var vf in _libraryManager.GetVirtualFolders())
            {
                if (string.Equals(vf.CollectionType?.ToString(), nameof(CollectionTypeOptions.music), StringComparison.OrdinalIgnoreCase)
                    && vf.Locations is { Length: > 0 })
                {
                    foreach (var loc in vf.Locations)
                    {
                        if (!string.IsNullOrWhiteSpace(loc) && !folders.Contains(loc, StringComparer.Ordinal))
                        {
                            folders.Add(loc);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DoujinStyle] Could not enumerate Jellyfin virtual folders.");
        }

        return folders;
    }

    public string? ResolveTargetMusicDirectory()
    {
        var configured = Plugin.Instance?.Configuration.TargetMusicDirectory?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var detected = DetectMusicLibraryFolders();
        return detected.Count > 0 ? detected[0] : null;
    }

    public string SuggestDefaultMusicDirectory()
    {
        var existing = ResolveTargetMusicDirectory();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        try
        {
            foreach (var vf in _libraryManager.GetVirtualFolders())
            {
                if (vf.Locations is not { Length: > 0 })
                {
                    continue;
                }

                foreach (var loc in vf.Locations)
                {
                    if (string.IsNullOrWhiteSpace(loc))
                    {
                        continue;
                    }

                    var parent = Directory.GetParent(loc.TrimEnd('/', '\\'))?.FullName;
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        return Path.Combine(parent, "Music");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DoujinStyle] Could not infer sibling media directory.");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, "media", "Music");
        }

        return "/media/Music";
    }

    public async Task<DownloadJob> EnqueueAlbumAsync(string albumId, DoujinAlbum? knownAlbum, Guid? triggerUserId, CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(albumId, out var existing)
            && existing.Status is DownloadJobStatus.Queued
                or DownloadJobStatus.ResolvingLink
                or DownloadJobStatus.Downloading
                or DownloadJobStatus.Extracting
                or DownloadJobStatus.ScanningLibrary)
        {
            return existing;
        }

        var album = knownAlbum;
        if (album is null && !_stubCache.TryGetByAlbumId(albumId, out album))
        {
            album = await _doujinClient.GetAlbumDetailAsync(albumId, cancellationToken).ConfigureAwait(false);
        }

        var job = new DownloadJob
        {
            AlbumId = albumId,
            AlbumTitle = album?.Title ?? $"DoujinStyle #{albumId}",
            Artist = album?.Artist ?? "Doujin Artist",
            CoverUrl = album?.CoverUrl,
            Status = DownloadJobStatus.Queued,
            ProgressPercent = 0,
            StatusMessage = "En file d'attente..."
        };

        _jobs[albumId] = job;
        await _queue.Writer.WriteAsync(albumId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("[DoujinStyle] Enqueued download for #{Id}: {Artist} — {Title}", albumId, job.Artist, job.AlbumTitle);
        _ = NotifySessionsAsync(
            triggerUserId,
            "DoujinStyle Importer",
            $"Téléchargement lancé : {job.Artist} — {job.AlbumTitle}");

        return job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 2 background workers max to stay gentle on seedbox disk/network I/O
        var workers = new[]
        {
            RunWorkerLoopAsync(1, stoppingToken),
            RunWorkerLoopAsync(2, stoppingToken)
        };

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerLoopAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var albumId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_jobs.TryGetValue(albumId, out var job))
            {
                continue;
            }

            try
            {
                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                job.Status = DownloadJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                job.StatusMessage = $"Échec : {ex.Message}";
                job.CompletedAt = DateTimeOffset.UtcNow;
                _logger.LogError(ex, "[DoujinStyle][Worker {Worker}] Download failed for #{Id}", workerId, albumId);
                _ = NotifySessionsAsync(null, "DoujinStyle — Échec", $"{job.AlbumTitle} : {ex.Message}");
            }
        }
    }

    private async Task ProcessJobAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var musicDir = ResolveTargetMusicDirectory();
        if (string.IsNullOrWhiteSpace(musicDir))
        {
            throw new InvalidOperationException(
                "Aucun dossier de médiathèque 'Musique' détecté dans Jellyfin. Créez une bibliothèque Musique dans Jellyfin ou renseignez 'Dossier Musique cible' dans les paramètres du plugin DoujinStyle.");
        }

        job.Status = DownloadJobStatus.ResolvingLink;
        job.ProgressPercent = 2;
        job.StatusMessage = "Récupération des métadonnées et du lien hébergeur...";

        var detail = await _doujinClient.GetAlbumDetailAsync(job.AlbumId, cancellationToken).ConfigureAwait(false)
            ?? new DoujinAlbum
            {
                Id = job.AlbumId,
                Title = job.AlbumTitle,
                Artist = job.Artist,
                CoverUrl = job.CoverUrl
            };

        job.AlbumTitle = detail.Title;
        job.Artist = detail.Artist;
        job.CoverUrl = detail.CoverUrl;

        var externalUrl = await _doujinClient.ResolveExternalDownloadUrlAsync(job.AlbumId, cancellationToken).ConfigureAwait(false);
        job.SourceHost = new Uri(externalUrl).Host;

        var tempDownloadDir = Path.Combine(
            Path.GetTempPath(),
            "jellyfin-doujinstyle-dl",
            $"{job.AlbumId}_{Guid.NewGuid():N}");

        try
        {
            job.Status = DownloadJobStatus.Downloading;
            job.ProgressPercent = 5;
            job.StatusMessage = $"Téléchargement depuis {job.SourceHost}...";

            var cfg = Plugin.Instance?.Configuration;
            long maxBytes = Math.Clamp(cfg?.MaxArchiveSizeMb ?? 2048, 50, 16384) * 1024L * 1024L;

            var payload = await _fileHostResolver.DownloadAsync(
                externalUrl,
                tempDownloadDir,
                maxBytes,
                (pct, msg) =>
                {
                    job.ProgressPercent = 5 + (pct * 75 / 100);
                    job.StatusMessage = msg;
                },
                cancellationToken).ConfigureAwait(false);

            job.SourceHost = payload.SourceHost;
            job.Status = DownloadJobStatus.Extracting;
            job.ProgressPercent = 85;
            job.StatusMessage = "Extraction de l'archive et organisation des pistes...";

            byte[]? coverBytes = null;
            if (!string.IsNullOrWhiteSpace(detail.CoverUrl))
            {
                coverBytes = await _doujinClient.FetchImageBytesAsync(detail.CoverUrl, cancellationToken).ConfigureAwait(false);
            }

            var finalDir = await _archiveExtractor.ExtractAndOrganizeAsync(
                payload.FilePaths,
                musicDir,
                detail,
                coverBytes,
                cfg?.WriteMissingAudioTags ?? true,
                cancellationToken).ConfigureAwait(false);

            job.DestinationPath = finalDir;
            job.Status = DownloadJobStatus.ScanningLibrary;
            job.ProgressPercent = 95;
            job.StatusMessage = "Actualisation de la médiathèque Jellyfin...";

            _libraryManager.QueueLibraryScan();

            job.Status = DownloadJobStatus.Completed;
            job.ProgressPercent = 100;
            job.StatusMessage = "Importé dans Jellyfin !";
            job.CompletedAt = DateTimeOffset.UtcNow;

            _ = NotifySessionsAsync(
                null,
                "DoujinStyle — Import terminé",
                $"L'album '{job.Artist} — {job.AlbumTitle}' a été ajouté à votre bibliothèque Jellyfin.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDownloadDir))
                {
                    Directory.Delete(tempDownloadDir, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup failure
            }
        }
    }

    private async Task NotifySessionsAsync(Guid? targetUserId, string header, string text)
    {
        if (Plugin.Instance?.Configuration.SendSessionNotification != true)
        {
            return;
        }

        try
        {
            foreach (var session in _sessionManager.Sessions)
            {
                if (!session.IsActive)
                {
                    continue;
                }

                if (targetUserId.HasValue && targetUserId.Value != Guid.Empty && session.UserId != targetUserId.Value)
                {
                    continue;
                }

                await _sessionManager.SendMessageCommand(
                    session.Id,
                    session.Id,
                    new MessageCommand
                    {
                        Header = header,
                        Text = text,
                        TimeoutMs = 5000
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DoujinStyle] Could not send session toast notification.");
        }
    }
}
