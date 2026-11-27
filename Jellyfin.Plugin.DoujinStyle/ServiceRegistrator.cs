using System.Net;
using Jellyfin.Plugin.DoujinStyle.Filters;
using Jellyfin.Plugin.DoujinStyle.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DoujinStyle;

/// <summary>
/// Registers DoujinStyle Importer services, HTTP clients, background workers, and MVC filters into Jellyfin.
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        Plugin.ServerSystemId = applicationHost.SystemId;

        serviceCollection.AddHttpClient(DoujinStyleClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,fr;q=0.8,ja;q=0.7");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        });

        serviceCollection.AddHttpClient(DoujinStyleClient.NoRedirectHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,fr;q=0.8,ja;q=0.7");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });

        serviceCollection.AddSingleton<StubCache>();
        serviceCollection.AddSingleton<DoujinStyleClient>();
        serviceCollection.AddSingleton<FileHostResolver>();
        serviceCollection.AddSingleton<ArchiveExtractor>();
        serviceCollection.AddSingleton<DownloadQueueService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<DownloadQueueService>());

        serviceCollection.AddScoped<DoujinStyleImageFilter>();
        serviceCollection.AddScoped<DoujinStyleItemActionFilter>();
        serviceCollection.AddScoped<DoujinStyleSearchFilter>();

        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<DoujinStyleImageFilter>();
            options.Filters.AddService<DoujinStyleItemActionFilter>();
            options.Filters.AddService<DoujinStyleSearchFilter>();
        });
    }
}
