using System.Net;
using System.Net.Http.Json;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace HomeBuild.Web.Tests;

/// <summary>Stands in for Cognito's token endpoint (refresh grant only); counts calls.</summary>
internal sealed class FakeCognito : HttpMessageHandler
{
    public const string Domain = "https://login.test";

    public static OpenIdConnectConfiguration Configuration => new()
    {
        Issuer = "https://cognito-idp.test/pool",
        AuthorizationEndpoint = Domain + "/oauth2/authorize",
        TokenEndpoint = Domain + "/oauth2/token",
    };

    private int refreshes;

    public int Refreshes => refreshes;

    public string? LastGrant { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.ToString() != Configuration.TokenEndpoint)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var form = await request.Content!.ReadAsStringAsync(cancellationToken);
        LastGrant = form;
        var count = Interlocked.Increment(ref refreshes);
        await Task.Delay(50, cancellationToken); // long enough for concurrent callers to pile up
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = $"refreshed-access-{count}", id_token = "id", expires_in = 3600, token_type = "Bearer" }),
        };
    }
}
