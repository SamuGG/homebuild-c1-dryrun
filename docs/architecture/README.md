# Architecture

C4 diagrams of HomeBuild, as Mermaid flowcharts (they render on GitHub with no extra tooling). Each level zooms into one box of the level above.

1. [System context](context.md): who uses HomeBuild and which external systems it depends on.
2. [Containers](containers.md): the applications and data stores that make up HomeBuild.
3. [Components of the API](components.md): the API's modules, how they call each other and the events between them.

Level 4 (code) is the code itself. Decisions behind these diagrams are in the [ADR index](../adr/README.md).

**Kept current by a test.** `ArchitectureDiagramTests` (in `tests/HomeBuild.Architecture.Tests`, run in CI) fails when a module exists in `HomeBuild.Api` but not in the component diagram, or the diagram shows a module the code does not have. Update the diagram in the same PR as the module.
