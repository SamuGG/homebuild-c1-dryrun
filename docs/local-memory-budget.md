# Local memory budget (16 GB laptop)

C11 Lab 7. Measured on an Apple-silicon Mac with 16 GB, Docker Desktop at 6 GB, the AppHost running (`aspire start`), 24 Nov 2026.

| Process | Resident memory | Limit |
| --- | --- | --- |
| Docker Desktop VM (`com.apple.Virtualization.VirtualMachine`) | 2.7 GB | Docker Desktop: 6 GB |
| Docker Desktop helpers (`com.docker.sailor`, `com.docker.backend`) | 0.84 + 0.36 GB | none |
| PostgreSQL container | 89–97 MB | `--memory 1g` (AppHost) |
| Valkey container | 34 MB | `--memory 256m` (AppHost) |
| Aspire: `dcp`, AppHost, dashboard | 474 + 396 + 172 MB | none |
| HomeBuild.Api | 259 MB | none |
| HomeBuild.Workers | 198 MB | `DOTNET_GCHeapHardLimit=0x8000000` (128 MB heap) |
| HomeBuild.Web | 185 MB | none |

Workers GC comparison (90 s after start, idle):

| GC setting | RSS | GC committed |
| --- | --- | --- |
| Server (default, DATAS) | 204 MB | 18.0 MB |
| Workstation | 201 MB | 24.9 MB |
| Workstation + 128 MB hard limit | 200 MB | 24.9 MB |

The GC mode makes no measurable difference here: about 180 MB of each process is the runtime and code, not the heap. The heap limit is kept so a runaway worker fails with `OutOfMemoryException` rather than growing into swap.

Limits are AppHost configuration: `Limits:Postgres`, `Limits:Valkey`, `Limits:WorkersHeapHardLimit` (empty turns it off) and `Limits:WorkersGcServer`.

## Rules for heavy tasks

- **Playwright journeys** run against the AppHost with nothing else heavy running (no load tool, no container builds). Measured: 89 tests, no new swap-outs.
- **The load tools** (C8's `tools/RaceScript/race.sh`, C15's load runner) run with only what they need: the AppHost's Postgres and the API. Stop the web app and Playwright first.
- **Container image builds** (Labs 1–3, the Native AOT build container) run with the AppHost stopped (`dotnet aspire stop`).
- If Activity Monitor shows Memory Pressure yellow, or `sysctl vm.swapusage` grows during a session, stop something before carrying on; never raise a limit to hide it.
