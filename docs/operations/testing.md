# Testing

## Purpose and scope

This document says which checks exist and how to report them. It does not list every test method.

Source of the commands below: `Wasla.sln`, `tests/Wasla.UnitTests`, `tests/Wasla.PrintBridge.Tests`, `.github/workflows/ci.yml`, and `scripts/ci/assert-test-results.ps1`. There is no repository `package.json` and no npm test script.

## Layers

| Layer | Where | What it covers |
| --- | --- | --- |
| Unit and source-contract tests | `tests/Wasla.UnitTests` | Application, infrastructure, Web contracts, orders, auth, sync, signup |
| Print Bridge tests | `tests/Wasla.PrintBridge.Tests` | Desktop client settings, polling, setup, the Core engine against a fake API (`PrintBridgeRuntimeEngineTests`), Stop while a job is claimed, printed or reported, against a fake server that follows the job contract, including printer and server failures and a restart (`PrintBridgeStopRaceTests`), connection changes while a job is active against two fake servers with their own fake tokens: a setup link opened before the claim, during the claim, printing, a delayed or retried `mark-printed` and after Stop returned, several jobs draining, the classic window's lease and Reset, with every report checked for its server and token, settings compared byte for byte and no secret in the logs (`PrintBridgeSetupLinkGuardTests`), the refusal's window mapping and its text in every culture (`PrintBridgeSetupLinkRefusalUiTests`), Gregorian operational dates (`PrintBridgeGregorianCalendarTests`), and the WebView2 app (`WebShell/`): message contract, host operations (single-flight, request-id idempotency, sanitized errors), history projection, printer and operational-settings persistence, the connection setup against the real engine and a fake API (`ShellConnectionSetupTests`: check before save, cancel and failure without writes, refusal while a print job is being claimed, printed or reported, no tokens in logs), the native connection dialog on a Windows Forms message loop (`ShellConnectionDialogTests`: masking, Escape and Enter, accessible errors, tab order, right-to-left), security policy, accessibility markup and localization. The test-isolation guards (`TestIsolationGuardTests`, and `TrayShutdownIsolationTests` with the real tray application: an exit save that runs after its test ended, a failing test), and exiting the real tray application while it listens to a local fake API with a fake token (`TrayExitTests`, `TrayShutdownLifecycleTests`: listening, stopped, the classic window open, the shutdown order with each component disposed once, repeated and concurrent Exit requests, a tray update queued before Exit, a setup link during shutdown, an active job within and beyond the stop limit, settings and history afterwards) need no WebView2 Runtime |
| Print Bridge WebView2 runtime tests | `ShellWebViewRuntimeTests.cs`, `ShellTrayRoutingTests.cs` and two tests in `TrayShutdownLifecycleTests.cs` in `tests/Wasla.PrintBridge.Tests/WebShell/` (trait `Category=WebView2Runtime`) | Real WebView2 control: applied settings, host-to-page rendering, blocked popups, navigation, remote requests, permissions and invalid messages, keyboard tabs in both reading directions, start/stop, single-flight test print, reprint confirmation, light/dark, minimum window and 200 % reflow, the native connection dialog and immediate status refresh, operational settings and the native test-mode confirmation, tab requests. The real tray application: Print history and Settings open the app tabs in one window, the classic fallback entry, and the classic window when the runtime is missing or the app fails to start. Exit with the app window open or closed to the tray (`TrayShutdownLifecycleTests`). Windows stay off-screen and the tray icon stays hidden; a startup failure is simulated before any browser process starts. Skipped when no WebView2 Runtime is installed; needs an interactive Windows session |
| Login redirect browser tests | `TenantLoginRedirectBrowserTests` in `tests/Wasla.UnitTests/Web/` (WAS-113; same driver, browser and CDN prerequisites as the headless-browser tests below) | The post-login redirect chain and Access denied against `Auth/TenantSessionWebHost.cs` (real tenant resolution from the host name, cookie scheme with session revalidation, policies, controllers and views), with the browser mapping `alpha.wasla.local` to the in-process server: every role signs in through the rendered form and lands on its page, a deep link the role may not open, and the Access denied start-page button in four cultures, right to left, both themes and phone and desktop widths, followed by a real click |
| Headless-browser tests | `AdminRtlLayoutBrowserTests`, `AdminMobileNavigationBrowserTests`, `ThemePreferenceBrowserTests`, `TenantDarkModeBrowserTests`, `LiveScreenSyncStatusBrowserTests`, `LiveScreenLargeSnapshotBrowserTests`, `Realtime/PrintStatusBrowserTests`, `Realtime/LiveScreenRealtimeBrowserTests` and `Realtime/RealtimeLifecycleBrowserTests` in `tests/Wasla.UnitTests/` (driver: `Admin/HeadlessChromium.cs`) | The real Admin and tenant layouts served in-process and rendered by a headless Chromium browser over the DevTools protocol: RTL overflow, mobile navigation, theme preference, the Live Screen with about 5,755 active orders (`LargeActiveOrderSeed`: usable within seconds and responsive, header and column counts, one page per column with every order reachable through Show more, unchanged refreshes without DOM churn, an invalidation burst during slow reads, a failed refresh, Board scrolling at 768 px and stacking at 390 px in both directions and themes, and the details dialog returning focus), the Live Screen's platform connection status (a paused connection is shown as a connection issue, not "Live", in every culture and right to left), live receipt print status over the real SignalR hub (details page and Live Screen modal, the disconnected fallback and the read on reconnect), Live Screen invalidation (new orders and lifecycle changes without polling, one announcement per new order, bursts, the 10-second fallback, reconnect and back/forward-cache restore with one connection, order training, a Kitchen screen, English and Arabic, and a connected page that keeps the 10-second rate without Web's internal listener), several Live Screen pages of one user (one connection each, released when a page closes or navigates away, notification permission never requested and preferences unchanged in every permission state), and the painted dark mode: `TenantDarkModeBrowserTests` serves the populated tenant app (`Web/TenantVisualWebHost.cs`: real controllers, views and Infrastructure services over seeded SQLite databases with fake data) and checks for light surfaces and text below WCAG AA contrast (outside the exceptions listed in [../frontend/design-system.md](../frontend/design-system.md#dark-mode-in-the-tenant-app)) on every tenant page, the Live Screen, guided setup (a second host seeds a tenant still in Setup), menus, the drawer, modals, validation and keyboard focus, at phone and desktop widths in LTR and RTL. The browser is the one named by `WASLA_TEST_BROWSER`, otherwise an installed Edge or Chrome. The pages load Bootstrap, Bootstrap Icons and AdminLTE from `cdn.jsdelivr.net`, so these tests need internet access. Skipped when no browser is found or the CDN does not load. The driver's startup (reading `DevToolsActivePort` while the browser still writes it, browser exit, timeout, cancellation and cleanup after a failed start) is covered without a browser by `HeadlessChromiumStartupTests`. The driver removes an inherited `__COMPAT_LAYER` from the browser's environment, because Edge started with a compatibility layer relaunches itself and the started process exits with code 0; `HeadlessChromiumCompatibilityLayerTests` covers this, once without a browser and once with the real browser. Disposing a browser stops the processes it started (the browser process and its descendants, found by parent process and creation time, never by name) and deletes the exact profile folder it created. A termination that is refused while a process is already exiting is not an error; a process that still runs 10 s after the kill is. A stopped process that Windows is still tearing down is waited for, and the profile deletion retries while its files are held, up to 15 s; anything left fails with the profile path and the state of each process. Setup between the start and the test's `await using` (cookies, viewport, a first page) goes through `SetUpAsync`, which disposes the browser when the setup fails, skips or is cancelled. Profile folders from earlier runs are never deleted. `HeadlessChromiumCleanupTests` covers the cleanup without a browser, and `HeadlessChromiumCleanupBrowserTests` with the real browser, including two browsers and an Edge started outside the driver |
| SQL Server concurrency tests | `tests/Wasla.UnitTests/Orders/OrderActionConcurrencySqlServerTests.cs` (database: `SqlServerTenantDatabase`) | Order lifecycle actions on a real SQL Server (WAS-104): simultaneous identical and conflicting actions from separate contexts and connection pools, operator versus Worker auto-approve, provider failure and retry, a status written by sync during the provider call, and a claim held by a session that then ends. See [SQL Server concurrency tests](#sql-server-concurrency-tests) |
| Node tests | Every tracked `*.test.js` file, under `tests/Wasla.UnitTests/` and `tests/Wasla.PrintBridge.Tests/WebShell/` | Browser-side Web scripts and the Print Bridge app message model and view helpers. Not compiled by the csproj |
| Browser smoke | Manual | Flows the headless-browser tests do not cover |

Some Print Bridge tests also skip when no Windows printer is installed. They only enumerate the installed printers, because the runtime refuses to start without one; output always goes to a recording fake.

## Continuous integration

`.github/workflows/ci.yml` runs on pushes to `dev` and `master`, on pull requests into them, and manually (**Run workflow** on the Actions tab). It has two jobs that run in parallel. Each must pass.

| Job | Runner | What it runs |
| --- | --- | --- |
| `build-and-test` | `windows-latest`, .NET 8 SDK (`8.0.x`) | Restore and Release build of `Wasla.sln`; the full `Wasla.UnitTests` project, including the headless-browser tests; the full `Wasla.PrintBridge.Tests` project, including the `Category=WebView2Runtime` tests. No test filter, and no test is excluded |
| `javascript-tests` | `ubuntu-latest`, Node.js `24.21.0` (pinned in the workflow) | `node --test` over every file `git ls-files '*.test.js'` lists. The job fails when that list is empty |

Print Bridge, its WebView2 runtime tests and the Edge headless-browser tests need Windows, so all .NET tests share the Windows job and its single Release build. The Node tests need no Windows API and run on Ubuntu.

A new push to a pull request cancels that pull request's older run. Runs for pushes to `dev` and `master` and manual runs are never cancelled, so every commit on those branches gets a result. NuGet packages are cached by the project files' hash; restore still runs every time, so the cache only saves download time.

Prerequisites on the Windows runner:

- The headless browser is the runner image's Microsoft Edge. The workflow sets `WASLA_TEST_BROWSER` to it and fails before the build if Edge is missing.
- The WebView2 runtime tests use the image's WebView2 Runtime and the runner's interactive desktop session. Windows stay off-screen.
- The Print Bridge runtime tests need at least one installed printer on the runner. Nothing is sent to it.
- The headless-browser tests need `cdn.jsdelivr.net`. Tests make no other network calls in CI, the workflow uses no repository secrets, and every test server is in-process with fake tokens and data.
- The runner has no developer environment: no `ENCRYPTION_MASTER_KEY`, user secrets, certificates or local databases. The runner image does ship the SQL Server LocalDB runtime (with Visual Studio); the [SQL Server concurrency tests](#sql-server-concurrency-tests) create their own throwaway database on `(localdb)\MSSQLLocalDB`. A test host that registers the real Infrastructure services must replace `ISecretManager` with a fake (as `TenantVisualWebHost` and `TenantSessionApiHost` do); otherwise a test can pass on a developer machine and fail in CI. `TenantVisualWebHostTests` checks the visual host without the variable.
- The runner reports `prefers-reduced-motion: reduce`, and a developer machine usually does not. The headless-browser driver (`HeadlessChromium`) therefore starts every browser emulating `prefers-reduced-motion: no-preference`, so a page renders the same everywhere. A test that needs reduced motion sets it for its own browser with `SetReducedMotionAsync("reduce")`. AdminLTE 4.0.0-rc3 displays every `.fade` element under reduced motion, which let a closed modal cover the page (WAS-100); `wasla-theme.css` keeps closed modals hidden, and the `UnderReducedMotion_*` tests in `TenantDarkModeBrowserTests` and `AdminMobileNavigationBrowserTests` check a tenant and an Admin modal at desktop LTR and phone RTL.

The workflow logs the browser version, the WebView2 Runtime version and the number of installed printers before the build.

How failures surface:

- A build error or a failing test fails its step and the job. The Print Bridge tests still run after a unit-test failure, so one run reports both projects.
- `scripts/ci/assert-test-results.ps1` then reads every result file. It fails the job when a file is missing, has no tests, or reports a failed or **skipped** test. A missing browser, an unreachable CDN, a missing WebView2 Runtime or a missing printer therefore fails CI instead of passing as a skip. The script adds an error annotation per failed or skipped test (first 20) and a totals table to the job summary.
- A test that hangs for 15 minutes is stopped by `--blame-hang-timeout`. The run then fails with the hanging test's name and no memory dump. Each job also has a time limit (60 minutes for Windows, 15 for Node).
- After the tests, the Windows job warns (it does not fail or kill anything) when an Edge, WebView2 or test host process outlived them.
- Results are kept for 14 days as the `dotnet-test-results` (TRX files) and `javascript-test-results` (JUnit XML) artifacts. They contain test names, messages and output from fake data only.

To find a failure: open the failed run. The annotations name each failed or skipped test with the first line of its message, and the job summary has the totals. Download the artifact for the full messages and stack traces (the TRX files open in Visual Studio or any XML viewer). A headless-browser page that does not finish loading reports where the page is and what it still waits for. `chrome-error://chromewebdata/` means the server answered with an error. Tests served by `TenantVisualWebHost` put the server's exception first.

CI does not cover browser smoke of real screens, SQL Server beyond the [SQL Server concurrency tests](#sql-server-concurrency-tests), real platform providers, real printers or physical output, or the Print Bridge installer and update delivery. Those stay manual.

## Commands

From the repository root, with the .NET 8 SDK:

```powershell
dotnet build Wasla.sln
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj
dotnet test tests/Wasla.PrintBridge.Tests/Wasla.PrintBridge.Tests.csproj
node --test (git ls-files '*.test.js')
```

In a POSIX shell, the last command is `node --test $(git ls-files '*.test.js')`.

The projects target .NET 8 (`net8.0`, `net8.0-windows`). There is no `global.json`, so a newer SDK also builds them, but the .NET 8 runtimes (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`) must be installed to run the tests. CI uses SDK `8.0.x` and Node.js `24.21.0`.

To reproduce the Windows CI job exactly, run from the repository root in PowerShell. Clear `ENCRYPTION_MASTER_KEY` in that session first, because the runner does not have it:

```powershell
$env:ENCRYPTION_MASTER_KEY = $null
dotnet restore Wasla.sln
dotnet build Wasla.sln --configuration Release --no-restore
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj --configuration Release --no-build --logger "trx;LogFileName=Wasla.UnitTests.trx" --results-directory artifacts/test-results --blame-hang-timeout 15m --blame-hang-dump-type none
dotnet test tests/Wasla.PrintBridge.Tests/Wasla.PrintBridge.Tests.csproj --configuration Release --no-build --logger "trx;LogFileName=Wasla.PrintBridge.Tests.trx" --results-directory artifacts/test-results --blame-hang-timeout 15m --blame-hang-dump-type none
./scripts/ci/assert-test-results.ps1 -Path artifacts/test-results/Wasla.UnitTests.trx, artifacts/test-results/Wasla.PrintBridge.Tests.trx
```

The Node job's exact command (bash, as in CI) writes a JUnit file that the same script checks:

```bash
mkdir -p artifacts/test-results
node --test --test-reporter=spec --test-reporter-destination=stdout --test-reporter=junit --test-reporter-destination=artifacts/test-results/javascript-tests.xml $(git ls-files '*.test.js')
```

`assert-test-results.ps1` runs in Windows PowerShell 5.1 and in PowerShell 7 (`pwsh`, used by CI). `artifacts/` is ignored by Git.

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

## Realtime load tests

`tests/Wasla.UnitTests/Realtime/RealtimeLoadTests.cs` (WAS-25) runs the live hub under bounded load: the real Web pipeline in-process and many lightweight .NET SignalR clients over WebSockets, one per "tab" of a signed-in user. It runs with the rest of `Wasla.UnitTests` in every CI run at its default size. Every wait has a timeout, every count an upper bound, and the tests stop only what they started (their own in-process host and clients; no browser).

| Variable | Default | Allowed | Meaning |
|----------|---------|---------|---------|
| `WASLA_REALTIME_LOAD_CONNECTIONS` | 120 | 12–2000 | Hub connections across all tenants and roles |
| `WASLA_REALTIME_LOAD_EVENTS` | 200 | 10–5000 | Notifications per tenant in each burst |
| `WASLA_REALTIME_LOAD_TENANTS` | 3 | 2–10 | Tenants |

A value outside its range fails the test instead of being clamped. The revocation test uses at most 80 connections and 3 tenants whatever is set. The heavier run, for a developer machine before a release or after a change to the hub (PowerShell, from the repository root):

```powershell
$env:ENCRYPTION_MASTER_KEY = $null
$env:WASLA_REALTIME_LOAD_CONNECTIONS = "1000"; $env:WASLA_REALTIME_LOAD_EVENTS = "1000"; $env:WASLA_REALTIME_LOAD_TENANTS = "5"
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj --configuration Release --filter "FullyQualifiedName~Wasla.UnitTests.Realtime.RealtimeLoadTests" --logger "console;verbosity=detailed"
Remove-Item Env:WASLA_REALTIME_LOAD_CONNECTIONS, Env:WASLA_REALTIME_LOAD_EVENTS, Env:WASLA_REALTIME_LOAD_TENANTS
```

Each test writes its sizes, durations, send-time percentiles and the test process's threads, handles and memory to the test output. The figures describe the machine that ran them, with server and clients in one process; they are not a production capacity claim. `RealtimeLifecycleBrowserTests` is the real-browser layer: one browser at a time with a fixed number of Live Screen pages.

## SQL Server concurrency tests

`OrderActionConcurrencySqlServerTests` (WAS-104) need a real SQL Server, because SQLite does not run application locks and does not prove SQL Server locking. Each test class creates a throwaway database named `Wasla_ConcurrencyTest_<random>`, applies this build's tenant migrations, and drops only that database afterwards; no existing database is read or changed.

The server is the `WASLA_TEST_SQLSERVER` environment variable (an instance name such as `(localdb)\WaslaTests`, not a connection string), otherwise `(localdb)\MSSQLLocalDB`, which CI uses. They run with the rest of `Wasla.UnitTests`. Without a reachable SQL Server they fail rather than skip. To keep them off a developer's own LocalDB instance, create a separate one first:

```powershell
sqllocaldb create WaslaTests
sqllocaldb start WaslaTests
$env:WASLA_TEST_SQLSERVER = "(localdb)\WaslaTests"
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj --filter "FullyQualifiedName~OrderActionConcurrencySqlServerTests"
```

Every caller uses its own service, DbContext and connection pool (`Application Name` differs), as separate Web and Worker processes would. A held provider call makes the contested windows deterministic; the free races start all callers at one barrier. They assert one winner, one provider call, one `ChangeVersion` raise and one notification per contested transition, and `Orders.InvalidStatusForAction` for every loser.
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

Also report warnings, failures, and failures that come from unrelated dirty files. “All tests passed” is only accurate when the full projects you name were run and passed. A focused filter is not the full suite. A green CI run covers the suites listed under [Continuous integration](#continuous-integration) and nothing else.
