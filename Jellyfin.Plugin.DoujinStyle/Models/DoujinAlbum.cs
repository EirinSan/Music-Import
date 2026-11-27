using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.DoujinStyle.Models;

/// <summary>
/// Represents an album found on DoujinStyle.
/// </summary>
public sealed class DoujinAlbum
{
    public required string Id { get; init; }

    public required string Title { get; set; }

    public string Artist { get; set; } = "Unknown Artist";

    public string? CoverUrl { get; set; }

    public string? PageUrl { get; set; }

    public string? Format { get; set; }

    public string? DateAdded { get; set; }

    public List<string> Tags { get; set; } = [];

    public List<string> Tracks { get; set; } = [];

    public bool DetailsLoaded { get; set; }

    public Guid AlbumGuid => DoujinGuidCodec.EncodeAlbum(Id);

    public Guid ArtistGuid => DoujinGuidCodec.EncodeArtist(Artist);
}

/// <summary>
/// Encodes and decodes deterministic synthetic Jellyfin GUIDs for DoujinStyle albums and tracks.
/// Numeric DoujinStyle IDs are embedded directly into the GUID so favorites/plays still work
/// even after a Jellyfin server restart or cache expiration.
/// </summary>
public static class DoujinGuidCodec
{
    // Magic 32-bit header: 0xD0071571 ("DOUJINST")
    private const uint MagicPrefix = 0xD0071571;
    private const ushort KindAlbumNumeric = 0xA1B0;
    private const ushort KindTrackNumeric = 0x7AC0;
    private const ushort KindArtistHash = 0xA271;
    private const ushort KindPlaylistNumeric = 0xCA70;

    public static Guid EncodePlaylist(ushort feedIndex)
    {
        return BuildNumericGuid(KindPlaylistNumeric, feedIndex, feedIndex);
    }

    public static Guid EncodeAlbum(string doujinId)
    {
        if (ulong.TryParse(doujinId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
            && numericId <= 0x0000_FFFF_FFFF_FFFFUL)
        {
            return BuildNumericGuid(KindAlbumNumeric, 0, numericId);
        }

        return BuildHashedGuid("ds-album", doujinId);
    }

    public static Guid EncodeTrack(string doujinAlbumId, ushort trackIndex)
    {
        if (ulong.TryParse(doujinAlbumId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
            && numericId <= 0x0000_FFFF_FFFF_FFFFUL)
        {
            return BuildNumericGuid(KindTrackNumeric, trackIndex, numericId);
        }

        return BuildHashedGuid("ds-track", $"{doujinAlbumId}:{trackIndex}");
    }

    public static Guid EncodeArtist(string artistName)
    {
        var normalized = (artistName ?? string.Empty).Trim().ToLowerInvariant();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("ds-artist:" + normalized), hash);

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[..4], MagicPrefix);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(4, 2), KindArtistHash);
        ushort hi = (ushort)((BinaryPrimitives.ReadUInt16BigEndian(hash[..2]) & 0x0FFF) | 0x4000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(6, 2), hi);
        bytes[8] = (byte)((hash[2] & 0x3F) | 0x80);
        hash.Slice(3, 7).CopyTo(bytes.Slice(9, 7));
        return new Guid(bytes);
    }

    public static bool TryDecode(Guid guid, out DoujinStubKind kind, out string? numericAlbumId, out ushort trackIndex)
    {
        kind = DoujinStubKind.None;
        numericAlbumId = null;
        trackIndex = 0;

        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) != MagicPrefix)
        {
            return false;
        }

        var marker = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(4, 2));
        if (marker == KindArtistHash)
        {
            kind = DoujinStubKind.Artist;
            return true;
        }

        ushort rawTrack = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        trackIndex = (ushort)(rawTrack & 0x0FFF);

        if (marker == KindPlaylistNumeric)
        {
            kind = DoujinStubKind.Playlist;
            numericAlbumId = trackIndex.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (marker != KindAlbumNumeric && marker != KindTrackNumeric)
        {
            return false;
        }

        ulong id = ((ulong)bytes[10] << 40)
            | ((ulong)bytes[11] << 32)
            | ((ulong)bytes[12] << 24)
            | ((ulong)bytes[13] << 16)
            | ((ulong)bytes[14] << 8)
            | bytes[15];

        numericAlbumId = id.ToString(CultureInfo.InvariantCulture);
        kind = marker == KindAlbumNumeric ? DoujinStubKind.Album : DoujinStubKind.Track;
        return true;
    }

    private static Guid BuildNumericGuid(ushort kindMarker, ushort subIndex, ulong numericId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[..4], MagicPrefix);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(4, 2), kindMarker);
        ushort versionedIndex = (ushort)((subIndex & 0x0FFF) | 0x4000); // RFC 4122 v4 nibble
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(6, 2), versionedIndex);
        bytes[8] = 0x80; // RFC 4122 variant
        bytes[9] = 0x00;
        bytes[10] = (byte)((numericId >> 40) & 0xFF);
        bytes[11] = (byte)((numericId >> 32) & 0xFF);
        bytes[12] = (byte)((numericId >> 24) & 0xFF);
        bytes[13] = (byte)((numericId >> 16) & 0xFF);
        bytes[14] = (byte)((numericId >> 8) & 0xFF);
        bytes[15] = (byte)(numericId & 0xFF);
        return new Guid(bytes);
    }

    private static Guid BuildHashedGuid(string ns, string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{ns}:{value}"), hash);
        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

public enum DoujinStubKind
{
    None = 0,
    Album = 1,
    Track = 2,
    Artist = 3,
    Playlist = 4
}
