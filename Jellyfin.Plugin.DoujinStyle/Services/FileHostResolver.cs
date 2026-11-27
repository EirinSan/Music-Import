using System.Buffers;
using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoujinStyle.Services;

public sealed record DownloadedPayload(
    IReadOnlyList<string> FilePaths,
    string SourceHost);

/// <summary>
/// Resolves and downloads album archives or folders from external file hosts used by DoujinStyle
/// (MediaFire files &amp; folders, Google Drive, Mega.nz, and direct HTTP links).
/// </summary>
public sealed partial class FileHostResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FileHostResolver> _logger;

    public FileHostResolver(IHttpClientFactory httpClientFactory, ILogger<FileHostResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [GeneratedRegex(@"/file/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GDrivePathIdRegex();

    [GeneratedRegex(@"[?&]id=(?<id>[a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GDriveQueryIdRegex();

    [GeneratedRegex(@"mega(?:\.co)?\.nz/(?:file/(?<id>[a-zA-Z0-9_-]+)#(?<key>[a-zA-Z0-9_-]+)|#!(?<lid>[a-zA-Z0-9_-]+)!(?<lkey>[a-zA-Z0-9_-]+))", RegexOptions.IgnoreCase)]
    private static partial Regex MegaFileRegex();

    public async Task<DownloadedPayload> DownloadAsync(
        string externalUrl,
        string tempDirectory,
        long maxBytes,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempDirectory);
        var uri = new Uri(externalUrl);
        var host = uri.Host.ToLowerInvariant();

        if (host.Contains("mediafire.com", StringComparison.Ordinal))
        {
            var files = await DownloadMediaFireAsync(externalUrl, tempDirectory, maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
            return new DownloadedPayload(files, "MediaFire");
        }

        if (host.Contains("drive.google.com", StringComparison.Ordinal) || host.Contains("drive.usercontent.google.com", StringComparison.Ordinal))
        {
            var file = await DownloadGoogleDriveAsync(externalUrl, tempDirectory, maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
            return new DownloadedPayload([file], "Google Drive");
        }

        if (host.Contains("mega.nz", StringComparison.Ordinal) || host.Contains("mega.co.nz", StringComparison.Ordinal))
        {
            var file = await DownloadMegaFileAsync(externalUrl, tempDirectory, maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
            return new DownloadedPayload([file], "Mega.nz");
        }

        var directFile = await DownloadDirectHttpAsync(externalUrl, tempDirectory, fallbackFileName: null, maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
        return new DownloadedPayload([directFile], uri.Host);
    }

    public static string? ExtractMediaFireDirectUrlFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var dlButton = doc.DocumentNode.SelectSingleNode("//*[@id='downloadButton']");
        if (dlButton is not null)
        {
            var scrambled = dlButton.GetAttributeValue("data-scrambled-url", string.Empty);
            if (!string.IsNullOrWhiteSpace(scrambled))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(scrambled.Trim()));
                    if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        return decoded;
                    }
                }
                catch
                {
                    // Fall back to href attribute
                }
            }

            var href = HtmlEntity.DeEntitize(dlButton.GetAttributeValue("href", string.Empty)).Trim();
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !href.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                return href;
            }
        }

        var fallbackAnchor = doc.DocumentNode.SelectSingleNode("//a[contains(@href, 'download') and contains(@href, 'mediafire.com')]");
        return fallbackAnchor is not null
            ? HtmlEntity.DeEntitize(fallbackAnchor.GetAttributeValue("href", string.Empty)).Trim()
            : null;
    }

    public static string? ExtractGoogleDriveConfirmUrlFromHtml(string html, string fallbackFileId)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var form = doc.DocumentNode.SelectSingleNode("//form[@id='download-form']")
            ?? doc.DocumentNode.SelectSingleNode("//form[contains(@action, 'drive.usercontent.google.com/download')]")
            ?? doc.DocumentNode.SelectSingleNode("//form[.//input[@name='confirm']]");

        if (form is null)
        {
            return null;
        }

        var action = HtmlEntity.DeEntitize(form.GetAttributeValue("action", "https://drive.usercontent.google.com/download")).Trim();
        var inputs = form.SelectNodes(".//input[@name]");
        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (inputs is not null)
        {
            foreach (var input in inputs)
            {
                var name = input.GetAttributeValue("name", string.Empty);
                var val = HtmlEntity.DeEntitize(input.GetAttributeValue("value", string.Empty));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    queryParams[name] = val;
                }
            }
        }

        if (!queryParams.ContainsKey("id") && !string.IsNullOrWhiteSpace(fallbackFileId))
        {
            queryParams["id"] = fallbackFileId;
        }

        if (!queryParams.ContainsKey("export"))
        {
            queryParams["export"] = "download";
        }

        if (!queryParams.ContainsKey("confirm"))
        {
            queryParams["confirm"] = "t";
        }

        var qs = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return action.Contains('?', StringComparison.Ordinal) ? $"{action}&{qs}" : $"{action}?{qs}";
    }

    public static (string Handle, byte[] AesKey, byte[] Iv8) ParseMegaUrl(string url)
    {
        var match = MegaFileRegex().Match(url);
        if (!match.Success)
        {
            if (url.Contains("/folder/", StringComparison.OrdinalIgnoreCase) || url.Contains("#F!", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("Les dossiers Mega.nz partagés (/folder/) ne sont pas supportés directement ; seuls les liens de fichiers Mega sont supportés.");
            }

            throw new ArgumentException($"URL Mega.nz invalide : {url}", nameof(url));
        }

        var handle = !string.IsNullOrEmpty(match.Groups["id"].Value) ? match.Groups["id"].Value : match.Groups["lid"].Value;
        var keyB64 = !string.IsNullOrEmpty(match.Groups["key"].Value) ? match.Groups["key"].Value : match.Groups["lkey"].Value;

        var rawKey = Base64UrlDecode(keyB64);
        if (rawKey.Length < 32)
        {
            throw new InvalidOperationException("Clé de déchiffrement Mega.nz invalide (longueur < 32 octets).");
        }

        var aesKey = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            aesKey[i] = (byte)(rawKey[i] ^ rawKey[i + 16]);
        }

        var iv8 = new byte[8];
        Buffer.BlockCopy(rawKey, 16, iv8, 0, 8);
        return (handle, aesKey, iv8);
    }

    public static string? DecryptMegaAttributesFileName(string atBase64Url, byte[] aesKey)
    {
        try
        {
            var cipherBytes = Base64UrlDecode(atBase64Url);
            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            var text = Encoding.UTF8.GetString(plainBytes).TrimEnd('\0');
            if (!text.StartsWith("MEGA{", StringComparison.Ordinal))
            {
                return null;
            }

            var json = text[4..];
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("n", out var nameProp))
            {
                return nameProp.GetString();
            }
        }
        catch
        {
            // Ignore malformed attribute metadata
        }

        return null;
    }

    public static async Task DecryptMegaCtrStreamAsync(
        Stream encryptedInput,
        Stream plainOutput,
        byte[] aesKey,
        byte[] iv8,
        long? expectedSize,
        long maxBytes,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        byte[] counterBlock = new byte[16];
        byte[] keystreamBlock = new byte[16];
        Buffer.BlockCopy(iv8, 0, counterBlock, 0, 8);

        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            ulong blockIndex = 0;
            int offsetInBlock = 0;
            long totalRead = 0;
            int lastPercent = -1;

            while (true)
            {
                int read = await encryptedInput.ReadAsync(buffer.AsMemory(0, 65536), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new InvalidOperationException($"Archive trop volumineuse (> {maxBytes / (1024 * 1024)} Mo).");
                }

                for (int i = 0; i < read; i++)
                {
                    if (offsetInBlock == 0)
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(counterBlock.AsSpan(8, 8), blockIndex);
                        encryptor.TransformBlock(counterBlock, 0, 16, keystreamBlock, 0);
                        blockIndex++;
                    }

                    buffer[i] ^= keystreamBlock[offsetInBlock];
                    offsetInBlock = (offsetInBlock + 1) & 15;
                }

                await plainOutput.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                if (expectedSize is > 0 && onProgress is not null)
                {
                    int pct = (int)Math.Clamp(totalRead * 100L / expectedSize.Value, 0, 100);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        onProgress(pct);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<IReadOnlyList<string>> DownloadMediaFireAsync(
        string mediaFireUrl,
        string tempDirectory,
        long maxBytes,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (mediaFireUrl.Contains("/folder/", StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadMediaFireFolderAsync(mediaFireUrl, tempDirectory, maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
        }

        var singlePath = await DownloadMediaFireSingleFileAsync(mediaFireUrl, tempDirectory, maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
        return [singlePath];
    }

    private async Task<string> DownloadMediaFireSingleFileAsync(
        string pageUrl,
        string targetDirectory,
        long maxBytes,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(DoujinStyleClient.HttpClientName);
        using var resp = await client.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Sometimes MediaFire links have Direct Download enabled and immediately return binary bytes
        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var directName = ExtractFileNameFromHeadersOrUrl(resp.Content.Headers, resp.RequestMessage?.RequestUri ?? new Uri(pageUrl), "album.zip");
            var destPath = Path.Combine(targetDirectory, SanitizeFileName(directName));
            await CopyHttpContentWithProgressAsync(resp.Content, destPath, maxBytes, pct => onProgress?.Invoke(pct, "Téléchargement MediaFire..."), cancellationToken).ConfigureAwait(false);
            return destPath;
        }

        var html = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var directUrl = ExtractMediaFireDirectUrlFromHtml(html);
        if (string.IsNullOrWhiteSpace(directUrl))
        {
            throw new InvalidOperationException("Impossible d'extraire le lien de téléchargement direct depuis la page MediaFire.");
        }

        return await DownloadDirectHttpAsync(directUrl, targetDirectory, fallbackFileName: null, maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> DownloadMediaFireFolderAsync(
        string folderUrl,
        string tempDirectory,
        long maxBytes,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var segments = new Uri(folderUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folderIdx = Array.FindIndex(segments, s => s.Equals("folder", StringComparison.OrdinalIgnoreCase));
        if (folderIdx < 0 || folderIdx + 1 >= segments.Length)
        {
            throw new InvalidOperationException($"Clé de dossier MediaFire introuvable dans {folderUrl}");
        }

        var folderKey = segments[folderIdx + 1];
        var filesToDownload = new List<(string RelativeDir, string FileName, string PageUrl)>();
        await CollectMediaFireFolderFilesAsync(folderKey, string.Empty, filesToDownload, cancellationToken).ConfigureAwait(false);

        if (filesToDownload.Count == 0)
        {
            throw new InvalidOperationException("Le dossier MediaFire est vide ou protégé par mot de passe.");
        }

        var downloadedPaths = new List<string>(filesToDownload.Count);
        for (int i = 0; i < filesToDownload.Count; i++)
        {
            var item = filesToDownload[i];
            var subDir = string.IsNullOrEmpty(item.RelativeDir)
                ? tempDirectory
                : Path.Combine(tempDirectory, item.RelativeDir);
            Directory.CreateDirectory(subDir);

            int itemIndex = i;
            var filePath = await DownloadMediaFireSingleFileAsync(
                item.PageUrl,
                subDir,
                maxBytes,
                (pct, _) =>
                {
                    int overall = (int)(((itemIndex * 100.0) + pct) / filesToDownload.Count);
                    onProgress?.Invoke(overall, $"MediaFire ({itemIndex + 1}/{filesToDownload.Count}) : {item.FileName}");
                },
                cancellationToken).ConfigureAwait(false);

            downloadedPaths.Add(filePath);
        }

        return downloadedPaths;
    }

    private async Task CollectMediaFireFolderFilesAsync(
        string folderKey,
        string relativeDir,
        List<(string RelativeDir, string FileName, string PageUrl)> sink,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(DoujinStyleClient.HttpClientName);
        var filesApi = $"https://www.mediafire.com/api/1.5/folder/get_content.php?content_type=files&version=1.5&folder_key={Uri.EscapeDataString(folderKey)}&response_format=json";
        using var filesDoc = await JsonDocument.ParseAsync(await client.GetStreamAsync(filesApi, cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (filesDoc.RootElement.TryGetProperty("response", out var respElem)
            && respElem.TryGetProperty("folder_content", out var fcElem)
            && fcElem.TryGetProperty("files", out var filesArr)
            && filesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in filesArr.EnumerateArray())
            {
                var filename = f.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                var normalDl = f.TryGetProperty("links", out var links) && links.TryGetProperty("normal_download", out var nd)
                    ? nd.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(filename) && !string.IsNullOrWhiteSpace(normalDl))
                {
                    sink.Add((relativeDir, SanitizeFileName(filename), normalDl));
                }
            }
        }

        var foldersApi = $"https://www.mediafire.com/api/1.5/folder/get_content.php?content_type=folders&version=1.5&folder_key={Uri.EscapeDataString(folderKey)}&response_format=json";
        using var foldersDoc = await JsonDocument.ParseAsync(await client.GetStreamAsync(foldersApi, cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (foldersDoc.RootElement.TryGetProperty("response", out var fResp)
            && fResp.TryGetProperty("folder_content", out var fContent)
            && fContent.TryGetProperty("folders", out var foldersArr)
            && foldersArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var sub in foldersArr.EnumerateArray())
            {
                var subKey = sub.TryGetProperty("folderkey", out var fk) ? fk.GetString() : null;
                var subName = sub.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (!string.IsNullOrWhiteSpace(subKey) && !string.IsNullOrWhiteSpace(subName))
                {
                    var nextRel = string.IsNullOrEmpty(relativeDir) ? SanitizeFileName(subName) : Path.Combine(relativeDir, SanitizeFileName(subName));
                    await CollectMediaFireFolderFilesAsync(subKey, nextRel, sink, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<string> DownloadGoogleDriveAsync(
        string gdriveUrl,
        string tempDirectory,
        long maxBytes,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        string? fileId = null;
        var pathMatch = GDrivePathIdRegex().Match(gdriveUrl);
        if (pathMatch.Success)
        {
            fileId = pathMatch.Groups["id"].Value;
        }
        else
        {
            var queryMatch = GDriveQueryIdRegex().Match(gdriveUrl);
            if (queryMatch.Success)
            {
                fileId = queryMatch.Groups["id"].Value;
            }
        }

        var initialUrl = !string.IsNullOrWhiteSpace(fileId)
            ? $"https://drive.google.com/uc?id={Uri.EscapeDataString(fileId)}&export=download"
            : gdriveUrl;

        var client = _httpClientFactory.CreateClient(DoujinStyleClient.HttpClientName);
        using var resp = await client.GetAsync(initialUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = ExtractFileNameFromHeadersOrUrl(resp.Content.Headers, resp.RequestMessage?.RequestUri ?? new Uri(initialUrl), $"{fileId ?? "gdrive"}.zip");
            var destPath = Path.Combine(tempDirectory, SanitizeFileName(fileName));
            await CopyHttpContentWithProgressAsync(resp.Content, destPath, maxBytes, pct => onProgress?.Invoke(pct, "Téléchargement Google Drive..."), cancellationToken).ConfigureAwait(false);
            return destPath;
        }

        var html = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var confirmUrl = ExtractGoogleDriveConfirmUrlFromHtml(html, fileId ?? string.Empty);
        if (string.IsNullOrWhiteSpace(confirmUrl))
        {
            throw new InvalidOperationException("Google Drive a renvoyé une page HTML sans formulaire de téléchargement (quota dépassé ou fichier privé).");
        }

        return await DownloadDirectHttpAsync(confirmUrl, tempDirectory, $"{fileId ?? "gdrive"}.zip", maxBytes, onProgress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadMegaFileAsync(
        string megaUrl,
        string tempDirectory,
        long maxBytes,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var (handle, aesKey, iv8) = ParseMegaUrl(megaUrl);
        var client = _httpClientFactory.CreateClient(DoujinStyleClient.HttpClientName);

        var apiPayload = new object[] { new Dictionary<string, object> { ["a"] = "g", ["g"] = 1, ["p"] = handle } };
        using var apiResp = await client.PostAsJsonAsync("https://g.api.mega.co.nz/cs?id=1", apiPayload, cancellationToken).ConfigureAwait(false);
        apiResp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await apiResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Réponse inattendue de l'API Mega.nz.");
        }

        var item = doc.RootElement[0];
        if (item.ValueKind == JsonValueKind.Number)
        {
            throw new InvalidOperationException($"Erreur API Mega.nz (code {item.GetInt32()}) — lien expiré ou supprimé.");
        }

        var downloadUrl = item.GetProperty("g").GetString()
            ?? throw new InvalidOperationException("Mega.nz n'a pas fourni d'URL de flux chiffré.");
        long? size = item.TryGetProperty("s", out var sProp) && sProp.ValueKind == JsonValueKind.Number ? sProp.GetInt64() : null;
        if (size is > 0 && size.Value > maxBytes)
        {
            throw new InvalidOperationException($"Le fichier Mega.nz dépasse la limite autorisée ({size.Value / (1024 * 1024)} Mo).");
        }

        string fileName = $"{handle}.bin";
        if (item.TryGetProperty("at", out var atProp) && atProp.GetString() is { } atStr)
        {
            fileName = DecryptMegaAttributesFileName(atStr, aesKey) ?? fileName;
        }

        var destPath = Path.Combine(tempDirectory, SanitizeFileName(fileName));
        _logger.LogInformation("[DoujinStyle] Downloading & decrypting Mega.nz file '{File}' ({Bytes} bytes)", fileName, size);

        using var dlResp = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        dlResp.EnsureSuccessStatusCode();

        await using var encStream = await dlResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await DecryptMegaCtrStreamAsync(
            encStream,
            outStream,
            aesKey,
            iv8,
            size,
            maxBytes,
            pct => onProgress?.Invoke(pct, $"Déchiffrement Mega.nz ({pct}%)..."),
            cancellationToken).ConfigureAwait(false);

        return destPath;
    }

    private async Task<string> DownloadDirectHttpAsync(
        string url,
        string targetDirectory,
        string? fallbackFileName,
        long maxBytes,
        Action<int, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(DoujinStyleClient.HttpClientName);
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"L'hébergeur ({new Uri(url).Host}) a renvoyé une page HTML au lieu d'une archive binaire.");
        }

        var fileName = ExtractFileNameFromHeadersOrUrl(
            resp.Content.Headers,
            resp.RequestMessage?.RequestUri ?? new Uri(url),
            fallbackFileName ?? "album.bin");
        var destPath = Path.Combine(targetDirectory, SanitizeFileName(fileName));

        await CopyHttpContentWithProgressAsync(
            resp.Content,
            destPath,
            maxBytes,
            pct => onProgress?.Invoke(pct, $"Téléchargement ({pct}%)..."),
            cancellationToken).ConfigureAwait(false);

        return destPath;
    }

    private static async Task CopyHttpContentWithProgressAsync(
        HttpContent content,
        string destinationPath,
        long maxBytes,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        var totalLength = content.Headers.ContentLength;
        if (totalLength is > 0 && totalLength.Value > maxBytes)
        {
            throw new InvalidOperationException($"Fichier trop volumineux ({totalLength.Value / (1024 * 1024)} Mo > limite {maxBytes / (1024 * 1024)} Mo).");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long totalRead = 0;
            int lastPercent = -1;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, 65536), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new InvalidOperationException($"Téléchargement interrompu : dépassement de la limite ({maxBytes / (1024 * 1024)} Mo).");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                if (totalLength is > 0 && onProgress is not null)
                {
                    int pct = (int)Math.Clamp(totalRead * 100L / totalLength.Value, 0, 100);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        onProgress(pct);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ExtractFileNameFromHeadersOrUrl(HttpContentHeaders headers, Uri finalUri, string defaultName)
    {
        var cd = headers.ContentDisposition;
        var candidate = cd?.FileNameStar ?? cd?.FileName;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return Uri.UnescapeDataString(candidate.Trim('"', '\'', ' '));
        }

        var pathName = Path.GetFileName(finalUri.LocalPath);
        if (!string.IsNullOrWhiteSpace(pathName) && pathName.Contains('.', StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(pathName);
        }

        return defaultName;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 || ch == '/' || ch == '\\' ? '_' : ch);
        }

        var cleaned = sb.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned;
    }
}
