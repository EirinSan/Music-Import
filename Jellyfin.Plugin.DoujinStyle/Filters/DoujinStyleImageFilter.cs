using System.Collections.Concurrent;
using Jellyfin.Plugin.DoujinStyle.Models;
using Jellyfin.Plugin.DoujinStyle.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.DoujinStyle.Filters;

/// <summary>
/// Serves album cover art for synthetic DoujinStyle items (proxied &amp; cached in memory, with crisp SVG fallback).
/// </summary>
public sealed class DoujinStyleImageFilter : IAsyncActionFilter
{
    private const int MaxCachedImages = 250;
    private readonly StubCache _stubCache;
    private readonly DoujinStyleClient _doujinClient;
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    public DoujinStyleImageFilter(StubCache stubCache, DoujinStyleClient doujinClient)
    {
        _stubCache = stubCache;
        _doujinClient = doujinClient;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (!path.Contains("/Images/", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        Guid itemId = Guid.Empty;
        foreach (var key in new[] { "itemId", "ItemId", "id", "Id" })
        {
            if (context.RouteData.Values.TryGetValue(key, out var v) && v is not null && Guid.TryParse(v.ToString(), out itemId))
            {
                break;
            }
        }

        if (itemId == Guid.Empty)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!_stubCache.TryGetByGuid(itemId, out var kind, out var album, out var trackIdx, out var albumId))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (kind == DoujinStubKind.Playlist)
        {
            var feed = DoujinCatalogFeeds.GetByIndex(trackIdx);
            var plSvg = DoujinDtoBuilder.BuildFallbackSvgCover(
                feed?.DisplayTitle.Replace("[DoujinStyle] ", string.Empty, StringComparison.Ordinal) ?? "Catalogue",
                feed?.Subtitle ?? "DoujinStyle");
            context.Result = new FileContentResult(plSvg, "image/svg+xml");
            return;
        }

        if (kind is not (DoujinStubKind.Album or DoujinStubKind.Track) || string.IsNullOrEmpty(albumId))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (_imageCache.TryGetValue(albumId, out var cachedImg))
        {
            context.Result = new FileContentResult(cachedImg.Bytes, cachedImg.ContentType);
            return;
        }

        var coverUrl = album?.CoverUrl;
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            try
            {
                album = await _doujinClient.GetAlbumDetailAsync(albumId, context.HttpContext.RequestAborted).ConfigureAwait(false);
                coverUrl = album?.CoverUrl;
            }
            catch
            {
                // Fallback to SVG below
            }
        }

        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var bytes = await _doujinClient.FetchImageBytesAsync(coverUrl, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (bytes is { Length: > 128 })
            {
                var contentType = GuessImageMimeType(bytes);
                if (_imageCache.Count > MaxCachedImages)
                {
                    _imageCache.Clear();
                }

                _imageCache[albumId] = (bytes, contentType);
                context.Result = new FileContentResult(bytes, contentType);
                return;
            }
        }

        var svgBytes = DoujinDtoBuilder.BuildFallbackSvgCover(
            album?.Title ?? $"Album #{albumId}",
            album?.Artist ?? "DoujinStyle");
        context.Result = new FileContentResult(svgBytes, "image/svg+xml");
    }

    private static string GuessImageMimeType(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/jpeg";
    }
}
