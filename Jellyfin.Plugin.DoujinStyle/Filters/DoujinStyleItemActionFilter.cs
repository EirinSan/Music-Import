using System.Text.Json;
using Jellyfin.Plugin.DoujinStyle.Models;
using Jellyfin.Plugin.DoujinStyle.Services;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoujinStyle.Filters;

/// <summary>
/// Handles item detail pages, track listings, Favorite (heart) triggers, PlaybackInfo, and audio stream triggers
/// for synthetic DoujinStyle items before Jellyfin's internal library validator rejects synthetic GUIDs.
/// </summary>
public sealed class DoujinStyleItemActionFilter : IAsyncActionFilter
{
    private readonly StubCache _stubCache;
    private readonly DoujinStyleClient _doujinClient;
    private readonly DownloadQueueService _downloadQueue;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<DoujinStyleItemActionFilter> _logger;

    public DoujinStyleItemActionFilter(
        StubCache stubCache,
        DoujinStyleClient doujinClient,
        DownloadQueueService downloadQueue,
        IAuthorizationContext authContext,
        ILogger<DoujinStyleItemActionFilter> logger)
    {
        _stubCache = stubCache;
        _doujinClient = doujinClient;
        _downloadQueue = downloadQueue;
        _authContext = authContext;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try
        {
            if (await TryHandleStubRequestAsync(context).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DoujinStyle] Error in DoujinStyleItemActionFilter for {Path}", context.HttpContext.Request.Path);
        }

        await next().ConfigureAwait(false);
    }

