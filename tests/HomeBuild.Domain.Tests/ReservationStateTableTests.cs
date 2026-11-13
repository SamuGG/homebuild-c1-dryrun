using static HomeBuild.Testing.Builders;

namespace HomeBuild.Domain.Tests;

/// <summary>
/// Every (state, action) pair of a reservation, with the expected result. Adding a state or an action fails
/// <see cref="Every_state_and_action_pair_has_a_row"/> until the table has its rows.
/// </summary>
public class ReservationStateTableTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromDays(7);
    private static readonly DateTimeOffset Expires = Now + Hold;

    // State to start from: a held reservation, or one already moved to Expired, Confirmed or Cancelled.
    private static readonly Dictionary<string, Func<Reservation>> States = new()
    {
        ["Held"] = () => AReservation().CreatedAt(Now).HeldFor(Hold).Build(),
        ["Expired"] = () => AReservation().CreatedAt(Now).HeldFor(Hold).Expired().Build(),
        ["Confirmed"] = () => AReservation().CreatedAt(Now).HeldFor(Hold).Confirmed().Build(),
        ["Cancelled"] = () => AReservation().CreatedAt(Now).HeldFor(Hold).Cancelled().Build(),
    };

    private static readonly Dictionary<string, Func<Reservation, DateTimeOffset, TransitionResult>> Actions = new()
    {
        ["Expire"] = (r, at) => r.Expire(at),
        ["Confirm"] = (r, at) => r.Confirm(at),
        ["Cancel"] = (r, at) => r.Cancel(at),
    };

    // (state, action, when, expected): "when" is "before expiry" or "at expiry"; expected is a state, or "invalid".
    public static TheoryData<string, string, string, string> Table => new()
    {
        Row("Held", "Expire", "before expiry", "invalid"),
        Row("Held", "Expire", "at expiry", "Expired"),
        Row("Held", "Confirm", "before expiry", "Confirmed"),
        Row("Held", "Confirm", "at expiry", "invalid"),
        Row("Held", "Cancel", "before expiry", "Cancelled"),
        Row("Held", "Cancel", "at expiry", "Cancelled"),
        Row("Expired", "Expire", "at expiry", "invalid"),
        Row("Expired", "Confirm", "before expiry", "invalid"),
        Row("Expired", "Cancel", "at expiry", "invalid"),
        Row("Confirmed", "Expire", "at expiry", "invalid"),
        Row("Confirmed", "Confirm", "before expiry", "invalid"),
        Row("Confirmed", "Cancel", "at expiry", "Cancelled"),
        Row("Cancelled", "Expire", "at expiry", "invalid"),
        Row("Cancelled", "Confirm", "before expiry", "invalid"),
        Row("Cancelled", "Cancel", "at expiry", "invalid"),
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Reservation_state_table(string state, string action, string when, string expected)
    {
        var reservation = States[state]();
        var at = when == "at expiry" ? Expires : Expires - TimeSpan.FromSeconds(1);

        var result = Actions[action](reservation, at).Value;

        if (expected == "invalid")
        {
            Assert.IsType<InvalidTransition>(result);
            Assert.Equal(state, reservation.State.GetType().Name); // a refused action changes nothing
        }
        else
        {
            Assert.Equal(expected, Assert.IsType<Transitioned>(result).To.GetType().Name);
            Assert.Equal(expected, reservation.State.GetType().Name);
        }
    }

    [Fact]
    public void Every_state_and_action_pair_has_a_row()
    {
        var states = typeof(ReservationState).Assembly.GetExportedTypes()
            .Where(t => t.IsSubclassOf(typeof(ReservationState))).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        var actions = typeof(ReservationState).GetMethods()
            .Where(m => m.ReturnType == typeof(TransitionResult) && m.DeclaringType == typeof(ReservationState))
            .Select(m => m.Name).Order(StringComparer.Ordinal).ToList();
        var rows = Table.Select(r => ((ITheoryDataRow)r).GetData()).Select(d => ((string)d[0]!, (string)d[1]!)).ToHashSet();

        Assert.Equal(states, States.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(actions, Actions.Keys.Order(StringComparer.Ordinal));
        var missing = states.SelectMany(s => actions.Select(a => (s, a))).Where(pair => !rows.Contains(pair)).ToList();
        Assert.True(missing.Count == 0, "No row for: " + string.Join(", ", missing));
    }

    private static TheoryDataRow<string, string, string, string> Row(string state, string action, string when, string expected) =>
        new TheoryDataRow<string, string, string, string>(state, action, when, expected)
            .WithTestDisplayName($"State {state}, {action} {when} → {expected}");
}
