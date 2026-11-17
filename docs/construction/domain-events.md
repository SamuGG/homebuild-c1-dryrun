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
