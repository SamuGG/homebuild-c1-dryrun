# HomeBuild

## Conventions
- Commits follow Conventional Commits (`feat:`, `fix:`, `chore:`, `ci:`, `refactor:`).
- Warnings are errors (`Directory.Build.props`). Do not add suppressions without a comment.
- Package versions live only in `Directory.Packages.props`; never put `Version` on a `PackageReference`.
- Never paste secrets or tokens into code, config, commits or logs.
- Never paste secrets or tokens into prompts (Claude Code or any AI tool): use placeholders such as `<stripe-secret-key>`. Decode only expired or `dotnet user-jwts` tokens.

## Build and test
- `dotnet build`
- `dotnet test`

## Architecture
HomeBuild is a modular monolith: `HomeBuild.Api` holds one module per business capability (`Modules/{Name}`, its own schema, `Add{Name}Module`/`Map{Name}Module`), and modules use each other only through published interfaces (`ICatalogueQueries`, `IUnitAvailability`) or after-commit domain events (`MilestoneCompleted`), never another module's internals or tables. Catalogue, Sales and Construction are vertical slices that may use their own `DbContext` directly; Payments is ports and adapters: rules and use cases in `HomeBuild.Payments.Core`, which references only `HomeBuild.Domain`, with adapters in the host or `HomeBuild.Payments.Infrastructure`. Aggregates (`BuildProgress`, `Reservation`) change only through their root and return results, not exceptions, for business rules. A PR that adds, removes or renames a module or an event between modules updates `docs/architecture/components.md` (`ArchitectureDiagramTests` fails otherwise), and a decision with lasting consequences gets an ADR in `docs/adr/` and a row in its index.

## Transactions and external calls (C8 Lab 4)
- The Npgsql execution strategy is on (retries transient failures). Every explicit transaction runs inside `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`, so a retry replays the whole unit of work; clear the change tracker at the start of the delegate. A `BeginTransaction` outside it fails at its first command.
- No external call (Stripe, S3, email) inside a database transaction. The order is: write intent, commit, call out, record the result. `OpenTransactions` enforces it for the Stripe adapter; new adapters call `EnsureNone` too.
- Keep transactions short; isolation stays Read Committed unless an ADR says otherwise.
