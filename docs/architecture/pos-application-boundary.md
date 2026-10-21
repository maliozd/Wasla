# Decision: Wasla POS as a local-first installed product in this repository (WAS-34)

## Status and scope

Proposed in WAS-34 (2026-10). It becomes accepted when the pull request that adds it is merged. It replaces an earlier draft in the same pull request that put the POS on a cloud host and required internet for every sale.

It records how Wasla POS is installed at a restaurant, where its data lives and who is authoritative for it, how it synchronizes with the Wasla cloud, how it prints, how devices and staff are authenticated, how it is licensed while offline, and where its code lives relative to Wasla Orders.

**Nothing here is built.** No POS project, database, installer, table or route exists; this repository still implements only Wasla Orders ([../product/product-boundaries.md](../product/product-boundaries.md)). The product scope is in [../product/pos-mvp.md](../product/pos-mvp.md), and the build order in [../product/pos-first-slice.md](../product/pos-first-slice.md).

**"Offline" in this document means offline from the internet and the Wasla cloud.** The restaurant's local network and its POS Edge machine must still be running. A failed Edge machine is a separate availability problem, handled by backup and restore ([Failure behavior](#failure-behavior)).

## Context: what the current code gives POS

Facts from the code on `dev` at `40fe698`:

- **Every Orders server project depends on one Infrastructure assembly.** `Wasla.Web`, `Wasla.Api`, `Wasla.Worker` and `Wasla.Cli` reference `Wasla.Infrastructure`. It holds both `CentralDbContext` and `TenantDbContext`, their migrations, the provider clients, order and printing services, `TenantResolver` and `AesSecretManager`. Referencing it from POS would bring in all of Orders.
- **Tenant users live in the Orders tenant database.** `AppUser` (email, BCrypt hash, `UserRole`, security stamp) is a `TenantDbContext` entity. Web login checks the password against that database (`AuthValidationService`), and every request is revalidated against it ([authentication.md](authentication.md#tenant-session-revalidation)). CentralDb's `TenantMembership` is a plan and subscription record, not a user store. `Tenant.SubscriptionStatus` and `TenantMembership` are where subscription state is today. Billing with iyzico (WAS-145 to WAS-154) is not implemented.
- **Roles are Orders roles.** `UserRole` (Owner, Manager, Kitchen, Cashier, Viewer) and `WaslaTenantPolicies` describe platform-order work ([../product/roles-and-permissions.md](../product/roles-and-permissions.md)).
- **There is no catalog.** `TenantDbContext` has orders, order items and options copied from provider payloads, but no product, category, modifier or table entity.
- **Print jobs are Orders receipts.** `PrintJob.OrderId` is required and has a cascading foreign key to `Order`. `PrintJobType` has one value, `Receipt`, and `PrintBridgeJobService` and the receipt and history services filter on it. The server stores structured order JSON (`ReceiptPayloadBuilder`). Print Bridge formats it into plain text (`ReceiptFormatter`) and sends it to the Windows spooler as a RAW job (`WindowsReceiptPrinter`, `winspool`). `Wasla.PrintBridge.Core` references `Wasla.Application`.
- **Print Bridge is a per-user tray application with one server connection.** `Wasla.PrintBridge` is a WinForms `WinExe` (`TrayApplicationContext`) that runs in a signed-in Windows session. It holds one `ServerUrl` and one device token, and each polling cycle and job keeps one copy of that connection (WAS-58). A manually entered `ServerUrl` may be `http` or `https` (`PrintBridgeSettingsValidator`). Setup links are accepted only for allow-listed `https` production hosts or local development hosts (`PrintBridgeProtocolUri`). It ships as a portable ZIP built by `scripts/release/package-print-bridge.ps1`; there is no installer or updater yet (WAS-55). See [../integrations/print-bridge.md](../integrations/print-bridge.md).
- **Nothing in the repository runs as a Windows service.** There is no `UseWindowsService` host. The SQLite EF Core provider (`Microsoft.EntityFrameworkCore.Sqlite`) is referenced only by the two test projects. Production code uses SQL Server.
- **Releases stop Orders and migrate every Orders database first** ([../operations/deployment.md](../operations/deployment.md#release-order-migrate-every-database-first)). Tenant migrations run through `Wasla.Cli`. `Tenant.SchemaVersion` is a constant that nothing reads at runtime.
- **CI builds one solution.** `.github/workflows/ci.yml` builds `Wasla.sln` and runs every test project on every pull request, with no path filter.

## Decision summary

1. **Wasla POS is installed at the restaurant.** Each restaurant location runs one **POS Edge**. It is a Windows service hosting an ASP.NET Core application with the local POS database, the POS user interface, the local print queue and the cloud synchronizer.
2. **The operational POS database is local SQLite**, owned by POS Edge. Nothing else opens it.
3. **Cashier devices are Windows touch terminals running the Wasla POS Terminal**, a WebView2 desktop shell. It shows the user interface served by POS Edge, over the restaurant network or on the Edge machine itself, through HTTPS. The Terminal pins the installation's own certificate authority, so routine certificate renewal never needs re-pairing.
4. **The Wasla cloud is not in the sale path.** Opening checks, adding lines, kitchen tickets, payments, closing, printing, history and PIN sign-in on paired devices all run against POS Edge alone.
5. **POS Edge synchronizes with Wasla POS Cloud** over outbound HTTPS only. It uses a durable local outbox, per-installation sequence numbers, idempotent uploads and acknowledgements given after durable storage ([Synchronization](#synchronization)). After a restore, POS Cloud returns the synced facts the backup lacks in a signed recovery package ([Restore and recovery](#restore-and-recovery-from-the-cloud)).
6. **POS Edge prints locally.** It uses a printing library extracted from Print Bridge's proven Windows printing code, and it never needs the cloud or Wasla Orders. Print Bridge stays the Orders printing client ([Printing](#printing)).
7. **Code stays in this repository as separate applications**: POS Edge, POS Terminal, POS Cloud and their tools. None of them reference Orders projects, and Orders references none of them ([Repository](#repository-and-application-boundaries)).

## Installed product

### Restaurant topology

```text
                 Wasla cloud (outbound HTTPS only, not in the sale path)
                 └── Wasla POS Cloud: activation, entitlement, ingestion, commands, releases
                          ▲
                          │ outbound HTTPS 443 from the Edge machine
┌─────────────────────────┼───────────────── restaurant ──────────────────────┐
│  POS Edge machine (Windows 10/11 Pro, always on, wired, UPS recommended)    │
│   ├── Wasla POS Edge (Windows service, NT SERVICE\WaslaPosEdge)             │
│   │     ├── HTTPS on the LAN (pinned installation CA) and loopback          │
│   │     ├── SQLite database, outbox, backups (ProgramData\Wasla\Pos)        │
│   │     └── print queue → Windows spooler → receipt and kitchen printers    │
│   └── Wasla POS Terminal (optional: the Edge machine as a cashier station)  │
│                                                                             │
│  Windows touch terminals ── restaurant LAN ──► POS Edge (HTTPS)             │
│  Wasla Print Bridge (only if the restaurant also uses Wasla Orders)         │
└─────────────────────────────────────────────────────────────────────────────┘
```

- One POS Edge per restaurant location. A location is still one tenant ([tenancy.md](tenancy.md)). A second Edge for the same tenant, or failover between two Edges, is out of scope for the MVP.
- Printers are installed in Windows on the Edge machine for all users (USB, or network printers added by an administrator). Terminals never print directly.
- The Edge machine needs no inbound internet access. The restaurant router must not forward any port to it.

### Options considered

| Concern | Options | Chosen | Why |
| --- | --- | --- | --- |
| Local runtime | Windows service with ASP.NET Core; a desktop app that also serves the network; a container | **Windows service** hosting ASP.NET Core (`Microsoft.Extensions.Hosting.WindowsServices`) | Runs without a signed-in user, restarts on failure, starts at boot. It reuses the repository's ASP.NET Core, Razor and EF Core skills. Containers would need Docker on a restaurant PC |
| Local user interface | Plain browser; WebView2 desktop shell; WinUI native UI | **WebView2 shell (Wasla POS Terminal)** showing Razor pages served by Edge | One UI codebase in the repository's MVC/Razor, Bootstrap and vanilla JavaScript stack. The shell pins Edge's certificate, keeps the device credential out of browser storage, and runs full screen. The repository already ships a WebView2 shell with a navigation and security policy (Print Bridge). A plain browser would need a trusted LAN certificate on every device. WinUI would be a second UI stack |
| Terminals | Same machine only; LAN terminals | **Both**: the Edge machine may run a Terminal, and further Windows terminals connect over the LAN | Most restaurants have one or two Windows touch terminals. Tablets and phones need a separate certificate and shell decision ([Later](#later-decisions)) |
| Local database | SQLite; SQL Server Express; SQL Server LocalDB; PostgreSQL | **SQLite (WAL mode)** | No database server to install, patch or administer. One file to back up and restore. The installer stays small. POS Edge is the only process that writes, which suits SQLite's single writer. The EF Core provider is already used in this repository's tests. SQL Server Express adds a service, an administrator and a 10 GB limit. LocalDB is per user and unsuitable for a service |
| Local transport | Plain HTTP on the LAN; HTTPS with a public certificate; HTTPS with a per-installation certificate | **HTTPS with a per-installation private CA, pinned at pairing** | PINs and device credentials never cross the LAN in clear text. No public DNS or certificate authority is needed, and nothing depends on the internet |

### Local database

- One SQLite database file per installation, in `C:\ProgramData\Wasla\Pos\data`. Access is restricted to the service account and Administrators. It holds only that tenant's data.
- **Money is stored as integer kuruş (`long`)**, not `decimal`. EF Core stores `decimal` as text on SQLite, which breaks sums and ordering.
- Writes are serialized inside POS Edge. Concurrency between terminals uses version columns and compare-and-set (a check edited from two terminals: one wins). This is not a SQL Server lock. The Orders SQL Server concurrency tests do not prove it, so POS needs its own tests.
- Not encrypted at the file level in the MVP. No card data is stored, PIN hashes are peppered ([Staff](#staff-pins-and-recovery)), and BitLocker on the Edge machine is a documented recommendation.
- Migrations belong to POS Edge and run only as an explicit step (`migrate`) after a verified backup. The installer and updater run that step. The service never migrates at startup, and it refuses to start on a database whose schema version it does not support.

### Installation, update, backup and restore

- **Installer.** One signed, per-machine Windows installer with two features: *POS Edge* and *POS Terminal*. Both are .NET 8 self-contained, so no .NET runtime prerequisite is needed. The installer:
  - creates the service with a virtual service account;
  - generates the installation's private certificate authority and first server certificate ([Certificate lifecycle](#certificate-lifecycle)); repair and upgrade keep both;
  - adds a Windows Firewall rule that allows the Edge port only on the Private profile and the local subnet;
  - creates the data and backup folders with restricted ACLs.

  The Terminal needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes; the installer installs it where it is missing. The installer technology is chosen in its backlog issue.
- **Updates** are signed packages listed in a release manifest served by POS Cloud. POS Edge downloads and verifies a package while online. It never applies one by itself during service: an Owner or Manager applies it from the back office, or it is applied at a configured quiet hour with no request in flight. A separate updater process runs these steps:
  1. Stop the service.
  2. Back up the database.
  3. Install the package.
  4. Run `migrate`.
  5. Start the service and check its health.
- **Rollback.** If the health check fails, the updater reinstalls the previous package and restores the pre-update backup automatically. No sale can have been recorded at that point, because the new version never became healthy. Rolling back later, after sales on the new version, is a support procedure. It never happens automatically, because it would lose those sales.
- **Staged updates.** POS Cloud supports the current and previous sync protocol versions ([Versioning](#versioning)), so installations can update over days.
- **Backups.**
  - SQLite online backups (`VACUUM INTO`) run every 15 minutes to a local backup folder, and one daily backup is kept for 14 days. A second backup path (another disk or a USB drive) is configurable and recommended.
  - Each backup is checked with `PRAGMA integrity_check` before it replaces an older one. Backup failures are shown in the sync health and the back office.
  - Encrypted whole-database backups uploaded to the cloud are later work. Synced facts are already recoverable from POS Cloud ([Restore and recovery](#restore-and-recovery-from-the-cloud)).
- **Restore** is an explicit operation on a stopped service; the back office cannot start one. It always ends with a recovery from POS Cloud, which brings back the facts synced after the backup ([Restore and recovery](#restore-and-recovery-from-the-cloud)). A restore and recovery drill is part of the slice and of pilot readiness.

### Diagnostics

- POS Edge and POS Terminal write rolling log files (Serilog file sink, as Print Bridge does) under `C:\ProgramData\Wasla\Pos\logs`. Logs never contain PINs, device or installation credentials, activation or emergency codes, or card data. Like Orders, they hold identifiers but no customer content.
- **Support bundle.** An Owner or Manager can create a ZIP from the back office or from the loopback-only console. It contains the last 7 days of logs, versions, sync health, configuration without secrets, the last backup's status and the result of a database integrity check. It contains no database contents. It is saved locally and uploaded to POS Cloud only when the Owner chooses to, while online.
- Sync health is also sent to POS Cloud with each successful contact, so support can see an installation's last checkpoint and last contact without the bundle.

## Activation and installation identity

1. **Tenant activation (cloud, operator).** `Wasla.Pos.Cli activate --tenant <slug>` checks that the tenant is active, then:
   - creates the tenant's POS cloud database (SQL Server, created the same way as Orders tenant databases: explicit server, server-assigned name, ownership marker, reserved-name refusal, application lock; WAS-93);
   - records it in CentralDb;
   - issues a one-time **installation activation code**, valid for 24 hours.
2. **Installation activation (Edge, online once).** On first start, POS Edge asks on its loopback-only console for the activation code. It sends the code to POS Cloud over HTTPS. POS Cloud verifies the code against that tenant's POS cloud database and returns:
   - a new **installation id**;
   - a random **installation credential** (256 bit; POS Cloud stores only its SHA-256 hash, as the Print Bridge device token is stored);
   - the first **entitlement lease**;
   - a one-time **Owner setup code**.

   The credential is stored encrypted with DPAPI for the machine and readable only by the service account.
3. **One active installation per tenant.** A replacement machine, or a restore on the same machine, does not activate a second installation. It runs a restore operation with a one-time *restore code*. That operation revokes the current credential, keeps the installation id, starts a new **epoch** and recovers the synced facts ([Restore and recovery](#restore-and-recovery-from-the-cloud)).
4. **Sync authentication.** Every request to POS Cloud carries the tenant id, the installation id and the installation credential. POS Cloud opens that tenant's POS cloud database through the CentralDb registry and compares hashes. A wrong tenant, installation or credential is answered 401 with no detail, and nothing else of that tenant is read. The credential can be rotated by a cloud command.

POS Cloud never writes CentralDb at runtime. Only `Wasla.Pos.Cli`, an operator tool, writes the POS registration row, through the platform library.

## Devices and the local network

- **Listening.** POS Edge listens with HTTPS on the configured LAN interface and on loopback, on one configurable port. It has no plain-HTTP listener on the LAN. A second, loopback-only listener serves the setup console (activation, first Owner PIN, restore status). It refuses any non-loopback request.
- **Certificates.** Each installation has its own private certificate authority (the *installation CA*). It issues POS Edge's short-lived server certificates, and Terminals pin the CA's public key, not a server certificate ([Certificate lifecycle](#certificate-lifecycle)).
- **Pairing a terminal.**
  1. On an already signed-in terminal, or on the Edge console, an Owner or Manager creates a **pairing code**. It is single-use, valid 10 minutes, and includes the first characters of the installation CA's public-key fingerprint.
  2. On the new terminal, the POS Terminal asks for the Edge address and the code. It connects, checks that the server certificate chains to a CA whose key matches the fingerprint in the code, and only then sends the code. A mismatch stops pairing.
  3. POS Edge returns a random **device credential** (stored hashed in the Edge database), the installation id and the CA's full public key. The Terminal pins that key and stores the credential with DPAPI.

  There is no anonymous pairing endpoint: an exchange without a valid code is refused and rate limited.
- **Requests.** Every request except the pairing exchange and an anonymous health probe must present a valid, unrevoked device credential. The health probe shows no tenant data. Staff actions also need a PIN session bound to that device. Device revocation in the back office takes effect immediately.
- **Firewall.** The installer's rule admits the port only from the local subnet on the Private network profile. The documentation tells installers not to expose the Edge machine to the internet and not to put it on a guest Wi-Fi network.

### Certificate lifecycle

Terminals trust a per-installation key that changes rarely and only through a signed handover. They do not trust a server certificate that expires. No public certificate authority and no internet access are involved.

| Item | Lifetime | Where | Notes |
| --- | --- | --- | --- |
| **Installation CA** | 10 years | Generated by the installer (ECDSA P-256, `CA:true`, path length 0). Its private key is protected with DPAPI on the Edge machine and readable only by the service account | Signs only this installation's server certificates and CA rollovers. It is never in database backups, and never leaves the machine |
| **Server certificate** | 90 days | Issued by the installation CA. Subject names: the machine name, `localhost`, `127.0.0.1`, `::1`, and the URI `urn:wasla:pos:installation:<installationId>` | Renewed by POS Edge itself. Server authentication only |

**How a Terminal validates POS Edge.** It accepts a server certificate only when all of the following hold:
- it chains to a pinned installation CA key;
- it is within its validity period;
- it has the server-authentication usage;
- it carries the `urn:wasla:pos:installation:` URI of the paired installation.

The address itself is not part of the check: the Terminal connects to whatever address it has stored (an IP address or a host name). The pinned key never signs anything for anyone else, so a matching name would add no security. A changed IP address or machine name therefore needs **no new certificate** and no re-pairing; the Owner corrects the address in the Terminal's settings. A DHCP reservation for the Edge machine is recommended. Anything that fails these checks is refused with "This is not your POS Edge", and is never offered as a click-through. The WebView2 shell and the Terminal's own HTTP client use the same check.

**Server certificate renewal (routine, no Terminal involvement).**
- 30 days before expiry, POS Edge issues the next server certificate from the installation CA.
- It tests the new certificate with a loopback TLS handshake and only then serves it.
- The previous certificate stays on disk until it expires, and it is still valid during that time. That is the overlap: a Terminal accepts either certificate, because both chain to the pinned CA.
- If issuing or the self-test fails, POS Edge keeps serving the current certificate and retries daily. Sync health shows a warning from 14 days before expiry.
- Rollback is going back to the previous, still-valid certificate.
- A corrupted or deleted server certificate is reissued from the CA at startup.
- Updater repairs and upgrades keep the CA and the certificates, because they live in `C:\ProgramData\Wasla\Pos`, not in the program folder.

**Installation CA rollover (rare, signed by the old key).**
1. POS Edge creates a new CA 12 months before the current CA expires, or when an Owner requests a rollover.
2. It publishes a **rollover statement**, signed by the current CA key. The statement carries the new CA's public key, the installation id and the validity period.
3. A Terminal fetches the statement over a connection authenticated with the current pin. It verifies the old-key signature, then pins **both** keys.
4. POS Edge switches to server certificates from the new CA once every active paired Terminal has acknowledged the new pin, or 30 days before the old CA expires, whichever comes first. A Terminal that missed the overlap must be re-paired.
5. The old pin is dropped when the old CA expires.

A new key is never trusted without the old key's signature. If the rollover fails before the switch, POS Edge keeps using the old CA and retries.

**Lost or compromised keys.**
- The installation CA key and the server certificate keys sit on the same machine under the same protection, so a suspected compromise of either is treated as a compromise of the installation CA.
- The Owner, or support with a cloud command, marks the CA **compromised**. POS Edge then creates a new CA, revokes every device credential, and every Terminal must be re-paired with a new pairing code.
- A lost CA key has the same outcome: on a replacement machine, or after the key is corrupted, there is no old key to sign a rollover.
- **Restoring the database on the same machine keeps the CA**, so no re-pairing is needed. **A replacement machine needs re-pairing.**

## Staff, PINs and recovery

POS staff identity stays POS-owned (Orders users live in the Orders tenant database; see [Context](#context-what-the-current-code-gives-pos)). Its authority is now the local installation.

- **Staff records** (name, POS role, active, PIN hash) are created and changed in the local back office and work offline. They are uploaded to POS Cloud for reporting and audit attribution **without the PIN hash**.
- **PIN.** 4–6 digits (6 for Owner and Manager). It is hashed with BCrypt over the PIN combined with a per-installation secret pepper held with DPAPI. A stolen database file alone is therefore not enough to test PINs offline. PINs are only valid on a paired device of this installation.
- **Lockout.** Failures are rate limited per device and staff member. Five failures lock that staff member on that device for 5 minutes, then for longer periods. Every failure and lock is audited, without the PIN.
- **Sessions** are short and bound to the device. They lock after a configurable inactivity period, so another staff member signs in with their own PIN.
- **First Owner.** The Owner setup code from activation sets the first Owner PIN on the loopback-only console.
- **Owner recovery** without a permanent bypass:
  - *Offline:* activation also issues a printed one-time **recovery code**, stored hashed. Using it lets one new Owner PIN be set, is audited, and issues a new recovery code.
  - *Online:* support or the tenant's Orders Owner can request a **recovery token** from POS Cloud. It is signed, bound to this installation id, single-use and valid 24 hours, and it is audited both locally and in the cloud.
  - There is no master PIN, no default password and no support backdoor.
- **Revocation from the cloud.** Support can revoke a device or a staff member. The revocation reaches POS Edge as a cloud command on its next contact. Until then the local state applies. A revocation is never undone by a later local change; it has to be re-granted locally.

## Licensing while offline

- POS Edge holds an **entitlement lease**. It is signed by POS Cloud with a Wasla POS signing key, and POS Edge verifies it with the public key built into each release. It states the tenant, the installation, the entitlement status and `validUntil`. Every successful sync refreshes it.

**Lease length is a provisional design assumption of 14 days.** The final period is an [open product decision](../product/pos-mvp.md#unresolved-product-decisions). POS Cloud sets `validUntil` in each lease and POS Edge only honors it, so changing the period needs no Edge release. With 14 days, a two-week internet or Wasla cloud outage changes nothing for the restaurant.
- **While the lease is valid:** full operation. In its last 3 days, Owners and Managers see a warning to connect to the internet.
- **After the lease expires, or after POS Cloud reports an expired, cancelled or revoked entitlement on reconnection:** *restricted mode* starts at the next business day.
  - Restricted mode refuses **new** checks. Nothing else.
  - **An expired lease never blocks completing, printing or reviewing a sale that is already open.** Restricted mode still allows:
    - adding lines to an open check and sending them to the kitchen;
    - paying and closing open checks;
    - printing and reprinting;
    - voids on open checks;
    - history;
    - backups, support bundles and synchronization.
  - The restaurant is never stopped in the middle of a service, and never loses access to its own sale records.
- **Emergency extension.** Support can issue a one-time code. It is signed, bound to the installation id, extends the lease by at most 7 days, and can be used once (its id is recorded locally). It is entered by an Owner, works offline and is audited. It cannot be reused or chained beyond what support issues.
- **Clock tampering.** POS Edge persists the highest time it has seen and evaluates the lease against the later of that and the current clock. Moving the clock back therefore does not extend a lease. Large clock changes are audited.

## Data ownership

Authority means who may change the data. Every type has exactly one authority, so no conflict is resolved by "last write wins".

| Data | Authority | Stored | Sync direction |
| --- | --- | --- | --- |
| Tenant identity, subscription and POS entitlement | Cloud (CentralDb; billing in WAS-147) | Cloud; local copy as the signed lease | Cloud → local (lease) |
| POS activation registration | Cloud (`Wasla.Pos.Cli`) | CentralDb | None |
| Installation identity (id, epoch, credential hash) | Cloud issues it; local holds the credential | Both | Issued at activation |
| POS devices (terminals) and device credentials | Local | Local (hashes); cloud gets metadata without credentials | Local → cloud; cloud → local revocation commands |
| POS staff and roles | Local | Local; cloud copy without PIN hash | Local → cloud; cloud → local revocation commands |
| PIN hashes, pepper, recovery code hash | Local | Local and its backups only | Never leaves the installation |
| Installation CA key and server certificate keys | Local | Edge machine only, protected with DPAPI; not in database backups | Never leaves the machine |
| Menu, categories, products, modifiers, prices | Local (MVP) | Local; cloud copy as versioned snapshots | Local → cloud |
| Areas and tables | Local | Local; cloud copy | Local → cloud |
| Open checks and their lines | Local | Local only | Not uploaded while open |
| Closed or cancelled checks with lines and voids | Local | Local; immutable cloud copy | Local → cloud once, at close or cancel |
| Kitchen tickets and void slips | Local | Local; immutable cloud copy | Local → cloud |
| Payment records and reversals | Local | Local; immutable cloud copy | Local → cloud; never edited anywhere |
| Receipts (number, rendered snapshot, reprints) | Local | Local; cloud copy | Local → cloud |
| Audit records | Local | Local, append-only; cloud copy | Local → cloud |
| POS settings (receipt header, business-day cutoff, inactivity lock) | Local | Local; cloud copy | Local → cloud |
| Printer role mapping (Windows printer names) | Local | Local only | Not synced (machine-specific); sent only in support bundles |
| Entitlement lease, revocations, emergency extensions, credential rotation, release manifest | Cloud | Cloud; applied locally | Cloud → local commands |
| Sync health and checkpoints | Each side for its own state | Both | Exchanged on every contact |
| Cloud reporting and read models | Cloud, derived | Cloud | Never flows back |
| Wasla Orders data | Orders | Orders tenant database | None; POS never reads it |

Rules:

- The cloud never creates, edits or deletes a check, line, payment, reversal, kitchen ticket, receipt or audit record. It stores what the installation sends. It returns those records to the installation only in a signed recovery package, during an explicit restore operation ([Restore and recovery](#restore-and-recovery-from-the-cloud)), never during normal selling.
- Active checks exist only at the installation. Nothing in the cloud can change them.
- Cloud-authored data is limited to commands that only restrict or extend (revoke, lease, rotate, extend). They are applied idempotently in their own sequence, and a revocation is never reversed by sync.
- Editing the menu or configuration in the cloud is later work. When it comes, each configuration aggregate gets exactly one writer at a time and version-checked changes. A stale change is refused, never merged silently.

## Synchronization

The minimum protocol for the first product. It is not a general event platform.

**Local outbox.** Every business write that produces a synced fact appends an outbox record **in the same SQLite transaction**. Each record has:

| Field | Meaning |
| --- | --- |
| `recordId` | Globally unique id (UUID v7), also the cloud idempotency key |
| `tenantId`, `installationId`, `epoch` | Who sent it; the epoch increases on every restore or replacement |
| `sequence` | Gapless, strictly increasing per installation and epoch, assigned in the transaction |
| `type`, `schemaVersion` | For example `CheckClosed` v1, `PaymentRecorded` v1, `AuditRecorded` v1 |
| `entityId` | The check, payment, ticket or configuration aggregate it describes |
| `occurredAtUtc`, `businessDay` | From the local clock and the local business-day rule, informational |
| `payload`, `payloadHash` | The immutable fact; SHA-256 of its canonical JSON |

**Upload.**

1. When online, the synchronizer sends batches of at most 200 records or 1 MB, starting at `cloudCheckpoint + 1` for the current epoch: `POST /pos-sync/v1/batches`, authenticated with the installation credential.
2. POS Cloud stores each record in the tenant's POS cloud database, with unique keys on `recordId` and on `(installationId, epoch, sequence)`, in order and in one transaction per batch. It then answers with the highest contiguous sequence durably stored. **Only that answer moves the local checkpoint.**
3. **Partial acknowledgement:** if record *n* fails validation, records before *n* are stored and acknowledged, and *n* is returned with an error code.
4. **Retry:** exponential backoff with jitter, from 5 seconds up to 5 minutes, honoring `Retry-After`, with an immediate retry when connectivity returns.
5. **Crash replay:** records are kept until acknowledged. A record resent after a crash (or an acknowledgement lost in transit) is acknowledged again without being stored twice.

**Duplicates and conflicts.**

| Situation | Cloud behavior | Local behavior |
| --- | --- | --- |
| Same `recordId`, same sequence, same hash | Acknowledged again; stored once | Checkpoint moves |
| Same `recordId` or sequence, different hash | Refused as an integrity conflict; nothing stored | Sync **blocked** and shown as needing support. Sales continue |
| A record the cloud cannot accept (unknown type, invalid payload) | Refused with a reason | Sync blocked at that record. It is never skipped, because skipping would leave a gap in financial history |
| Sequence gap (first record not `checkpoint + 1`) | Refused, with the expected sequence | Resends from the expected sequence |
| Upload from a revoked installation or an old epoch | 401 or 409 with the reason | Sync stops and shows the reason; restricted mode follows the lease rules |

**Restore and epochs.** A restore always starts a new epoch, so new records never reuse an old `(epoch, sequence)`. The facts synced after the restored backup are brought back by the recovery package, not by normal sync ([Restore and recovery](#restore-and-recovery-from-the-cloud)).

**Cloud → local commands.** POS Edge reads `GET /pos-sync/v1/commands?after=<n>` on every contact. It applies each command idempotently, in order, records the applied number, and reports it back. Commands: lease refresh, installation credential rotation, device or staff revocation, emergency extension metadata, release availability.

### Versioning

- Each batch carries `protocolVersion` (an integer). Each record carries its type's `schemaVersion`.
- POS Cloud accepts the current and the previous protocol version. Cloud releases ship first; installations update afterwards, in stages.
- An installation with an unsupported version gets `protocol_unsupported`. It keeps queueing locally, shows "update needed to synchronize", and keeps selling.
- New optional fields are ignored by older readers. A change that older readers cannot ignore is a new `schemaVersion` or protocol version, never a silent reinterpretation.

### Retention and clocks

- Acknowledged outbox records are pruned 30 days after acknowledgement. Unacknowledged records are never pruned.
- Business records (checks, payments, tickets, audit) stay in the local database for at least 13 months. Pruning older records is a later, explicit feature.
- Low disk space is a warning in sync health, never a silent deletion.
- Ordering and authority never depend on timestamps; they use `(epoch, sequence)`. POS Cloud records its own receive time and the reported skew. Skew over 5 minutes is shown in sync health. The business day is decided locally when the fact happens and is never recalculated by the cloud.

### Sync health

POS Edge keeps, shows Owners and Managers in the back office, shows all staff as a status chip, and sends to POS Cloud:
- the last successful upload;
- the last cloud contact;
- pending record count and oldest pending age;
- blocked state and reason;
- protocol status;
- lease expiry;
- clock skew;
- the last backup result;
- the server certificate and installation CA expiry, and whether a CA rollover is in progress;
- the current epoch, and the last restore operation and its result.

POS Cloud keeps each installation's checkpoint, epoch, last contact and blocked reason. Support reads them with `Wasla.Pos.Cli installation-status`. A Central Admin view is later work.

## Printing

Options considered:

| Option | Assessment |
| --- | --- |
| POS Cloud serves jobs; Print Bridge gets a second remote connection (earlier draft) | Rejected: printing would stop without internet |
| Reuse the Print Bridge application as the local transport, polling POS Edge over loopback | Possible, but it brings in Print Bridge's limits. It runs only in a signed-in Windows session as a tray app. It holds one connection by design (WAS-58), so it would need a multi-connection redesign. A restaurant would need it even when it does not use Wasla Orders |
| A shared local print protocol between POS Edge and Print Bridge | The same costs as above, plus a protocol to version between two local processes |
| **POS Edge prints itself, through a printing library extracted from Print Bridge** | **Chosen** |

Decision:

1. **Extract the product-neutral printing code from `Wasla.PrintBridge.Core`** into a shared Windows library:
   - the `winspool` RAW submission (`WindowsReceiptPrinter`);
   - printer enumeration and health (`WindowsPrinterHealth`);
   - plain-text line layout.

   The library references no Orders and no POS project. Print Bridge then uses it with **no behavior change**: same receipts, same job flow, same tests.
2. **POS Edge keeps its print jobs in its local database**, with the Print Bridge state rules:
   - Pending → Printing → Printed or Failed, with conditional transitions.
   - "Printed" means accepted by the Windows spooler, not paper out.
   - A job found in Printing after a crash or restart is **never reprinted automatically**. It is shown as "print uncertain" with a manual reprint, which prints a copy marked as a reprint.
   - A failed job is shown on the terminals that created it and in the back office, with a manual retry.
   - A print failure never undoes a kitchen send or a payment.
3. **POS renders its documents itself:** kitchen ticket, void slip, pre-bill and receipt. It renders them to printable lines in the receipt language, with Arabic handled on the Edge. POS document changes ship with POS Edge, never with Print Bridge.
4. **Printer roles are local.** The back office maps "receipt" and "kitchen" to Windows printers installed on the Edge machine for all users. The kitchen role may map to the receipt printer.
5. **Orders and POS printing stay isolated.** Orders jobs stay in the Orders tenant database and are printed by Print Bridge. POS jobs stay in the Edge database and are printed by POS Edge. They may share a physical printer: the Windows spooler queues jobs from both. Neither product reads the other's jobs.

Consequence: printing from a Windows service sees only printers installed for all users on the Edge machine. Per-user printers and printers attached only to a terminal are not supported in the MVP.

## Restore and recovery from the cloud

A backup alone does not restore a restaurant's history. Suppose the newest backup is from 14:00, sales from 14:00 to 14:12 were synced, and the disk fails at 14:13. Restoring the 14:00 backup misses those sales, although POS Cloud holds them. A restore therefore always ends with a **cloud recovery**: POS Cloud returns, as a signed package, the acknowledged immutable facts that are newer than the restored backup. The recovery is part of the restore, never part of selling.

### What can be recovered

| Category | Example | Outcome |
| --- | --- | --- |
| **Synced before the failure** (acknowledged by POS Cloud) | The 14:00–14:12 closed checks, payments, reversals, kitchen tickets, receipts and audit records, and the latest menu, table, staff and settings snapshots | **Restored** from the recovery package with their original ids. Local history and day totals are complete up to the last acknowledged record |
| **Committed but not synced, present in a surviving backup** | A backup on the second backup path holds sales that never reached the cloud | **Restored** from that backup, then uploaded under their original `(epoch, sequence)` |
| **Committed but not synced, in no surviving backup** | A sale closed at 14:12:50, after the last backup and before its upload | **Lost.** The restore report names the last known record and its time, so the restaurant can reconcile from paper receipts |
| **Open checks** | A table still open at 14:13 | Restored only as far as the restored backup holds it. Open checks are never uploaded |
| **Unsent Terminal drafts** | Lines on a Terminal screen that were never sent | **May be unrecoverable.** They exist only on that Terminal |
| **PIN hashes, device credentials, the installation CA key** | — | Never in the cloud. They come back only from the backup (PINs and device credentials) or not at all (the CA key on a replacement machine). See below |

### The restore operation

1. **Restore code (operator, cloud).** `Wasla.Pos.Cli restore-code --tenant <slug> --installation <id>` first **revokes the installation's current credential and closes its current epoch**. From then on, POS Cloud refuses normal uploads from that credential, so nothing new can arrive for the old epoch while the package is built. It then issues a one-time restore code, valid for 24 hours and bound to the tenant and the installation.
2. **Restore locally (Edge, service stopped).** On the same or a replacement machine:
   - restore the chosen backup with `restore --backup <file>`; or
   - with `restore --cloud-only`, when no usable backup exists.

   POS Edge enters the **Recovering** state. Its sale endpoints refuse every request, and Terminals show "POS Edge is being restored".
3. **Handshake (Edge → Cloud, outbound HTTPS).** On the loopback console, POS Edge sends:
   - the restore code;
   - a new restore-operation id;
   - the restored position `B`, the highest `(epoch, sequence)` in the restored database;
   - the `(recordId, epoch, sequence, payloadHash)` of every restored outbox record that the backup had not yet seen acknowledged.
4. **Comparison (Cloud).** Both the backup and the cloud hold a **prefix of the same ordered history**: the cloud only acknowledges contiguous sequences, and a backup is a past state. One of them is therefore ahead of the other, and records they share must have equal hashes. POS Cloud checks the shared records, then answers with:
   - a new installation credential and a new epoch;
   - the **recovery package**: every acknowledged record of this installation after `B`, in `(epoch, sequence)` order (empty when the backup is ahead);
   - the list of backup records the cloud lacks (empty when the cloud is ahead);
   - for a replacement machine or a cloud-only restore, a new Owner setup code.
5. **Package integrity.** The package is signed with the POS Cloud signing key, the same key that signs leases. It is bound to:
   - the tenant, installation and restore-operation id;
   - the new epoch;
   - `B` and the cloud's last acknowledged position;
   - the record count, and a SHA-256 over the ordered record hashes.

   POS Edge verifies the signature, the binding and each record's `payloadHash` before writing anything. Asking again with the same operation id returns the same package. A new operation needs a new restore code.
6. **Apply (Edge, one SQLite transaction).**
   - Each recovered fact is inserted into the business tables under its original id: closed and cancelled checks with lines and voids, payments, reversals, kitchen tickets, receipts, audit records, and configuration snapshots (menu, tables, staff without PIN hashes, settings).
   - A record that is already present with the same hash is skipped, so applying twice changes nothing.
   - Each recovered record is also written to the outbox history as **Recovered**, with its original `(epoch, sequence)`. It counts as acknowledged and is **never uploaded again**, and it is never treated as a new sale.
   - The local cloud checkpoint is set to the cloud's last acknowledged position.
7. **Upload the backup's unsynced records**, if the backup was ahead. They are sent under their original `(epoch, sequence)` with the restore-operation id. POS Cloud accepts them for the closed epoch only from this operation, and only as the contiguous continuation of what it holds.
8. **Resume.**
   - POS Edge switches to the new epoch, where sequences start at 1.
   - It recalculates its counters ([numbering](#numbering-after-a-restore)).
   - It writes a restore audit record: operation id, `B`, records recovered, records uploaded, and the last known record.
   - It leaves the Recovering state. Selling resumes.

### Mismatched records

If the backup and the cloud hold the same `recordId` or the same `(epoch, sequence)` with different hashes, they are not two versions of one history. That points to corruption or tampering. Then:
- POS Edge **writes nothing** and stays in the Recovering state.
- POS Cloud records the operation as a conflict, with both hashes.
- Nothing is merged and neither side is chosen automatically.

Support then chooses one of two paths, and the choice is audited in both places:
- **Another backup.** Try an older or a different backup.
- **Cloud-only recovery.** POS Cloud's acknowledged records stand as the financial record of what was synced. The backup's unsynced records are then lost and are listed in the restore report.

### Numbering after a restore

- Check, takeaway and receipt numbers are counters per business day.
- After recovery, each counter continues from the highest number in the restored and recovered records for that day.
- Numbers issued after the newest backup and never synced may already be on printed paper. So from epoch 2 onwards, numbers are printed with the epoch as a prefix (for example `2-0046`). A number can then never repeat one from an earlier epoch.
- Epoch 1 numbers have no prefix.

### Credentials, Terminals and PINs after a restore

- **Old credential.** Revoked before the package is issued (step 1), so a failed machine that comes back cannot upload any more.
- **Same-machine restore.** The installation CA stays. Device credentials and PIN hashes come back from the backup, and Terminals keep working once the backup's device records are back.
- **Replacement machine.** A new installation CA, so every Terminal is re-paired. PIN hashes come back from the backup.
- **Cloud-only restore.** Staff come back without PINs:
  - the new Owner setup code sets the Owner PIN;
  - the Owner then resets the other staff PINs;
  - Terminals are re-paired.

### What recovery needs and never does

- **Internet once.** Recovery needs the internet for the handshake. POS Edge does not sell from a restored backup before it completes, because selling on a stale history would reissue numbers and understate totals. During an internet outage the restaurant uses its paper or fiscal-register fallback until the handshake completes; a mobile hotspot is enough, because only outbound HTTPS is used.
- **Never during selling.** The recovery endpoint accepts only a valid restore code. Normal sync and cloud commands never carry sale facts to an installation, and nothing in recovery can change an open check.

## Failure behavior

| Failure | Effect | Recovery |
| --- | --- | --- |
| Internet or Wasla cloud down | None for sales, printing, history or sign-in. Sync health shows "offline since". The outbox grows | Automatic upload on reconnection. The lease covers the lease period (provisionally 14 days) |
| A terminal loses the LAN | That terminal cannot send, pay or close. Its unsent draft lines stay on screen. Other terminals continue | It reconnects by itself. Repeated requests carry the same idempotency keys |
| POS Edge service crashes | Terminals show "POS Edge unavailable" | Windows restarts the service. Committed work and the outbox are in SQLite; draft lines on terminals survive |
| Edge machine fails (disk, power supply, theft) | **The restaurant cannot use POS** until it is replaced. Terminals depend on it | Install on a replacement machine, restore the newest surviving backup, run the restore operation with cloud recovery, and re-pair the Terminals. **Synced facts are restored into local history**, not merely visible to support. Only committed-but-unsynced records missing from every surviving backup, open-check changes after the backup, and unsent Terminal drafts can be lost ([Restore and recovery](#restore-and-recovery-from-the-cloud)). Target: within 2 hours with a spare machine. Meanwhile the restaurant follows its paper and fiscal-register fallback |
| Database corruption | POS Edge refuses to start on a failed integrity check | Restore operation on the same machine from the newest backup that passes its integrity check, with cloud recovery. No re-pairing |
| Server certificate expired, corrupted or lost | Terminals refuse an invalid certificate | POS Edge reissues it from the installation CA. No re-pairing ([Certificate lifecycle](#certificate-lifecycle)) |
| Installation CA key lost or suspected compromised | Terminals cannot verify POS Edge | New CA, device credentials revoked, all Terminals re-paired |
| Printer offline or out of paper | The job fails, or the spooler holds it | Manual retry or reprint, as above |

A secondary or failover Edge, and replication between Edges, are out of scope for the MVP.

## Repository and application boundaries

"Separate application" and "separate repository" are two decisions.

**Separate applications (yes).** Each is built, versioned and released on its own:

| Application | What | Deployed to |
| --- | --- | --- |
| `Wasla.Pos.Edge` | Windows service: ASP.NET Core, Razor UI, SQLite, outbox, synchronizer, print queue | Restaurant Edge machine (installer) |
| `Wasla.Pos.Terminal` | WebView2 desktop shell | Restaurant terminals (installer) |
| `Wasla.Pos.Cloud` | ASP.NET Core: activation, entitlement leases, ingestion, commands, release manifest | Wasla servers, its own process and host (name set with WAS-66) |
| `Wasla.Pos.Cli` | Operator tool: tenant activation, POS cloud database migrations, installation status | Operators |
| Updater | Applies Edge and Terminal updates with backup and rollback | Restaurant Edge machine |

Shared libraries: `Wasla.Pos.Domain` and `Wasla.Pos.Application` (POS rules, used by Edge), `Wasla.Pos.Sync.Contracts` (the versioned sync protocol, used by Edge and Cloud), the printing library (used by POS Edge and Print Bridge), and the platform library (secrets and the CentralDb registry reader, used by POS Cloud, `Wasla.Pos.Cli` and Orders).

**Same repository (yes, for now).** Evaluated again with the installed product in mind:

| Concern | Effect on the repository decision |
| --- | --- |
| Local installer and updater | Needs its own build and signing pipeline whatever the repository. It does not favor a split |
| Separate release cadence | Met with separate versions, tags (`pos-edge-v…`, `pos-cloud-v…`) and release workflows in one repository |
| Shared cloud contracts | The sync protocol is shared by Edge and Cloud, and the printing and platform libraries by POS and Orders. One repository changes a contract and every user of it in one pull request. Separate repositories would need a package feed and version pinning |
| Installer size | Depends on the self-contained publish (expected below 150 MB), not on the repository |
| CI duration | POS gets its own solution (`Wasla.Pos.sln`) and Windows CI job with path filters. Orders-only changes do not wait for it, except changes to shared libraries, which run both |
| Operational ownership | POS releases, installers and installation support have their own owner and runbooks under `docs/` |
| A future dedicated POS team | The trigger to move POS to its own repository. Shared contracts would first be published as versioned packages |

Guardrails:

- **Reference-direction tests** in `Wasla.UnitTests` and the POS test project read every `*.csproj`. They fail when:
  - a POS project references an Orders project (`Wasla.Domain`, `Wasla.Application`, `Wasla.Infrastructure`, `Wasla.Contracts`, `Wasla.Web`, `Wasla.Api`, `Wasla.Worker`, `Wasla.Cli`, `Wasla.PrintBridge*`);
  - an Orders project references a POS project;
  - a shared library references either product.
- POS code has no `TenantDbContext`, `AppUser`, `UserRole` or `WaslaTenantPolicies`. Orders code has no POS type.
- CentralDb changes for POS are additive and migrate before the POS Cloud release that reads them.
- POS issues use their own branches and worktrees and focused POS tests.

### Stack

- .NET 8.
- ASP.NET Core MVC/Razor, Bootstrap 5 and vanilla JavaScript for the POS UI ([../frontend/architecture.md](../frontend/architecture.md)).
- EF Core with SQLite at the Edge, and SQL Server for POS cloud databases.
- WebView2 for the Terminal.
- The repository's architecture boundary rules inside the POS projects ([10-architecture-boundaries.mdc](../../.cursor/rules/10-architecture-boundaries.mdc)), and its localization rules and cultures.

## Later decisions

- Tablets and phones (Android, iPadOS) as waiter devices: they need their own shell, or a trusted LAN certificate scheme.
- Cloud menu and configuration editing, with the single-writer rule above.
- Encrypted cloud backups of the whole Edge database. They would also restore open checks, and PIN hashes under the restaurant's own key.
- Restoring the installation CA on a replacement machine, which would avoid re-pairing. It needs a key escrow design.
- A secondary Edge, or failover.
- One Wasla login for Orders and POS, which needs identity to move out of the Orders tenant database.
- Central Admin views of POS installations.

## Consequences

- **Selling works without internet or the Wasla cloud**, for at least the lease period.
- **The Edge machine is the single point of failure in each restaurant.** Its backups, a replacement procedure and a paper fallback are part of every installation.
- **A restore needs one short online handshake** before selling resumes. In exchange, history and totals are complete up to the last synced record.
- **Terminals pin a per-installation CA, not a server certificate.** Routine renewal and IP changes never need re-pairing; a replacement machine or a compromised key does.
- **Wasla now ships installed software:** a signed installer, an updater, a certificate per installation, Windows Firewall rules and support bundles. That is new operational work, owned by POS.
- **SQLite needs care:** money as integer kuruş, POS's own concurrency tests, and explicit migrations after a backup.
- **Owners have separate Orders and POS credentials** until the identity decision is made.
- **Orders behavior is unchanged.** The first code changes that touch Orders are two refactors: extracting the platform library, and extracting the printing library from Print Bridge Core. Each must leave every Orders and Print Bridge test green with no behavior change.

## Related documents

- [../product/pos-mvp.md](../product/pos-mvp.md)
- [../product/pos-first-slice.md](../product/pos-first-slice.md)
- [tenancy.md](tenancy.md), [authentication.md](authentication.md)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
- [../product/product-boundaries.md](../product/product-boundaries.md)
