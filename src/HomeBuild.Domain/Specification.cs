using System.Linq.Expressions;

namespace HomeBuild.Domain;

/// <summary>
/// Specification: a named business rule about selection. The rule is written once, as an expression, so the same rule
/// filters objects in memory (<see cref="IsSatisfiedBy"/>) and translates to SQL when an EF Core query uses
/// <see cref="ToExpression"/>. Specifications combine with <see cref="And"/>, <see cref="Or"/> and <see cref="Not"/>.
/// </summary>
public abstract class Specification<T>
{
    private Func<T, bool>? compiled;

    public abstract Expression<Func<T, bool>> ToExpression();

    public bool IsSatisfiedBy(T candidate) => (compiled ??= ToExpression().Compile())(candidate);

    public Specification<T> And(Specification<T> other) => new Combined(this, other, Expression.AndAlso);

    public Specification<T> Or(Specification<T> other) => new Combined(this, other, Expression.OrElse);

    public Specification<T> Not() => new Negated(this);

    private sealed class Combined(Specification<T> left, Specification<T> right, Func<Expression, Expression, BinaryExpression> combine) : Specification<T>
    {
        public override Expression<Func<T, bool>> ToExpression()
        {
            var l = left.ToExpression();
            var r = right.ToExpression();
            // One parameter for both sides (not Expression.Invoke), so EF Core sees a single lambda it can translate.
            var body = combine(l.Body, new ReplaceParameter(r.Parameters[0], l.Parameters[0]).Visit(r.Body));
            return Expression.Lambda<Func<T, bool>>(body, l.Parameters[0]);
        }
    }

    private sealed class Negated(Specification<T> inner) : Specification<T>
    {
        public override Expression<Func<T, bool>> ToExpression()
        {
            var e = inner.ToExpression();
            return Expression.Lambda<Func<T, bool>>(Expression.Not(e.Body), e.Parameters[0]);
        }
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>Behind schedule: a milestone's planned date has passed and it is not completed (or skipped).</summary>
public sealed class BehindSchedule(DateOnly today) : Specification<BuildProgress>
{
    public override Expression<Func<BuildProgress, bool>> ToExpression() =>
        progress => progress.Milestones.Any(m =>
            m.PlannedOn < today && m.Status != MilestoneStatus.Completed && m.Status != MilestoneStatus.Skipped);
}

/// <summary>Practical completion is done.</summary>
public sealed class PracticallyComplete : Specification<BuildProgress>
{
    public override Expression<Func<BuildProgress, bool>> ToExpression() =>
        progress => progress.Milestones.Any(m => m.Milestone == Milestone.PracticalCompletion && m.Status == MilestoneStatus.Completed);
}

/// <summary>
/// Open pre-handover defects. No course has built snags yet, so the open defects C5 recorded on each milestone stand in
/// for them.
/// </summary>
public sealed class HasOpenDefects : Specification<BuildProgress>
{
    public override Expression<Func<BuildProgress, bool>> ToExpression() =>
        progress => progress.Milestones.Any(m => m.OpenDefects > 0);
}

/// <summary>Ready for handover: practical completion done and no open defects.</summary>
public static class BoardSpecifications
{
    public static Specification<BuildProgress> ReadyForHandover() => new PracticallyComplete().And(new HasOpenDefects().Not());
}
