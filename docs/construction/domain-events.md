# Domain events (C7 Lab 5)

`BuildProgress.Complete` records `MilestoneCompleted(UnitId, Milestone, CompletedAt)`. `DomainEventInterceptor` (an EF Core `SaveChangesInterceptor` and `IDbTransactionInterceptor` on `ConstructionDbContext`) collects the events in `SavingChanges` and dispatches them in `TransactionCommitted`, in process, in the request's scope:

| Module | Handler | Effect | Idempotent by |
| --- | --- | --- | --- |
| Payments | `RecordStagePaymentDue` (core use case) | A `payments.stage_payments_due` row when the development's schedule has a stage at that milestone | `ExistsAsync` check, then the unique index on (unit, milestone) |
| Extras | `UnlockExtrasOnFrame` | Records `ExtrasUnlocked` when the frame completes (in memory until C10) | One entry per unit |
| Notifications | `TellBuyerAboutMilestone` | A message to the unit's current buyer (in memory until C10's emails) | One message per unit and milestone |

A failed save or a rolled-back transaction drops the events. A handler that throws is logged (event 1201); the others still run and the request succeeds, because the milestone is already committed.

## Crash windows (for C9's outbox)

Every gap between the commit and the end of the last handler loses work if the process stops there:

1. **After the commit, before dispatch starts.** The milestone and its audit row are saved; no handler runs. Lost: the stage payment, the extras unlock, the buyer message.
2. **Between handlers.** Payments has recorded the stage payment; Extras and Notifications have not run. Lost: the later handlers' work.
3. **Inside a handler, between its own steps.** Payments' `ExistsAsync` and `AddAsync` are two round trips; a crash between them loses nothing (nothing written yet). A crash after Payments' commit but before the response loses nothing in Payments, but the caller may retry.
4. **A handler that throws** (not a crash, same effect): its work is lost and only a log line remains.
5. **In-memory stubs.** Extras' ledger and Notifications' messages live in memory: a restart loses all of them, not only in-flight ones.
6. **The response after dispatch.** If the connection drops after the commit, the site manager may retry the completion; the aggregate refuses it (`AlreadyCompleted`, 422), so nothing doubles, but the retry does not re-dispatch either.

C9 Lab 2 replaces dispatch-after-commit with an outbox written in the same transaction as the milestone, which closes windows 1, 2 and 4.

## C9 Lab 2: the outbox

`MilestoneCompleted` is now also written to `construction.outbox_messages` by the same `SaveChanges` as the milestone (`ConstructionDbContext.StageOutboxMessages`), so the message exists if and only if the milestone does. A relay (`OutboxRelay<TContext>`, a `BackgroundService` in the API) publishes it to the `homebuild-events` SNS topic: each pass selects up to 50 unpublished rows `ORDER BY occurred_at FOR UPDATE SKIP LOCKED`, publishes them, marks them published and commits. Payments (`DepositPaid`, in the webhook's transaction) and Sales (`ReservationExpired`, in the expiry sweep's transaction) have their own outboxes and relays.

What this closes, window by window:

| Window | With the outbox |
| --- | --- |
| 1. After the commit, before dispatch | **Closed** for consumers of the queue: the message is committed with the milestone and the relay publishes it whenever the process is back. |
| 2. Between handlers | **Closed** for queue consumers: each consumer has its own queue and works independently. |
| 3. Inside a handler | Unchanged: each consumer's own transaction (Lab 3's inbox) covers it. |
| 4. A handler that throws | **Closed** for queue consumers: the message stays in its queue and is retried (Lab 4), then parked in the DLQ. |
| 5. In-memory stubs | Unchanged until those modules store their state (C10). |
| 6. The response after dispatch | Unchanged (the aggregate refuses the retry). |
| New: the relay crashes after publishing, before marking | A **duplicate**, never a loss: the rows stay unpublished and go out again; consumers deduplicate by message ID (Lab 3's inbox). |

The windows close only for work that reads the queue. In Lab 2 the three in-process handlers above (`RecordStagePaymentDue`, `UnlockExtrasOnFrame`, `TellBuyerAboutMilestone`) still run after the commit as well, because no consumer reads their queues yet; each moves off in-process dispatch when its queue's consumer exists (notifications: Lab 7's Lambda).
