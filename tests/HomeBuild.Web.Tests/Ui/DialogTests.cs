using Bunit;
using HomeBuild.Web.Components.Ui;
using Microsoft.JSInterop;

namespace HomeBuild.Web.Tests.Ui;

public sealed class DialogTests : UiTestContext
{
    private readonly BunitJSModuleInterop module;

    public DialogTests() => module = JSInterop.SetupModule(Dialog.ModulePath);

    [Fact]
    public void A_closed_dialog_is_an_empty_element_and_shows_no_modal()
    {
        var cut = Render<Dialog>(p => p.Add(d => d.Title, "Reserve?").Add(d => d.Open, false));

        Assert.Empty(cut.Find("dialog").Children);
        Assert.Empty(module.Invocations);
    }

    [Fact]
    public void Opening_shows_a_native_modal_which_traps_focus_and_focuses_the_title()
    {
        var cut = Render<Dialog>(p => p.Add(d => d.Title, "Reserve?").Add(d => d.Open, true).AddChildContent("<p>Body</p>"));

        var dialog = cut.Find("dialog");
        var title = cut.Find("h2");
        Assert.Equal(title.Id, dialog.GetAttribute("aria-labelledby"));
        // showModal() makes everything outside the dialog inert: the browser, not our code, keeps Tab inside.
        var shown = module.VerifyInvoke("show");
        Assert.Equal(dialog.GetAttribute("blazor:elementreference"), FocusTarget(shown));
        var focus = Assert.Single(JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1).ToList());
        Assert.Equal(title.GetAttribute("blazor:elementreference"), FocusTarget(focus));
    }

    [Fact]
    public void Re_rendering_an_open_dialog_does_not_show_it_again()
    {
        var cut = Render<Dialog>(p => p.Add(d => d.Title, "Reserve?").Add(d => d.Open, true));

        cut.Render(p => p.Add(d => d.Title, "Reserve this home?"));

        Assert.Single(module.Invocations["show"]);
    }

    [Fact]
    public void Escape_raises_OnClose()
    {
        var closed = 0;
        var cut = Render<Dialog>(p => p.Add(d => d.Title, "Reserve?").Add(d => d.Open, true).Add(d => d.OnClose, () => closed++));

        // The browser turns Escape on a modal <dialog> into "cancel"; Dialog.razor.js calls back with this reference.
        var callback = (DotNetObjectReference<Dialog>)module.VerifyInvoke("show").Arguments[1]!;
        cut.InvokeAsync(() => callback.Value.CloseFromBrowser());

        Assert.Equal(1, closed);
    }

    [Fact]
    public void Closing_closes_the_native_dialog_so_focus_returns_to_the_opener()
    {
        var cut = Render<Dialog>(p => p.Add(d => d.Title, "Reserve?").Add(d => d.Open, true));

        cut.Render(p => p.Add(d => d.Open, false));

        // The same element is closed, not removed: removing an open modal skips the browser's focus return.
        Assert.Equal(FocusTarget(module.VerifyInvoke("show")), FocusTarget(module.VerifyInvoke("close")));
        Assert.Empty(cut.Find("dialog").Children);
    }

    [Fact]
    public void Close_button_raises_OnClose()
    {
        var closed = 0;
        var cut = Render<Dialog>(p => p.Add(d => d.Title, "Reserve?").Add(d => d.Open, true).Add(d => d.OnClose, () => closed++));

        cut.Find("button").Click();

        Assert.Equal(1, closed);
    }
}
