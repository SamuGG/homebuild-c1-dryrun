namespace HomeBuild.Domain;

/// <summary>Something that happened in the domain, named in the past tense.</summary>
#pragma warning disable CA1040 // a marker interface: handlers and the dispatcher are typed by it
public interface IDomainEvent;
#pragma warning restore CA1040

/// <summary>An aggregate that records events while it changes; the persistence layer dispatches them after commit.</summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

/// <summary>Reacts to an event. Handlers live in the modules that care; the publisher never knows them.</summary>
public interface IHandleDomainEvent<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>A unit's milestone completed (C7 Lab 5). Payments, Extras and Notifications react to it.</summary>
public sealed record MilestoneCompleted(UnitId UnitId, Milestone Milestone, DateOnly CompletedAt) : IDomainEvent;
