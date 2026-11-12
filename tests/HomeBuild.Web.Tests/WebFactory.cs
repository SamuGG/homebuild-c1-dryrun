using HomeBuild.Web.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Web.Tests;

/// <summary>The web app in memory, with the API replaced by <see cref="FakeCatalogueApi"/>.</summary>
internal sealed class WebFactory(FakeCatalogueApi api) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
            services.AddHttpClient<CatalogueClient>().ConfigurePrimaryHttpMessageHandler(() => api));
}
