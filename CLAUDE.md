@AGENTS.md

# Claude Code notes

`AGENTS.md`, imported above, holds the project rules for every coding agent. This section adds only how Claude Code loads them.

- Path-scoped rules live in `.claude/rules/`. Claude Code loads a rule when you read, write or edit a file its `paths` frontmatter matches. Before you create files in an area whose existing files you have not opened, read the matching rule.
- Compaction drops path-scoped rules. They return only when you touch a matching file again.
- The built-in Explore and Plan subagents do not load `CLAUDE.md` or `AGENTS.md`. When you delegate to them, put the rules the task depends on into the prompt.
