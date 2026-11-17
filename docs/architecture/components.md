# Components of the API (C4 level 3)

Each module is a component inside `HomeBuild.Api` with its own schema. Modules call each other only through published interfaces (solid arrows) or react to events after commit (dotted arrows). Accounts turns each request's token into a tenant claim (a claims transformation) that every module's queries filter on. Payments' rules live in `HomeBuild.Payments.Core` (ports and adapters); the other modules are vertical slices.

```mermaid
flowchart LR
  web["Web<br/>[container]"]
  db[("PostgreSQL")]

  subgraph modules [API modules]
    Accounts["Accounts<br/>tenant memberships"]
    Catalogue["Catalogue<br/>developments, units, prices, floor plans"]
    Sales["Sales<br/>availability, reservations, deposits"]
    Construction["Construction<br/>BuildProgress, milestones, site diary, audit"]
    Payments["Payments<br/>stage payments due, overdue (ports and adapters)"]
    Extras["Extras<br/>extras unlocked (stub until C10)"]
    Notifications["Notifications<br/>buyer messages (stub until C10)"]
  end

  core["HomeBuild.Payments.Core<br/>use cases and ports"]
  domain["HomeBuild.Domain<br/>Money, Unit, Reservation, BuildProgress"]

  web -- "HTTPS, JSON" --> Catalogue
  web -- "HTTPS, JSON" --> Sales
  web -- "HTTPS, JSON" --> Construction
  web -- "HTTPS, JSON" --> Payments
  web -- "HTTPS, JSON (who am I)" --> Accounts

  Catalogue -- "IUnitAvailability" --> Sales
  Sales -- "ICatalogueQueries" --> Catalogue
  Construction -- "ICatalogueQueries" --> Catalogue
  Construction -- "IUnitAvailability" --> Sales
  Payments -- "ICatalogueQueries (unit price, schedule)" --> Catalogue
  Notifications -- "IUnitAvailability (current buyer)" --> Sales

  Construction -. "MilestoneCompleted" .-> Payments
  Construction -. "MilestoneCompleted" .-> Extras
  Construction -. "MilestoneCompleted" .-> Notifications

  Payments --> core
  core --> domain
  Construction --> domain
  Sales --> domain

  modules -- "one schema each" --> db
```
