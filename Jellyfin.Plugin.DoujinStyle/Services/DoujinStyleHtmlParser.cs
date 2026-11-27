using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jellyfin.Plugin.DoujinStyle.Models;

namespace Jellyfin.Plugin.DoujinStyle.Services;

/// <summary>
/// Parses DoujinStyle HTML search grids, album pages, and download forms.
/// </summary>
public static partial class DoujinStyleHtmlParser
{
    [GeneratedRegex(@"id=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumIdRegex();

    [GeneratedRegex(@"url\(['""]?(?<path>[^)'""]+)['""]?\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssBackgroundUrlRegex();

    [GeneratedRegex(@"^\s*(?:(?:\d{1,2}\s*[-.]\s*)|(?:Track\s*\d+\s*[:.-]\s*)).+", RegexOptions.IgnoreCase)]
    private static partial Regex TrackLineRegex();

    /// <summary>
    /// Detects whether an HTML response is an anti-bot or CAPTCHA challenge page.
    /// Does NOT attempt to solve or bypass any challenge.
    /// </summary>
    public static bool IsChallengePage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (html.Contains("<title>One moment, please...</title>", StringComparison.OrdinalIgnoreCase)
            || html.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cf-chl-bypass", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/cdn-cgi/challenge-platform/", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static List<DoujinAlbum> ParseSearchResults(string html, string baseUrl)
    {
        var results = new List<DoujinAlbum>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return results;
        }

        var normalizedBase = baseUrl.TrimEnd('/');
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var gridBoxes = doc.DocumentNode.SelectNodes("//*[contains(concat(' ', normalize-space(@class), ' '), ' gridBox ')]");
        if (gridBoxes is null)
        {
            return results;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var box in gridBoxes)
        {
            var detailsNode = box.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' gridDetails ')]") ?? box;
            var links = detailsNode.SelectNodes(".//a[@href]");
            if (links is null || links.Count == 0)
            {
                continue;
            }

            HtmlNode? albumLink = null;
            string? albumId = null;
            foreach (var link in links)
            {
                var href = HtmlEntity.DeEntitize(link.GetAttributeValue("href", string.Empty));
                var match = AlbumIdRegex().Match(href);
                if (match.Success && (href.Contains("p=page", StringComparison.OrdinalIgnoreCase) || albumLink is null))
                {
                    albumLink = link;
                    albumId = match.Groups[1].Value;
                    break;
                }
            }

            if (albumLink is null || string.IsNullOrWhiteSpace(albumId) || !seenIds.Add(albumId))
            {
                continue;
            }

            var rawTitle = albumLink.GetAttributeValue("title", string.Empty);
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                rawTitle = albumLink.InnerText;
            }

            var title = CleanText(rawTitle);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            string artist = "Doujin Artist";
            var artistLink = detailsNode.SelectSingleNode(".//a[contains(@href, 'type=artist')]")
                ?? (links.Count > 1 ? links[1] : null);
            if (artistLink is not null)
            {
                var parsedArtist = CleanText(artistLink.InnerText);
                if (string.IsNullOrWhiteSpace(parsedArtist))
                {
                    parsedArtist = CleanText(artistLink.GetAttributeValue("title", string.Empty));
                }

                if (!string.IsNullOrWhiteSpace(parsedArtist))
                {
                    artist = parsedArtist;
                }
            }

            string? coverUrl = ExtractGridCoverUrl(box, normalizedBase);

            var tags = new List<string>();
            var tagNodes = detailsNode.SelectNodes(".//*[contains(@class, 'gridTinyTag')]//a | .//a[contains(@href, 'type=tag') or contains(@href, 'type=blanket')]");
            if (tagNodes is not null)
            {
                foreach (var tNode in tagNodes)
                {
                    if (ReferenceEquals(tNode, albumLink) || ReferenceEquals(tNode, artistLink))
                    {
                        continue;
                    }

                    var tag = CleanText(tNode.InnerText);
                    if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(tag);
                    }
                }
            }

            results.Add(new DoujinAlbum
            {
                Id = albumId,
                Title = title,
                Artist = artist,
                CoverUrl = coverUrl,
                PageUrl = $"{normalizedBase}/?p=page&type=1&id={albumId}",
                Tags = tags
            });
        }

        return results;
    }

