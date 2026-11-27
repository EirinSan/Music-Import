using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DoujinStyle.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DoujinStyle.Services;

public sealed record CatalogFeedDefinition(
    ushort Index,
    string Key,
    string DisplayTitle,
    string Subtitle,
    string AccentHex);

/// <summary>
/// Curated DoujinStyle catalog sections surfaced both in the Jellyfin main-menu Catalogue page
/// and as browsable playlists inside Finamp, Symfonium, and Jellyfin Mobile.
/// </summary>
public static class DoujinCatalogFeeds
{
    public static readonly IReadOnlyList<CatalogFeedDefinition> All =
    [
        new(1, "recent", "🆕 [DoujinStyle] Nouveautés", "Derniers albums ajoutés sur DoujinStyle", "#a855f7"),
        new(2, "random", "🎲 [DoujinStyle] Sélection Aléatoire", "Découverte d'albums au hasard", "#ec4899"),
        new(3, "Touhou", "⛩️ [DoujinStyle] Touhou Project", "Arrangements et albums Touhou Project", "#ef4444"),
        new(4, "Vocaloid", "🎤 [DoujinStyle] Vocaloid", "Albums Hatsune Miku, GUMI, IA & Vocaloid", "#06b6d4"),
        new(5, "Hardcore", "⚡ [DoujinStyle] Hardcore & J-Core", "Speedcore, J-Core, UK Hardcore & Rhythm Games", "#f59e0b"),
        new(6, "Trance", "🌌 [DoujinStyle] Trance & Electronic", "Trance, House, EDM & Synth", "#3b82f6"),
        new(7, "Eurobeat", "🏎️ [DoujinStyle] Eurobeat", "Touhou Eurobeat & Initial D style", "#eab308"),
        new(8, "Rock", "🎸 [DoujinStyle] Rock & Metal", "Doujin Rock, Symphonic Metal & Guitare", "#f97316"),
        new(9, "Orchestral", "🎹 [DoujinStyle] Piano, Jazz & Orchestral", "Arrangements acoustiques, Bossa Nova & Orchestre", "#10b981"),
        new(10, "Original", "✨ [DoujinStyle] Créations Originales", "Albums doujin originaux (M3 / Comiket)", "#8b5cf6")
    ];

    public static CatalogFeedDefinition? GetByIndex(ushort index)
    {
        return All.FirstOrDefault(f => f.Index == index);
    }

    public static BaseItemDto BuildPlaylistDto(CatalogFeedDefinition feed)
    {
        var guid = DoujinGuidCodec.EncodePlaylist(feed.Index);
        return new BaseItemDto
        {
            Id = guid,
            ServerId = Plugin.ServerSystemId,
            Name = feed.DisplayTitle,
            Overview = $"{feed.Subtitle}\nOuvrez cette liste et appuyez sur ♥ Favori ou ▶ Lecture sur un titre pour télécharger l'album complet dans Jellyfin.",
            Type = BaseItemKind.Playlist,
            MediaType = MediaType.Audio,
            IsFolder = true,
            Tags = ["DoujinStyle"],
            ImageTags = new Dictionary<ImageType, string>
            {
                [ImageType.Primary] = $"ds-feed-{feed.Index}"
            },
            PrimaryImageAspectRatio = 1.0
        };
    }
}
