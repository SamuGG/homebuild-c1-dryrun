using System.Reflection;
using HomeBuild.Api.Modules.Catalogue;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

public sealed class ModuleTests
{
    private const string ModulesNamespace = "HomeBuild.Api.Modules";
    private static readonly Assembly Api = typeof(CatalogueModule).Assembly;

    [Fact]
    public void Program_maps_only_module_entry_points()
    {
        using var factory = new ApiFactory("Host=unused"); // builds the endpoint table only; no database is touched
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.NotEmpty(endpoints);

        // Lab 5: Program.cs also maps the OpenAPI document (Development and CI only). It is infrastructure, not a feature.
        endpoints = endpoints.Where(e => e.RoutePattern.RawText != "/openapi/{documentName}.json").ToList();
        Assert.All(endpoints, e =>
        {
            var tags = e.Metadata.GetOrderedMetadata<ITagsMetadata>().SelectMany(t => t.Tags);
            Assert.True(
                tags.Any(t => t is CatalogueModule.Name or Modules.Sales.SalesModule.Name),
                $"{e.RoutePattern.RawText} is not mapped through a module route group");
        });
    }

    [Fact]
    public void Adding_a_slice_changes_one_folder()
    {
        // A slice is a namespace one level below a module: Modules.<Module>.<Slice>.
        // No slice may use a type from another slice, so adding one touches only its folder
        // (plus one Map call in the module's entry point).
        foreach (var type in TypesUnder(ModulesNamespace))
        {
            var slice = SliceOf(type);
            if (slice is null)
            {
                continue;
            }

            var foreign = ReferencedTypes(type)
                .Where(t => SliceOf(t) is { } other && other != slice)
                .Select(t => t.FullName)
                .Distinct()
                .ToList();
            Assert.True(foreign.Count == 0, $"{type.FullName} (slice {slice}) uses {string.Join(", ", foreign)}");
        }
    }

    [Fact]
    public void Sales_does_not_reference_Catalogue_internals()
    {
        Type[] publicSurface = [typeof(ICatalogueQueries)];
        var catalogue = typeof(CatalogueModule).Namespace!;

        var leaks = TypesUnder(typeof(Modules.Sales.SalesModule).Namespace!)
            .SelectMany(t => ReferencedTypes(t).Select(r => (From: t, To: r)))
            .Where(x => x.To.Namespace?.StartsWith(catalogue, StringComparison.Ordinal) == true
                        && !publicSurface.Contains(x.To))
            .Select(x => $"{x.From.FullName} -> {x.To.FullName}")
            .Distinct()
            .ToList();

        Assert.True(leaks.Count == 0, "Sales uses Catalogue internals: " + string.Join("; ", leaks));
    }

    private static IEnumerable<Type> TypesUnder(string ns) =>
        Api.GetTypes().Where(t => t.Namespace is { } n && (n == ns || n.StartsWith(ns + ".", StringComparison.Ordinal)));

    private static string? SliceOf(Type type)
    {
        var parts = (type.Namespace ?? "").Split('.');
        return parts.Length == 5 && type.Namespace!.StartsWith(ModulesNamespace, StringComparison.Ordinal)
            ? type.Namespace
            : null;
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var direct = new List<Type>();
        if (type.BaseType is { } b)
        {
            direct.Add(b);
        }

        direct.AddRange(type.GetInterfaces());
        direct.AddRange(type.GetFields(all).Select(f => f.FieldType));
        direct.AddRange(type.GetProperties(all).Select(p => p.PropertyType));
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            direct.AddRange(method.GetParameters().Select(p => p.ParameterType));
            if (method is MethodInfo mi)
            {
                direct.Add(mi.ReturnType);
            }

            if (method.GetMethodBody() is { } body)
            {
                direct.AddRange(body.LocalVariables.Select(v => v.LocalType));
            }
        }

        return direct.SelectMany(Expand);
    }

    private static IEnumerable<Type> Expand(Type t)
    {
        if (t.HasElementType)
        {
            foreach (var e in Expand(t.GetElementType()!))
            {
                yield return e;
            }

            yield break;
        }

        yield return t;
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments().SelectMany(Expand))
            {
                yield return arg;
            }
        }
    }
}
