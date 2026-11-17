using System.Reflection;
using System.Text;

namespace HomeBuild.Domain.Tests;

/// <summary>
/// C7 Lab 2: every (state, action) pair of a milestone, with the expected result. Every allowed transition succeeds
/// and every other is refused. A new state or action fails <see cref="Every_state_and_action_pair_has_a_row"/> until
/// the table has its rows, and <see cref="State_diagram_matches_the_transition_table"/> until the diagram is redrawn.
/// </summary>
public class MilestoneStateTableTests
{
    private static readonly DateOnly On = new(2026, 7, 1);
    private const string SiteManager = "site-manager";

    private static readonly Dictionary<string, MilestoneState> States = new()
    {
        ["NotStarted"] = new NotStarted(),
        ["InProgress"] = new InProgress(),
        ["Completed"] = new Completed(On, SiteManager),
        ["Skipped"] = new Skipped(),
    };

    private static readonly Dictionary<string, Func<MilestoneState, MilestoneTransition>> Actions = new()
    {
        ["Start"] = s => s.Start(),
        ["Complete"] = s => s.Complete(On, SiteManager),
        ["Skip"] = s => s.Skip(),
    };

    // (state, action, expected): expected is the state moved to, or "refused".
    public static TheoryData<string, string, string> Table => new()
    {
        { "NotStarted", "Start", "InProgress" },
        { "NotStarted", "Complete", "refused" },
        { "NotStarted", "Skip", "Skipped" },
        { "InProgress", "Start", "refused" },
        { "InProgress", "Complete", "Completed" },
        { "InProgress", "Skip", "refused" },
        { "Completed", "Start", "refused" },
        { "Completed", "Complete", "refused" },
        { "Completed", "Skip", "refused" },
        { "Skipped", "Start", "refused" },
        { "Skipped", "Complete", "refused" },
        { "Skipped", "Skip", "refused" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Transition_matches_the_table(string state, string action, string expected)
    {
        var result = Actions[action](States[state]);

        var actual = result.Value switch
        {
            MilestoneMoved moved => moved.To.GetType().Name,
            TransitionRefused refused => "refused",
            _ => throw new InvalidOperationException("Unexpected transition result."),
        };
        Assert.Equal(expected, actual);
        if (result.Value is TransitionRefused r)
        {
            Assert.Same(States[state], r.From);
        }
    }

    [Fact]
    public void Completing_records_the_date_and_the_site_manager()
    {
        var moved = Assert.IsType<MilestoneMoved>(new InProgress().Complete(On, SiteManager).Value);

        Assert.Equal(new Completed(On, SiteManager), moved.To);
    }

    [Fact]
    public void Every_state_and_action_pair_has_a_row()
    {
        var states = StateTypes().Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        var actions = typeof(MilestoneState).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(MilestoneTransition)).Select(m => m.Name).Order(StringComparer.Ordinal).ToList();
        var rows = Table.Select(r => ((ITheoryDataRow)r).GetData()).Select(d => ((string)d[0]!, (string)d[1]!)).ToHashSet();

        Assert.Equal(states, States.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(actions, Actions.Keys.Order(StringComparer.Ordinal));
        Assert.All(states.SelectMany(s => actions.Select(a => (s, a))), pair => Assert.Contains(pair, rows));
    }

    [Fact]
    public void State_diagram_matches_the_transition_table()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "construction", "milestone-states.md");
        var document = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = document.IndexOf("```mermaid\n", StringComparison.Ordinal) + "```mermaid\n".Length;
        var drawn = document[start..document.IndexOf("```", start, StringComparison.Ordinal)];

        Assert.True(drawn == Diagram(), $"docs/construction/milestone-states.md is out of date. Replace its mermaid block with:\n{Diagram()}");
    }

    /// <summary>The state diagram, drawn from the states' real behaviour (not from the table above).</summary>
    private static string Diagram()
    {
        var text = new StringBuilder("stateDiagram-v2\n    [*] --> NotStarted\n");
        foreach (var (name, state) in States)
        {
            var moves = Actions.Select(a => (Action: a.Key, Result: a.Value(state).Value)).Where(m => m.Result is MilestoneMoved).ToList();
            foreach (var (action, result) in moves)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    {name} --> {((MilestoneMoved)result!).To.GetType().Name} : {action}\n");
            }

            if (moves.Count == 0)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"    {name} --> [*]\n");
            }
        }

        return text.ToString();
    }

    private static IEnumerable<Type> StateTypes() =>
        typeof(MilestoneState).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(MilestoneState)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HomeBuild.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("HomeBuild.slnx not found above the test output.");
    }
}
