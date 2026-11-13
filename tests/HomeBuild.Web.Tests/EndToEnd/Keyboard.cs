using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>C5's keyboard checklist, automated: every focusable element is reached by Tab, with visible focus.</summary>
internal static class Keyboard
{
    public static async Task<(int Focusable, int Reached, List<string> Problems)> CheckTabOrderAsync(IPage page)
    {
        // Number every element a keyboard user should reach (visible, enabled, not tabindex=-1).
        var focusable = await page.EvaluateAsync<int>("""
            () => {
              const all = [...document.querySelectorAll('a[href], button, input, select, textarea, [tabindex]')]
                .filter(e => e.tabIndex >= 0 && !e.disabled && e.getClientRects().length > 0 && e.type !== 'hidden'
                             && getComputedStyle(e).visibility !== 'hidden' && !e.closest('dialog:not([open])'));
              all.forEach((e, i) => e.dataset.kbd = i);
              return all.length;
            }
            """);

        var problems = new List<string>();
        var reached = new HashSet<string>();
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        for (var i = 0; i < focusable + 3; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            var focus = await page.EvaluateAsync<string[]>("""
                () => {
                  const e = document.activeElement, s = getComputedStyle(e);
                  const visible = (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0) || s.boxShadow !== 'none';
                  return [e.dataset.kbd ?? '', String(visible), e.tagName.toLowerCase() + ' "' + (e.textContent || e.value || e.id || '').trim().slice(0, 30) + '"'];
                }
                """);
            if (focus[0].Length > 0)
            {
                reached.Add(focus[0]);
                if (focus[1] != "true")
                {
                    problems.Add("no visible focus: " + focus[2]);
                }
            }
        }

        var missed = await page.EvaluateAsync<string[]>(
            "(reached) => [...document.querySelectorAll('[data-kbd]')].filter(e => !reached.includes(e.dataset.kbd)).map(e => e.tagName.toLowerCase() + ' \"' + (e.textContent || e.id || '').trim().slice(0, 30) + '\"')",
            reached.ToArray());
        problems.AddRange(missed.Select(m => "not reached by Tab: " + m));
        return (focusable, reached.Count, problems.Distinct().ToList());
    }
}
