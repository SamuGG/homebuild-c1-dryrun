namespace HomeBuild.Api.Modules.Aftercare;

/// <summary>BREAK (C7 Lab 7): a new module added without updating docs/architecture/components.md.</summary>
public static class AftercareModule
{
    public const string Name = "Aftercare";

    public static IServiceCollection AddAftercareModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapAftercareModule(this IEndpointRouteBuilder endpoints) => endpoints;
}
