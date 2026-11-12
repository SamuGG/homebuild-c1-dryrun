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
