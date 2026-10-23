# Testing

## Purpose and scope

This document says which checks exist and how to report them. It does not list every test method.

Source of the commands below: `Wasla.sln`, `tests/Wasla.UnitTests`, `tests/Wasla.PrintBridge.Tests`, `.github/workflows/ci.yml`, `scripts/ci/select-test-scope.ps1`, and `scripts/ci/assert-test-results.ps1`. There is no repository `package.json` and no npm test script.

## Layers

| Layer | Where | What it covers |
| --- | --- | --- |
| Unit and source-contract tests | `tests/Wasla.UnitTests` | Application, infrastructure, Web contracts, orders, auth, sync, signup |
| Print Bridge tests | `tests/Wasla.PrintBridge.Tests` | Desktop client settings, polling, setup, the Core engine against a fake API (`PrintBridgeRuntimeEngineTests`), Stop while a job is claimed, printed or reported, against a fake server that follows the job contract, including printer and server failures and a restart (`PrintBridgeStopRaceTests`), connection changes while a job is active against two fake servers with their own fake tokens: a setup link opened before the claim, during the claim, printing, a delayed or retried `mark-printed` and after Stop returned, several jobs draining, the connection-change lease and Reset, with every report checked for its server and token, settings compared byte for byte and no secret in the logs (`PrintBridgeSetupLinkGuardTests`), the refusal's result mapping and its text in every culture (`PrintBridgeSetupLinkRefusalUiTests`), Gregorian operational dates (`PrintBridgeGregorianCalendarTests`), and the WebView2 app (`WebShell/`): message contract, host operations (single-flight, request-id idempotency, sanitized errors), history projection, printer and operational-settings persistence, the connection setup against the real engine and a fake API (`ShellConnectionSetupTests`: check before save, cancel and failure without writes, refusal while a print job is being claimed, printed or reported, no tokens in logs), the native connection dialog on a Windows Forms message loop (`ShellConnectionDialogTests`: masking, Escape and Enter, accessible errors, tab order, right-to-left), security policy, accessibility markup and localization. The exact set of window types and UI settings (`PrintBridgeWindowInventoryTests`). The real tray application without a usable WebView2 Runtime (`TrayRuntimeNoticeTests`: every tray entry, double-click and a setup link show the one runtime notice, a start without a connection shows it, a configured start stays in the tray, printing continues, a setup link is refused during a print job and still exchanged after it, the exact tray menu, the notice in every culture and right to left). The test-isolation guards (`TestIsolationGuardTests`, and `TrayShutdownIsolationTests` with the real tray application: an exit save that runs after its test ended, a failing test), and exiting the real tray application while it listens to a local fake API with a fake token (`TrayExitTests`, `TrayShutdownLifecycleTests`: listening, stopped, the runtime notice open, the shutdown order with each component disposed once, repeated and concurrent Exit requests, a tray update queued before Exit, a setup link during shutdown, an active job within and beyond the stop limit, settings and history afterwards) need no WebView2 Runtime |
| Print Bridge WebView2 runtime tests | `ShellWebViewRuntimeTests.cs`, `ShellTrayRoutingTests.cs` and two tests in `TrayShutdownLifecycleTests.cs` in `tests/Wasla.PrintBridge.Tests/WebShell/` (trait `Category=WebView2Runtime`) | Real WebView2 control: applied settings, host-to-page rendering, blocked popups, navigation, remote requests, permissions and invalid messages, keyboard tabs in both reading directions, start/stop, single-flight test print, reprint confirmation, light/dark, minimum window and 200 % reflow, the native connection dialog and immediate status refresh, operational settings and the native test-mode confirmation, tab requests. The real tray application: a start without a connection opens the app on its connect step, Open, double-click, Print history, Settings and a setup link open the one app window on the right tab, and the runtime notice replaces it when the app fails to start. Exit with the app window open or closed to the tray (`TrayShutdownLifecycleTests`). Windows stay off-screen and the tray icon stays hidden; a startup failure is simulated before any browser process starts. Skipped when no WebView2 Runtime is installed; needs an interactive Windows session |
| Login redirect browser tests | `TenantLoginRedirectBrowserTests` in `tests/Wasla.UnitTests/Web/` (WAS-113; same driver, browser and CDN prerequisites as the headless-browser tests below) | The post-login redirect chain and Access denied against `Auth/TenantSessionWebHost.cs` (real tenant resolution from the host name, cookie scheme with session revalidation, policies, controllers and views), with the browser mapping `alpha.app.wasla.local` to the in-process server: every role signs in through the rendered form and lands on its page, a deep link the role may not open, and the Access denied start-page button in four cultures, right to left, both themes and phone and desktop widths, followed by a real click |
| Host routing browser tests | `WaslaHostRoutingBrowserTests` in `tests/Wasla.UnitTests/Web/` (WAS-66; same driver, browser and CDN prerequisites as the headless-browser tests below) | The host chain against `Auth/TenantSessionWebHost.cs` with the browser mapping `wasla.local`, `app.wasla.local`, `alpha.app.wasla.local` and two invalid hosts (port included) to the in-process server: a real click on the marketing header's sign-in link leads to the application entry's tenant address page in Turkish and Arabic (right to left), sign-in on the tenant host lands on its page, the application entry does not accept the tenant session, and reserved or look-alike hosts show the unknown-tenant page. The host contract itself is covered without a browser by `WaslaHostClassifierTests` (including `localhost`/IP only where local hosts are allowed), `TenantResolutionMiddlewareTests` (the host and path matrix, and `localhost`/IPv4/IPv6 in Production, Staging, Pilot and Development), `WaslaHostRoutingTests` (production domains over the real Web pipeline), `HostUrlBuilderTests` (canonical, sign-in, signup and homepage URLs never take a `localhost`/IP host outside Development), `ApiHostContractTests` (the real Api pipeline with active tenants stored under every rejected shape: no lookup, health on every host, Print Bridge on valid hosts only), `HostDomainStartupPolicyTests` and `TenantHostSessionTests` |
| Headless-browser tests | `AdminRtlLayoutBrowserTests`, `AdminMobileNavigationBrowserTests`, `ThemePreferenceBrowserTests`, `TenantDarkModeBrowserTests`, `LiveScreenSyncStatusBrowserTests`, `LiveScreenLargeSnapshotBrowserTests`, `OrdersDateFilterBrowserTests`, `Realtime/PrintStatusBrowserTests`, `Realtime/LiveScreenRealtimeBrowserTests`, `Realtime/RealtimeLifecycleBrowserTests`, `Web/TenantAccessibleNamesBrowserTests` and `Web/PrintBridgeDevicesFocusBrowserTests` in `tests/Wasla.UnitTests/` (driver: `Admin/HeadlessChromium.cs`) | The real Admin and tenant layouts served in-process and rendered by a headless Chromium browser over the DevTools protocol: RTL overflow, mobile navigation, theme preference, accessible names read from the browser's accessibility tree (`AccessibleNodesAsync`: tenant and Central Admin sign-in fields named by their visible labels, every tenant sidebar control with the same name expanded and collapsed, the settings menu with Logout, the phone drawer, in every culture, both themes, right to left and at 390 px), the Print Bridge Devices page while its regions refresh (an unchanged print-job answer leaves the focused reprint button, the text selection and the region alone; a changed one gives focus back to the same job's button and keeps the table scrolled; while the focused button would disappear or text is selected, the update waits, says so in a localized status line and applies once the user moves on; the restored button keeps its `:focus-visible` ring; the devices table follows the same rules; waits are on requests the page started and rows it shows, never fixed sleeps), the Live Screen with about 5,755 active orders (`LargeActiveOrderSeed`: usable within seconds and responsive, header and column counts, one page per column with every order reachable through Show more, unchanged refreshes without DOM churn, an invalidation burst during slow reads, a failed refresh, Board scrolling at 768 px and stacking at 390 px in both directions and themes, and the details dialog returning focus), the Orders date filter in Gregorian days in every culture (a date chosen in the input and applied with a real Filter click, the inverted-range message right to left at 390 px and desktop, and an earlier day's order on the Arabic Live Screen), the Live Screen's platform connection status (a paused connection is shown as a connection issue, not "Live", in every culture and right to left), live receipt print status over the real SignalR hub (details page and Live Screen modal, the disconnected fallback and the read on reconnect), Live Screen invalidation (new orders and lifecycle changes without polling, one announcement per new order, bursts, the 10-second fallback, reconnect and back/forward-cache restore with one connection, order training, a Kitchen screen, English and Arabic, and a connected page that keeps the 10-second rate without Web's internal listener), several Live Screen pages of one user (one connection each, released when a page closes or navigates away, notification permission never requested and preferences unchanged in every permission state), and the painted dark mode: `TenantDarkModeBrowserTests` serves the populated tenant app (`Web/TenantVisualWebHost.cs`: real controllers, views and Infrastructure services over seeded SQLite databases with fake data) and checks for light surfaces and text below WCAG AA contrast (outside the exceptions listed in [../frontend/design-system.md](../frontend/design-system.md#dark-mode-in-the-tenant-app)) on every tenant page, the Live Screen, guided setup (a second host seeds a tenant still in Setup), menus, the drawer, modals, validation and keyboard focus, at phone and desktop widths in LTR and RTL. The browser is the one named by `WASLA_TEST_BROWSER`, otherwise an installed Edge or Chrome. The pages load Bootstrap, Bootstrap Icons and AdminLTE from `cdn.jsdelivr.net`, so these tests need internet access. Skipped when no browser is found or the CDN does not load. The driver's startup (reading `DevToolsActivePort` while the browser still writes it, browser exit, timeout, cancellation and cleanup after a failed start) is covered without a browser by `HeadlessChromiumStartupTests`. The driver removes an inherited `__COMPAT_LAYER` from the browser's environment, because Edge started with a compatibility layer relaunches itself and the started process exits with code 0; `HeadlessChromiumCompatibilityLayerTests` covers this, once without a browser and once with the real browser. Disposing a browser stops the processes it started (the browser process and its descendants, found by parent process and creation time, never by name) and deletes the exact profile folder it created. A termination that is refused while a process is already exiting is not an error; a process that still runs 10 s after the kill is. A stopped process that Windows is still tearing down is waited for, and the profile deletion retries while its files are held, up to 15 s; anything left fails with the profile path and the state of each process. Setup between the start and the test's `await using` (cookies, viewport, a first page) goes through `SetUpAsync`, which disposes the browser when the setup fails, skips or is cancelled. Profile folders from earlier runs are never deleted. `HeadlessChromiumCleanupTests` covers the cleanup without a browser, and `HeadlessChromiumCleanupBrowserTests` with the real browser, including two browsers and an Edge started outside the driver. Every test class that starts a real browser, including the login redirect and host routing classes above, carries the trait `Category=Browser` (see [Test scope](#test-scope)) |
| SQL Server concurrency tests | `tests/Wasla.UnitTests/Orders/OrderActionConcurrencySqlServerTests.cs` and `OrderSyncStatusRaceSqlServerTests.cs` (database: `SqlServerTenantDatabase`); `tests/Wasla.UnitTests/Billing/BillingSqlServerTests.cs` and `BillingMigrationSqlServerTests` (database: `SqlServerCentralDatabase`) | Order lifecycle actions on a real SQL Server (WAS-104): simultaneous identical and conflicting actions from separate contexts and connection pools, operator versus Worker auto-approve, provider failure and retry, a status written by sync during the provider call, and a claim held by a session that then ends. Worker sync updating an order while an operator action or another Worker commits a status for it (WAS-123). Billing subscriptions in CentralDb (WAS-147): simultaneous starts, identical payments and conflicting provider states, database constraints, and the migration's rollback guard. See [SQL Server concurrency tests](#sql-server-concurrency-tests) |
| Node tests | Every tracked `*.test.js` file, under `tests/Wasla.UnitTests/` and `tests/Wasla.PrintBridge.Tests/WebShell/` | Browser-side Web scripts and the Print Bridge app message model and view helpers. Not compiled by the csproj |
| Browser smoke | Manual | Flows the headless-browser tests do not cover |

Some Print Bridge tests also skip when no Windows printer is installed. They only enumerate the installed printers, because the runtime refuses to start without one; output always goes to a recording fake.

## Continuous integration

`.github/workflows/ci.yml` runs on pushes to `dev` and `master`, on pull requests into them, and manually (**Run workflow** on the Actions tab, or `gh workflow run`). It has two jobs that run in parallel. Each must pass. The job names are the same in every scope.

| Job | Runner | What it runs |
| --- | --- | --- |
| `build-and-test` | `windows-latest`, .NET 8 SDK (`8.0.x`) | Restore and Release build of `Wasla.sln`, then `Wasla.UnitTests` and `Wasla.PrintBridge.Tests` in the [test scope](#test-scope) of the event |
| `javascript-tests` | `ubuntu-latest`, Node.js `24.21.0` (pinned in the workflow) | `node --test` over every file `git ls-files '*.test.js'` lists, in every scope. The job fails when that list is empty |

Print Bridge, its WebView2 runtime tests and the Edge headless-browser tests need Windows, so all .NET tests share the Windows job and its single Release build. The Node tests need no Windows API and run on Ubuntu.

A new push to a pull request cancels that pull request's older run; a cancelled run shows as cancelled, never as passed. Runs for pushes to `dev` and `master` and manual runs are never cancelled, so every commit on those branches gets a result. NuGet packages are cached by the project files' hash; restore still runs every time, so the cache only saves download time.

### Test scope

`scripts/ci/select-test-scope.ps1` chooses the scope from the event, logs it with the filters, and adds it to the job summary. The two test steps are named `Run Wasla.UnitTests (fast)` or `(full)`, and likewise for Print Bridge.

| Event | Scope | `Wasla.UnitTests` | `Wasla.PrintBridge.Tests` |
| --- | --- | --- | --- |
| Pull request into `dev` | fast | `--filter "Category!=Browser"` | `--filter "Category!=WebView2Runtime"` |
| Push to `dev`, pull request into `master` (also when its base changes to `master`), push to `master`, manual run on any branch | full | No filter: every test | No filter: every test |

Any other event or base branch also gets the full scope. Both scopes build the whole solution, run every tracked Node test, check the prerequisites, require every selected test to run and pass, report leftover processes and upload the results.

The fast scope defers two groups, each named by a trait:

- `Category=Browser` (`Wasla.UnitTests`): the test classes that start a real headless browser through `HeadlessChromium` (20 classes, 78 test methods). The trait is on the class, so a class's few tests that do not open a browser are deferred with it.
- `Category=WebView2Runtime` (`Wasla.PrintBridge.Tests`): the tests that host the real WebView2 Runtime (22 test methods).

A test without one of these traits runs in both scopes, so a new test is in the fast scope until its author tags it. Give a new class that starts a real browser `[Trait("Category", "Browser")]`, and a new test that needs the real WebView2 Runtime `[Trait("Category", "WebView2Runtime")]`. Do not use either trait to move a test that needs neither out of pull requests; that is a change to this policy. To check that the two selections still cover the discovered suite, list both against the build output and compare them with the whole list (PowerShell, after a Release build):

```powershell
$exe = 'tests/Wasla.UnitTests/bin/Release/net8.0/Wasla.UnitTests.exe'
$all = & $exe -list tests | Where-Object { $_ -like 'Wasla.*' }
$fast = & $exe -list tests -trait- 'Category=Browser' | Where-Object { $_ -like 'Wasla.*' }
$deferred = & $exe -list tests -trait 'Category=Browser' | Where-Object { $_ -like 'Wasla.*' }
"$($all.Count) = $($fast.Count) + $($deferred.Count)"
Compare-Object $all ($fast + $deferred)
```

`Compare-Object` prints nothing when every test is in exactly one selection. For Print Bridge use `tests/Wasla.PrintBridge.Tests/bin/Release/net8.0-windows/Wasla.PrintBridge.Tests.exe` and `Category=WebView2Runtime`.

Other in-process integration tests stay in the fast scope, among them the web-host tests for authentication, tenant isolation, session revalidation and rate limits, the realtime hub and load tests and the SQL Server concurrency tests. Since the browsers left the fast scope they are most of its remaining time.

Measured on `windows-latest` (wall time of the Windows job; the Node job takes about 1 to 1.5 minutes in parallel):

| Run | Scope | Windows job | `Wasla.UnitTests` | `Wasla.PrintBridge.Tests` |
| --- | --- | --- | --- | --- |
| Before WAS-101: push to `dev`, `8d25a72`, 2026-10-22 ([run](https://github.com/maliozd/Wasla/actions/runs/39744886947)) | everything | 30 min 46 s | 26 min 15 s, 3,517 results | 1 min 54 s, 705 results |
| WAS-101 pull request into `dev`, `98b034d`, 2026-10-22 ([run](https://github.com/maliozd/Wasla/actions/runs/39826007059)) | fast | 17 min 13 s | 14 min 13 s, 3,354 results | 1 min 8 s, 682 results |
| WAS-101 manual run, `98b034d`, 2026-10-22 ([run](https://github.com/maliozd/Wasla/actions/runs/39826003000)) | full | 32 min 6 s | 27 min 23 s, 3,554 results | 2 min 2 s, 705 results |

Results count theory cases, so they are higher than the test-method counts above. On the same commit the fast results plus the deferred groups' results (200 and 23) equal the full run's. Earlier full runs of the Windows job took 30 to 47 minutes, so a single run is only an indication.

### Full validation of a feature branch

A green fast run does not cover the browser or WebView2 runtime tests. Run the full scope on the branch before merging a pull request into `dev` that changes authentication, authorization or tenant isolation, payment, signup or provisioning, data integrity or migrations, the Web layouts, Live Screen or other browser-visible behavior, the Print Bridge WebView2 app or tray, this workflow or its scripts, or that comes from a machine where the full suite is not run locally:

```bash
gh workflow run ci.yml --ref <branch>
```

Or open **Actions → CI → Run workflow** and choose the branch. The run tests the branch's head commit at that moment; it shows in the pull request's checks with `(workflow_dispatch)` after the job name. Make sure its commit is the pull request's head, and run it again after a later push that should be covered.

### Tradeoff: `dev` can fail after a merge

A pull request into `dev` can merge with only the fast scope green. The full scope then runs on the push to `dev`, so `dev` can turn red for a browser or WebView2 runtime failure that the pull request never ran. Treat a red `dev` run as the first thing to fix (fix forward or revert), and do not open a pull request from `dev` into `master` until the latest `dev` run is green. `master` gets no unvalidated change: pull requests into `master` and pushes to `master` always run the full scope.

Moving a pull request's base to `master` starts the full scope by itself: `.github/workflows/ci-base-change.yml` reacts to an edit of a pull request into `master`, and when the edit changed the base it runs the CI workflow on the merge with `master`. The event still carries the merge with the old base, so its first job (`Find the merge with the new base`) waits, up to 10 minutes, until GitHub's merge commit has the current `master` and the pull request's head as parents, and the CI workflow checks out that commit. It fails, rather than skips, when it cannot read the pull request, when the pull request conflicts with `master` or when no merge commit appears. The CI checks are named `Full CI after base change / ` followed by the CI job names. They replace an older run of that pull request that is still in progress. A title or description edit only skips its jobs and starts no CI run. Moving a base from `master` back to `dev` starts nothing; the existing full checks stay. Re-running an old run keeps the event and base branch it started with, and so its scope.

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
- `scripts/ci/assert-test-results.ps1` then reads every result file, in both scopes. It fails the job when a file is missing, has no tests, or reports a failed or **skipped** test. A filter that selects no test still writes a result file and `dotnet test` still exits with 0, so this check is what fails such a run. A missing browser, an unreachable CDN, a missing WebView2 Runtime or a missing printer therefore fails CI instead of passing as a skip. The script adds an error annotation per failed or skipped test (first 20) and a totals table to the job summary.
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

To reproduce the Windows CI job's full scope exactly, run from the repository root in PowerShell. Clear `ENCRYPTION_MASTER_KEY` in that session first, because the runner does not have it. For the fast scope, add `--filter "Category!=Browser"` to the first `dotnet test` and `--filter "Category!=WebView2Runtime"` to the second:

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

To leave out the real-runtime tests, filter with `--filter "Category!=WebView2Runtime"`. In `Wasla.UnitTests`, `--filter "Category!=Browser"` leaves out the headless-browser tests.

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

Local runs follow the risk of the change. Run the affected tests and the tests of the contracts the change depends on or exposes; a full local suite after every task is not required, and GitHub Actions supplies the Windows, browser and WebView2 runs. Do not run a full suite locally again for a commit that already has a green full CI run, or the reverse. Changes to security, authentication or tenant isolation, payment or provisioning, data integrity or migrations need full verification before they are merged into `dev`: a full local run where that machine is meant to run one, otherwise the [manual full run](#full-validation-of-a-feature-branch) of the branch. Avoid starting several full suites at once from different worktrees on one machine.

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

`OrderActionConcurrencySqlServerTests` (WAS-104) and `OrderSyncStatusRaceSqlServerTests` (WAS-123) need a real SQL Server, because SQLite does not run application locks and does not prove SQL Server locking. Each test class creates a throwaway database named `Wasla_ConcurrencyTest_<random>`, applies this build's tenant migrations, and drops only that database afterwards; no existing database is read or changed.

The server is the `WASLA_TEST_SQLSERVER` environment variable (an instance name such as `(localdb)\WaslaTests`, not a connection string), otherwise `(localdb)\MSSQLLocalDB`, which CI uses. They run with the rest of `Wasla.UnitTests`. Without a reachable SQL Server they fail rather than skip. To keep them off a developer's own LocalDB instance, create a separate one first:

```powershell
sqllocaldb create WaslaTests
sqllocaldb start WaslaTests
$env:WASLA_TEST_SQLSERVER = "(localdb)\WaslaTests"
dotnet test tests/Wasla.UnitTests/Wasla.UnitTests.csproj --filter "FullyQualifiedName~SqlServerTests"
```

Every caller uses its own service, DbContext and connection pool (`Application Name` differs), as separate Web and Worker processes would. A held provider call makes the contested windows deterministic; the free races start all callers at one barrier. They assert one winner, one provider call, one `ChangeVersion` raise and one notification per contested transition, and `Orders.InvalidStatusForAction` for every loser.

`OrderSyncStatusRaceSqlServerTests` pause a Worker sync pass after it read an existing order and before its update transaction (a transaction interceptor on that sync's context), and commit another writer in that window: an operator reject while the provider reports the order accepted, an operator approve while the sync's merge keeps `New` (with a scalar change, and with an items-only change), and a second Worker applying the same payload, with and without changed items and options. They assert that sync keeps the committed status and timestamps, never moves `UpdatedAt` backwards, raises `ChangeVersion` only for a real change, does not rewrite items another Worker already wrote, and requests no receipt for a cancelled or already handled order. While sync holds its row lock, an update from another connection with a short `LOCK_TIMEOUT` must time out.
## Repository-root discovery

Many source-contract tests walk parent directories from the test assembly output until they find `Wasla.sln`, then read files under `src/`.

An output directory that is still inside this repository (including a gitignored `artifacts/` folder under the repo) can still find the solution. An output directory outside the repository cannot. Those tests then fail even though the product code is unchanged.

Do not set a global output path outside the repo to “make tests pass.”

## Process-wide console

`Console.Out` and `Console.Error` are shared by every test in the process, and xUnit runs test classes in parallel. A test class that redirects them to capture CLI output, or that runs a CLI command writing to the console, belongs to `CliConsoleCollection` (`tests/Wasla.UnitTests/Cli/CliConsoleCollection.cs`), which runs alone (WAS-171). Otherwise one capture can restore another's already disposed writer, so a later console write fails with `ObjectDisposedException`, or a capture can collect another test's output. Tests that start the CLI as a separate process read that process's own output and need no collection.

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

Also report warnings, failures, and failures that come from unrelated dirty files. “All tests passed” is only accurate when the full projects you name were run and passed. A focused filter is not the full suite. A green CI run covers the suites listed under [Continuous integration](#continuous-integration) and nothing else; name its scope. A green fast run (a pull request into `dev`) does not include the `Category=Browser` and `Category=WebView2Runtime` tests.
