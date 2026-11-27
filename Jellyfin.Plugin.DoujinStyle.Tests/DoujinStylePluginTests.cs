using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.DoujinStyle.Models;
using Jellyfin.Plugin.DoujinStyle.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.DoujinStyle.Tests;

public class DoujinStylePluginTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("22816")]
    [InlineData("987654")]
    public void GuidCodec_RoundTrips_AlbumAndTrackIds(string albumId)
    {
        var albumGuid = DoujinGuidCodec.EncodeAlbum(albumId);
        Assert.True(DoujinGuidCodec.TryDecode(albumGuid, out var aKind, out var decodedAlbumId, out var aTrackIdx));
        Assert.Equal(DoujinStubKind.Album, aKind);
        Assert.Equal(albumId, decodedAlbumId);
        Assert.Equal(0, aTrackIdx);

        var trackGuid = DoujinGuidCodec.EncodeTrack(albumId, 7);
        Assert.True(DoujinGuidCodec.TryDecode(trackGuid, out var tKind, out var decodedTrackAlbumId, out var tTrackIdx));
        Assert.Equal(DoujinStubKind.Track, tKind);
        Assert.Equal(albumId, decodedTrackAlbumId);
        Assert.Equal(7, tTrackIdx);

        var artistGuid = DoujinGuidCodec.EncodeArtist("ShibayanRecords");
        Assert.True(DoujinGuidCodec.TryDecode(artistGuid, out var arKind, out _, out _));
        Assert.Equal(DoujinStubKind.Artist, arKind);

        var playlistGuid = DoujinGuidCodec.EncodePlaylist(3);
        Assert.True(DoujinGuidCodec.TryDecode(playlistGuid, out var plKind, out var plId, out var plIdx));
        Assert.Equal(DoujinStubKind.Playlist, plKind);
        Assert.Equal("3", plId);
        Assert.Equal(3, plIdx);
        Assert.NotNull(DoujinCatalogFeeds.GetByIndex(3));

        Assert.False(DoujinGuidCodec.TryDecode(Guid.NewGuid(), out _, out _, out _));
    }

    [Fact]
    public void HtmlParser_ParsesSearchGrid_AndDetectsChallenge()
    {
        const string challengeHtml = "<!DOCTYPE html><html><head><title>One moment, please...</title></head><body></body></html>";
        Assert.True(DoujinStyleHtmlParser.IsChallengePage(challengeHtml));

        const string searchHtml = """
            <div id="container">
              <div class="gridBox">
                <div class="gridCover"><div style="background-image: url('/22816.jpg');"></div></div>
                <div class="gridDetails">
                  <a href="./?p=page&amp;type=1&amp;id=22816" title="Rebirth Story"><span>Rebirth Story</span></a>
                  <a href="./?p=search&amp;source=1&amp;type=artist&amp;result=FELT"><span>FELT</span></a>
                  <span class="gridTinyTag">
                    <a href="./?p=search&amp;result=Touhou">Touhou</a>,
                    <a href="./?p=search&amp;result=C85">C85</a>,
                    <a href="./?p=search&amp;result=Vocal&#45;Trance">Vocal&#45;Trance</a>
                  </span>
                </div>
              </div>
            </div>
            """;

        Assert.False(DoujinStyleHtmlParser.IsChallengePage(searchHtml));
        var results = DoujinStyleHtmlParser.ParseSearchResults(searchHtml, "https://doujinstyle.com");
        Assert.Single(results);
        Assert.Equal("22816", results[0].Id);
        Assert.Equal("Rebirth Story", results[0].Title);
        Assert.Equal("FELT", results[0].Artist);
        Assert.Equal("https://doujinstyle.com/thumbs/22816.jpg", results[0].CoverUrl);
        Assert.Contains("Touhou", results[0].Tags);
        Assert.Contains("Vocal-Trance", results[0].Tags);
    }

    [Fact]
    public void HtmlParser_ParsesAlbumDetail_AndDownloadForm()
    {
        const string detailHtml = """
            <html><body>
              <h2>TOHO BOSSA NOVA 2</h2>
              <div id="imgClick"><a href="/covers/15001_full.jpg"><img class="coverWrap" src="/thumbs/15001.jpg"/></a></div>
              <div class="pageWrap">
                <span class="pageSpan1">Artist:</span>
                <span class="pageSpan2"><a href="?p=search&amp;type=artist&amp;result=ShibayanRecords">ShibayanRecords</a></span>
              </div>
              <div class="pageWrap">
                <span class="pageSpan1">Format:</span>
                <span class="pageSpan2">FLAC / Lossless</span>
              </div>
              <div class="pageWrap">
                <span class="pageSpan1">Tags:</span>
                <span class="pageSpan2"><a href="#">Bossa Nova</a>, <a href="#">Touhou</a></span>
              </div>
              <div class="pageWrap">
                <span class="pageSpan1">Tracklist:</span>
                <span class="pageSpan2">01. MyonMyon<br/>02. Fall in the Dark</span>
              </div>
              <form id="downloadForm" method="post" action="/">
                <input type="hidden" name="type" value="1" />
                <input type="hidden" name="id" value="15001" />
                <input type="hidden" name="source" value="0" />
                <input type="hidden" name="download_link" value="" />
              </form>
            </body></html>
            """;

        var detail = DoujinStyleHtmlParser.ParseAlbumDetail(detailHtml, "15001", "https://doujinstyle.com");
        Assert.NotNull(detail);
        Assert.Equal("TOHO BOSSA NOVA 2", detail!.Title);
        Assert.Equal("ShibayanRecords", detail.Artist);
        Assert.Equal("FLAC / Lossless", detail.Format);
        Assert.Equal("https://doujinstyle.com/covers/15001_full.jpg", detail.CoverUrl);
        Assert.Equal(2, detail.Tracks.Count);
        Assert.Equal("01. MyonMyon", detail.Tracks[0]);

        var fields = DoujinStyleHtmlParser.ParseDownloadFormFields(detailHtml, "15001");
        Assert.Equal("15001", fields["id"]);
        Assert.Equal("1", fields["type"]);
    }

    [Fact]
    public void FileHostResolver_ParsesMediaFire_And_GoogleDrive()
    {
        var scrambled = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://download1502.mediafire.com/abc/file.rar"));
        var mfHtml = $"<html><body><a id=\"downloadButton\" href=\"javascript:void(0)\" data-scrambled-url=\"{scrambled}\">Download</a></body></html>";
        Assert.Equal("https://download1502.mediafire.com/abc/file.rar", FileHostResolver.ExtractMediaFireDirectUrlFromHtml(mfHtml));

        const string gdHtml = """
            <html><body>
              <form id="download-form" action="https://drive.usercontent.google.com/download" method="get">
                <input type="hidden" name="id" value="1AbCdEfGhIjK" />
                <input type="hidden" name="export" value="download" />
                <input type="hidden" name="confirm" value="t" />
                <input type="hidden" name="uuid" value="9988-7766" />
              </form>
            </body></html>
            """;
        var confirmUrl = FileHostResolver.ExtractGoogleDriveConfirmUrlFromHtml(gdHtml, "1AbCdEfGhIjK");
        Assert.NotNull(confirmUrl);
        Assert.Contains("drive.usercontent.google.com/download", confirmUrl!);
        Assert.Contains("uuid=9988-7766", confirmUrl);
        Assert.Contains("confirm=t", confirmUrl);
    }

    [Fact]
    public async Task FileHostResolver_DecryptsMegaUrl_Attributes_AndCtrStream()
    {
        byte[] rawKey = new byte[32];
        RandomNumberGenerator.Fill(rawKey);
        string keyB64Url = Convert.ToBase64String(rawKey).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var megaUrl = $"https://mega.nz/file/AbCdEf12#{keyB64Url}";
        var (handle, aesKey, iv8) = FileHostResolver.ParseMegaUrl(megaUrl);
        Assert.Equal("AbCdEf12", handle);

        // Encrypt a MEGA{"n":"Touhou_Album.rar"} attribute block with AES-128-CBC zero IV
        byte[] attrPlain = new byte[32];
        Encoding.UTF8.GetBytes("MEGA{\"n\":\"Touhou_Album.rar\"}").CopyTo(attrPlain, 0);
        using var cbc = Aes.Create();
        cbc.Key = aesKey;
        cbc.IV = new byte[16];
        cbc.Mode = CipherMode.CBC;
        cbc.Padding = PaddingMode.None;
        var attrCipher = cbc.CreateEncryptor().TransformFinalBlock(attrPlain, 0, attrPlain.Length);
        var attrB64Url = Convert.ToBase64String(attrCipher).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal("Touhou_Album.rar", FileHostResolver.DecryptMegaAttributesFileName(attrB64Url, aesKey));

        // Encrypt 100 KB of random payload with Mega AES-128-CTR and verify round-trip decryption
        byte[] originalPayload = new byte[100_123];
        RandomNumberGenerator.Fill(originalPayload);

        using var encryptedStream = new MemoryStream();
        await FileHostResolver.DecryptMegaCtrStreamAsync(
            new MemoryStream(originalPayload),
            encryptedStream,
            aesKey,
            iv8,
            originalPayload.Length,
            maxBytes: 10 * 1024 * 1024,
            onProgress: null,
            CancellationToken.None);

        encryptedStream.Position = 0;
        using var decryptedStream = new MemoryStream();
        await FileHostResolver.DecryptMegaCtrStreamAsync(
            encryptedStream,
            decryptedStream,
            aesKey,
            iv8,
            originalPayload.Length,
            maxBytes: 10 * 1024 * 1024,
            onProgress: null,
            CancellationToken.None);

        Assert.Equal(originalPayload, decryptedStream.ToArray());
    }

    [Fact]
    public async Task ArchiveExtractor_ExtractsNestedZip_UnwrapsFolder_WritesCover_AndTagsWav()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ds-test-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempRoot, "release.zip");
        var musicLibraryDir = Path.Combine(tempRoot, "Music");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(musicLibraryDir);

        try
        {
            var wavBytes = DoujinDtoBuilder.GetConfirmationWavBytes();
            using (var zipFs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(zipFs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("FELT - Rebirth Story [WAV]/01 Opening.wav");
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(wavBytes);
            }

            var extractor = new ArchiveExtractor(NullLogger<ArchiveExtractor>.Instance);
            var album = new DoujinAlbum
            {
                Id = "22816",
                Title = "Rebirth Story",
                Artist = "FELT",
                Tags = ["Touhou", "Vocal"]
            };

            byte[] dummyCover = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
            var finalDir = await extractor.ExtractAndOrganizeAsync(
                [zipPath],
                musicLibraryDir,
                album,
                dummyCover,
                writeMissingTags: true,
                CancellationToken.None);

            Assert.Equal(Path.Combine(musicLibraryDir, "FELT", "Rebirth Story"), finalDir);
            var placedTrack = Path.Combine(finalDir, "01 Opening.wav");
            var placedCover = Path.Combine(finalDir, "cover.jpg");
            Assert.True(File.Exists(placedTrack));
            Assert.True(File.Exists(placedCover));

            using var tfile = TagLib.File.Create(placedTrack);
            Assert.Equal("Rebirth Story", tfile.Tag.Album);
            Assert.Contains("FELT", tfile.Tag.AlbumArtists);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
