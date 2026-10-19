# Wasla POS first vertical slice and backlog

## Status and scope

Plan from WAS-34 (2026-10). **Nothing here is implemented.** It defines the smallest end-to-end POS slice that proves the decisions in [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md), and the dependency-ordered issues that build it. The product scope is in [pos-mvp.md](pos-mvp.md).

The slice is a development and test milestone. It is not production-ready and not a pilot: the [fiscal decision](pos-mvp.md#unresolved-product-decisions), the menu editor, staff management and a physical-printer pilot come after it.

## The slice

> An operator activates POS for a test restaurant and imports a minimal menu → the Owner pairs a device and signs in with a PIN → opens table 1, or a quick sale → adds products, one with a modifier → sends a kitchen ticket → records a cash or external-terminal payment → the check closes → the Owner reopens it read-only from history.

### Included

- POS activation for one tenant: POS database created and migrated, first Owner and first device pairing code issued (`Wasla.Pos.Cli`)
- Menu import from a JSON file by the operator (`Wasla.Pos.Cli`): categories, products with prices, one modifier group type (single and multiple choice), areas and tables. Idempotent by the file's own keys
- Device pairing; Owner PIN sign-in; inactivity lock; POS roles and policies for Owner and Cashier (Manager, Waiter and Kitchen exist in the policy table and tests, without screens of their own)
- Sale screen: categories, product tiles, modifier panel, cart; tables screen; dine-in check for a table; quick sale
- Send to kitchen: kitchen ticket record and a POS print job (rendered document) available through the POS device protocol
- Payment records: cash (tendered, change) and external terminal (optional reference); several payments per check; close on full payment
- History of the business day; read-only check detail with lines, kitchen tickets and payments
- Audit records for sign-in, send, payment and close
- TR, EN, AR, RU resources for every slice screen; Arabic right to left

### Not included

Takeaway, voids and cancellations, payment reversals, pre-bill, receipt printing, reprints, the menu and table editor, staff management, Manager/Waiter screens, the Print Bridge second connection, shifts, and everything listed as later or out of scope in [pos-mvp.md](pos-mvp.md#mvp-boundary). They follow in the [backlog](#backlog).

## Acceptance criteria

Each criterion names the test that proves it. "Browser test" means a headless-browser test like the existing ones in `tests/Wasla.UnitTests` ([../operations/testing.md](../operations/testing.md)), run against an in-process POS host with fake data.

1. **Activation.** `Wasla.Pos.Cli activate` for an active tenant creates a POS database with a server-assigned name and ownership marker, applies POS migrations, records the activation in CentralDb, and prints one Owner setup code and one pairing code. Repeating it reports "already active" and changes nothing. Two simultaneous runs produce one database. A tenant without activation gets 404 on its POS host. *SQL Server test on a throwaway database; CLI unit tests.*
2. **Menu import.** Importing the sample file twice gives the same menu (no duplicates); an invalid file (missing price, unknown modifier group, negative price) changes nothing and lists the errors. *Unit tests.*
3. **Isolation.** A device paired to tenant A, presenting its credential on tenant B's POS host, is rejected, and nothing of tenant B is read. A POS request never opens an Orders tenant database. *Unit tests; reference-direction tests.*
4. **Sign-in.** A PIN works only on a paired device; five wrong PINs lock that staff member on that device for the configured time; the lock and each failure are audited without the PIN. *Unit tests; browser test of the PIN pad in four cultures and right to left.*
5. **Sale screen.** Tapping a product without modifiers adds a line; a product with a required single-choice group cannot be added until a choice is made; totals update with option prices. All controls are at least 48×48 CSS px; the layout mirrors in Arabic; there is no horizontal scroll at 1024×768. *Node tests for the cart module; browser test at 1024×768 and 768×1024, LTR and RTL, light and dark.*
6. **Table check.** Opening table 1 creates its check; opening it from a second device shows the same check; a second open check for table 1 is impossible even when two devices try at the same moment. *SQL Server concurrency test.*
7. **Send to kitchen.** Sending creates one kitchen ticket with exactly the unsent lines and one Pending POS print job of kind rendered document for the kitchen printer role. Repeating the request with the same idempotency key creates nothing new. A protocol client with a fake token claims the job, and `mark-printing` and `mark-printed` follow the same rules as Orders jobs (a repeated report is `skipped`). *Unit tests; protocol contract tests against a fake client.*
8. **Payment.** Recording exact cash closes the check and frees the table; cash above the total records change; an external-terminal payment with reference `123456` is recorded, and a reference of 16 digits that passes the Luhn check is refused. Two devices paying the same check at the same moment: one wins, the other sees the closed check, and exactly one payment exists. A timed-out payment retried with the same idempotency key is recorded once. *SQL Server concurrency tests; unit tests.*
9. **History.** The closed check appears in today's history (`Europe/Istanbul` business day) with its total and payment method and opens read-only: no control on it changes the check, and every write endpoint answers that a closed check cannot change. *Unit tests; browser test.*
10. **Audit.** Sign-in, send to kitchen, payment and close each produce one audit record with time, device, staff member, check and amounts. *Unit tests.*
11. **Orders unaffected.** `Wasla.sln` builds; the full `Wasla.UnitTests`, `Wasla.PrintBridge.Tests` and Node tests pass on the slice's final commit, with no Orders behavior change. *CI.*

Not proven by the slice: real printers and paper output, real restaurant networks, performance targets, Print Bridge talking to POS. The [Print Bridge issue](#backlog) covers the last one; the others need a pilot.

## Implementation order

Each step is one issue in the backlog, merged into `dev` through its own branch and worktree, with focused tests. No step changes Orders behavior; step 1 moves Orders code without changing it.

1. Extract the platform library.
2. Scaffold the POS application and guardrails.
3. POS activation and database.
4. Devices, PIN sign-in, roles, audit.
5. Menu and tables import.
6. Sale screen, tables, checks and send to kitchen.
7. Payments, close and history. **The slice is complete here.**
8. Print protocol extraction and POS print rendering.
9. Print Bridge second connection.

Steps 8 and 9 can start once step 6 has merged, in parallel with step 7.

## Backlog

Created in the Linear project *Wasla POS — MVP*. Each issue's description holds its full boundary and verification; this table is the summary.

| # | Issue | Boundary | Depends on | Verification |
| --- | --- | --- | --- | --- |
| 1 | [WAS-156](https://linear.app/wasla-food/issue/WAS-156) Extract the shared platform library for tenant resolution, secrets and the CentralDb registry | Move `TenantResolver`, `TenantHostMissCache`, `ISecretManager`/`AesSecretManager` and a read-only registry reader into a new library referenced by Orders. No behavior, configuration or schema change | This ADR merged | `dotnet build Wasla.sln`; full `Wasla.UnitTests` and `Wasla.PrintBridge.Tests`; reference-direction test for the new library |
| 2 | [WAS-157](https://linear.app/wasla-food/issue/WAS-157) Scaffold the Wasla POS application, solution, CI job and boundary tests | `src/Pos/*`, `tests/Pos/*`, `Wasla.Pos.sln`, a CI job, POS host resolution (unknown or unactivated tenant → 404), health endpoint, POS layout with tokens, cultures and RTL. No POS feature | 1 | POS build and tests; reference-direction tests in both test projects; existing CI job unchanged and green |
| 3 | [WAS-158](https://linear.app/wasla-food/issue/WAS-158) Activate POS per tenant: CentralDb registration, POS database and migrations | Additive CentralDb table and migration; `PosDbContext` and initial migration; `Wasla.Pos.Cli` activate, migrate, migrate-all, status; ownership marker, reserved names, application lock | 2 | Unit tests; SQL Server test on a throwaway database; `migration-status` unchanged for Orders; no migration applied to a shared database |
| 4 | [WAS-159](https://linear.app/wasla-food/issue/WAS-159) Pair POS devices and sign in staff with PINs under POS roles | Device pairing and revocation, PIN hashing, lockout, inactivity lock, POS role policies, audit log | 3 | Unit tests for isolation, lockout and the policy matrix; browser test of the PIN pad in four cultures, RTL, both themes |
| 5 | [WAS-160](https://linear.app/wasla-food/issue/WAS-160) Import a POS menu, modifiers, areas and tables from a file | Catalog, modifier and table entities; validated, idempotent import command; sale-screen menu read model | 4 | Unit tests for import idempotency and validation |
| 6 | [WAS-161](https://linear.app/wasla-food/issue/WAS-161) Build the touch sale screen, table checks, quick sale and send to kitchen | Sale and tables screens, cart module, check and line writes with idempotency keys, one open check per table, kitchen ticket records and POS print job rows | 5 | Unit, Node and browser tests (sizes, RTL, themes); SQL Server concurrency test for one open check per table |
| 7 | [WAS-162](https://linear.app/wasla-food/issue/WAS-162) Record cash and external-terminal payments, close checks and show sale history | Payment records, close, compare-and-set, duplicate protection, reference validation, business-day history, read-only detail | 6 | SQL Server concurrency tests; unit tests; browser test. Slice acceptance criteria 1–11 |
| 8 | [WAS-163](https://linear.app/wasla-food/issue/WAS-163) Extract the Print Bridge device protocol and add rendered-document jobs for POS | Shared protocol library; rendered-document job kind; POS device endpoints and print-device pairing; kitchen ticket rendering in four languages. Orders routes and receipts unchanged | 6 | Orders print tests unchanged and green; protocol contract tests against both servers with fake clients |
| 9 | [WAS-164](https://linear.app/wasla-food/issue/WAS-164) Let one Print Bridge installation print for Wasla Orders and Wasla POS | A second connection with its own `ServerUrl`, token and loop; printer roles; connection UI in the WebView2 app; rendered-document printing | 8 | `Wasla.PrintBridge.Tests` with two fake servers and fake tokens (one connection per job, stop and exit rules); WebView2 runtime tests; manual test with a physical receipt printer, reported separately |

### After the slice

Not created as issues yet. Each is created when the slice is accepted, in this order: the back-office editor for menu, tables, staff and devices; takeaway; voids, cancellations and payment reversals with manager approval; pre-bill, receipt printing and reprints; a pilot readiness review covering the fiscal decision, offline expectations, backups of POS databases, and physical printer and network checks at a pilot restaurant.

## Risks

| Risk | Mitigation |
| --- | --- |
| The platform extraction (step 1) breaks Orders | Move only, with no behavior change and the full Orders test suites; it lands before any POS code |
| Fiscal rules make a non-fiscal POS unsellable in Türkiye | Legal review before any paid pilot ([open decisions](pos-mvp.md#unresolved-product-decisions)); receipts say they are not fiscal documents |
| Online-only POS is not acceptable to pilot restaurants | Ask during pilot selection; the slice already uses ids and idempotency keys that offline capture needs |
| Two credentials per owner feel broken | Explicit open decision with triggers in the [ADR](../architecture/pos-application-boundary.md#identity-and-sign-in) |
| Print Bridge with two connections regresses Orders printing | Separate issue with the existing stop, exit and connection-change tests extended to two servers |
