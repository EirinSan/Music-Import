namespace Jellyfin.Plugin.DoujinStyle.Models;

public enum DownloadJobStatus
{
    Queued,
    ResolvingLink,
    Downloading,
    Extracting,
    ScanningLibrary,
    Completed,
    Failed
}

/// <summary>
/// Tracks the live state of an album download and import job.
/// </summary>
public sealed class DownloadJob
{
    public required string AlbumId { get; init; }

    public required string AlbumTitle { get; set; }

    public required string Artist { get; set; }

    public string? CoverUrl { get; set; }

    public string? SourceHost { get; set; }

    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;

    public int ProgressPercent { get; set; }

    public string? StatusMessage { get; set; }

    public string? DestinationPath { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
