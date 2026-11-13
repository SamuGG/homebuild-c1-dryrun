# Test strategy

HomeBuild is an API over PostgreSQL with a Blazor front end. Most expensive bugs live in the wiring: row-level security, transactions and constraints, authorisation, ProblemDetails shapes, and the contract the web app relies on. So the suite is shaped like a trophy rather than a pyramid:
- fast unit tests for the domain rules;
- most confidence from integration tests on a real database;
- a contract check on the API's OpenAPI documents;
- a boundary check on project references;
- a few end-to-end journeys.

Each layer is here because of the bugs it catches cheaply.

## Layers

| Layer | Project or step | Catches | Costs | Runs | Budget | Measured (12 Nov 2026) |
| --- | --- | --- | --- | --- | --- | --- |
| Unit | `tests/HomeBuild.Domain.Tests` | Domain rule mistakes: deposit limits, money arithmetic, reservation states and transitions, ID parsing | Milliseconds; no infrastructure. Breaks when a rule changes, which is the point | Every PR | 10 s | 29 tests: 0.9 s local, 0.2 s in the runner |
| Web components and in-memory host | `tests/HomeBuild.Web.Tests` (bUnit and `WebApplicationFactory` with a fake API) | Component rendering and events, accessibility rules, render modes, forms and ProblemDetails display, sign-in plumbing (cookie, token store, refresh, sign-out), antiforgery | Seconds; no browser, no database. A fake API can drift from the real one (C5 Lab 6 shipped one such bug) | Every PR | 20 s | 68 tests (4 opt-in skipped): 2.1–2.3 s local |
| Integration | `tests/HomeBuild.Api.Tests` (real PostgreSQL in Testcontainers, the API in `WebApplicationFactory`; see "Integration test infrastructure") | Wiring: RLS and tenancy, transactions and concurrency (the C3 race), constraints, EF Core translation, migrations, authorisation per role, ProblemDetails shapes, rate limits | Tens of seconds; Docker. The slowest layer, and the one that finds the most real bugs | Every PR | 90 s | 121 tests (5 opt-in skipped): 18.9–20.6 s local; the CI test step took 35 s |
| Contract | `contracts/openapi.json` (v1) and `contracts/openapi_v2.json`, written by the build and diffed in CI with `git diff --exit-code -- contracts/` | Accidental API changes: a renamed or removed field, a changed status code or type | Under a second | Every PR | 10 s | 0.1 s |
| Boundary | `tests/HomeBuild.Architecture.Tests` (project references, from the assemblies and the project files) and `ModuleTests` in the integration project (modules inside `HomeBuild.Api`, by reflection) | Architecture erosion: a project depending in the wrong direction (Payments core → adapters, web → database or AWS), a module reaching into another's internals, an endpoint outside a module | Milliseconds | Every PR | 10 s | 10 tests: under a second |
| API acceptance | Bruno collection in CI against the running API and web app (main collection, red team, web smoke) | Deployed behaviour over HTTP: status codes, headers, attack attempts failing, pages served | Seconds once the app is running | Every PR | 60 s | About 3 s in CI |
| End-to-end | Playwright (`tests/HomeBuild.Web.Tests/EndToEnd`), against the local stack and the real Cognito pool | Whole journeys a user takes in a real browser: catalogue to unit, a buyer reserving and finding it in My Home, staff filtering the board; keyboard access on each page; sign-in, sign-out, reconnect, CORS to S3 | Seconds per journey, but a running stack, a browser and Cognito; the most to maintain | Locally, opt-in (`HOMEBUILD_COGNITO_TESTS=1`); not in CI, which cannot sign in to Cognito | 5 min | 4 journey tests: 3.0–3.1 s per run, 20 runs in a row green |

**Whole PR pipeline budget: 8 minutes.** The last CI run on `main` took 231 s: restore 18 s, build 17 s, tests 35 s, migration bundles 61 s.

`scripts/test-layers.sh` runs the unit, web, integration and contract layers. It prints each layer's time against its budget in the CI job summary, and the Bruno step adds its own row. A layer over budget is a warning that asks for action, not a failure; a failing layer fails CI.

## Integration test infrastructure

