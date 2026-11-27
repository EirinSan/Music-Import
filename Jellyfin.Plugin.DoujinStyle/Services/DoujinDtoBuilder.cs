using System.Buffers.Binary;
using System.Net;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DoujinStyle.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.DoujinStyle.Services;

/// <summary>
/// Builds Jellyfin DTOs, SearchHints, SVG fallback covers, and confirmation audio streams for DoujinStyle items.
/// </summary>
public static class DoujinDtoBuilder
{
    private static readonly Lazy<byte[]> ConfirmationWavLazy = new(CreateConfirmationChimeWav);

    public static byte[] GetConfirmationWavBytes() => ConfirmationWavLazy.Value;

    public static BaseItemDto BuildAlbumDto(DoujinAlbum album, DownloadJob? job)
    {
        var albumGuid = album.AlbumGuid;
        var artistGuid = album.ArtistGuid;
        var artistPair = new NameGuidPair
        {
            Id = artistGuid,
            Name = album.Artist
        };

        var formatBadge = !string.IsNullOrWhiteSpace(album.Format) ? $" [{album.Format}]" : string.Empty;
        var statusLine = job is not null
            ? $"Statut : {FormatJobStatus(job)}"
            : "Astuce : Ajoutez cet album en Favori (♥) ou lancez la lecture (▶) pour le télécharger automatiquement dans Jellyfin.";

        var overviewParts = new List<string>
        {
            $"Source : DoujinStyle (#{album.Id})",
            $"Artiste / Cercle : {album.Artist}"
        };

        if (!string.IsNullOrWhiteSpace(album.Format))
        {
            overviewParts.Add($"Format : {album.Format}");
        }

        if (!string.IsNullOrWhiteSpace(album.DateAdded))
        {
            overviewParts.Add($"Ajouté le : {album.DateAdded}");
        }

        if (album.Tags.Count > 0)
        {
            overviewParts.Add($"Tags : {string.Join(", ", album.Tags)}");
        }

        overviewParts.Add(statusLine);

        return new BaseItemDto
        {
            Id = albumGuid,
            ServerId = Plugin.ServerSystemId,
            Name = $"[DoujinStyle] {album.Title}{formatBadge}",
            OriginalTitle = album.Title,
            Overview = string.Join("\n", overviewParts),
            Type = BaseItemKind.MusicAlbum,
            MediaType = MediaType.Audio,
            IsFolder = true,
            Artists = [album.Artist],
            AlbumArtist = album.Artist,
            AlbumArtists = [artistPair],
            ArtistItems = [artistPair],
            Genres = album.Tags.Take(8).ToArray(),
            Tags = ["DoujinStyle"],
            ImageTags = new Dictionary<ImageType, string>
            {
                [ImageType.Primary] = $"ds-{album.Id}"
            },
            PrimaryImageAspectRatio = 1.0,
            UserData = new UserItemDataDto
            {
                Key = albumGuid.ToString("N"),
                ItemId = albumGuid,
                IsFavorite = job is not null && job.Status != DownloadJobStatus.Failed,
                Played = job?.Status == DownloadJobStatus.Completed
            }
        };
    }

    public static List<BaseItemDto> BuildAlbumTrackDtos(DoujinAlbum album, DownloadJob? job)
    {
        var tracks = new List<BaseItemDto>();
        var formatInfo = !string.IsNullOrWhiteSpace(album.Format) ? $" ({album.Format})" : string.Empty;

        string actionTitle = job switch
        {
            { Status: DownloadJobStatus.Completed } => "✓ Album importé dans Jellyfin ! (Relancer si besoin)",
            { Status: DownloadJobStatus.Failed } => $"⚠ Réessayer le téléchargement ({job.ErrorMessage})",
            { } active => $"⏳ {FormatJobStatus(active)}",
            null => $"⬇ Télécharger l'album sur Jellyfin{formatInfo}"
        };

        tracks.Add(BuildSingleTrackDto(album, 0, actionTitle, indexNumber: 1, job));

        for (int i = 0; i < album.Tracks.Count && i < 99; i++)
        {
            tracks.Add(BuildSingleTrackDto(album, (ushort)(i + 1), album.Tracks[i], indexNumber: i + 2, job));
        }

        return tracks;
    }

    public static BaseItemDto BuildSingleTrackDto(DoujinAlbum album, ushort trackSlot, string trackTitle, int indexNumber, DownloadJob? job)
    {
        var trackGuid = DoujinGuidCodec.EncodeTrack(album.Id, trackSlot);
        var albumGuid = album.AlbumGuid;
        var artistGuid = album.ArtistGuid;
        var artistPair = new NameGuidPair { Id = artistGuid, Name = album.Artist };
        var wavBytes = GetConfirmationWavBytes();
        const long runTimeTicks = 2 * TimeSpan.TicksPerSecond;

        var mediaSource = BuildStubMediaSource(trackGuid, wavBytes.Length, runTimeTicks);

        return new BaseItemDto
        {
            Id = trackGuid,
            ServerId = Plugin.ServerSystemId,
            ParentId = albumGuid,
            AlbumId = albumGuid,
            Album = $"[DoujinStyle] {album.Title}",
            Name = trackTitle,
            IndexNumber = indexNumber,
            ParentIndexNumber = 1,
            Type = BaseItemKind.Audio,
            MediaType = MediaType.Audio,
            IsFolder = false,
            RunTimeTicks = runTimeTicks,
            Artists = [album.Artist],
            AlbumArtist = album.Artist,
            AlbumArtists = [artistPair],
            ArtistItems = [artistPair],
            ImageTags = new Dictionary<ImageType, string>
            {
                [ImageType.Primary] = $"ds-{album.Id}"
            },
            AlbumPrimaryImageTag = $"ds-{album.Id}",
            MediaSources = [mediaSource],
            UserData = new UserItemDataDto
            {
                Key = trackGuid.ToString("N"),
                ItemId = trackGuid,
                IsFavorite = job is not null && job.Status != DownloadJobStatus.Failed
            }
        };
    }

