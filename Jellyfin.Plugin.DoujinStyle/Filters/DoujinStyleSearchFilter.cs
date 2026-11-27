using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DoujinStyle.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoujinStyle.Filters;

/// <summary>
/// Intercepts Jellyfin search responses (/Items and /Search/Hints) and appends matching albums from DoujinStyle.
/// Works seamlessly across Jellyfin Web, Jellyfin Mobile, Finamp, and Symfonium.
/// </summary>
public sealed class DoujinStyleSearchFilter : IAsyncResultFilter
{
    private static readonly HashSet<string> SupportedMusicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MusicAlbum", "Audio", "MusicArtist"
    };

    private readonly DoujinStyleClient _doujinClient;
    private readonly DownloadQueueService _downloadQueue;
    private readonly ILogger<DoujinStyleSearchFilter> _logger;

    public DoujinStyleSearchFilter(
        DoujinStyleClient doujinClient,
        DownloadQueueService downloadQueue,
        ILogger<DoujinStyleSearchFilter> logger)
    {
        _doujinClient = doujinClient;
        _downloadQueue = downloadQueue;
        _logger = logger;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        try
        {
            if (ShouldInjectSearch(context, out var searchTerm))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await AugmentSearchResponseAsync(context, searchTerm, cts.Token).ConfigureAwait(false);
            }
            else if (ShouldInjectBrowsePlaylists(context))
            {
                AugmentBrowsePlaylists(context);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[DoujinStyle] Search augmentation timed out or was cancelled.");
        }
        catch (DoujinStyleChallengeException ex)
        {
            _logger.LogWarning("[DoujinStyle] Search skipped due to challenge: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DoujinStyle] Unexpected error while augmenting search results.");
        }

        await next().ConfigureAwait(false);
    }

    private static bool ShouldInjectBrowsePlaylists(ResultExecutingContext ctx)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableBrowsePlaylists)
        {
            return false;
        }

        if (!HttpMethods.IsGet(ctx.HttpContext.Request.Method))
        {
            return false;
        }

        var q = ctx.HttpContext.Request.Query;
        if (q.ContainsKey("searchTerm") || q.ContainsKey("SearchTerm"))
        {
            return false;
        }

        var includeTypes = ExtractIncludeItemTypes(ctx.HttpContext);
        if (!includeTypes.Contains("Playlist"))
        {
            return false;
        }

        // Only inject on root playlist listings (not single-item or StartIndex > 0 pagination pages)
        if (q.TryGetValue("StartIndex", out var si) && int.TryParse(si, out var idx) && idx > 0)
        {
            return false;
        }

        return ctx.Result is ObjectResult { Value: QueryResult<BaseItemDto> };
    }

    private static void AugmentBrowsePlaylists(ResultExecutingContext ctx)
    {
        if (ctx.Result is not ObjectResult { Value: QueryResult<BaseItemDto> qr })
        {
            return;
        }

        var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
        var playlistDtos = DoujinCatalogFeeds.All
            .Select(DoujinCatalogFeeds.BuildPlaylistDto)
            .Where(dto => existingIds.Add(dto.Id))
            .ToList();

        if (playlistDtos.Count > 0)
        {
            var combined = qr.Items.Concat(playlistDtos).ToArray();
            qr.Items = combined;
            qr.TotalRecordCount = Math.Max(qr.TotalRecordCount, combined.Length);
        }
    }

    private static bool ShouldInjectSearch(ResultExecutingContext ctx, out string searchTerm)
    {
        searchTerm = string.Empty;
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableSearchInjection)
        {
            return false;
        }

        if (!HttpMethods.IsGet(ctx.HttpContext.Request.Method))
        {
            return false;
        }

        var query = ctx.HttpContext.Request.Query;
        if (query.TryGetValue("searchTerm", out var st) && !string.IsNullOrWhiteSpace(st))
        {
            searchTerm = st.ToString().Trim();
        }
        else if (query.TryGetValue("SearchTerm", out var st2) && !string.IsNullOrWhiteSpace(st2))
        {
            searchTerm = st2.ToString().Trim();
        }

        if (searchTerm.Length < 2)
        {
            return false;
        }

        var includeTypes = ExtractIncludeItemTypes(ctx.HttpContext);
        if (includeTypes.Count > 0 && !includeTypes.Any(t => SupportedMusicTypes.Contains(t)))
        {
            return false;
        }

        return ctx.Result is ObjectResult { Value: QueryResult<BaseItemDto> or SearchHintResult };
    }

    private async Task AugmentSearchResponseAsync(ResultExecutingContext ctx, string searchTerm, CancellationToken cancellationToken)
    {
        if (ctx.Result is not ObjectResult objectResult || objectResult.Value is null)
        {
            return;
        }

        var includeTypes = ExtractIncludeItemTypes(ctx.HttpContext);

        // If client searches specifically for MusicAlbum or global (empty IncludeItemTypes), inject Album DTOs.
        // If client asks ONLY for Audio (e.g. "Songs" tab in Jellyfin/Finamp), inject 1 downloadable track entry per DoujinStyle album!
        bool wantsAlbum = includeTypes.Count == 0 || includeTypes.Contains("MusicAlbum");
        bool wantsAudioOnly = !wantsAlbum && includeTypes.Contains("Audio");
        if (!wantsAlbum && !wantsAudioOnly)
        {
            return;
        }

        var doujinAlbums = await _doujinClient.SearchAsync(searchTerm, cancellationToken).ConfigureAwait(false);
        if (doujinAlbums.Count == 0)
        {
            return;
        }

        if (objectResult.Value is QueryResult<BaseItemDto> qr)
        {
            var existingTitles = qr.Items
                .Where(i => i.Type == BaseItemKind.MusicAlbum && !string.IsNullOrWhiteSpace(i.Name))
                .Select(i => NormalizeKey(i.AlbumArtist, i.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
            var injected = new List<BaseItemDto>();

            foreach (var album in doujinAlbums)
            {
                if (existingTitles.Contains(NormalizeKey(album.Artist, album.Title)))
                {
                    continue;
                }

                var job = _downloadQueue.GetJob(album.Id);
                if (wantsAlbum)
                {
                    var dto = DoujinDtoBuilder.BuildAlbumDto(album, job);
                    if (existingIds.Add(dto.Id))
                    {
                        injected.Add(dto);
                    }
                }
                else if (wantsAudioOnly)
                {
                    var trackDto = DoujinDtoBuilder.BuildSingleTrackDto(
                        album,
                        0,
                        $"[DoujinStyle] {album.Artist} — {album.Title}",
                        1,
                        job);
                    if (existingIds.Add(trackDto.Id))
                    {
                        injected.Add(trackDto);
                    }
                }
            }

            if (injected.Count > 0)
            {
                var combined = qr.Items.Concat(injected).ToArray();
                qr.Items = combined;
                qr.TotalRecordCount = Math.Max(qr.TotalRecordCount, combined.Length);
                _logger.LogInformation("[DoujinStyle] Appended {Count} DoujinStyle hits for query '{Query}'", injected.Count, searchTerm);
            }
        }
        else if (objectResult.Value is SearchHintResult hintResult)
        {
            var existingIds = hintResult.SearchHints.Select(h => h.Id).ToHashSet();
            var hints = new List<SearchHint>();
            foreach (var album in doujinAlbums)
            {
                var hint = DoujinDtoBuilder.BuildSearchHint(album);
                if (existingIds.Add(hint.Id))
                {
                    hints.Add(hint);
                }
            }

            if (hints.Count > 0)
            {
                var combined = hintResult.SearchHints.Concat(hints).ToArray();
                objectResult.Value = new SearchHintResult(
                    combined,
                    Math.Max(hintResult.TotalRecordCount, combined.Length));
            }
        }
    }

    internal static HashSet<string> ExtractIncludeItemTypes(HttpContext http)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "IncludeItemTypes", "includeItemTypes" })
        {
            if (http.Request.Query.TryGetValue(key, out var values))
            {
                foreach (var val in values)
                {
                    if (string.IsNullOrWhiteSpace(val))
                    {
                        continue;
                    }

                    foreach (var part in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        result.Add(part);
                    }
                }
            }
        }

        return result;
    }

    private static string NormalizeKey(string? artist, string? album)
    {
        return $"{(artist ?? string.Empty).Trim().ToLowerInvariant()}::{(album ?? string.Empty).Trim().ToLowerInvariant()}";
    }
}
