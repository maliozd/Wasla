# Decision: Wasla POS as a separate application in this repository (WAS-34)

## Status and scope

Proposed in WAS-34 (2026-10). It becomes accepted when the pull request that adds it is merged. It records where Wasla POS code, data, identity and printing live relative to Wasla Orders. **Nothing here is built.** No POS project, database, table or route exists; this repository still implements only Wasla Orders ([../product/product-boundaries.md](../product/product-boundaries.md)). The product scope is in [../product/pos-mvp.md](../product/pos-mvp.md), and the build order in [../product/pos-first-slice.md](../product/pos-first-slice.md).

## Context: what the current code gives POS

Facts from the code on `dev` at `198b49b`, the base of this branch, rechecked at `40fe698`:

- **Every Orders server project depends on one Infrastructure assembly.** `Wasla.Web`, `Wasla.Api`, `Wasla.Worker` and `Wasla.Cli` reference `Wasla.Infrastructure`. That assembly holds both `CentralDbContext` and `TenantDbContext`, their migrations, the provider clients, order services, printing services, `TenantResolver` and `AesSecretManager`. Referencing it from POS would bring in all of Orders.
- **Tenant users live in the Orders tenant database.** `AppUser` (email, BCrypt hash, `UserRole`, security stamp) is a `TenantDbContext` entity. Web login checks the password against that database (`AuthValidationService`), and every request is revalidated against it ([authentication.md](authentication.md#tenant-session-revalidation)). CentralDb's `TenantMembership` is a plan and subscription record, not a user store.
- **Roles are Orders roles.** `UserRole` (Owner, Manager, Kitchen, Cashier, Viewer) and `WaslaTenantPolicies` describe platform-order work ([../product/roles-and-permissions.md](../product/roles-and-permissions.md)).
- **There is no catalog.** `TenantDbContext` has orders, order items and options copied from provider payloads, but no product, category, modifier or table entity.
- **Print jobs are order receipts.** `PrintJob.OrderId` is required and has a cascading foreign key to `Order`. `PrintJobType` has one value, `Receipt`, and the pending feed (`PrintBridgeJobService`), receipt and history services filter on it. The server stores structured order JSON (`ReceiptPayloadBuilder`), and Print Bridge formats it into text on the client (`ReceiptFormatter` in `Wasla.PrintBridge.Core`). `Wasla.PrintBridge.Core` references `Wasla.Application`.
- **Print Bridge has one server.** A device token is looked up in CentralDb `PrintBridgeDevices`, which gives the tenant. Print Bridge holds one `ServerUrl` and one token, and each polling cycle and job uses one copy of that connection (WAS-58, [../integrations/print-bridge.md](../integrations/print-bridge.md#job-flow)). The device job routes are served by both Web and Api. The setup exchange is served only by Web (`PrintBridgeSetupApiController`).
- **Releases stop Orders and migrate every Orders database first** ([../operations/deployment.md](../operations/deployment.md#release-order-migrate-every-database-first)). Tenant migrations run through `Wasla.Cli` (`migrate-all-customers`), and `Tenant.SchemaVersion` is a constant nobody reads at runtime.
- **Subscription billing is CentralDb's.** The `Tenant` billing and subscription enums, `TenantMembership` and the iyzico work in WAS-145 to WAS-154 concern Wasla's SaaS fee. There is no restaurant-sale payment entity; `Order.PaymentMethod` is copied from providers.
- **Hosts and cookies.** Tenants resolve by host from CentralDb (`TenantResolver`, `PrimaryDomain`). Cookies are host-only. WAS-66 plans `{slug}.app.wasla.tr` for the tenant app and reserves root names such as `pos.wasla.tr` for products.
- **CI builds one solution.** `.github/workflows/ci.yml` builds `Wasla.sln` and runs every test project on every pull request. There is no path filter, no `Directory.Build.props` and no central package management.

## Options compared

| Concern | A. Separate repository and solution | B. Separate application inside this repository (chosen) |
| --- | --- | --- |
| Tenant and account sharing | Tenant resolution, secret handling and the CentralDb registry must be copied or published as packages from this repository | Shared through a small platform library referenced by both products, versioned with the code that uses it |
| Authentication and authorization | POS owns its own; reaching Orders users needs a cross-repository contract | Same: POS owns its own (see [Identity](#identity-and-sign-in)). Shared pieces stay project references, checked by tests |
| CentralDb ownership | CentralDb schema and migrations stay here, so POS would depend on another repository for every registry change | One CentralDb schema history stays here; POS reads it, and registry changes ship as one reviewed change |
| POS database ownership | Own | Own: separate `PosDbContext`, migrations and tool |
| Release independence | Complete | Separate deployable, version and release procedure; a shared-library change needs both builds green, not both deploys |
| Migration independence | Complete | Complete for the POS database; additive CentralDb changes go through the existing central history |
| Offline and network loss | No difference: both put POS behind its own server | Same |
| Test and build isolation | Complete, at the cost of a second CI, rule set and agent setup | Separate solution and CI job for POS, plus reference-direction tests |
| Shared contracts (print protocol, tenancy) | Need a package feed and version pinning across repositories | Project references; a protocol change and both its users change in one pull request |
| Operational cost (one small team) | Second repository, CI, rules, issue routing, package publishing | One repository, one rule set, one more CI job |
| Future desktop, tablet and web clients | No difference: clients use POS's HTTP API | Same |
| Code ownership and accidental coupling | Coupling is impossible, and so is sharing without packaging | Coupling is possible and must be blocked by tests and review rules ([Guardrails](#guardrails)) |

Option A buys isolation this team can get more cheaply, and pays for it with package publishing and cross-repository coordination for exactly the pieces POS must share: tenancy, CentralDb and the Print Bridge protocol. Option B keeps those changes atomic and reviewable. Its main risk, accidental coupling, is testable.

## Decision

1. **Wasla POS is a separate, independently deployable application inside this repository.** Its projects are `Wasla.Pos.Domain`, `Wasla.Pos.Application`, `Wasla.Pos.Infrastructure` and `Wasla.Pos.Web`, an ops console `Wasla.Pos.Cli`, and `tests/Wasla.Pos.Tests`, under `src/Pos/` and `tests/Pos/`. They are built by their own `Wasla.Pos.sln` and CI job. `Wasla.sln` keeps building Wasla Orders and the shared libraries.
2. **POS never references an Orders project**: not `Wasla.Domain`, `Wasla.Application`, `Wasla.Infrastructure`, `Wasla.Contracts`, `Wasla.Web`, `Wasla.Api`, `Wasla.Worker` or `Wasla.Cli`. **Orders never references a POS project.**
3. **Shared code is extracted, not referenced across products.** A platform library (`Wasla.Platform`, name to be confirmed in the extraction issue) receives only what both need now: host-to-tenant resolution, the secret manager, and a read-only CentralDb registry reader. The extraction changes no Orders behavior and lands before any POS project. Further sharing (culture list, TRY formatting, design tokens) is extracted when a POS issue needs it, never copied.
4. **POS has its own process and host.** `Wasla.Pos.Web` is a separate ASP.NET Core MVC/Razor process on its own per-tenant host name under a POS domain (`{slug}.pos.wasla.local` in development; the production pattern is set together with WAS-66). It issues its own host-only cookies. It never reads Orders cookies or Data Protection keys and never calls Orders Web or Api.
5. **POS stores its data in its own database per tenant** ([Data ownership](#data-ownership)). It does not read or write the Orders tenant database.
6. **POS staff identity is POS-owned for the MVP** ([Identity](#identity-and-sign-in)).
7. **Print Bridge is shared through a versioned device protocol, not through Orders' job table** ([Print Bridge](#print-bridge)).

### Stack

POS follows the repository's conventions: .NET 8, ASP.NET Core MVC/Razor, Bootstrap 5, vanilla JavaScript, EF Core on SQL Server, the same architecture boundary rules inside the POS projects ([10-architecture-boundaries.mdc](../../.cursor/rules/10-architecture-boundaries.mdc)), the same localization rules and cultures, and the Wasla design tokens. A future native tablet or desktop client is a client of `Wasla.Pos.Web`'s JSON endpoints, not a second POS server.

## Data ownership

| Data | Owner and store | Notes |
| --- | --- | --- |
| Tenant registry (`Tenants`), subscription (`TenantMembership`, WAS-147 billing) | Platform, **CentralDb** | POS reads it, never writes it at runtime |
| POS activation per tenant: status, POS database name, encrypted connection string, key version, last migration | Platform, **CentralDb**, new table added through the existing central migration history | Written only by the activation command of `Wasla.Pos.Cli`, through the platform library; `Wasla.Pos.Web` only reads it to open the tenant's POS database |
| Products, categories, prices | POS, **POS tenant database** | Orders has no catalog; provider order items stay order snapshots in the Orders database |
| Modifier groups and options | POS, POS tenant database | Lines store name and price snapshots, so later menu edits never change history |
| Areas and tables | POS, POS tenant database | |
| Checks (adisyon) and lines, voids | POS, POS tenant database | Ids created by the device ([idempotency](../product/pos-mvp.md#idempotency-and-duplicate-protection)) |
| Payment records and reversals | POS, POS tenant database | Records only; never in CentralDb, never with subscription billing |
| Kitchen tickets, receipts (number and rendered snapshot), POS print jobs | POS, POS tenant database | Not Orders' `PrintJobs` table |
| POS staff, PIN hashes, POS devices, POS settings | POS, POS tenant database | |
| POS audit records | POS, POS tenant database, append-only | |
| Shifts (later) | POS, POS tenant database | Not in the MVP |
| Orders, platform connections, Orders users, Orders print jobs | Orders, **Orders tenant database** | Unchanged; POS never reads them |
| Print Bridge device registry for Orders | Orders/platform, CentralDb | Unchanged |

**Why a second database per tenant, not POS tables in the Orders tenant database:** the Orders database is migrated by the Orders release procedure, which must finish before Orders starts. Sharing it would put POS schema changes into every Orders release, and Orders changes into every POS release, and would invite cross-product joins. A second database costs one more database per POS tenant, and one more backup and migration step per tenant. The POS database is created like an Orders tenant database: on the explicitly configured SQL Server, with a server-assigned name (`WaslaPos_{Label}_{12 hex}`), a database ownership marker as in WAS-93, and a refusal of reserved names.

**CentralDb is the one shared database, by design.** It is the platform registry both products need before they can open a tenant's database. Its schema has one history (today in `Wasla.Infrastructure`, applied by `migrate-central`). POS-related changes to it are additive (new table or nullable column), ship and migrate before the POS release that reads them, and never require an Orders code change. The platform library maps only the registry tables it reads or writes; it never migrates CentralDb.

**Migrations:** POS database migrations belong to `Wasla.Pos.Infrastructure` and are applied by `Wasla.Pos.Cli` (activate one tenant's POS database, migrate one, migrate all, status), never at startup or from middleware. Activation is an operator command, like Orders provisioning, never a request side effect, and it takes an application lock per tenant so two attempts cannot create or migrate the same database. The POS release procedure is POS's own: stop POS, migrate every POS database, start POS. It does not stop Orders.

**Shared contracts:** the platform library, the Print Bridge device protocol ([below](#print-bridge)), and, when needed, a read model for showing Orders and POS activity together. That read model would be an API or event contract, never a shared table.

## Identity and sign-in

The shared account is the **tenant**: one CentralDb registry row, one subscription, one slug. Users are not shared yet, because Orders users live in the Orders tenant database, and sharing them would mean POS reading that database or every POS request depending on Orders being up.

For the MVP:

- **Device pairing.** A POS device is paired to one tenant with a one-time pairing code. The first code is issued by `Wasla.Pos.Cli` at activation; later codes are issued by the POS Owner on a paired device. Pairing sets a long random device credential in a host-only, HttpOnly, Secure cookie. Its hash is stored in the POS database, and it can be revoked. The tenant comes from the resolved host; the device credential must belong to that tenant's POS database.
- **Staff sign-in.** POS staff have a name, a POS role ([roles](../product/pos-mvp.md#roles)) and a PIN of 4–6 digits (6 for Owner and Manager), stored hashed in the POS database. A PIN works only on a paired device of the same tenant. Failed attempts are rate limited per device and staff member and lock after repeated failures. Sessions are short and lock after inactivity, so another staff member can sign in with their own PIN.
- **Back office** (menu, tables, staff, devices) runs on a paired device after an Owner or Manager PIN. Remote back office in an ordinary browser is later and depends on the decision below.
- **POS authorization** uses its own policies over POS roles, enforced server side, and never Orders' `WaslaTenantPolicies` or `UserRole`.

**Consequence:** a restaurant owner has an Orders login and a POS Owner PIN. Moving tenant user identity out of the Orders tenant database into a platform identity store is a separate decision, listed in [open decisions](../product/pos-mvp.md#unresolved-product-decisions). It is triggered by selling Orders and POS together or by remote POS back office. The POS identity code sits behind POS Application interfaces so that a later platform identity replaces the account source, not the POS policies.

## Print Bridge

Options considered:

| Option | Why not, or why |
| --- | --- |
| Keep Print Bridge Orders-only; POS does not print | Rejected: kitchen tickets and receipts are must-haves |
| POS writes jobs into Orders' `PrintJobs` table | Rejected: shared mutable database; `OrderId` is a required foreign key to Orders' `Order` |
| Orders Web/Api serve POS jobs | Rejected: POS printing would depend on Orders' deployment and availability |
| Print Bridge formats each POS job kind itself (kitchen-ticket formatter, void-slip formatter, …) | Rejected: every POS document change would need a Print Bridge release on every restaurant PC |
| **POS serves its own jobs through a shared, versioned device protocol, with server-rendered documents** | **Chosen** |

Decision:

1. **Extract the device protocol** (health, pending jobs, `mark-printing`, `mark-printed`, `mark-failed`, the token header, status and skip semantics) into a small shared protocol library used by the Orders server, the POS server and Print Bridge. Orders' existing routes, `Receipt` jobs, client-side receipt formatting and behavior stay unchanged.
2. **Add one product-neutral job kind: a rendered document.** The server sends printable lines with simple emphasis (bold, double height, separator, alignment) and a target printer role (receipt or kitchen). Print Bridge prints it as it is, so POS kitchen tickets, void slips, pre-bills and receipts need no Print Bridge code per kind. Text shaping for Arabic and the receipt language are decided on the server.
3. **POS implements the protocol on its own host**, with its own job table in the POS database and the same rules: Pending → Printing → Printed/Failed, conditional transitions, a repeated report answered `skipped`, one connection per job. POS print devices are paired and revoked in POS, and their tokens are stored hashed in the POS database. They resolve the tenant from the POS host, so POS needs no CentralDb device table.
4. **Print Bridge gains a second connection.** One installation holds one connection per product (Orders, POS), each with its own `ServerUrl`, token and polling loop, and each job keeps the connection that claimed it (WAS-58). This is a Print Bridge release. It does not require an Orders Web or Api release, and POS deployment does not depend on Orders.
5. **Printer roles.** POS jobs target "receipt" or "kitchen". The restaurant maps each role to a Windows printer in Print Bridge, and the kitchen role may map to the receipt printer.

Not changed by this decision: Orders printing, the `X-PrintBridge-Token` contract, `ServerUrl` and token setup rules, polling defaults, the WebView2 app's message contract version (bumped only when the connection UI changes), and the existing `Wasla.PrintBridge.Core` reference to `Wasla.Application`. The extraction may remove that reference, but it is not required by this decision.

## Network loss and offline

The MVP is **online-only**: POS state is on the server, and the device holds only unsent draft lines ([network loss](../product/pos-mvp.md#network-loss)). That keeps the first slice small and keeps one source of truth for money.

The MVP still prepares for offline capture:

- Device-generated ids and idempotency keys on every write, so a queued write can be replayed safely later.
- Compare-and-set on check versions, so a replayed write after a conflict is refused instead of overwriting.
- Append-only audit and payment records, which merge without updates.

Offline sale capture (a local queue on the device, or a local POS edge service in the restaurant) is a later decision with its own ADR. Triggers: the pilot decision on [offline service](../product/pos-mvp.md#unresolved-product-decisions), or measured outages at pilot restaurants.

## Guardrails

- **Reference-direction tests** in both test projects read every `*.csproj`. They fail when a POS project references an Orders project, when an Orders project references a POS project, or when the platform or protocol library references either product.
- **No cross-product data access:** POS code has no connection string for an Orders tenant database, and no `TenantDbContext`, `AppUser`, `UserRole` or `WaslaTenantPolicies` type. Orders code has no `PosDbContext`.
- **Shared-library changes** run both CI jobs and are reviewed as platform changes.
- **CentralDb changes for POS** are additive and migrate before the POS release that reads them.
- POS issues use their own branches and worktrees and focused POS tests. An Orders-only change does not need the POS job, except for shared-library changes.

## Consequences

- Wasla Orders behavior is unchanged by this decision. The first code change touching Orders is the platform library extraction, which must leave every Orders test green with no behavior change.
- POS can be released, migrated and rolled back without stopping Orders. A Print Bridge release is needed before POS jobs can print.
- Each POS tenant has two tenant databases. Backups, restores and the Central Admin tenant overview have to cover both. The operations-center work for POS databases is follow-up, not part of the first slice.
- Owners have separate Orders and POS credentials until the identity decision is made.
- The POS server is unreachable when the restaurant's internet is down.
- Revisit this decision, and consider a separate repository, if POS gets a separate team, if the shared libraries stabilize enough to publish as packages, or if a common CI run becomes too slow for the team.

## Related documents

- [../product/pos-mvp.md](../product/pos-mvp.md)
- [../product/pos-first-slice.md](../product/pos-first-slice.md)
- [tenancy.md](tenancy.md), [authentication.md](authentication.md)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
- [../product/product-boundaries.md](../product/product-boundaries.md)