    private async Task<bool> TryHandleStubRequestAsync(ActionExecutingContext ctx)
    {
        var http = ctx.HttpContext;
        var path = http.Request.Path.Value ?? string.Empty;
        var ct = http.RequestAborted;

        // 1. Favorite routes: POST/DELETE /Users/{u}/FavoriteItems/{itemId} or /UserFavoriteItems/{itemId}
        if (IsFavoriteRoute(path, ctx, out var favItemId))
        {
            if (_stubCache.TryGetByGuid(favItemId, out var favKind, out var favAlbum, out _, out var favAlbumId)
                && favKind is DoujinStubKind.Album or DoujinStubKind.Track
                && !string.IsNullOrEmpty(favAlbumId))
            {
                bool isFavorite = HttpMethods.IsPost(http.Request.Method);
                if (isFavorite && (Plugin.Instance?.Configuration.DownloadOnFavorite ?? true))
                {
                    var userId = await TryGetUserIdAsync(http).ConfigureAwait(false);
                    await _downloadQueue.EnqueueAlbumAsync(favAlbumId, favAlbum, userId, ct).ConfigureAwait(false);
                }

                ctx.Result = new ObjectResult(new UserItemDataDto
                {
                    ItemId = favItemId,
                    Key = favItemId.ToString("N"),
                    IsFavorite = isFavorite
                });
                return true;
            }
        }

        // 2. UserData POST route: POST /Users/{u}/Items/{itemId}/UserData or /UserItems/{itemId}/UserData
        if (HttpMethods.IsPost(http.Request.Method)
            && path.EndsWith("/UserData", StringComparison.OrdinalIgnoreCase)
            && TryExtractRouteGuid(ctx, out var udItemId)
            && _stubCache.TryGetByGuid(udItemId, out var udKind, out var udAlbum, out _, out var udAlbumId)
            && udKind is DoujinStubKind.Album or DoujinStubKind.Track
            && !string.IsNullOrEmpty(udAlbumId))
        {
            bool wantsFav = await ReadIsFavoriteFromBodyAsync(http.Request, ct).ConfigureAwait(false);
            if (wantsFav && (Plugin.Instance?.Configuration.DownloadOnFavorite ?? true))
            {
                var userId = await TryGetUserIdAsync(http).ConfigureAwait(false);
                await _downloadQueue.EnqueueAlbumAsync(udAlbumId, udAlbum, userId, ct).ConfigureAwait(false);
            }

            ctx.Result = new ObjectResult(new UserItemDataDto
            {
                ItemId = udItemId,
                Key = udItemId.ToString("N"),
                IsFavorite = wantsFav
            });
            return true;
        }

        // 3. PlaybackInfo route: /Items/{itemId}/PlaybackInfo
        if (path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
            && TryExtractRouteGuid(ctx, out var pbId)
            && _stubCache.TryGetByGuid(pbId, out var pbKind, out var pbAlbum, out _, out var pbAlbumId)
            && pbKind is DoujinStubKind.Track or DoujinStubKind.Album
            && !string.IsNullOrEmpty(pbAlbumId))
        {
            if (Plugin.Instance?.Configuration.DownloadOnPlay ?? true)
            {
                var userId = await TryGetUserIdAsync(http).ConfigureAwait(false);
                await _downloadQueue.EnqueueAlbumAsync(pbAlbumId, pbAlbum, userId, ct).ConfigureAwait(false);
            }

            var wavBytes = DoujinDtoBuilder.GetConfirmationWavBytes();
            var mediaSource = DoujinDtoBuilder.BuildStubMediaSource(pbId, wavBytes.Length, 2 * TimeSpan.TicksPerSecond);
            ctx.Result = new ObjectResult(new PlaybackInfoResponse
            {
                MediaSources = [mediaSource],
                PlaySessionId = Guid.NewGuid().ToString("N")
            });
            return true;
        }

        // 4. Audio stream / download routes: /Audio/{id}/universal, /Audio/{id}/stream..., /Items/{id}/File, /Items/{id}/Download
        if (IsAudioStreamOrDownloadRoute(path)
            && TryExtractGuidFromRouteOrPath(ctx, path, out var streamId)
            && _stubCache.TryGetByGuid(streamId, out var streamKind, out var streamAlbum, out _, out var streamAlbumId)
            && streamKind is DoujinStubKind.Track or DoujinStubKind.Album
            && !string.IsNullOrEmpty(streamAlbumId))
        {
            if (Plugin.Instance?.Configuration.DownloadOnPlay ?? true)
            {
                var userId = await TryGetUserIdAsync(http).ConfigureAwait(false);
                await _downloadQueue.EnqueueAlbumAsync(streamAlbumId, streamAlbum, userId, ct).ConfigureAwait(false);
            }

            // If Jellyfin Web calls /Audio/{id}/universal?EnableRedirection=true, redirect cleanly preserving PathBase (/jellyfin)
            bool isUniversal = path.Contains("/universal", StringComparison.OrdinalIgnoreCase);
            bool wantsRedirect = isUniversal
                && http.Request.Query.TryGetValue("EnableRedirection", out var redir)
                && string.Equals(redir.ToString(), "true", StringComparison.OrdinalIgnoreCase);

            if (wantsRedirect)
            {
                var basePrefix = http.Request.PathBase.HasValue ? http.Request.PathBase.Value : string.Empty;
                var target = $"{basePrefix}/Audio/{streamId:N}/stream.wav";
                if (http.Request.Query.TryGetValue("api_key", out var ak) || http.Request.Query.TryGetValue("ApiKey", out ak))
                {
                    target += $"?api_key={Uri.EscapeDataString(ak.ToString())}";
                }

                ctx.Result = new RedirectResult(target, permanent: false);
                return true;
            }

            var wav = DoujinDtoBuilder.GetConfirmationWavBytes();
            ctx.Result = new FileContentResult(wav, "audio/wav")
            {
                EnableRangeProcessing = true
            };
            return true;
        }

        // 5. Playback progress / stopped reporting for synthetic tracks (/Sessions/Playing...) -> 204 NoContent so Jellyfin doesn't 404
        if (path.Contains("/Sessions/Playing", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(http.Request.Method))
        {
            if (await IsBodyReferencingStubAsync(http.Request, ct).ConfigureAwait(false))
            {
                ctx.Result = new NoContentResult();
                return true;
            }
        }

        if (!HttpMethods.IsGet(http.Request.Method))
        {
            return false;
        }

        // 6. Bulk item lookup: GET /Items?ids=guid1,guid2,...
        if (TryGetGuidListFromQuery(http, "ids", out var bulkIds) || TryGetGuidListFromQuery(http, "Ids", out bulkIds))
        {
            var stubDtos = new List<BaseItemDto>();
            foreach (var bid in bulkIds)
            {
                if (_stubCache.TryGetByGuid(bid, out var bKind, out var bAlbum, out var bTrackIdx, out var bAlbumId)
                    && bKind != DoujinStubKind.None)
                {
                    var dto = await BuildDtoForStubAsync(bid, bKind, bAlbum, bAlbumId, bTrackIdx, ct).ConfigureAwait(false);
                    if (dto is not null)
                    {
                        stubDtos.Add(dto);
                    }
                }
            }

            // Short-circuit when all requested IDs are DoujinStyle stubs (prevents Jellyfin throwing on unknown GUIDs)
            if (stubDtos.Count > 0 && stubDtos.Count == bulkIds.Count)
            {
                ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, stubDtos.Count, stubDtos));
                return true;
            }
        }

