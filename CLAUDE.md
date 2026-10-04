# Claude Code instructions for Wasla

Read `AGENTS.md` completely before investigating or changing this repository. Then read every rule referenced by it:

- `.cursor/rules/00-wasla-core.mdc`
- `.cursor/rules/10-architecture-boundaries.mdc`
- `.cursor/rules/20-localization-ui.mdc`
- `.cursor/rules/30-provider-worker.mdc`

Those files are the shared source of repository rules. Do not restate or fork their detailed instructions in this file.

Before a task:

1. Inspect the relevant source, tests, and current Git state.
2. Start with `docs/README.md` and read the task-specific canonical documents it links.
3. Trace the current implementation across the affected layers before proposing a change.
4. Call out any disagreement between code, canonical documentation, and historical files.
5. Make the smallest coherent change within the requested scope and verify it according to `docs/operations/testing.md`.

## Product boundary

Wasla has two products: **Wasla Orders** and **Wasla POS**. This repository currently implements Wasla Orders. Wasla POS is a separate product and has no project or module in this solution yet.

Do not reinterpret the Wasla Orders Live Screen as a POS interface. Shared infrastructure belongs in this repository only when the requested work establishes a real cross-product need.

The old root handoff is archived at `docs/archive/INITIAL_SKELETON.md`. It is historical context, not a list of unfinished work.