- **One container, one database.** `PostgresFixture` is a collection fixture (`[Collection(IntegrationDatabase.Name)]`). It starts one PostgreSQL 18 container per run and creates one database, migrated once with the same module-by-module logic as the migration bundles (`HomeBuildDatabase.MigrateAsync`). Migration tests that need an empty database still create their own.
- **Reset by truncation.** `database.CreateDatabaseAsync()` and `CreateApiAsync()` reset the shared database for the calling test: one `TRUNCATE … RESTART IDENTITY CASCADE` of every module table except the migration histories, then the modules' seeding and the test users' memberships again. About 42 ms per reset. The tests in this collection run one at a time.
- **Users from attributes.** `[AsBuyer(Tenant = "B")]`, `[AsBuyer(Buyer = "owner")]` or `[AsStaff("admin")]` on a test (or its class) signs the factory's client in through a fake authentication scheme: no token, the same claims a Cognito access token carries, and the tenant still comes from the membership. A test without an attribute uses a real token from the C4 test issuer, so JWT validation stays covered (`AuthorizationTests`, `TokenValidationTests`, `TenantClaimTests`, `Without_an_attribute_requests_go_through_real_JWT_validation`). The attribute makes the user visible in the test; it is not faster (500 requests: fake 752–774 ms, JWT 687–700 ms).
- **Random order.** `RandomTestOrder` shuffles each collection's test cases with a seed: `HOMEBUILD_TEST_SEED`, or a random one. `scripts/test-layers.sh` sets a new seed per run and prints it in the summary heading; set it to replay a failing order. xUnit v3's own order is unpredictable but stable for an unchanged assembly, so it would never shake out an order dependency.
- **Fakes, never the database.** External services get fakes (Stripe in C8, SQS in C9). The database is never faked. EF Core's in-memory provider cannot even query HomeBuild's model: complex properties such as `Money` fail at query compilation. It also has no `xmin` row version, no partial unique indexes and no transactions, which are exactly what the C3 race relies on.

## Conventions

- **Names.** One rule (the C1 style): the test name is a sentence about behaviour, `Subject_condition_result`, for example `Reserving_reserved_unit_fails` or `Tenant_A_cannot_read_tenant_B_units_through_the_API`.
- **Assertions.** xUnit's `Assert` only; no assertion library.
- **Test data comes from builders** (`tests/HomeBuild.Testing`, plus `CatalogueBuilders` in the API tests): `AUnit().Reserved().InDevelopment(dev).Build()`, `AReservation().Confirmed().Build()`, `APolicy().Build()`, `ADevelopment().Named("…").Build()`.
  - Every builder makes a valid object by default, and each call changes one thing.
  - A test sets only what its name is about. Anything else, including the currency, comes from the builder.
  - `BuilderTests.Builders_cover_every_aggregate` fails when a new aggregate has no builder.
- **Time.** Tests that depend on time use a fixed clock (`Builders.Now`, or `FakeTimeProvider`), never `DateTimeOffset.UtcNow`.
- **Opt-in tests** that need AWS or Cognito skip unless their environment variable is set (`HOMEBUILD_AWS_TESTS`, `HOMEBUILD_COGNITO_TESTS`); CI never sets them.

## Known over-specified tests

Lab 1's Break changed the builders' default currency to EUR. Ten tests broke, and only one of them is about currency. Two were fixed: `Reserving_available_unit_creates_reservation_and_marks_unit_reserved` (deposit from the builder's currency) and `Deposit_in_other_currency_is_rejected` (now states the unit's currency). Still to fix: the seven that pass a hard-coded GBP deposit to a builder-made unit, listed in the dry-run log.

The Break also found a real bug: a deposit in a currency other than the unit's made `POST /units/{id}/reservations` answer **500**, because `Unit.Reserve` threw `ArgumentException` through the endpoint. Fixed in C6 Lab 2: the endpoint now answers 422 "Deposit currency rejected" (row M14 below).

## Rule boundaries and the breaks that prove the tests bite

The rules are written as boundary tables: `[Theory]` with `TheoryData<…>`, one named row per case (`TheoryDataRow<…>.WithTestDisplayName`). The tables are:
- `MoneyRuleTests`: amounts, currency codes, addition, equality.
- `DepositRuleTests`: minimum ≤ deposit ≤ price, the unit's currency, an available unit.
- `ReservationStateTableTests`: every (state, action) pair; `Every_state_and_action_pair_has_a_row` fails when a state or an action has no rows.
- `DepositStrategyRuleTests`: the fixed and percentage minimums, and the currency rule at the API.

Each rule was broken on purpose, one at a time, and the suite run. The test that failed is the one that guards the rule.

