using System.Globalization;
using Jellyfin.Plugin.DoujinStyle.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DoujinStyle;

/// <summary>
/// Main entry point for the DoujinStyle Importer Jellyfin plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuidString = "91b47a12-5d3e-4c8f-a72e-681f0c4d9e21";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "DoujinStyle Importer";

    public override string Description =>
        "Recherche automatiquement des albums sur DoujinStyle depuis Jellyfin/Finamp/Symfonium et les télécharge + extrait dans la médiathèque.";

    public override Guid Id => Guid.Parse(PluginGuidString);

    public static Plugin? Instance { get; private set; }

    public static string ServerSystemId { get; set; } = string.Empty;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "DoujinStyleImporter",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}
