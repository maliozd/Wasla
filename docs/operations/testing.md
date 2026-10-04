# Testing

## Purpose and scope

This document says which checks exist and how to report them. It does not list every test method.

Source of the commands below: `Wasla.sln`, `tests/Wasla.UnitTests`, `tests/Wasla.PrintBridge.Tests`, and `.github/workflows/ci.yml`. There is no repository `package.json` and no npm test script.

## Layers

| Layer | Where | What it covers |
| --- | --- | --- |
| Unit and source-contract tests | `tests/Wasla.UnitTests` | Application, infrastructure, Web contracts, orders, auth, sync, signup |
| Print Bridge tests | `tests/Wasla.PrintBridge.Tests` | Desktop client settings, polling, setup |
| Node tests | `tests/Wasla.UnitTests/**/*.test.js` | Live Screen coordinator, audio, notifications, signup business types. Not compiled by the csproj |
| Browser smoke | Manual | Flows with no automated browser runner in this repo |

CI (`.github/workflows/ci.yml`) builds `Wasla.sln` in Release on `windows-latest` and runs **only** `Wasla.UnitTests`. It does not run Print Bridge tests or Node tests.

## Commands

From the repository root, with the .NET 8 SDK:

```powershell
dotnet build Wasla.sln
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj
dotnet test tests/Wasla.PrintBridge.Tests/Wasla.PrintBridge.Tests.csproj
node --test tests/Wasla.UnitTests/Orders/*.test.js tests/Wasla.UnitTests/Signup/*.test.js
```

Focused runs use `dotnet test <project> --filter <FullyQualifiedName>` (or a trait/name filter the runner accepts). Prefer a focused filter before the full project when the change is narrow.

Node files present today:

- `tests/Wasla.UnitTests/Orders/live-screen-coordinator.test.js`
- `tests/Wasla.UnitTests/Orders/live-screen-view-transition.test.js`
- `tests/Wasla.UnitTests/Orders/live-screen-audio-permission.test.js`
- `tests/Wasla.UnitTests/Orders/notification-audio-status-label.test.js`
- `tests/Wasla.UnitTests/Orders/notification-preview-stop.test.js`
- `tests/Wasla.UnitTests/Orders/notification-sound-preview.test.js`
- `tests/Wasla.UnitTests/Signup/signup-business-types.test.js`

Run the files that match the change. `node --test` on one file is a focused frontend check, not the whole suite.

## What to run

| Change | Required verification |
| --- | --- |
| Domain, application, infrastructure, Worker, API, Web C# | Focused `Wasla.UnitTests`, then the full unit project when the change is shared |
| Print Bridge | `Wasla.PrintBridge.Tests`, plus unit tests if shared server code changed |
| Live Screen or orders JS | The matching Node tests and any C# source-contract tests that read those files. Browser smoke of the changed screen when behavior is visual or depends on a real browser |
| Signup JS | `signup-business-types.test.js` and related C# tests |
| Migrations | Confirm `--context` and `--output-dir` before generating anything. Build the affected projects. Do not apply migrations to a shared database from a doc or unrelated task. See [migrations.md](migrations.md) |
| Docs only | Link check of the edited Markdown and `git diff --check`. No product build is required |

Browser smoke means opening the affected tenant screen and exercising the changed control. Razor compilation is not that check.

## Repository-root discovery

Many source-contract tests walk parent directories from the test assembly output until they find `Wasla.sln`, then read files under `src/`.

An output directory that is still inside this repository (including a gitignored `artifacts/` folder under the repo) can still find the solution. An output directory outside the repository cannot. Those tests then fail even though the product code is unchanged.

Do not set a global output path outside the repo to “make tests pass.”

## Shared working tree

Other agents may have processes and uncommitted files in this tree.

- Do not `git clean`, reset, or delete another agent’s build output.
- A running `Wasla.Web` (or another project) can lock DLLs under `bin/` and `obj/` and make `dotnet build` fail on copy. Do not stop a process until you have confirmed it belongs to this task.
- If the lock is someone else’s Web process, build or test with an output directory that stays **inside** the repository, or limit the build to projects that are not locked.
- Failures in files you did not change may be concurrent work. Report them separately. Do not revert them to get a green run.

## How to report

Say which of these you actually ran:

- Focused tests
- Full `Wasla.UnitTests`
- `Wasla.PrintBridge.Tests`
- Node tests (name the files)
- Browser smoke
- `dotnet build`

Also report warnings, failures, and failures that come from unrelated dirty files. “All tests passed” is only accurate when the full projects you name were run and passed. A focused filter is not the full suite. CI’s unit-test job is not Print Bridge or Node coverage.
