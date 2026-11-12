# UI components

The components in `src/HomeBuild.Web/Components/Ui/`. Each one works with a keyboard, shows a visible focus outline (`Styles.FocusRing`), and uses a native element before ARIA. Each has bUnit tests in `tests/HomeBuild.Web.Tests/Ui/`. To try them all, run the web app in Development and open `/dev/components`.

| Component | Element | Parameters | Accessibility rule (tested) |
| --- | --- | --- | --- |
| `Button` | `<button>` | `ChildContent` (required), `Variant` (`Primary`, `Secondary`), `Type` (default `button`), `Disabled`, `OnClick`, any extra attribute (for example `aria-pressed`) | `type="button"` by default, so it never submits a form by accident; visible focus |
| `Badge` | `<span>` | `Text` (required), `Variant` (`Neutral`, `Success`, `Warning`); `Badge.ForUnitStatus(status)` picks the variant for a unit | Always shows its text: colour is never the only signal (WCAG 1.4.1) |
| `Card` | `<article>` | `Title` (required), `Href` (makes the title the card's one link), `HeadingLevel` (2 or 3), `ChildContent`, extra attributes | Labelled by its own heading (`aria-labelledby`) |
| `PriceTag` | `<data>` | `Amount`, `Currency` (both required); text from `Money.Format` | Machine-readable `value` next to the formatted price |
| `Pager` | `<nav>` | `Next` (the API's cursor), `IsFirstPage`, `QueryParameter` (default `after`), `Label` (landmark name) | A named `nav` landmark; links that lead nowhere are left out rather than disabled |
| `DataTable<TItem>` | `<table>` | `Caption` (required), `CaptionVisible`, `Items`, `HeaderContent` (`<th scope="col">` cells), `RowTemplate`, `EmptyContent`, extra attributes | Has a `<caption>`; with no items it shows `EmptyContent` instead of an empty table |
| `Dialog` | `<dialog>` | `Open`, `Title` (required), `OnClose`, `ChildContent` | Opened with `showModal()`: the rest of the page is inert, so Tab stays inside; focus moves to the title; Escape and Close raise `OnClose`; closing returns focus to the opener |

## Notes

- **Static and interactive.** `Dialog` needs events and a small JS module (`Dialog.razor.js`), so it works only inside an interactive component. The others render the same in static SSR and interactive pages.
- **Class names.** Write Tailwind classes as complete strings, never built from parts (`"bg-" + colour`): the Tailwind CLI finds classes by scanning the source text.
- **Lifecycle.** Load data that depends on a parameter in `OnParametersSetAsync`, not `OnInitializedAsync`. `Catalogue/UnitCard` shows why: the same instance receives a new `UnitId`.
- **Contrast.** `ContrastTests` computes WCAG contrast for every text and background pair from the `@theme` tokens. Every pair is at least 4.5:1; the lowest is `danger-600` with white, at 5.40:1.
