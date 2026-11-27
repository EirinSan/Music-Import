using System.Collections.Concurrent;
using System.Net.Http.Json;
using Jellyfin.Plugin.DoujinStyle.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoujinStyle.Services;

/// <summary>
/// Exception raised when DoujinStyle gates access behind an anti-bot or CAPTCHA challenge.
/// </summary>
public sealed class DoujinStyleChallengeException(string message) : Exception(message);

/// <summary>
/// HTTP client for searching DoujinStyle, loading album details, and resolving external download redirects.
/// Supports both direct HTTP requests and an optional user-hosted residential bridge.
/// </summary>
public sealed class DoujinStyleClient
{
    public const string HttpClientName = "DoujinStyleDirect";
    public const string NoRedirectHttpClientName = "DoujinStyleNoRedirect";

    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StubCache _stubCache;
    private readonly ILogger<DoujinStyleClient> _logger;
    private readonly ConcurrentDictionary<string, CachedSearch> _searchCache = new(StringComparer.OrdinalIgnoreCase);

    public DoujinStyleClient(
        IHttpClientFactory httpClientFactory,
        StubCache stubCache,
        ILogger<DoujinStyleClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _stubCache = stubCache;
        _logger = logger;
    }

    public string? LastWarning { get; private set; }

    public async Task<IReadOnlyList<DoujinAlbum>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            return [];
        }

        var cfg = Plugin.Instance?.Configuration;
        var maxResults = Math.Clamp(cfg?.MaxSearchResults ?? 12, 1, 50);
        var onlyFlac = cfg?.OnlyFlac ?? false;
        var cacheKey = $"{trimmed}|flac={onlyFlac}";

        if (_searchCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Results.Take(maxResults).ToList();
        }

        List<DoujinAlbum> albums;
        if (!string.IsNullOrWhiteSpace(cfg?.BridgeUrl))
        {
            albums = await SearchViaBridgeAsync(cfg.BridgeUrl, cfg.BridgeApiKey, trimmed, onlyFlac, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            albums = await SearchDirectAsync(GetBaseUrl(), trimmed, onlyFlac, cancellationToken).ConfigureAwait(false);
        }

        foreach (var album in albums)
        {
            _stubCache.PutAlbum(album);
        }

        _searchCache[cacheKey] = new CachedSearch(albums, DateTimeOffset.UtcNow.Add(SearchCacheTtl));
        LastWarning = null;
        return albums.Take(maxResults).ToList();
    }

    public async Task<DoujinAlbum?> GetAlbumDetailAsync(string albumId, CancellationToken cancellationToken)
    {
        if (_stubCache.TryGetByAlbumId(albumId, out var cached) && cached is { DetailsLoaded: true })
        {
            return cached;
        }

        var cfg = Plugin.Instance?.Configuration;
        DoujinAlbum? detail;
        if (!string.IsNullOrWhiteSpace(cfg?.BridgeUrl))
        {
            detail = await GetDetailViaBridgeAsync(cfg.BridgeUrl, cfg.BridgeApiKey, albumId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            detail = await GetDetailDirectAsync(GetBaseUrl(), albumId, cancellationToken).ConfigureAwait(false);
        }

        if (detail is not null)
        {
            if (cached is not null && string.IsNullOrWhiteSpace(detail.CoverUrl))
            {
                detail.CoverUrl = cached.CoverUrl;
            }

            _stubCache.PutAlbum(detail);
            return detail;
        }

        return cached;
    }

    public async Task<string> ResolveExternalDownloadUrlAsync(string albumId, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (!string.IsNullOrWhiteSpace(cfg?.BridgeUrl))
        {
            return await ResolveViaBridgeAsync(cfg.BridgeUrl, cfg.BridgeApiKey, albumId, cancellationToken).ConfigureAwait(false);
        }

        var baseUrl = GetBaseUrl();
        var pageUrl = $"{baseUrl}/?p=page&type=1&id={Uri.EscapeDataString(albumId)}";

        Dictionary<string, string> formFields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "1",
            ["id"] = albumId,
            ["source"] = "0",
            ["download_link"] = string.Empty
        };

        // Best-effort read of the album page first so Referer & exact form fields match
        try
        {
            var directClient = CreateDirectClient();
            var html = await directClient.GetStringAsync(pageUrl, cancellationToken).ConfigureAwait(false);
            if (DoujinStyleHtmlParser.IsChallengePage(html))
            {
                RecordChallengeWarning();
                throw new DoujinStyleChallengeException(
                    "DoujinStyle a renvoyé une vérification anti-bot à l'IP du serveur. Configurez un Bridge résidentiel optionnel si l'IP de votre seedbox est filtrée.");
            }

            formFields = DoujinStyleHtmlParser.ParseDownloadFormFields(html, albumId);
        }
        catch (DoujinStyleChallengeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DoujinStyle] Could not pre-load album page {Id} before POST; using default form fields.", albumId);
        }

        var noRedirectClient = _httpClientFactory.CreateClient(NoRedirectHttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/")
        {
            Content = new FormUrlEncodedContent(formFields)
        };
        req.Headers.Referrer = new Uri(pageUrl);

        using var resp = await noRedirectClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (resp.Headers.Location is { } location)
        {
            var resolved = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(baseUrl + "/"), location).ToString();
            _logger.LogInformation("[DoujinStyle] Resolved album {Id} -> external host {Host}", albumId, new Uri(resolved).Host);
            return resolved;
        }

        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (DoujinStyleHtmlParser.IsChallengePage(body))
        {
            RecordChallengeWarning();
            throw new DoujinStyleChallengeException(
                "DoujinStyle bloque actuellement l'IP du serveur par un contrôle anti-bot ('One moment, please...').");
        }

        throw new InvalidOperationException($"DoujinStyle n'a pas renvoyé de lien de redirection (HTTP {(int)resp.StatusCode}) pour l'album #{albumId}.");
    }

    public async Task<byte[]?> FetchImageBytesAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        try
        {
            var cfg = Plugin.Instance?.Configuration;
            if (!string.IsNullOrWhiteSpace(cfg?.BridgeUrl) && imageUrl.Contains("doujinstyle.com", StringComparison.OrdinalIgnoreCase))
            {
                var bridgeClient = CreateBridgeClient(cfg.BridgeApiKey);
                var proxyUrl = $"{cfg.BridgeUrl.TrimEnd('/')}/api/image?url={Uri.EscapeDataString(imageUrl)}";
                using var bridgeResp = await bridgeClient.GetAsync(proxyUrl, cancellationToken).ConfigureAwait(false);
                if (bridgeResp.IsSuccessStatusCode)
                {
                    return await bridgeResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var client = CreateDirectClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            req.Headers.Referrer = new Uri(GetBaseUrl() + "/");
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DoujinStyle] Cover fetch failed for {Url}", imageUrl);
            return null;
        }
    }

    private async Task<List<DoujinAlbum>> SearchDirectAsync(string baseUrl, string query, bool onlyFlac, CancellationToken cancellationToken)
    {
        var searchUrl = $"{baseUrl}/?p=search&source=1&type=blanket&result={Uri.EscapeDataString(query)}&page=0";
        if (onlyFlac)
        {
            searchUrl += "&format0=on";
        }

        var client = CreateDirectClient();
        using var resp = await client.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        var html = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (DoujinStyleHtmlParser.IsChallengePage(html))
        {
            RecordChallengeWarning();
            throw new DoujinStyleChallengeException(
                "DoujinStyle affiche une page de vérification anti-bot ('One moment, please...') pour l'IP de ce serveur.");
        }

        return DoujinStyleHtmlParser.ParseSearchResults(html, baseUrl);
    }

    private async Task<DoujinAlbum?> GetDetailDirectAsync(string baseUrl, string albumId, CancellationToken cancellationToken)
    {
        var pageUrl = $"{baseUrl}/?p=page&type=1&id={Uri.EscapeDataString(albumId)}";
        var client = CreateDirectClient();
        using var resp = await client.GetAsync(pageUrl, cancellationToken).ConfigureAwait(false);
        var html = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (DoujinStyleHtmlParser.IsChallengePage(html))
        {
            RecordChallengeWarning();
            throw new DoujinStyleChallengeException(
                "DoujinStyle affiche une page de vérification anti-bot ('One moment, please...') pour l'IP de ce serveur.");
        }

        return DoujinStyleHtmlParser.ParseAlbumDetail(html, albumId, baseUrl);
    }

    private async Task<List<DoujinAlbum>> SearchViaBridgeAsync(string bridgeUrl, string? apiKey, string query, bool onlyFlac, CancellationToken cancellationToken)
    {
        var client = CreateBridgeClient(apiKey);
        var url = $"{bridgeUrl.TrimEnd('/')}/api/search?q={Uri.EscapeDataString(query)}&only_flac={(onlyFlac ? "true" : "false")}";
        var list = await client.GetFromJsonAsync<List<DoujinAlbum>>(url, cancellationToken).ConfigureAwait(false);
        return list ?? [];
    }

    private async Task<DoujinAlbum?> GetDetailViaBridgeAsync(string bridgeUrl, string? apiKey, string albumId, CancellationToken cancellationToken)
    {
        var client = CreateBridgeClient(apiKey);
        var url = $"{bridgeUrl.TrimEnd('/')}/api/album/{Uri.EscapeDataString(albumId)}";
        using var resp = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return await resp.Content.ReadAsFromJsonOrDefaultAsync<DoujinAlbum>(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveViaBridgeAsync(string bridgeUrl, string? apiKey, string albumId, CancellationToken cancellationToken)
    {
        var client = CreateBridgeClient(apiKey);
        var url = $"{bridgeUrl.TrimEnd('/')}/api/resolve/{Uri.EscapeDataString(albumId)}";
        var payload = await client.GetFromJsonAsync<BridgeResolveResponse>(url, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload?.DownloadUrl))
        {
            throw new InvalidOperationException($"Le Bridge n'a pas renvoyé d'URL de téléchargement pour l'album #{albumId}.");
        }

        return payload.DownloadUrl;
    }

    private HttpClient CreateDirectClient() => _httpClientFactory.CreateClient(HttpClientName);

    private HttpClient CreateBridgeClient(string? apiKey)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        if (!string.IsNullOrWhiteSpace(apiKey) && !client.DefaultRequestHeaders.Contains("X-Bridge-Key"))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Bridge-Key", apiKey);
        }

        return client;
    }

    private static string GetBaseUrl()
    {
        var raw = Plugin.Instance?.Configuration.DoujinStyleBaseUrl;
        return string.IsNullOrWhiteSpace(raw) ? "https://doujinstyle.com" : raw.TrimEnd('/');
    }

    private void RecordChallengeWarning()
    {
        LastWarning = "DoujinStyle filtre actuellement l'IP du serveur par une page anti-bot ('One moment, please...'). Vous pouvez configurer le Bridge résidentiel optionnel dans les paramètres du plugin.";
        _logger.LogWarning("[DoujinStyle] {Warning}", LastWarning);
    }

    private sealed record CachedSearch(List<DoujinAlbum> Results, DateTimeOffset ExpiresAt);

    private sealed class BridgeResolveResponse
    {
        public string? DownloadUrl { get; set; }
    }
}

internal static class HttpContentJsonExtensions
{
    public static async Task<T?> ReadAsFromJsonOrDefaultAsync<T>(this HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return default;
        }
    }
}
