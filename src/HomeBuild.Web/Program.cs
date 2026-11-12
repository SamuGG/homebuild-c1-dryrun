using HomeBuild.Web.Api;
using HomeBuild.Web.Auth;
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

// Signed-in calls: the BFF attaches the user's access token on the server (C5 Lab 4).
builder.Services.AddHomeBuildSignIn();
builder.Services.AddHttpClient<HomeApiClient>((sp, http) =>
        http.BaseAddress = sp.GetRequiredService<IOptions<CatalogueApiOptions>>().Value.BaseAddress)
    .AddHttpMessageHandler<AccessTokenHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
// .NET 11 protects form posts by Sec-Fetch-Site/Origin on its own, and its template no longer calls this. Without it,
// <AntiforgeryToken /> renders nothing and tokens are never checked. HomeBuild keeps tokens as a second layer.
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
