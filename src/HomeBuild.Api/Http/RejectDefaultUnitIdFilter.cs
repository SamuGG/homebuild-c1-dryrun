using HomeBuild.Domain;

namespace HomeBuild.Api.Http;

/// <summary>Rejects a bound <see cref="UnitId"/> that is the default (all-zero) ID before the handler runs.</summary>
internal sealed class RejectDefaultUnitIdFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.Arguments.OfType<UnitId>().Any(id => id == default))
        {
            return ValueTask.FromResult<object?>(TypedResults.Problem(
                title: "Invalid unit ID",
                detail: "The unit ID must not be empty.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        return next(context);
    }
}