    public static BaseItemDto BuildArtistDto(Guid artistGuid, string artistName)
    {
        return new BaseItemDto
        {
            Id = artistGuid,
            ServerId = Plugin.ServerSystemId,
            Name = artistName,
            Type = BaseItemKind.MusicArtist,
            IsFolder = true,
            Tags = ["DoujinStyle"]
        };
    }

    public static MediaSourceInfo BuildStubMediaSource(Guid trackGuid, long sizeBytes, long runTimeTicks)
    {
        var idStr = trackGuid.ToString("N");
        return new MediaSourceInfo
        {
            Id = idStr,
            Name = "DoujinStyle Trigger",
            Protocol = MediaProtocol.Http,
            Container = "wav",
            Size = sizeBytes,
            Bitrate = 1411200,
            RunTimeTicks = runTimeTicks,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            IsInfiniteStream = false,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 0,
                    Codec = "pcm_s16le",
                    Channels = 2,
                    SampleRate = 44100,
                    BitRate = 1411200,
                    IsDefault = true
                }
            ]
        };
    }

    public static SearchHint BuildSearchHint(DoujinAlbum album)
    {
        var albumGuid = album.AlbumGuid;
        var formatBadge = !string.IsNullOrWhiteSpace(album.Format) ? $" [{album.Format}]" : string.Empty;
        return new SearchHint
        {
            Id = albumGuid,
            Name = $"[DoujinStyle] {album.Title}{formatBadge}",
            Type = BaseItemKind.MusicAlbum,
            MediaType = MediaType.Audio,
            Album = album.Title,
            AlbumArtist = album.Artist,
            Artists = [album.Artist],
            PrimaryImageTag = $"ds-{album.Id}",
            PrimaryImageAspectRatio = 1.0,
            IsFolder = true
        };
    }

    public static byte[] BuildFallbackSvgCover(string title, string artist)
    {
        var safeTitle = WebUtility.HtmlEncode(Truncate(title, 34));
        var safeArtist = WebUtility.HtmlEncode(Truncate(artist, 40));
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">
              <defs>
                <linearGradient id="bg" x1="0%" y1="0%" x2="100%" y2="100%">
                  <stop offset="0%" stop-color="#1a1033"/>
                  <stop offset="50%" stop-color="#2b1954"/>
                  <stop offset="100%" stop-color="#0f172a"/>
                </linearGradient>
              </defs>
              <rect width="512" height="512" fill="url(#bg)"/>
              <circle cx="256" cy="210" r="92" fill="none" stroke="#a855f7" stroke-width="6" opacity="0.85"/>
              <circle cx="256" cy="210" r="26" fill="#c084fc" opacity="0.9"/>
              <rect x="136" y="34" width="240" height="36" rx="18" fill="#7e22ce"/>
              <text x="256" y="58" text-anchor="middle" fill="#ffffff" font-family="sans-serif" font-size="18" font-weight="bold">DOUJINSTYLE</text>
              <text x="256" y="370" text-anchor="middle" fill="#f8fafc" font-family="sans-serif" font-size="24" font-weight="bold">{safeTitle}</text>
              <text x="256" y="412" text-anchor="middle" fill="#cbd5e1" font-family="sans-serif" font-size="19">{safeArtist}</text>
              <text x="256" y="468" text-anchor="middle" fill="#a855f7" font-family="sans-serif" font-size="15">♥ Favori ou ▶ Lecture pour importer</text>
            </svg>
            """;
        return Encoding.UTF8.GetBytes(svg);
    }

    private static string FormatJobStatus(DownloadJob job)
    {
        return job.Status switch
        {
            DownloadJobStatus.Queued => "En file d'attente...",
            DownloadJobStatus.ResolvingLink => "Résolution du lien DoujinStyle...",
            DownloadJobStatus.Downloading => $"Téléchargement en cours ({job.ProgressPercent}%)...",
            DownloadJobStatus.Extracting => "Extraction de l'archive vers la médiathèque...",
            DownloadJobStatus.ScanningLibrary => "Scan de la médiathèque Jellyfin...",
            DownloadJobStatus.Completed => "Importé dans Jellyfin ✓",
            DownloadJobStatus.Failed => $"Erreur : {job.ErrorMessage}",
            _ => job.StatusMessage ?? string.Empty
        };
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLen ? value : value[..(maxLen - 1)] + "…";
    }

    /// <summary>
    /// Synthesizes a clean 1.5-second two-note ascending chime (A4 -> E5) as 44.1 kHz 16-bit stereo WAV.
    /// </summary>
    private static byte[] CreateConfirmationChimeWav()
    {
        const int sampleRate = 44100;
        const int channels = 2;
        const int bitsPerSample = 16;
        const double durationSeconds = 1.5;
        int numSamples = (int)(sampleRate * durationSeconds);
        int dataSize = numSamples * channels * (bitsPerSample / 8);

        var wav = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), sampleRate * channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataSize);

        int offset = 44;
        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double freq = t < 0.22 ? 523.25 : 659.25; // C5 -> E5 pleasant soft chime
            double localT = t < 0.22 ? t : t - 0.22;
            double env = Math.Exp(-localT * 5.0) * Math.Min(1.0, localT * 80.0);
            short sample = (short)(Math.Sin(2.0 * Math.PI * freq * t) * env * 8500);

            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset + 2, 2), sample);
            offset += 4;
        }

        return wav;
    }
}
