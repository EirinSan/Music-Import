using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Jellyfin.Plugin.DoujinStyle.Models;
using Jellyfin.Plugin.DoujinStyle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DoujinStyle.Controllers;

/// <summary>
/// REST endpoints used by the Jellyfin Dashboard plugin page to test connectivity,
/// search DoujinStyle, trigger manual downloads, and monitor live download progress.
/// </summary>
[ApiController]
[Authorize]
[Route("DoujinStyle")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class DoujinStyleController : ControllerBase
{
    private readonly DoujinStyleClient _doujinClient;
    private readonly DownloadQueueService _downloadQueue;
    private readonly StubCache _stubCache;

    public DoujinStyleController(
        DoujinStyleClient doujinClient,
        DownloadQueueService downloadQueue,
        StubCache stubCache)
    {
        _doujinClient = doujinClient;
        _downloadQueue = downloadQueue;
        _stubCache = stubCache;
    }

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var detectedLibraries = _downloadQueue.DetectMusicLibraryFolders();
        var effectiveDir = _downloadQueue.ResolveTargetMusicDirectory();
        return Ok(new
        {
            DetectedMusicFolders = detectedLibraries,
            EffectiveTargetDirectory = effectiveDir,
            LastWarning = _doujinClient.LastWarning,
            Jobs = _downloadQueue.GetAllJobs()
        });
    }

    [HttpGet("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            var sample = await _doujinClient.SearchAsync("Touhou", cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                Success = true,
                ResultCount = sample.Count,
                Message = $"Connexion réussie ! {sample.Count} album(s) trouvé(s) lors du test."
            });
        }
        catch (DoujinStyleChallengeException ex)
        {
            return Ok(new
            {
                Success = false,
                ChallengeBlocked = true,
                Message = ex.Message
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                Success = false,
                ChallengeBlocked = false,
                Message = $"Erreur de connexion : {ex.Message}"
            });
        }
    }

    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Search([FromQuery][Required] string query, CancellationToken cancellationToken)
    {
        try
        {
            var results = await _doujinClient.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                Success = true,
                Items = results
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                Success = false,
                Error = ex.Message,
                Items = Array.Empty<DoujinAlbum>()
            });
        }
    }

    [HttpPost("Download/{albumId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DownloadJob>> EnqueueDownload([FromRoute] string albumId, CancellationToken cancellationToken)
    {
        var cleanId = ExtractIdFromInput(albumId);
        _stubCache.TryGetByAlbumId(cleanId, out var cached);
        var job = await _downloadQueue.EnqueueAlbumAsync(cleanId, cached, null, cancellationToken).ConfigureAwait(false);
        return Ok(job);
    }

    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DownloadJob>> GetJobs()
    {
        return Ok(_downloadQueue.GetAllJobs());
    }

    private static string ExtractIdFromInput(string raw)
    {
        var decoded = Uri.UnescapeDataString(raw).Trim();
        var idx = decoded.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var sub = decoded[(idx + 3)..];
            var amp = sub.IndexOf('&', StringComparison.Ordinal);
            return amp >= 0 ? sub[..amp] : sub;
        }

        return decoded;
    }
}
