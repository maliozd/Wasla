# Wasla POS first vertical slice and backlog

## Status and scope

Plan from WAS-34 (2026-10, revised for the local-first architecture). **Nothing here is implemented.**

It defines the smallest end-to-end POS slice that proves the local-first decisions in [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md): selling and printing with the internet cut, then synchronizing safely. It also lists the dependency-ordered issues that build the slice. The product scope is in [pos-mvp.md](pos-mvp.md).

The slice is a development and test milestone. It is not production-ready and not a pilot. The signed installer and updater, the restore drill on real hardware, physical-printer checks, a real ÖKC driver and the [e-Arşiv / e-Fatura / e-Adisyon review](pos-mvp.md#unresolved-product-decisions) come after it ([After the slice](#after-the-slice)).

## The slice

1. Install and initialize POS Edge for a test tenant, using the development service registration.
2. Create and migrate the local POS database.
3. Activate the installation with a one-time code from `Wasla.Pos.Cli`, while online.
4. Set the Owner PIN on the loopback console, pair a Terminal, and sign in with the Owner PIN.
5. Import a minimal menu (two categories, five products, one modifier group) and one table.
6. **Disconnect the WAN** (block outbound traffic from the Edge machine).
7. Open table 1, or a quick sale.
8. Add products, one of them with a modifier.
9. Send a kitchen ticket.
10. Print it locally, or prove the local print job flow against a recording printer, without cloud access.
11. Pay through the fake ÖKC driver (no device brand is chosen yet): a declined card, then a completed card or cash payment with fiscal and bank identifiers, and one transaction that ends unknown and is checked on the device before any retry.
12. Close the sale and reopen it read-only from local history.
13. **Restore the WAN.**
14. Upload the sale, payment, kitchen ticket and audit records through the durable outbox.
15. Simulate a retry, a crash and a replay, and prove the cloud stores each record once.
16. Show the installation as synchronized, with the correct checkpoint, in sync health and in `Wasla.Pos.Cli installation-status`.
17. Restore a deliberately stale backup taken before step 7. Recover the later synced sale from POS Cloud with a restore code. Prove that local history and day totals are complete, and that nothing uploads twice.

### Included

- **Applications:** POS Edge as a Windows service with local HTTPS and a loopback-only console. The POS Terminal shell. POS Cloud with activation and ingestion. `Wasla.Pos.Cli`.
- **Local data:** a local SQLite database with migrations, the transactional outbox, scheduled backups, and a restore command that ends in a cloud recovery.
- **Activation and licence:** installation activation, the installation credential, and an entitlement lease with grace and restricted mode.
- **Devices and staff:** an installation CA with renewable server certificates; Terminal pairing that pins the CA; Owner PIN sign-in, lockout, the recovery code; POS roles and policies (Manager, Cashier, Waiter and Kitchen exist in the policy table and tests, without screens of their own).
- **Menu and selling:** menu and table import. Sale screen, tables screen, dine-in check, quick sale and send to kitchen.
- **Payments and history:** the ÖKC device-adapter interface with a fake driver, fiscal transactions (sending, waiting for the customer, declined, cancelled, waiting for the fiscal receipt, completed, unknown and its check), payment records with separate check, fiscal and bank identifiers, closing, local history and the day summary.
- **Printing:** local printing of the kitchen ticket and pre-bill (non-fiscal) through the printing library extracted from Print Bridge. Fiscal receipts are the ÖKC's job and are simulated by the fake driver.
- **Sync:** outbox upload, ingestion, acknowledgements, protocol versioning, conflict blocking, cloud commands, sync health, and the restore operation with its signed recovery package.
- **Languages:** TR, EN, AR and RU resources for every slice screen, with Arabic right to left.

### Not included

The following follow in the [backlog](#backlog) or after it:
- the signed installer, updater and rollback;
- a real ÖKC driver, which waits for the device investigation;
- takeaway;
- voids, cancellations and payment reversals;
- the menu and table editor;
- staff management beyond the Owner;
- cloud-issued recovery tokens;
- shifts.

Everything listed as later or out of scope in [pos-mvp.md](pos-mvp.md#mvp-boundary) is also excluded.

## Acceptance criteria

Each criterion names the test that proves it. Tests run against an in-process POS Edge and an in-process POS Cloud with fake credentials and fake data. "WAN disconnected" means the Edge's cloud client fails every request, and a test asserts that **no** request reaches POS Cloud during the step. SQLite tests prove SQLite behavior, and are labelled so. They do not prove SQL Server locking ([../operations/testing.md](../operations/testing.md)).

1. **Sale with the WAN disconnected throughout.**
   - Steps 7–12 complete with every cloud request failing, and none of them waits for the cloud.
   - The sync chip shows "Offline since", and the outbox holds the expected records in sequence order.
   - *Integration test; Terminal or headless-browser test of the sale screen at 1024×768, LTR and RTL.*
2. **Local printing without cloud.**
   - Send to kitchen creates one Pending local print job, and the print worker sends it to a recording printer.
   - The job ends Printed, all with the WAN disconnected.
   - A restart while the job is in Printing leaves it "print uncertain", never reprinted automatically.
   - Print Bridge and Orders printing tests stay green and unchanged.
   - *Unit tests; `Wasla.PrintBridge.Tests`.*
3. **Restart with unsynchronized records.**
   - After the sale, stop POS Edge while records are unacknowledged, start it, and restore the WAN.
   - Every record is uploaded once, in order, and the checkpoint equals the last sequence.
   - *Integration test.*
4. **Duplicate upload.**
   - The same batch is sent twice, including concurrently. The cloud stores each `recordId` once and acknowledges both.
   - *Integration test; SQL Server test for concurrent ingestion on a throwaway database.*
5. **Partial batch acknowledgement.**
   - A batch whose record *n* is invalid stores records before *n* and acknowledges up to *n − 1*.
   - The Edge shows sync as blocked at *n* and never skips it. Selling continues.
   - *Integration test.*
6. **Cloud temporarily unavailable.**
   - POS Cloud answers 503 with `Retry-After`, then recovers. The Edge backs off, resumes, and uploads everything once.
   - A lost acknowledgement (the cloud stored the batch, the response was dropped) leads to a resend that stores nothing twice.
   - *Integration test.*
7. **Protocol-version mismatch.**
   - An Edge with an unsupported protocol version gets `protocol_unsupported`. It keeps queueing, shows "update needed to synchronize", and keeps selling.
   - The cloud accepts the previous version.
   - *Integration test.*
8. **Wrong tenant or installation credential.**
   - An upload with another tenant's id, another installation's id or a wrong credential gets 401 and stores nothing.
   - A Terminal paired with installation A is refused by installation B.
   - *Integration and unit tests.*
9. **Backup, stale restore and cloud recovery.**
   - A scheduled backup passes its integrity check.
   - **Stale backup, cloud ahead.** Restore a backup taken before a sale that was later synced. Until recovery completes, POS Edge refuses sales. The restore code revokes the old credential first, so an upload from the old credential is refused. The recovery package returns the later sale, its payment, kitchen ticket and audit records with their original ids. Local history and the day summary then match the totals from before the failure.
   - **Backup ahead of the cloud.** Restore a backup that holds records the cloud never received. They are uploaded once under their original `(epoch, sequence)`.
   - **Duplicates and replays.** Recovered records are marked Recovered and are never uploaded again. Applying the same package twice changes nothing. New records use the new epoch and never reuse an acknowledged `(epoch, sequence)`. Receipt numbers carry the epoch prefix.
   - **Rejected packages.**
     - A package for another tenant, installation or restore operation, or with a broken signature or a wrong record hash, is refused before any write.
     - A backup and cloud record with the same id but different hashes stops the recovery with nothing written.
   - **Cloud-only recovery.** With no backup, it restores every synced fact. Staff come back without PINs, and a new Owner setup code is issued.
   - *Integration tests with an in-process POS Cloud.*
10. **Synchronized checkpoint.**
    - After reconnection, sync health shows the last successful upload, zero pending records and the cloud's checkpoint.
    - `Wasla.Pos.Cli installation-status` shows the same checkpoint and epoch.
    - *Integration test.*
11. **Licence without cloud.**
    - With the WAN disconnected beyond the lease, an open check can still be paid, closed and printed, and history opens.
    - A new check is refused from the next business day.
    - Setting the clock back does not extend the lease.
    - *Unit tests.*
12. **Payment and card-data boundary.**
    - A card-number-like field returned by the fake ÖKC driver is dropped by the adapter and never stored, logged, audited or uploaded.
    - If the manual payment record is kept, a 16-digit reference that passes the Luhn check is refused at entry and is never uploaded.
    - No PIN, credential or activation code appears in logs, audit records, outbox payloads or support bundles.
    - *Unit tests.*
13. **Certificate lifecycle without re-pairing.**
    - POS Edge renews its server certificate before expiry, and a paired Terminal keeps working across the switch.
    - A changed Edge IP address needs only the address corrected on the Terminal.
    - A CA rollover is accepted only with the old key's signature. An unsigned new CA, a certificate from another installation's CA, or a certificate without the paired installation id is refused.
    - Marking the CA compromised revokes every device credential.
    - *Unit tests; Terminal test against an in-process Edge.*
14. **ÖKC outcomes through the fake driver.**
    - A decline or cancellation records no payment and allows a new attempt.
    - A completed transaction stores the fiscal receipt number, Z number and bank reference in separate fields, and the check closes.
    - A transaction that times out after approval becomes Unknown. A second payment for the check is refused until it is checked; the check finds it completed and records it once.
    - Only a definitive result, matched to the original transaction, proving that no payment was approved and no fiscal receipt was issued becomes Failed and allows a retry. A query returning no matching record, a timeout or an unavailable device stays Unknown and blocks another payment.
    - Approval followed by missing fiscal completion stays Awaiting fiscal receipt or Unknown; it never allows another charge.
    - A Manager or Owner resolution records the approver, time, reason and the matching ÖKC or bank evidence references. Approval by a manager alone, or a query with no matching record, cannot mark the transaction Failed or unlock payment. If the evidence is inconclusive, it stays unresolved and payment remains blocked.
    - Fake-driver scenarios cover approval followed by a lost response and no matching query record (still blocked), definitive non-completion (retry allowed), and an unsupported manager override (refused).
    - A POS Edge restart while a transaction is in flight leaves it Unknown, never Completed or Failed by assumption.
    - *Unit and integration tests, with the WAN disconnected.*
15. **Wasla Orders unaffected.**
    - `Wasla.sln` builds. The full `Wasla.UnitTests`, `Wasla.PrintBridge.Tests` and Node tests pass on the slice's final commit.
    - No Orders behavior, configuration or schema changes, except the two library extractions, which are behavior-neutral.
    - *CI.*

Not proven by the slice: real restaurant networks and routers, physical printers and paper output, the installer and updater, performance targets, long outages measured in days, a failed Edge machine replaced and recovered on real hardware, and a CA rollover over the real 10-year lifetime. [WAS-168](https://linear.app/wasla-food/issue/WAS-168) and [WAS-169](https://linear.app/wasla-food/issue/WAS-169) cover them.

## Implementation order

Each step is one Linear issue, merged into `dev` through its own branch and worktree, with focused tests. No step changes Orders behavior. WAS-156 and WAS-163 move Orders and Print Bridge code into shared libraries without changing behavior.

1. [WAS-157](https://linear.app/wasla-food/issue/WAS-157) Scaffold POS Edge, the Terminal shell and POS Cloud, with CI and boundary tests.
2. [WAS-158](https://linear.app/wasla-food/issue/WAS-158) Local SQLite database, migrations, outbox, backup and restore.
3. [WAS-156](https://linear.app/wasla-food/issue/WAS-156) Platform library for secrets and the CentralDb POS registry. It can run in parallel with steps 1 and 2.
4. [WAS-165](https://linear.app/wasla-food/issue/WAS-165) Activation, installation identity and the offline licence lease.
5. [WAS-159](https://linear.app/wasla-food/issue/WAS-159) Terminal pairing, PIN sign-in, POS roles, recovery code.
6. [WAS-160](https://linear.app/wasla-food/issue/WAS-160) Menu and tables import.
7. [WAS-161](https://linear.app/wasla-food/issue/WAS-161) Sale screen, table checks, quick sale, send to kitchen.
8. [WAS-162](https://linear.app/wasla-food/issue/WAS-162) Payments through the ÖKC adapter (fake driver), close, local history.
9. [WAS-163](https://linear.app/wasla-food/issue/WAS-163) Local printing with the extracted printing library. It can run in parallel with step 10.
10. [WAS-166](https://linear.app/wasla-food/issue/WAS-166) Outbox upload and cloud ingestion.
11. [WAS-167](https://linear.app/wasla-food/issue/WAS-167) Sync health, protocol versions, conflicts and cloud commands. **The slice is complete when this step and step 9 are merged.**
12. [WAS-168](https://linear.app/wasla-food/issue/WAS-168) Installer, update and rollback. It can start once steps 2 and 5 are merged (the installer creates the installation CA from step 5) and run in parallel with steps 6–11.
13. [WAS-169](https://linear.app/wasla-food/issue/WAS-169) Pilot readiness, including the fiscal gate.

## Backlog

Linear project *Wasla POS — MVP*. Each issue's description holds its full boundary and verification; this table is the summary. WAS-156 to WAS-163 were rewritten for the local-first architecture. [WAS-164](https://linear.app/wasla-food/issue/WAS-164) (a second, remote Print Bridge connection) was cancelled. WAS-165 to WAS-169 are new.

| Order | Issue | Area | Boundary | Depends on | Verification |
| --- | --- | --- | --- | --- | --- |
| 1 | [WAS-157](https://linear.app/wasla-food/issue/WAS-157) | Edge runtime foundation | POS projects and `Wasla.Pos.sln`; Edge as a Windows service with LAN and loopback HTTPS; loopback-only console; Terminal shell; Cloud health endpoint; CI job; reference-direction tests | This ADR merged | POS build and tests; existing CI unchanged; console refuses LAN requests |
| 2 | [WAS-158](https://linear.app/wasla-food/issue/WAS-158) | Local database and migrations | SQLite, WAL mode, money in kuruş, explicit `migrate`, transactional outbox, scheduled backups, local restore into the Recovering state (no selling until cloud recovery) | 1 | Outbox and backup/restore tests, labelled as SQLite evidence |
| 3 | [WAS-156](https://linear.app/wasla-food/issue/WAS-156) | Shared platform | Secrets and the CentralDb registry reader and POS registration writer, extracted from `Wasla.Infrastructure` with no Orders behavior change | This ADR merged | Full Orders suites; reference-direction test |
| 4 | [WAS-165](https://linear.app/wasla-food/issue/WAS-165) | Activation and licence | Tenant activation, POS cloud database, installation credential, signed lease, restricted mode, emergency code, clock high-water mark | 2, 3 | Unit tests; SQL Server test on a throwaway database |
| 5 | [WAS-159](https://linear.app/wasla-food/issue/WAS-159) | Pairing, PIN, certificates | Installation CA and renewable server certificates, CA rollover and compromise, pairing codes bound to the CA key, device credentials, peppered PINs, lockout, Owner setup and recovery codes, POS roles | 4 | Unit tests with the WAN disconnected; renewal, IP-change and rollover tests; Terminal or browser test in four cultures and RTL |
| 6 | [WAS-160](https://linear.app/wasla-food/issue/WAS-160) | Menu and tables | Local entities, validated idempotent import, snapshots to the outbox | 5 | Unit tests with the WAN disconnected |
| 7 | [WAS-161](https://linear.app/wasla-food/issue/WAS-161) | Local sale workflow | Sale and tables screens, checks, quick sale, send to kitchen with ticket, print job and outbox in one transaction | 6 | Unit, Node and Terminal/browser tests; concurrency on SQLite |
| 8 | [WAS-162](https://linear.app/wasla-food/issue/WAS-162) | Payments, history, audit | ÖKC device-adapter interface and fake driver, fiscal transactions with the unknown-outcome check, payment records with separate check, fiscal and bank identifiers, close, history, day summary, outbox and audit | 7 | Concurrency on SQLite; fake-driver outcome tests; unit and browser tests |
| 9 | [WAS-163](https://linear.app/wasla-food/issue/WAS-163) | Local printing | Printing library extracted from Print Bridge Core; Edge print worker; printer roles; non-fiscal documents (kitchen ticket, void slip, pre-bill; no customer receipt) | 8 | Print Bridge tests unchanged; worker tests with a recording printer; manual physical print reported separately |
| 10 | [WAS-166](https://linear.app/wasla-food/issue/WAS-166) | Outbox and cloud ingestion | Sync contracts v1, batches, ingestion with unique keys, acknowledgement after durable storage, partial acknowledgement, retry, replay | 4, 8 | Integration tests; SQL Server concurrent ingestion test |
| 11 | [WAS-167](https://linear.app/wasla-food/issue/WAS-167) | Sync health, conflicts, versions, recovery | Protocol N/N−1, integrity conflicts, poison records, cloud commands, sync health, `installation-status`, support bundle; restore codes, signed recovery packages, recovery conflicts, numbering after a restore | 10 | Integration tests, including stale-backup recovery; browser test of sync health |
| 12 | [WAS-168](https://linear.app/wasla-food/issue/WAS-168) | Installer, update, rollback | Signed per-machine installer, service account, firewall rule, installation CA and server certificate (kept on repair and upgrade), WebView2 Runtime, updater with backup, migrate, health check and rollback | 2, 5 | Clean Windows 10 and 11 VMs; forced rollback; tampered package refused |
| 13 | [WAS-169](https://linear.app/wasla-food/issue/WAS-169) | Backup/restore and pilot readiness | Restore and cloud-recovery drill from a deliberately stale backup on real hardware, physical printing, WAN-cut service, support runbook, fiscal gate | 9, 11, 12 | Written report from real hardware, separate from automated results |

## After the slice

In this order, before a pilot restaurant:

1. [WAS-168](https://linear.app/wasla-food/issue/WAS-168): the installer, updater and rollback.
2. **ÖKC technical investigation, then the first real driver** (issues to be created). The investigation tests the available Verifone devices and others, and records brand and model, SDK or protocol access, connection, manufacturer or authority approval requirements and fees ([ADR](../architecture/pos-application-boundary.md#fiscal-cash-register-ökc)). The driver implements the adapter interface proven by the fake driver in the slice.
3. The remaining MVP features. Each gets its own issue when the slice is accepted:
   - the back-office editor for menu, tables, staff and terminals;
   - takeaway;
   - voids, cancellations and payment reversals with manager approval;
   - void slips and reprints of non-fiscal documents;
   - ÖKC cancellations and returns (*iade*) for reversals and sale cancellations, as the chosen device allows;
   - cloud-issued Owner recovery tokens.
4. [WAS-169](https://linear.app/wasla-food/issue/WAS-169): pilot readiness. **A working ÖKC integration and the e-Arşiv / e-Fatura / e-Adisyon review are blockers for any paid pilot.**

## Risks

| Risk | Mitigation |
| --- | --- |
| The Edge machine is a single point of failure | Backups every 15 minutes with integrity checks, a second backup path, a restore operation that recovers synced facts from POS Cloud, a restore and recovery drill (WAS-169), and a paper or fiscal-register fallback in the runbook |
| SQLite behavior differs from the SQL Server the team knows | Money in kuruş, writes serialized in the Edge, POS's own concurrency tests labelled as SQLite evidence, explicit migrations after a backup |
| Shipping installed software is new operational work | One signed installer and updater with automatic rollback (WAS-168); support bundles; versions in sync health |
| Certificate expiry or IP changes force every Terminal to be re-paired | Terminals pin a per-installation CA; server certificates renew automatically with an overlap; IP changes need no new certificate; CA rollover is signed by the old key |
| Library extractions (WAS-156, WAS-163) break Orders or Print Bridge | Move only, no behavior change, the full Orders and Print Bridge suites |
| A long outage meets an expired lease | Provisionally 14-day lease (final length open), warnings, restricted mode that never stops a service in progress, and a one-time emergency extension |
| ÖKC integration depends on vendor SDK access, approvals and fees that are not known yet | Technical investigation with the available Verifone devices and others before choosing; a device-adapter boundary and a fake driver so the payment flow does not wait for the brand choice |
| An unknown ÖKC outcome leads to a double charge or a missing payment | Every unknown transaction is checked on the ÖKC before any retry; a new payment for the check is refused meanwhile; unresolved cases need a Manager or Owner decision, audited |
| Fiscal obligations beyond the ÖKC (e-Arşiv, e-Fatura, e-Adisyon) are not met | Legal and tax review before any paid pilot (WAS-169 gate); POS Edge's own documents say they are not fiscal documents |
| Owners find two credentials (Orders and POS) confusing | An explicit open decision with triggers in the [ADR](../architecture/pos-application-boundary.md#later-decisions) |