| # | Rule | Break | Caught by |
| --- | --- | --- | --- |
| M1 | Money: no negative amounts | `ThrowIfNegative` removed | `Money amount: one penny below zero is rejected` |
| M2 | Money: three-letter currency | `Length != 3` → `Length > 3` | `Currency: two letters are rejected`, `Currency: empty is rejected` |
| M3 | Money: upper-case currency | `IsAsciiLetterUpper` → `IsAsciiLetter` | `Currency: lower case is rejected` |
| M4 | Money: no adding mixed currencies | swapped comparison (`!=` → `==`) | `Money_in_mixed_currencies_cannot_be_added`, `Money_in_the_same_currency_adds_up`, `Adding_money_in_different_currencies_still_throws` |
| M5 | Deposit ≥ minimum | `<` → `<=` | `Deposit: exactly the minimum is accepted` |
| M6 | Deposit ≤ price | `>` → `>=` (the lab's Break) | `Deposit: exactly the price is accepted`. Before this lab **no test failed**; the row was added for it |
| M7 | Deposit in the unit's currency | swapped comparison (`!=` → `==`) | 16 tests, including `Deposit_in_a_currency_other_than_the_units_is_refused` |
| M8 | Only an available unit can be reserved | availability check removed | `Valid_deposit_on_a_reserved_unit_is_unavailable_not_rejected`, `Reserving_reserved_unit_fails`, `Reserving_reserved_unit_returns_UnitUnavailable` |
| M9 | A hold expires at its expiry time | `now >= expiry` → `now > expiry` | `State Held, Expire at expiry → Expired` and 6 more |
| M10 | Payment at the expiry time is too late | `paid < expiry` → `paid <= expiry` | `State Held, Confirm at expiry → invalid` |
| M11 | A confirmed reservation can be cancelled | `Confirmed` moved to the invalid arm | `State Confirmed, Cancel at expiry → Cancelled` |
| M12 | Percentage minimum is 5% of the price | 5% → 4% | `Minimum deposit: percentage is 5% of the price`, its rounding row, `Development_with_percentage_deposit_uses_percentage_strategy` |
| M13 | Percentage minimum rounds half a penny to even | `MidpointRounding.AwayFromZero` | `Minimum deposit: percentage rounds half a penny to even (12,500.005 → 12,500.00)` |
| M14 | The API answers a deposit in another currency with 422 | the endpoint's currency check disabled | `Deposit_in_another_currency_is_422_not_500` |

To re-run a break: change the line, run the layer's tests, and record what failed. Restore the file, and **touch it before the next run**. A restored file is older than the build output, so MSBuild's incremental build would skip recompiling and keep the broken code.

## Module boundaries

- **Payments is split into projects.** `HomeBuild.Payments.Core` holds the rules and contracts and references only `HomeBuild.Domain`. `HomeBuild.Payments.Infrastructure` holds the adapters (EF Core now, Stripe in C8) and references the core, never the other way round. A core that uses an adapter type fails to compile (`CS0234`); a project reference from the core to the infrastructure fails the build (`MSB4006` circular dependency).
- **Internal by default.** Only the module's contracts are public: the `TakeDepositPayment` command, the `DepositPaid` and `DepositPaymentFailed` events, and the `IPaymentGateway` and `IPaymentRecords` ports. Rules stay `internal`, and `InternalsVisibleTo` names only `HomeBuild.Payments.Tests`.
- **References are tested.** `ReferenceTests` compares each production assembly's `GetReferencedAssemblies()` with its allowed list. It also reads each project file, because the assembly lists only references the compiled code uses: a new, still-unused project reference passes the first test and fails the second.
- **Limit: Catalogue, Sales, Accounts and Construction still share one assembly** (`HomeBuild.Api`). Between them `internal` does not apply, and the compiler does not stop Sales from using a Catalogue internal type. `ModuleTests` (C2's reflection test) catches such a use only when it appears in a signature, not inside a method body. Splitting those modules into projects is the next step if the boundary needs the compiler.

## End-to-end journeys

- **Three journeys** (`JourneyTests`): a visitor opens a unit from the catalogue; a buyer reserves and sees the reservation in My Home; a site manager filters the milestone board. A fourth test closes the component gallery's dialog with Escape (the only dialog; it needs an interactive page).
- **Sign-in from stored state.** `SignedInState` signs each test user in through the managed login once and saves the browser storage state to the temp folder (`homebuild-e2e-state`, never in the repository). Journeys start from that state with `StorageStatePath`; a state that no longer works is made again. The tokens live in the web app's memory (C5 Lab 4), so a restart of the web app means one fresh sign-in per user.
- **Keyboard checks** (`Keyboard.CheckTabOrderAsync`): on each journey's main page, every visible focusable element is reached by Tab and shows a visible focus outline.
- **No fixed waits.** Assertions use Playwright's auto-waiting `Expect(…)`. Interactive islands expose `data-interactive="true"` once their circuit runs, and tests wait for it before typing: text typed into the prerendered page is lost. In the Lab 5 Break, typing with no wait failed every time with the CPU throttled 20×, while the auto-waiting version passed.
- **Data and limits.** The buyer journey frees its unit (Riverside P2) in the local database before reserving. The API allows 10 reservations a minute per client, so repeated runs must stay under that: 20 runs back to back hit `429` on runs 10 and 11.
- **Memory.** On a 16 GB machine, run the journeys with only PostgreSQL, the API and the web app running.

## Flaky tests

A flaky test is fixed, never retried. The one planted in C6 Lab 5 (`ReservationHoldTests`) failed in about half the runs, for two reasons:
- **Test order:** the second test read a reservation the first left in a static field. Seeds 2, 4 and 6 failed every time; 1, 3 and 5 passed.
- **Time:** it compared the wall clock twice, a few milliseconds apart, at the hold boundary.

Fixed by giving each test its own data on the builders' fixed clock and testing the boundary exactly: 100 runs in a row passed, each in a new random order.