        // 6b. Playlist items route: GET /Playlists/{playlistId}/Items
        if (path.Contains("/Playlists/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase)
            && TryExtractGuidFromRouteOrPath(ctx, path, out var plRouteGuid)
            && _stubCache.TryGetByGuid(plRouteGuid, out var plRouteKind, out _, out var plFeedIdx, out _)
            && plRouteKind == DoujinStubKind.Playlist)
        {
            var qr = await BuildPlaylistItemsQueryResultAsync(plFeedIdx, ct).ConfigureAwait(false);
            ctx.Result = new ObjectResult(qr);
            return true;
        }

        // 7. Album / Artist / Playlist children: GET /Items?ParentId={stub} or albumIds={stub}
        if (TryExtractStubAlbumQuery(http, out var parentGuid, out var pKind, out var pAlbum, out var pAlbumId))
        {
            var includeTypes = DoujinStyleSearchFilter.ExtractIncludeItemTypes(http);
            bool wantsAudio = includeTypes.Count == 0 || includeTypes.Contains("Audio");
            bool wantsAlbums = includeTypes.Count == 0 || includeTypes.Contains("MusicAlbum");

            if (pKind == DoujinStubKind.Playlist && ushort.TryParse(pAlbumId, out var feedIndex))
            {
                var qr = await BuildPlaylistItemsQueryResultAsync(feedIndex, ct).ConfigureAwait(false);
                ctx.Result = new ObjectResult(qr);
                return true;
            }

            if (pKind == DoujinStubKind.Album && !string.IsNullOrEmpty(pAlbumId))
            {
                if (!wantsAudio)
                {
                    ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                    return true;
                }

                var detail = await SafeLoadAlbumAsync(pAlbumId, pAlbum, ct).ConfigureAwait(false);
                if (detail is null)
                {
                    ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                    return true;
                }

                var job = _downloadQueue.GetJob(pAlbumId);
                var tracks = DoujinDtoBuilder.BuildAlbumTrackDtos(detail, job);
                ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, tracks.Count, tracks));
                return true;
            }

