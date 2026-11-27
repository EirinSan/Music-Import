using System.Collections.Concurrent;
using Jellyfin.Plugin.DoujinStyle.Models;

namespace Jellyfin.Plugin.DoujinStyle.Services;

/// <summary>
/// Thread-safe in-memory cache storing discovered DoujinStyle albums and artist names.
/// </summary>
public sealed class StubCache
{
    private const int MaxEntries = 2000;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, CacheItem> _albumsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _albumsByGuid = new();
    private readonly ConcurrentDictionary<Guid, string> _artistsByGuid = new();

    public void PutAlbum(DoujinAlbum album)
    {
        if (string.IsNullOrWhiteSpace(album.Id))
        {
            return;
        }

        if (_albumsById.Count > MaxEntries)
        {
            PruneExpired();
        }

        if (_albumsById.TryGetValue(album.Id, out var existing) && existing.Album.DetailsLoaded && !album.DetailsLoaded)
        {
            // Keep rich details if already loaded
            if (!string.IsNullOrWhiteSpace(album.CoverUrl) && string.IsNullOrWhiteSpace(existing.Album.CoverUrl))
            {
                existing.Album.CoverUrl = album.CoverUrl;
            }

            existing.ExpiresAt = DateTimeOffset.UtcNow.Add(EntryTtl);
            return;
        }

        _albumsById[album.Id] = new CacheItem(album, DateTimeOffset.UtcNow.Add(EntryTtl));
        _albumsByGuid[album.AlbumGuid] = album.Id;

        if (!string.IsNullOrWhiteSpace(album.Artist))
        {
            _artistsByGuid[album.ArtistGuid] = album.Artist;
        }
    }

    public bool TryGetByAlbumId(string albumId, out DoujinAlbum? album)
    {
        if (_albumsById.TryGetValue(albumId, out var item) && item.ExpiresAt > DateTimeOffset.UtcNow)
        {
            album = item.Album;
            return true;
        }

        album = null;
        return false;
    }

    public bool TryGetByGuid(Guid guid, out DoujinStubKind kind, out DoujinAlbum? album, out ushort trackIndex, out string? decodedAlbumId)
    {
        album = null;
        trackIndex = 0;
        decodedAlbumId = null;

        if (DoujinGuidCodec.TryDecode(guid, out kind, out decodedAlbumId, out trackIndex))
        {
            if (!string.IsNullOrEmpty(decodedAlbumId) && TryGetByAlbumId(decodedAlbumId, out album))
            {
                return true;
            }

            return kind != DoujinStubKind.None;
        }

        if (_albumsByGuid.TryGetValue(guid, out var mappedId) && TryGetByAlbumId(mappedId, out album))
        {
            kind = DoujinStubKind.Album;
            decodedAlbumId = mappedId;
            return true;
        }

        kind = DoujinStubKind.None;
        return false;
    }

    public bool TryGetArtistName(Guid artistGuid, out string? artistName)
    {
        return _artistsByGuid.TryGetValue(artistGuid, out artistName);
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _albumsById)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _albumsById.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class CacheItem(DoujinAlbum album, DateTimeOffset expiresAt)
    {
        public DoujinAlbum Album { get; } = album;

        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }
}
