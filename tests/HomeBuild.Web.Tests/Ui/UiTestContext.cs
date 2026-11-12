using Bunit;

namespace HomeBuild.Web.Tests.Ui;

/// <summary>bUnit context for component tests; FocusAsync calls are recorded, not run.</summary>
public abstract class UiTestContext : BunitContext
{
    protected UiTestContext() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>The element a FocusAsync call targeted, as the id bUnit renders in blazor:elementReference.</summary>
    protected static string FocusTarget(JSRuntimeInvocation invocation) =>
        ((Microsoft.AspNetCore.Components.ElementReference)invocation.Arguments[0]!).Id;
}
