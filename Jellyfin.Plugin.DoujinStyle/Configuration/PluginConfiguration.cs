using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DoujinStyle.Configuration;

/// <summary>
/// Configuration for the DoujinStyle Importer Jellyfin plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether DoujinStyle results are injected into Jellyfin searches.
    /// </summary>
    public bool EnableSearchInjection { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of DoujinStyle albums returned per search query.
    /// </summary>
    public int MaxSearchResults { get; set; } = 12;

    /// <summary>
    /// Gets or sets the target directory where downloaded albums are extracted.
    /// Leave empty to automatically detect Jellyfin's Music library root folder.
    /// </summary>
    public string TargetMusicDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether favoriting (hearting) a DoujinStyle album or track starts downloading it.
    /// </summary>
    public bool DownloadOnFavorite { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether pressing Play on a DoujinStyle album or track starts downloading it.
    /// </summary>
    public bool DownloadOnPlay { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether simply opening a DoujinStyle album detail page automatically starts downloading it.
    /// </summary>
    public bool AutoDownloadOnOpen { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether only FLAC releases should be requested in DoujinStyle search filters.
    /// </summary>
    public bool OnlyFlac { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether missing ID3/Vorbis tags (Album, Artist, Genre) and cover.jpg should be populated after extraction.
    /// </summary>
    public bool WriteMissingAudioTags { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether toast notifications are sent to active Jellyfin client sessions on download start/completion.
    /// </summary>
    public bool SendSessionNotification { get; set; } = true;

    /// <summary>
    /// Gets or sets the base URL of DoujinStyle.
    /// </summary>
    public string DoujinStyleBaseUrl { get; set; } = "https://doujinstyle.com";

    /// <summary>
    /// Gets or sets an optional companion HTTP bridge URL (e.g. http://my-home-ip:8787)
    /// used when the Jellyfin server's datacenter/seedbox IP is blocked by anti-bot protection.
    /// </summary>
    public string BridgeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional API key sent as X-Bridge-Key to the companion bridge.
    /// </summary>
    public string BridgeApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum allowed archive download size in megabytes.
    /// </summary>
    public int MaxArchiveSizeMb { get; set; } = 2048;
}