    public static DoujinAlbum? ParseAlbumDetail(string html, string albumId, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var normalizedBase = baseUrl.TrimEnd('/');
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var h3 = doc.DocumentNode.SelectSingleNode("//h3");
        if (h3 is not null && CleanText(h3.InnerText).Contains("Insufficient information to display content", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var h2 = doc.DocumentNode.SelectSingleNode("//h2");
        var title = h2 is not null ? CleanText(h2.InnerText) : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string artist = "Doujin Artist";
        string? format = null;
        string? dateAdded = null;
        var tags = new List<string>();
        var tracks = new List<string>();

        // Cover art: #imgClick a[href] or .coverWrap[src]
        string? coverUrl = null;
        var imgClickAnchor = doc.DocumentNode.SelectSingleNode("//*[@id='imgClick']//a[@href]");
        if (imgClickAnchor is not null)
        {
            coverUrl = ResolveRelativeUrl(imgClickAnchor.GetAttributeValue("href", string.Empty), normalizedBase);
        }

        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            var coverImg = doc.DocumentNode.SelectSingleNode("//*[contains(@class, 'coverWrap')][@src] | //*[@id='imgClick']//img[@src]");
            if (coverImg is not null)
            {
                coverUrl = ResolveRelativeUrl(coverImg.GetAttributeValue("src", string.Empty), normalizedBase);
            }
        }

        // Metadata pairs: .pageSpan1 -> .pageSpan2
        var labelSpans = doc.DocumentNode.SelectNodes("//*[contains(@class, 'pageSpan1')]");
        if (labelSpans is not null)
        {
            foreach (var labelNode in labelSpans)
            {
                var key = CleanText(labelNode.InnerText).TrimEnd(':');
                var valueNode = FindNextElementSibling(labelNode);
                if (valueNode is null)
                {
                    continue;
                }

                if (key.Equals("Artist", StringComparison.OrdinalIgnoreCase) || key.Equals("Artists", StringComparison.OrdinalIgnoreCase))
                {
                    var artistLinks = valueNode.SelectNodes(".//a");
                    if (artistLinks is { Count: > 0 })
                    {
                        var names = artistLinks.Select(a => CleanText(a.InnerText)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                        if (names.Count > 0)
                        {
                            artist = string.Join(", ", names);
                        }
                    }
                    else
                    {
                        var parsed = CleanText(valueNode.InnerText);
                        if (!string.IsNullOrWhiteSpace(parsed))
                        {
                            artist = parsed;
                        }
                    }
                }
                else if (key.Equals("Tags", StringComparison.OrdinalIgnoreCase))
                {
                    var tagAnchors = valueNode.SelectNodes(".//a");
                    if (tagAnchors is not null)
                    {
                        foreach (var a in tagAnchors)
                        {
                            var t = CleanText(a.InnerText);
                            if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                            {
                                tags.Add(t);
                            }
                        }
                    }
                    else
                    {
                        foreach (var part in valueNode.InnerText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var t = CleanText(part);
                            if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                            {
                                tags.Add(t);
                            }
                        }
                    }
                }
                else if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                {
                    format = CleanText(valueNode.InnerText);
                }
                else if (key.Equals("Date Added", StringComparison.OrdinalIgnoreCase))
                {
                    dateAdded = CleanText(valueNode.InnerText);
                }
                else if (key.Contains("Tracklist", StringComparison.OrdinalIgnoreCase) || key.Contains("Track", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractLinesIntoTracks(valueNode, tracks);
                }
            }
        }

        if (tracks.Count == 0)
        {
            var trackContainer = doc.DocumentNode.SelectSingleNode("//*[contains(@class, 'tracklist') or @id='tracklist']");
            if (trackContainer is not null)
            {
                ExtractLinesIntoTracks(trackContainer, tracks);
            }
        }

        return new DoujinAlbum
        {
            Id = albumId,
            Title = title,
            Artist = artist,
            CoverUrl = coverUrl ?? $"{normalizedBase}/thumbs/{albumId}.jpg",
            PageUrl = $"{normalizedBase}/?p=page&type=1&id={albumId}",
            Format = format,
            DateAdded = dateAdded,
            Tags = tags,
            Tracks = tracks,
            DetailsLoaded = true
        };
    }

    public static Dictionary<string, string> ParseDownloadFormFields(string html, string albumId)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "1",
            ["id"] = albumId,
            ["source"] = "0",
            ["download_link"] = string.Empty
        };

        if (string.IsNullOrWhiteSpace(html))
        {
            return fields;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var form = doc.DocumentNode.SelectSingleNode("//form[@id='downloadForm']")
            ?? doc.DocumentNode.SelectSingleNode("//form[.//*[@id='downloadForm']]");
        if (form is null)
        {
            return fields;
        }

        var inputs = form.SelectNodes(".//input[@name]");
        if (inputs is not null)
        {
            foreach (var input in inputs)
            {
                var name = input.GetAttributeValue("name", string.Empty);
                var val = HtmlEntity.DeEntitize(input.GetAttributeValue("value", string.Empty));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    fields[name] = val;
                }
            }
        }

        return fields;
    }

    private static string? ExtractGridCoverUrl(HtmlNode box, string normalizedBase)
    {
        var styledNodes = box.SelectNodes(".//*[@style]");
        if (styledNodes is not null)
        {
            foreach (var node in styledNodes)
            {
                var style = HtmlEntity.DeEntitize(node.GetAttributeValue("style", string.Empty));
                var match = CssBackgroundUrlRegex().Match(style);
                if (match.Success)
                {
                    var rawPath = match.Groups["path"].Value.Trim();
                    if (rawPath.Contains("default_cover", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    if (rawPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || rawPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return rawPath;
                    }

                    if (rawPath.StartsWith("./thumbs/", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{normalizedBase}/{rawPath[2..]}";
                    }

                    if (rawPath.StartsWith("/thumbs/", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{normalizedBase}{rawPath}";
                    }

                    if (rawPath.StartsWith('/'))
                    {
                        return $"{normalizedBase}/thumbs{rawPath}";
                    }

                    return ResolveRelativeUrl(rawPath, normalizedBase);
                }
            }
        }

        var img = box.SelectSingleNode(".//img[@src]");
        if (img is not null)
        {
            var src = img.GetAttributeValue("src", string.Empty);
            if (!src.Contains("default_cover", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveRelativeUrl(src, normalizedBase);
            }
        }

        return null;
    }

    private static HtmlNode? FindNextElementSibling(HtmlNode node)
    {
        var current = node.NextSibling;
        while (current is not null)
        {
            if (current.NodeType == HtmlNodeType.Element)
            {
                return current;
            }

            current = current.NextSibling;
        }

        return null;
    }

    private static void ExtractLinesIntoTracks(HtmlNode container, List<string> tracks)
    {
        var htmlWithBreaks = container.InnerHtml
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</div>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</li>", "\n", StringComparison.OrdinalIgnoreCase);

        var tempDoc = new HtmlDocument();
        tempDoc.LoadHtml(htmlWithBreaks);
        var text = WebUtility.HtmlDecode(tempDoc.DocumentNode.InnerText);

        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = CleanText(rawLine);
            if (!string.IsNullOrWhiteSpace(cleaned) && (TrackLineRegex().IsMatch(cleaned) || tracks.Count > 0))
            {
                tracks.Add(cleaned);
            }
        }
    }

    private static string? ResolveRelativeUrl(string raw, string normalizedBase)
    {
        var decoded = HtmlEntity.DeEntitize(raw).Trim();
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        if (decoded.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return decoded;
        }

        if (decoded.StartsWith("./", StringComparison.Ordinal))
        {
            return $"{normalizedBase}/{decoded[2..]}";
        }

        if (decoded.StartsWith('/'))
        {
            return $"{normalizedBase}{decoded}";
        }

        return $"{normalizedBase}/{decoded}";
    }

    private static string CleanText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(input).Replace("&#45;", "-", StringComparison.Ordinal);
        return string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