            if (pKind == DoujinStubKind.Artist && _stubCache.TryGetArtistName(parentGuid, out var artistName) && !string.IsNullOrWhiteSpace(artistName))
            {
                if (!wantsAlbums)
                {
                    ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                    return true;
                }

                var albums = await SafeSearchArtistAsync(artistName, ct).ConfigureAwait(false);
                var dtos = albums.Select(a => DoujinDtoBuilder.BuildAlbumDto(a, _downloadQueue.GetJob(a.Id))).ToList();
                ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, dtos.Count, dtos));
                return true;
            }
        }

        // 8. Single-item lookup: GET /Users/{u}/Items/{itemId} or GET /Items/{itemId} (excluding /Images)
        if (!path.Contains("/Images", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("/Similar", StringComparison.OrdinalIgnoreCase)
            && TryExtractRouteGuid(ctx, out var singleGuid)
            && _stubCache.TryGetByGuid(singleGuid, out var sKind, out var sAlbum, out var sTrackIdx, out var sAlbumId)
            && sKind != DoujinStubKind.None)
        {
            if (sKind == DoujinStubKind.Album
                && !string.IsNullOrEmpty(sAlbumId)
                && (Plugin.Instance?.Configuration.AutoDownloadOnOpen ?? false))
            {
                var userId = await TryGetUserIdAsync(http).ConfigureAwait(false);
                await _downloadQueue.EnqueueAlbumAsync(sAlbumId, sAlbum, userId, ct).ConfigureAwait(false);
            }

            var dto = await BuildDtoForStubAsync(singleGuid, sKind, sAlbum, sAlbumId, sTrackIdx, ct).ConfigureAwait(false);
            if (dto is not null)
            {
                ctx.Result = new ObjectResult(dto);
                return true;
            }
        }

        // 9. /Items/{stub}/Similar -> empty QueryResult so album page doesn't 404
        if (path.EndsWith("/Similar", StringComparison.OrdinalIgnoreCase)
            && TryExtractRouteGuid(ctx, out var simGuid)
            && _stubCache.TryGetByGuid(simGuid, out var simKind, out _, out _, out _)
            && simKind != DoujinStubKind.None)
        {
            ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
            return true;
        }

        return false;
    }

    private async Task<BaseItemDto?> BuildDtoForStubAsync(
        Guid guid,
        DoujinStubKind kind,
        DoujinAlbum? cachedAlbum,
        string? albumId,
        ushort trackIndex,
        CancellationToken ct)
    {
        if (kind == DoujinStubKind.Playlist)
        {
            var feed = DoujinCatalogFeeds.GetByIndex(trackIndex);
            return feed is not null ? DoujinCatalogFeeds.BuildPlaylistDto(feed) : null;
        }

        if (kind == DoujinStubKind.Artist)
        {
            _stubCache.TryGetArtistName(guid, out var artistName);
            return DoujinDtoBuilder.BuildArtistDto(guid, artistName ?? "Doujin Artist");
        }

        if (string.IsNullOrEmpty(albumId))
        {
            return null;
        }

        var detail = await SafeLoadAlbumAsync(albumId, cachedAlbum, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        var job = _downloadQueue.GetJob(albumId);
        if (kind == DoujinStubKind.Album)
        {
            return DoujinDtoBuilder.BuildAlbumDto(detail, job);
        }

        if (kind == DoujinStubKind.Track)
        {
            var tracks = DoujinDtoBuilder.BuildAlbumTrackDtos(detail, job);
            return trackIndex < tracks.Count ? tracks[trackIndex] : tracks[0];
        }

        return null;
    }

    private async Task<QueryResult<BaseItemDto>> BuildPlaylistItemsQueryResultAsync(ushort feedIndex, CancellationToken ct)
    {
        var feed = DoujinCatalogFeeds.GetByIndex(feedIndex);
        if (feed is null)
        {
            return new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>());
        }

        try
        {
            var albums = await _doujinClient.BrowseAsync(feed.Key, 0, null, false, ct).ConfigureAwait(false);
            var items = new List<BaseItemDto>(albums.Count);
            int idx = 1;
            foreach (var album in albums)
            {
                var job = _downloadQueue.GetJob(album.Id);
                items.Add(DoujinDtoBuilder.BuildSingleTrackDto(
                    album,
                    0,
                    $"{album.Title} — {album.Artist}",
                    idx++,
                    job));
            }

            return new QueryResult<BaseItemDto>(0, items.Count, items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DoujinStyle] Could not load catalog feed {Feed}", feed.Key);
            return new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>());
        }
    }

    private async Task<DoujinAlbum?> SafeLoadAlbumAsync(string albumId, DoujinAlbum? fallback, CancellationToken ct)
    {
        try
        {
            return await _doujinClient.GetAlbumDetailAsync(albumId, ct).ConfigureAwait(false) ?? fallback;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DoujinStyle] Could not load full detail for #{Id}; using cached summary.", albumId);
            return fallback ?? new DoujinAlbum { Id = albumId, Title = $"DoujinStyle #{albumId}" };
        }
    }

    private async Task<IReadOnlyList<DoujinAlbum>> SafeSearchArtistAsync(string artistName, CancellationToken ct)
    {
        try
        {
            return await _doujinClient.SearchAsync(artistName, ct).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private bool TryExtractStubAlbumQuery(
        HttpContext http,
        out Guid matchedGuid,
        out DoujinStubKind kind,
        out DoujinAlbum? album,
        out string? albumId)
    {
        matchedGuid = Guid.Empty;
        kind = DoujinStubKind.None;
        album = null;
        albumId = null;

        foreach (var queryKey in new[] { "ParentId", "parentId", "albumIds", "AlbumIds", "artistIds", "ArtistIds", "albumArtistIds", "AlbumArtistIds" })
        {
            if (TryGetGuidListFromQuery(http, queryKey, out var guids))
            {
                foreach (var g in guids)
                {
                    if (_stubCache.TryGetByGuid(g, out kind, out album, out _, out albumId) && kind != DoujinStubKind.None)
                    {
                        matchedGuid = g;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsFavoriteRoute(string path, ActionExecutingContext ctx, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (!path.Contains("FavoriteItems", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryExtractRouteGuid(ctx, out itemId);
    }

    private static bool IsAudioStreamOrDownloadRoute(string path)
    {
        return path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/File", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/Download", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractRouteGuid(ActionExecutingContext ctx, out Guid guid)
    {
        foreach (var key in new[] { "itemId", "ItemId", "id", "Id" })
        {
            if (ctx.RouteData.Values.TryGetValue(key, out var val)
                && val is not null
                && Guid.TryParse(val.ToString(), out guid))
            {
                return true;
            }
        }

        guid = Guid.Empty;
        return false;
    }

    private static bool TryExtractGuidFromRouteOrPath(ActionExecutingContext ctx, string path, out Guid guid)
    {
        if (TryExtractRouteGuid(ctx, out guid))
        {
            return true;
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(segment, out guid))
            {
                return true;
            }
        }

        guid = Guid.Empty;
        return false;
    }

    private static bool TryGetGuidListFromQuery(HttpContext http, string key, out List<Guid> guids)
    {
        guids = [];
        if (!http.Request.Query.TryGetValue(key, out var values))
        {
            return false;
        }

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Guid.TryParse(part, out var g))
                {
                    guids.Add(g);
                }
            }
        }

        return guids.Count > 0;
    }

    private async Task<Guid?> TryGetUserIdAsync(HttpContext http)
    {
        try
        {
            var auth = await _authContext.GetAuthorizationInfo(http.Request).ConfigureAwait(false);
            if (auth is not null && auth.UserId != Guid.Empty)
            {
                return auth.UserId;
            }
        }
        catch
        {
            // Ignore auth resolution failure
        }

        return null;
    }

    private static async Task<bool> ReadIsFavoriteFromBodyAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            request.EnableBuffering();
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
            request.Body.Position = 0;
            if (doc.RootElement.TryGetProperty("IsFavorite", out var p) || doc.RootElement.TryGetProperty("isFavorite", out p))
            {
                return p.ValueKind == JsonValueKind.True;
            }
        }
        catch
        {
            // Ignore non-JSON body
        }

        return false;
    }

    private static async Task<bool> IsBodyReferencingStubAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            request.EnableBuffering();
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
            request.Body.Position = 0;
            if ((doc.RootElement.TryGetProperty("ItemId", out var p) || doc.RootElement.TryGetProperty("itemId", out p))
                && p.GetString() is { } idStr
                && Guid.TryParse(idStr, out var g)
                && DoujinGuidCodec.TryDecode(g, out var kind, out _, out _)
                && kind != DoujinStubKind.None)
            {
                return true;
            }
        }
        catch
        {
            // Ignore
        }

        return false;
    }
}
