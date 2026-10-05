# Testing

## Purpose and scope

This document says which checks exist and how to report them. It does not list every test method.

Source of the commands below: `Wasla.sln`, `tests/Wasla.UnitTests`, `tests/Wasla.PrintBridge.Tests`, and `.github/workflows/ci.yml`. There is no repository `package.json` and no npm test script.

## Layers

| Layer | Where | What it covers |
| --- | --- | --- |
| Unit and source-contract tests | `tests/Wasla.UnitTests` | Application, infrastructure, Web contracts, orders, auth, sync, signup |
| Print Bridge tests | `tests/Wasla.PrintBridge.Tests` | Desktop client settings, polling, setup, the Core engine against a fake API (`PrintBridgeRuntimeEngineTests`), Gregorian operational dates (`PrintBridgeGregorianCalendarTests`), and the WebView2 app (`WebShell/`): message contract, host operations (single-flight, request-id idempotency, sanitized errors), history projection, printer and operational-settings persistence, the connection setup against the real engine and a fake API (`ShellConnectionSetupTests`: check before save, cancel and failure without writes, refusal while a print job is being claimed, printed or reported, no tokens in logs), the native connection dialog on a Windows Forms message loop (`ShellConnectionDialogTests`: masking, Escape and Enter, accessible errors, tab order, right-to-left), security policy, accessibility markup and localization. The test-isolation guards (`TestIsolationGuardTests`, and `TrayShutdownIsolationTests` with the real tray application: an exit save that runs after its test ended, a failing test) need no WebView2 Runtime |
| Print Bridge WebView2 runtime tests | `ShellWebViewRuntimeTests.cs` and `ShellTrayRoutingTests.cs` in `tests/Wasla.PrintBridge.Tests/WebShell/` (trait `Category=WebView2Runtime`) | Real WebView2 control: applied settings, host-to-page rendering, blocked popups, navigation, remote requests, permissions and invalid messages, keyboard tabs in both reading directions, start/stop, single-flight test print, reprint confirmation, light/dark, minimum window and 200 % reflow, the native connection dialog and immediate status refresh, operational settings and the native test-mode confirmation, tab requests. The real tray application: Print history and Settings open the app tabs in one window, the classic fallback entry, and the classic window when the runtime is missing or the app fails to start. Windows stay off-screen and the tray icon stays hidden; a startup failure is simulated before any browser process starts. Skipped when no WebView2 Runtime is installed; needs an interactive Windows session |
| Node tests | Every tracked `*.test.js` file, under `tests/Wasla.UnitTests/` and `tests/Wasla.PrintBridge.Tests/WebShell/` | Browser-side Web scripts and the Print Bridge app message model and view helpers. Not compiled by the csproj |
| Browser smoke | Manual | Flows with no automated browser runner in this repo |

CI (`.github/workflows/ci.yml`) builds `Wasla.sln` in Release on `windows-latest` and runs **only** `Wasla.UnitTests`. It does not run Print Bridge tests or Node tests.

## Commands

From the repository root, with the .NET 8 SDK:

```powershell
dotnet build Wasla.sln
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj
dotnet test tests/Wasla.PrintBridge.Tests/Wasla.PrintBridge.Tests.csproj
node --test (git ls-files '*.test.js')
```

In a POSIX shell, the last command is `node --test $(git ls-files '*.test.js')`.

Print Bridge tests reference the shipped `Wasla.PrintBridge.Core` and `Wasla.PrintBridge` assemblies, and they print only to a recording fake. They never read or write `C:\ProgramData\Wasla\PrintBridge`:

- Every test class that writes uses `IsolatedDataRoot` (`tests/Wasla.PrintBridge.Tests/IsolatedDataRoot.cs`), which redirects every Print Bridge path to its own temporary folder (`PrintBridgePaths.UseRootForTests`). Teardown first waits for every UI thread the class started (the tray application saves its settings when it exits) and only then releases the folder. If a thread does not end, the redirect stays for the rest of the run.
- Outside those folders, the default location for the whole run is an empty canary folder (`EscapedWriteCanary`, set with `PrintBridgePaths.UseDefaultRootForTests`) instead of ProgramData or a development root. A write that escapes its folder lands there and fails the test class and the run.

To leave out the real-runtime tests, filter with `--filter "Category!=WebView2Runtime"`.

Focused runs use `dotnet test <project> --filter <FullyQualifiedName>` (or a trait/name filter the runner accepts). Prefer a focused filter before the full project when the change is narrow.

The Node command asks Git for every tracked `*.test.js` file, so a new test file is included as soon as it is committed or staged; there is no list to keep up to date. `git ls-files '*.test.js'` shows which files that is. `tests/Wasla.PrintBridge.Tests/WebShell/shell-model.test.js` shares `shell-snapshot.fixture.json` with the C# serializer test.

For a focused check, run the files that match the change, for example `node --test tests/Wasla.UnitTests/Signup/signup-business-types.test.js`. That is not the whole Node suite.

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
- Node tests (all tracked files, or name the files you ran)
- Browser smoke
- `dotnet build`

Also report warnings, failures, and failures that come from unrelated dirty files. “All tests passed” is only accurate when the full projects you name were run and passed. A focused filter is not the full suite. CI’s unit-test job is not Print Bridge or Node coverage.
