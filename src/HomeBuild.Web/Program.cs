using HomeBuild.Web.Catalogue;
using HomeBuild.Web.Components;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Interactive server is registered but used only by components that opt in (none yet): every page is static SSR.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<CatalogueApiOptions>()
    .BindConfiguration(CatalogueApiOptions.Section)
    .Validate(o => o.BaseAddress is { IsAbsoluteUri: true } && o.TenantId != Guid.Empty, "CatalogueApi needs an absolute BaseAddress and a TenantId.")
    .ValidateOnStart();
builder.Services.AddHttpClient<CatalogueClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<CatalogueApiOptions>>().Value;
    http.BaseAddress = options.BaseAddress;
    http.DefaultRequestHeaders.Add(CatalogueClient.TenantHeader, options.TenantId.ToString());
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
