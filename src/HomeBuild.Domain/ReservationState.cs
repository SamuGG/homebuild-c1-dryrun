namespace HomeBuild.Domain;

public closed record class ReservationState
{
    public TransitionResult Expire(DateTimeOffset now) => this switch
    {
        Held held when now >= held.ExpiresAt => new Transitioned(new Expired(held.ExpiresAt)),
        Held => new InvalidTransition(this, "The hold period has not ended."),
        Expired or Confirmed or Cancelled => new InvalidTransition(this, "Only a held reservation can expire."),
    };

    public TransitionResult Confirm(DateTimeOffset depositPaidAt) => this switch
    {
        Held held when depositPaidAt < held.ExpiresAt => new Transitioned(new Confirmed(depositPaidAt)),
        Held => new InvalidTransition(this, "The hold period has ended."),
        Expired or Confirmed or Cancelled => new InvalidTransition(this, "Only a held reservation can be confirmed."),
    };

    public TransitionResult Cancel(DateTimeOffset now) => this switch
    {
        Held => new Transitioned(new Cancelled(now)),
        Expired or Confirmed or Cancelled => new InvalidTransition(this, "Only a held reservation can be cancelled."),
    };
}

public record Held(DateTimeOffset ExpiresAt) : ReservationState;

public sealed record Expired(DateTimeOffset At) : ReservationState;

public sealed record Confirmed(DateTimeOffset DepositPaidAt) : ReservationState;

public sealed record Cancelled(DateTimeOffset At) : ReservationState;

public sealed record Transitioned(ReservationState To);

public sealed record InvalidTransition(ReservationState From, string Reason);

public union TransitionResult(Transitioned, InvalidTransition);
