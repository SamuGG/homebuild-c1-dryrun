namespace HomeBuild.Web.Components.Ui;

/// <summary>Shared class lists. Complete class names only: Tailwind finds them by scanning this file.</summary>
internal static class Styles
{
    /// <summary>Visible keyboard focus (WCAG 2.2 SC 2.4.7), drawn only for keyboard focus, not mouse clicks.</summary>
    public const string FocusRing = "focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-700";

    public const string Link = "text-brand-700 underline-offset-2 hover:underline " + FocusRing;
}
