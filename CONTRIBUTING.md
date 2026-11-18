# Contributing

Every change reaches `main` through a branch, a pull request and a green CI run; `main` is protected by a ruleset.

## Commits and pull requests

- Commits follow Conventional Commits (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `ci:`, `chore:`).
- Package versions live only in `Directory.Packages.props`; every project's `packages.lock.json` is committed, and CI restores with `--locked-mode`.
- An architecture change updates `docs/architecture/` in the same PR (see `CLAUDE.md`, "Architecture").

## Dependabot

Dependabot opens one weekly PR for NuGet minor and patch updates, one per NuGet major, and weekly PRs for GitHub Actions and the `api-tests/` npm packages (`.github/dependabot.yml`). Majors of EF Core, Npgsql, `EFCore.NamingConventions` and `dotnet-ef` are ignored until the Npgsql EF Core provider 11 is GA: they must move together.

### A Dependabot NuGet PR that fails `--locked-mode`

Dependabot updates `Directory.Packages.props` but not always every project's lock file, so CI's `dotnet restore --locked-mode` fails with `NU1004`. To fix it:

```bash
gh pr checkout <number>
dotnet restore --force-evaluate
git add '**/packages.lock.json'
git commit -m "chore(deps): update lock files"
git push
```

Then wait for CI and review the PR as usual.

### Microsoft.CodeAnalysis.CSharp is updated by hand

Dependabot ignores `Microsoft.CodeAnalysis.CSharp`. The source generator (`src/HomeBuild.Generators`) runs inside the SDK's compiler, so the package must not be newer than that compiler's Roslyn. When `global.json` moves to a new SDK, check its compiler:

```bash
dotnet "$(dirname "$(dotnet --list-sdks | tail -1 | sed -E 's/.*\[(.*)\]/\1/')")/$(dotnet --version)/Roslyn/bincore/csc.dll" -version
```

and set `Microsoft.CodeAnalysis.CSharp` in `Directory.Packages.props` to that version or lower. Never add a direct reference to it elsewhere: EF Core's design-time package pins its own copy exactly (`NU1608`).
