using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HomeBuild.Web.Auth;

/// <summary>
/// The current circuit's services, for code that runs outside the circuit's DI scope (an HttpClient's
/// DelegatingHandler is created in its own scope). Pattern from the ASP.NET Core docs: "Access server-side Blazor
/// services from a different DI scope". Interactive components never read HttpContext; this is how the token
/// handler finds the circuit's user instead.
/// </summary>
public sealed class CircuitServicesAccessor
{
    private static readonly AsyncLocal<IServiceProvider?> Current = new();

    public IServiceProvider? Services
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

/// <summary>Sets <see cref="CircuitServicesAccessor.Services"/> for each inbound circuit activity (events, renders).</summary>
public sealed class ServicesAccessorCircuitHandler(IServiceProvider services, CircuitServicesAccessor accessor) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            accessor.Services = services;
            await next(context);
            accessor.Services = null;
        };
}
