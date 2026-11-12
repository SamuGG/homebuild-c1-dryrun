using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HomeBuild.Web.Api;

/// <summary>An API error as RFC 9457 ProblemDetails: 400 has field errors, 409 and 422 a title and detail.</summary>
public sealed record ApiProblem(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("errors")] IReadOnlyDictionary<string, string[]>? Errors)
{
    public static async Task<ApiProblem> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ApiProblem>(cancellationToken)
                ?? new ApiProblem((int)response.StatusCode, response.ReasonPhrase, null, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return new ApiProblem((int)response.StatusCode, response.ReasonPhrase, null, null);
        }
    }
}

/// <summary>Either the API's answer or its problem.</summary>
public sealed record ApiResult<T>(T? Value, ApiProblem? Problem)
{
    public bool Succeeded => Problem is null;
}
