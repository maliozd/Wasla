# Wasla POS MVP

## Status and scope

Product definition for the first usable Wasla POS release (WAS-34, 2026-10). **Nothing in this document is implemented.** No POS project, installer, database or screen exists in this repository. The current code is Wasla Orders ([product-boundaries.md](product-boundaries.md)).

Wasla POS is a **local-first product installed at the restaurant**. Selling never depends on the internet or the Wasla cloud. The architecture is decided in [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md); the first slice and the backlog are in [pos-first-slice.md](pos-first-slice.md).

This document owns:

- The MVP cashier, waiter and kitchen workflows, and installation and setup at the restaurant
- POS roles and what each may do
- The MVP boundary: must-have, later, out of scope, assumptions and open decisions
- The restaurant-payment boundary
- UX and non-functional requirements, including working without internet

**Fiscal direction (2026-10-22).** Wasla POS integrates with a Turkish fiscal cash register (ÖKC). Payments run through the ÖKC, which issues the fiscal receipt, and POS Edge manages the device communication ([workflow 6](#6-payment-through-the-fiscal-cash-register-ökc)). The first supported brand and model are not chosen; device access, approvals and fees are under [technical investigation](#unresolved-product-decisions). Choosing ÖKC integration does **not** settle e-Arşiv, e-Fatura or e-Adisyon obligations.

The Wasla POS page of the marketing Figma file (`Wasla-Marketing`, page `14:803`) shows the product intent:
- table service, delivery, quick sale and takeaway;
- touch product selection with modifiers;
- cash and card payment;
- kitchen and receipt printing;
- Orders and POS side by side.

It is a visual direction, not evidence that any of it exists, and it does not set the MVP scope below.

## Product position

Wasla POS is the in-restaurant point of sale. A cashier or waiter builds a check on a touch terminal, the kitchen gets a printed ticket, and the sale is closed when the payment is recorded. It is not the Wasla Orders Live Screen and does not handle platform orders. A restaurant may use both products. They share the restaurant account (tenant), not their operational screens or databases.

At the restaurant, **POS Edge** (a Windows service on one always-on PC) holds the POS database, serves the screens to the **POS Terminals** on the local network, prints, and synchronizes with the Wasla cloud whenever the internet is available. "Offline" below means offline from the internet and the Wasla cloud. The restaurant's network and its POS Edge machine must still be running.

Terms used below:

| Term | Meaning |
| --- | --- |
| **POS Edge** | The installed Wasla POS service and its local database on the restaurant's Edge machine |
| **Terminal** | A paired Windows touch device running the Wasla POS Terminal app, which talks to POS Edge over the restaurant network or on the same machine |
| **Check** (*adisyon*) | One open or closed sale: a table's bill, a takeaway order or a quick sale. Lines and payments belong to a check |
| **Line** | One product on a check with its chosen modifiers, quantity, note and price snapshot |
| **Kitchen ticket** | A printed request to prepare the lines sent from a check; also a durable record. Internal and non-fiscal |
| **Pre-bill** (*ön hesap*) | A printed, non-fiscal summary of an unpaid check for the guest. Printed by POS Edge, never a fiscal document |
| **ÖKC** | The restaurant's Turkish fiscal cash register (*ödeme kaydedici cihaz*), connected to POS Edge. It registers the sale, takes card payments through its bank payment application, and issues the fiscal receipt. Brand and model are open |
| **Fiscal receipt** (*mali fiş*) | The fiscal document the ÖKC prints when a sale is paid. Issued and numbered by the ÖKC (fiscal receipt number and Z number), never by Wasla |
| **Fiscal transaction** | Wasla's record of one payment attempt sent to the ÖKC: its state (sending, waiting for the customer, declined, cancelled, waiting for the fiscal receipt, completed, unknown) and the identifiers the ÖKC returns |
| **Payment record** | Wasla's record that a payment completed on the ÖKC (cash or card), with the fiscal receipt number, Z number and, for cards, the bank transaction reference. Wasla moves no money |
| **Business day** | The trading day used for history and totals. It runs from a configurable cut-off (default 05:00, `Europe/Istanbul`) to the next, so a service after midnight stays in its evening's day. There is no shift in the MVP |
| **Sync** | Uploading finished sales, payments, tickets and audit records from POS Edge to the Wasla cloud, and receiving licence and revocation updates |

## Installation and setup

1. **Activation (operator, internet needed once).** Wasla activates POS for the tenant and gives the restaurant a one-time activation code. The installer is run on the Edge machine, and POS Edge is activated with the code. That creates the installation's identity, its first licence lease, an Owner setup code and a printed Owner recovery code.
2. **First Owner.** On the Edge machine, the Owner sets their PIN with the setup code.
3. **Printers.** In the back office, the Owner maps the receipt and kitchen printer roles to printers installed on the Edge machine (the kitchen role may use the receipt printer) and prints a test page. The "receipt" role prints pre-bills and other non-fiscal slips; fiscal receipts come from the ÖKC.
4. **Fiscal cash register.** The ÖKC is connected to the Edge machine and paired in the back office by the Owner. Payments stay disabled until POS Edge passes a connection check with it. Connection type and pairing steps depend on the device ([open](#unresolved-product-decisions)).
5. **Terminals.** For each terminal: install the Terminal app, then pair it with a pairing code created by the Owner or a Manager. The code also verifies POS Edge's certificate.
6. **Menu, tables, staff.** The Owner imports or enters the menu, areas and tables, and adds staff with their roles and PINs. Products may need a VAT rate or ÖKC department for the fiscal receipt ([open](#unresolved-product-decisions)).
7. **Backups.** POS Edge backs up automatically. The Owner may add a second backup location (another disk or a USB drive).

After setup, internet is needed only to synchronize, to receive updates and licence renewals, and at least once per licence period ([licence](../architecture/pos-application-boundary.md#licensing-while-offline)).

## Cashier workflows

All flows run on a paired Terminal with a signed-in staff member ([Roles](#roles)). Every step that changes a check is a request to POS Edge over the restaurant network. **None of them needs the internet or the Wasla cloud.** The Terminal holds only unsent draft lines ([LAN loss](#lan-loss)).

### 1. Product, category and modifier selection

1. The sale screen shows category tabs (the restaurant's own order) and the products of the selected category as tiles: name, price, and a marker when the product has modifiers.
2. Tapping a product without required modifiers adds one line with quantity 1. Tapping it again increases that draft line's quantity.
3. A product with modifier groups opens a modifier panel.
   - Each group is single-choice (for example portion: 1, 1.5, 2) or multi-choice (extras), with a minimum and maximum and an optional price change per option.
   - Required groups must be completed before **Add**.
   - The panel shows the line total as options are chosen.
   - A free-text kitchen note is optional.
4. A search field filters products by name across categories, for keyboard use and long menus.
5. Inactive products and products marked unavailable today are hidden from the tiles, but remain on existing checks.

### 2. Touch-first cart

- The cart is always visible next to the products. It shows the lines with quantity, modifiers, note and line total; the subtotal and total; and the check's label (table, takeaway name or number, or quick sale).
- Lines not yet sent are editable: quantity −/+, change modifiers, note, remove. Lines already sent to the kitchen are locked; changing them is a [void](#9-void-and-cancellation).
- The primary actions are fixed in the same place on every check: **Send to kitchen**, **Pay**, and for tables **Save** (keep the check open without sending).

### 3. Dine-in table (adisyon)

1. The tables screen shows areas (for example Salon, Bahçe) and their tables. A table is **free** or **occupied** (has an open check), with the open check's total and age.
2. Tapping a free table opens a new check for it; tapping an occupied table opens its check. One table has at most one open check, even when two terminals try at the same moment.
3. Staff add lines, then **Send to kitchen**, or **Save** to keep drafts as unsent lines on POS Edge.
4. More rounds are added to the same check and sent as new kitchen tickets.
5. **Print bill** prints the current check as a pre-bill slip for the guest. It is not a receipt and can be printed many times.
6. **Pay** [through the ÖKC](#6-payment-through-the-fiscal-cash-register-ökc), by card or cash. When the payments completed on the ÖKC cover the total, the check closes and the table becomes free.

### 4. Takeaway (gel-al)

A check without a table, labelled with an optional name and a sequential takeaway number for the business day. It follows the dine-in steps: send to kitchen, then pay now or at pickup. Open takeaway checks are listed on the tables screen under **Takeaway**. Delivery to an address (*paket servis*) is not part of the MVP ([Out of scope](#out-of-scope)).

### 5. Quick sale (hızlı satış)

For counter sales paid immediately: the sale screen opens on an empty quick-sale check, and the cashier adds products and taps **Pay**. **Send to kitchen** is optional; when the restaurant enables it, it happens automatically on payment. The check closes on payment and the screen returns to an empty quick sale.

### 6. Payment through the fiscal cash register (ÖKC)

The primary payment flow, for card and cash. POS Edge talks to the ÖKC; Terminals never do.

1. **Pay** shows the amount due, the methods **Card (ÖKC)** and **Cash (ÖKC)**, and the ÖKC's state (ready, busy, not connected, needs attention). Pay is disabled while the ÖKC is not connected.
2. **Sending to the ÖKC.** POS Edge creates a fiscal transaction with its own id, then sends the sale to the ÖKC: the amount and, if the device needs them, the lines with their VAT groups or departments.
3. **Waiting for the customer.**
   - Card: the ÖKC shows the amount; the guest taps or inserts the card and enters the PIN on the ÖKC. Wasla never sees card data.
   - Cash: the cashier enters the tendered amount on the Terminal (quick notes and keypad; change is shown), and confirms on the ÖKC if the device asks.
   - **Cancel** asks the ÖKC to cancel, where the device supports it.
4. **Declined or cancelled.** The bank declines, the guest or the cashier cancels, or the ÖKC times out before approval. Nothing is recorded as paid and no fiscal receipt exists; the cashier may try again or choose another method.
5. **Waiting for the fiscal receipt.** After approval, the ÖKC prints the fiscal receipt and returns its result.
6. **Completed.** POS Edge records the payment with the ÖKC's result: the fiscal receipt number and Z number (plus any other device identifiers it returns) and, for cards, the bank transaction reference and approval code. The check closes when completed payments cover the total.
7. **Unknown outcome**: see [workflow 7](#7-unknown-ökc-outcome).

Three identifiers stay separate on every screen, document, audit record and sync record, and none is reused as another:

| Identifier | Issued by | Example |
| --- | --- | --- |
| Check number | Wasla (per business day; epoch prefix after a restore) | `Adisyon 38`, `2-0047` |
| Fiscal receipt number and Z number | The ÖKC | `Fiş 0127 · Z 0214` |
| Bank transaction reference and approval code | The bank, through the ÖKC's payment application | `RRN 629541873015 · Onay 5A2K9C` |

Several payments per check (part cash, part card), partial payments and several payments on one fiscal receipt depend on the device protocol ([open](#unresolved-product-decisions)). Splitting a check by items is not in the MVP.

### 7. Unknown ÖKC outcome

A fiscal transaction is **unknown** when POS Edge cannot tell how it ended: the connection to the ÖKC dropped, a response timed out, or POS Edge restarted after sending.

1. The Terminal says that the outcome is unknown and that **no other payment may be taken**, on the ÖKC or on any bank terminal, until it is checked. Every new payment attempt for that check is refused while a transaction on it is unknown.
2. **Check on the ÖKC first.** POS Edge queries the ÖKC for the transaction, by Wasla's reference if the device supports one, otherwise by its last transaction and fiscal receipt.
   - **Completed on the ÖKC:** recorded once as a completed payment with its fiscal and bank identifiers. Nothing is sent again.
   - **Not completed** (no approval and no fiscal receipt): marked failed; a new attempt is allowed.
   - **Cannot be determined** (ÖKC unreachable, or no matching record): stays unknown. A Manager or Owner resolves it from the ÖKC's own records (last fiscal receipt, bank slip, device reports) and records the decision with a reason. It is audited.
3. Only after the check ends in "not completed" or a recorded resolution may another payment be started.

**Manual payment record (exception, not the primary flow).** Recording a payment that was taken outside the ÖKC flow, for example on a separate bank terminal while the ÖKC integration is unavailable, is an exception. Whether it is allowed at all, who may use it and how the fiscal document is then issued is an [open decision](#unresolved-product-decisions). If kept: Manager or Owner only, with a reason, the card-number guard of the [payment boundary](#payment-boundary), and the check shows that no fiscal receipt is linked.

### 8. Printing and documents

Wasla documents and fiscal documents are kept visibly apart. POS Edge prints only non-fiscal documents, each marked as not being a fiscal document and laid out differently from a fiscal receipt. Fiscal documents come only from the ÖKC.

- **Kitchen ticket** (Wasla, internal): every **Send to kitchen** creates one ticket with the newly sent lines (product, modifiers, quantity, note), the check label, the staff member and the time. It prints on the kitchen printer role. Printing again marks the copy as a reprint.
- **Void slip** (Wasla, internal): voiding a sent line prints a slip to the kitchen printer, so the kitchen stops preparing it.
- **Pre-bill** (Wasla, for the guest, non-fiscal): an unpaid check summary. Whether and how pre-bills may be given to guests under e-Adisyon rules is [open](#unresolved-product-decisions).
- **Fiscal receipt** (ÖKC): printed by the ÖKC when a payment completes. Wasla prints no customer receipt of its own in the ÖKC flow. Copies, returns and cancellation documents are ÖKC functions and depend on the device.
- POS Edge prints on printers installed on the Edge machine, without the internet, the cloud or Wasla Orders ([print decision](../architecture/pos-application-boundary.md#printing)).
- A print job's state is shown honestly: queued, printing, printed (accepted by the Windows spooler) or failed. A job interrupted by a restart shows "print uncertain" and is never reprinted automatically. A print failure never undoes a kitchen send or a payment.

### 9. Void and cancellation

| Situation | Who | What happens |
| --- | --- | --- |
| Remove or change an unsent line | Cashier, Waiter | Line is removed; no kitchen effect |
| Void a sent line | Cashier or Waiter requests, Manager/Owner approves on the device with their PIN | Line stays on the check as voided with a reason; void slip prints in the kitchen; totals drop |
| Cancel an open check with sent lines | Manager/Owner | All sent lines voided with one reason; the table is freed |
| Reverse a recorded payment on an open check (wrong method or amount) | Manager/Owner | A reversal record cancels that payment; the original stays visible. For a payment completed on the ÖKC, the matching ÖKC cancellation or return (*iade*) document is also needed; how each device supports it is [open](#unresolved-product-decisions) |
| Cancel a closed sale (refund) | Manager/Owner | Each payment gets a reversal record and the check becomes **Cancelled** with a reason. The return or cancellation document and any card refund are made on the ÖKC (device-dependent, [open](#unresolved-product-decisions)); Wasla records the ÖKC result or, where none is returned, the staff member's confirmation. Wasla does not perform the refund |

Nothing is deleted. Every void, reversal and cancellation is audited ([Auditability](#auditability)) and synced. Reasons come from a short fixed list (wrong item, guest changed mind, kitchen problem, other with text). A closed sale that was already synced and is then cancelled reaches the cloud as a new cancellation record. The cloud's copy of the original sale is never rewritten.

### 10. Completed-sale history

- Lists closed and cancelled checks of a business day (default today), newest first, with the check number, the ÖKC fiscal receipt number, label, total, payment methods and status. Filter by status and payment method; search by check number, fiscal receipt number or bank reference.
- It reads only POS Edge's local database, so it works without internet.
- Opening a check shows it **read-only**: lines, voids, kitchen tickets, fiscal transactions (including declined, cancelled and resolved unknown ones), payments, reversals and audit entries.
- Actions from history: **Cancel sale** (Manager, Owner). A copy of a fiscal receipt is requested from the ÖKC where the device supports it ([open](#unresolved-product-decisions)); Wasla never reprints a fiscal receipt itself.
- A day summary shows totals by payment method, the expected cash for the day, and the number of cancelled checks. It is not a Z report and not a fiscal report; the Z report belongs to the ÖKC.

### 11. Shift open and close

**Not in the MVP, for an operational reason.** A cash-drawer shift (opening float, cash in and out, counted cash, difference, shift report) is how many restaurants control cash. The MVP leaves it out because:
- **The fiscal day closes on the ÖKC.** The ÖKC's Z report ends the fiscal day. Designing Wasla's own end-of-day and drawer flow before the [ÖKC integration details](#unresolved-product-decisions) are known risks a second, conflicting procedure the restaurant must run every night.
- **The slice proves something else.** It must prove local selling and safe sync, not cash control.

Until shifts exist, the day summary's expected cash per business day is the reconciliation figure. Shifts are the first should-have after the MVP ([Should-have later](#should-have-later)). They become a must-have for any pilot restaurant that does not use a fiscal register for end-of-day cash control.

### 12. Sync and connection state

- Every terminal shows a small sync chip: **Synchronized**, **Offline since hh:mm (n waiting)**, or **Needs attention**. It never blocks selling.
- Owners and Managers see sync health in the back office:
  - last successful upload;
  - waiting records and the oldest one's age;
  - a blocked reason, if any;
  - licence expiry;
  - clock difference;
  - the last backup result.

  They can create a support bundle from the same page.
- When the internet returns, the waiting records are uploaded by themselves. Nothing is lost by a restart in between.

## Roles

POS roles are owned by Wasla POS. They are **not** the Wasla Orders `UserRole` values (`Owner`, `Manager`, `Kitchen`, `Cashier`, `Viewer` in `src/Wasla.Domain/Enums/UserRole.cs`). There, Cashier and Kitchen handle platform orders on the Live Screen ([roles-and-permissions.md](roles-and-permissions.md)), which is a different job. Reusing that enum would tie POS permissions to Orders policies and migrations. Names may match; the definitions do not.

| Capability | Owner | Manager | Cashier | Waiter | Kitchen |
| --- | :-: | :-: | :-: | :-: | :-: |
| Open table, takeaway or quick-sale check; add and change unsent lines | ✓ | ✓ | ✓ | ✓ | |
| Send to kitchen; print pre-bill | ✓ | ✓ | ✓ | ✓ | |
| Take a payment through the ÖKC; close check | ✓ | ✓ | ✓ | | |
| Request a void of a sent line | ✓ | ✓ | ✓ | ✓ | |
| Approve a void; cancel an open check; reverse a payment; cancel a closed sale; resolve an unknown ÖKC outcome; manual payment record (if kept) | ✓ | ✓ | | | |
| View history | ✓ | ✓ | ✓ | | |
| Edit menu, prices, modifiers, tables | ✓ | ✓ | | | |
| View sync and backup status; create a support bundle; apply a downloaded update | ✓ | ✓ | | | |
| Manage POS staff and PINs; pair and revoke terminals; printer roles; pair the ÖKC; POS settings; enter an emergency licence code | ✓ | | | | |
| Use any POS screen | | | | | MVP: none |

- **Kitchen** works from printed tickets in the MVP. A kitchen display screen is later. The role exists so that the staff list is complete and a kitchen screen can be added without changing other roles.
- **Waiter** cannot take payments in the MVP. Whether some restaurants want waiters to collect payment is an [open decision](#unresolved-product-decisions).
- **Owner** in POS is the restaurant's POS administrator. Its PIN, recovery and relation to the Orders Owner account are decided in the [ADR](../architecture/pos-application-boundary.md#staff-pins-and-recovery).
- POS Edge enforces this table on every request. Hiding a button is not authorization.

## MVP boundary

### Must-have

**Installed product:**
- POS Edge, a Windows service with a local SQLite database, installed with a signed installer.
- The Terminal app.
- Activation of one installation per restaurant location.
- Update with automatic rollback when the new version is unhealthy.

**Selling without internet:**
- Opening checks, lines and modifiers, kitchen tickets, payments, closing, printing, history and PIN sign-in on paired terminals all work with the internet or the Wasla cloud unavailable.

**Sync:**
- A durable local outbox, idempotent upload, replay after a restart.
- Visible sync state.
- Licence and revocation updates from the cloud.

**Licence:**
- A signed licence lease cached locally with a grace period.
- A restricted mode that never stops a service in progress.
- A one-time emergency extension.

**Backup and recovery basics:**
- Automatic local backups with integrity checks.
- A restore onto the same or a replacement machine, followed by a **cloud recovery** that brings back every sale, payment, kitchen ticket and audit record synced after the restored backup, without duplicates ([ADR](../architecture/pos-application-boundary.md#restore-and-recovery-from-the-cloud)).
- A restore and recovery test from a deliberately stale backup.

**Devices and staff:**
- Paired terminals that pin a per-installation certificate authority. Server certificates renew without re-pairing, and IP address changes need no new certificate ([ADR](../architecture/pos-application-boundary.md#certificate-lifecycle)).
- Staff sign-in with a personal PIN on a paired terminal; lockout.
- Owner recovery without a permanent bypass.
- The role table above.

**Menu and tables:**
- Categories, products with price, active and available flags, modifier groups with options and price changes; edited by Owner or Manager.
- Areas and tables.

**Checks:**
- Dine-in table, takeaway, quick sale; touch cart; line notes.
- Send to kitchen with durable kitchen tickets.
- Close on full payment; one open check per table; single-winner close across terminals.

**Payments and fiscal device:**
- Payments through the ÖKC, card and cash, managed by POS Edge through a device adapter: sending, waiting for the customer, declined or cancelled, waiting for the fiscal receipt, completed, unknown.
- An unknown outcome is checked on the ÖKC before any new payment attempt.
- Check number, fiscal receipt and Z numbers, and bank reference stored and shown separately.
- Voids, check cancellation, payment reversal and sale cancellation with Manager or Owner approval and reasons.
- The first supported ÖKC brand and model follow the [technical investigation](#unresolved-product-decisions).

**Printing:**
- Local printing of the kitchen ticket, void slip and pre-bill (all non-fiscal) on mapped printer roles, with honest print state. Fiscal receipts are printed by the ÖKC.

**History, audit and support:**
- Completed-sale history (read-only) with reprint and a day summary by payment method.
- An audit log for every money- or kitchen-affecting action, synced to the cloud.
- Support bundle export.

**Language and correctness:**
- TR, EN, AR, RU with Arabic right to left; TRY amounts.
- Idempotent writes and duplicate-payment protection ([Non-functional requirements](#non-functional-requirements)).

### Should-have later

In rough order of expected value:

1. Shifts and cash drawer: opening float, cash in and out, counted cash and difference, shift report ([why later](#11-shift-open-and-close))
2. Table operations: move a check to another table, merge checks, split a check by items or guests
3. Discounts and service charge with permission and audit
4. Kitchen display screen for the Kitchen role, and routing tickets to several kitchen printers or stations
5. Encrypted cloud backups of the POS Edge database
6. Cloud reports and a remote back office (menu, reports) in a browser, with single-writer configuration rules
7. Waiter tablets and phones (Android, iPadOS)
8. Barcode lookup for packaged products; product images
9. Daily and period sales reports, product sales ranking, export
10. Delivery orders taken by phone (*paket servis*) with customer address
11. Showing Wasla Orders platform orders and POS checks together (the Figma "one ecosystem" panel), through a cloud read model, not a shared database
12. Menu translations for product names

### Out of scope

- Card processing by Wasla: no bank POS API, no payment provider, no card number or CVV entry, no card data stored. Card payments run on the ÖKC's own bank payment application
- Using Wasla's iyzico subscription merchant account for any restaurant sale
- Wasla printing its own customer receipt in place of the ÖKC's fiscal receipt
- e-Arşiv, e-Fatura or e-Adisyon issuance: not settled by the ÖKC decision; needs the [legal and tax review](#unresolved-product-decisions)
- More than one POS Edge per location, failover between Edges, and multi-master synchronization
- Inventory, stock, recipes and purchasing
- Accounting, payroll and tax reporting
- Delivery logistics and courier management
- Loyalty, campaigns and customer accounts
- Reservations
- Multi-location management (each location stays a separate tenant, as in [tenancy.md](../architecture/tenancy.md))
- Changes to Wasla Orders, its Live Screen or its order lifecycle

### Assumptions

- **Internet use.** Service does not need the internet. It is needed for activation, for updates, and to renew the licence at least once per lease period (provisionally 14 days; the final length is an [open decision](#unresolved-product-decisions)). It is also needed once during a restore after a failure ([Edge machine failure](#edge-machine-failure)). Sync uses outbound HTTPS only.
- **Edge machine.** The restaurant has one always-on Windows 10 or 11 Pro (64-bit) PC as the Edge machine, wired to the restaurant network. A UPS is recommended. It may also be a cashier station.
- **Terminals.** Windows touch devices at 1024×768 or larger, on the same network as the Edge machine (wired or a staff Wi-Fi, never a guest network).
- **Printers.** At least one ESC/POS-capable receipt printer is installed on the Edge machine for all users. A kitchen printer is optional; tickets can print on the receipt printer.
- **Fiscal cash register.** The restaurant has a supported new-generation ÖKC with a bank payment application, connected to the Edge machine. Which devices are supported is decided after the technical investigation; two Verifone devices are available for testing and others can be obtained.
- **Internet for cards.** Card approval uses the ÖKC's own bank connection. It does not go through POS Edge or the Wasla cloud.
- **Prices** include VAT; Wasla screens show no VAT breakdown. The fiscal receipt shows VAT as the ÖKC requires, which may need a VAT rate or department per product ([open](#unresolved-product-decisions)).
- **Currency.** One currency, TRY, as in Wasla Orders (`OrderCurrency`).
- **Pilot size.** Up to about 30 tables, 300 products and 5 terminals. These are design targets, not measured limits.

### Unresolved product decisions

| Decision | Why it matters | Needed before |
| --- | --- | --- |
| **ÖKC device and access (technical investigation).** The first brand and model; SDK or protocol access and its licence; how the device connects (USB, serial, LAN, or an app on the device); manufacturer or authority approval and certification requirements for software that drives the ÖKC; fees. ÖKC integration itself is decided (2026-10-22) | The adapter boundary is fixed; everything device-specific waits for this. Two Verifone devices are available for tests | **ÖKC driver implementation** (blocker) |
| **ÖKC transaction details.** Item-level sale or total only; VAT groups or departments per product; several payments and partial payments on one fiscal receipt; cancellation and return (*iade*) documents; fiscal receipt copies; which result fields come back (never card numbers, masked or not); whether the device accepts Wasla's reference for the unknown-outcome check | Shapes the menu data, the payment screens and reconciliation | ÖKC driver implementation |
| **e-Arşiv, e-Fatura and e-Adisyon obligations.** Which apply to a restaurant using Wasla POS with an ÖKC, including rules for giving pre-bills to guests | Not settled by choosing ÖKC integration; needs legal and tax review | **Any paid pilot** (blocker) |
| **Manual payment record.** Whether a payment taken outside the ÖKC flow may be recorded at all, by whom, and how the fiscal document is then issued | The earlier external-terminal flow is no longer primary | Pilot onboarding |
| **Licence lease length.** The final lease length (14 days is a provisional design assumption) and the warning period. Restricted mode is not open: it refuses only new checks and never blocks completing, printing or reviewing an open sale | Trades protection against unpaid use against the risk of stopping a restaurant during a long outage ([ADR](../architecture/pos-application-boundary.md#licensing-while-offline)) | Pilot onboarding |
| **Hardware and support model.** Who supplies and installs the Edge machine, whether a spare machine is required, and the restore time target | The Edge machine is the single point of failure ([ADR](../architecture/pos-application-boundary.md#failure-behavior)) | Pilot selection |
| **Owner account.** Whether the POS Owner signs in with the same email and password as Wasla Orders | POS staff identity is local for the MVP; one Wasla login needs identity to move out of the Orders tenant database ([ADR](../architecture/pos-application-boundary.md#staff-pins-and-recovery)) | Selling POS and Orders together |
| **Waiter payments.** Whether waiters may record payments, as a per-restaurant setting | Changes the role table and the audit trail | Pilot onboarding |
| **Waiter tablets.** Whether non-Windows devices are needed for the first pilots | Needs a second shell or a trusted LAN certificate scheme | Pilot selection |
| **Pricing and packaging.** Whether POS is a separate plan, an add-on to Orders, or bundled | Decides what the licence lease checks (WAS-147 owns the billing domain) | Self-service activation |
| **Kitchen printing layout.** One kitchen printer, or routing by category to several stations | Several stations need product-to-printer routing in the menu | First pilot with more than one kitchen station |
| **Check numbering.** Per business day or continuous for Wasla check numbers. After a restore, numbers carry the epoch as a prefix in either case ([ADR](../architecture/pos-application-boundary.md#numbering-after-a-restore)). Fiscal receipt and Z numbers belong to the ÖKC | Affects history search and how staff match a check to a fiscal receipt | First slice (the slice uses per-day numbers and can change before production) |

## Payment boundary

Wasla POS **sends** restaurant sale payments to the ÖKC and **records** their results. It never processes, authorizes, captures or refunds a card payment itself: the ÖKC's bank payment application does.

| | Wasla subscription billing | Restaurant sale payments (POS) |
| --- | --- | --- |
| Who pays whom | The restaurant pays Wasla for the SaaS | The restaurant's guest pays the restaurant |
| Money movement | iyzico, through Wasla's merchant account (WAS-145 to WAS-154; not implemented yet; signup uses a Development simulator, see [tenancy.md](../architecture/tenancy.md#signup-and-checkout-access)) | Cash, or card on the ÖKC's bank payment application. Wasla moves no money |
| Fiscal document | Wasla's own invoicing (out of POS scope) | Fiscal receipt printed by the ÖKC |
| Where it is stored | CentralDb (WAS-147) | POS Edge's local database, with immutable copies in the tenant's POS cloud database ([ADR](../architecture/pos-application-boundary.md#data-ownership)) |
| Card data | Never stored; iyzico's hosted form | Never entered or stored, locally or in the cloud. Wasla keeps only the bank transaction reference and approval code returned by the ÖKC: no card number (masked or not), expiry or CVV |

Rules:

- Payment methods in the MVP: **Card (ÖKC)** and **Cash (ÖKC)**. Adding a method is a product decision, not configuration.
- **ÖKC results.**
  - A payment counts as completed only when the ÖKC reports it completed, with its fiscal receipt number and Z number.
  - Result fields are filtered at the adapter: anything that could be card data is dropped before it is stored, logged, audited or uploaded.
  - An unknown outcome blocks any new payment for the check until it is [checked on the ÖKC](#7-unknown-ökc-outcome).
- **Manual records (exception, if kept).**
  - The record means only "a Manager or Owner confirmed a payment taken outside the ÖKC flow".
  - The optional reference is free text up to 32 characters, letters and digits only.
  - A run of 13–19 digits that passes the Luhn check (a possible card number) is refused, at entry and again before upload. Approval codes and 12-digit retrieval reference numbers stay allowed.
- Wasla's iyzico account, keys and code are never used by POS. POS has no payment-provider dependency.
- **Reversals and refunds are records too.**
  - A reversal points to the payment it cancels and has an amount equal to that payment, a reason, the approving Manager or Owner, and the confirming staff member.
  - A recorded payment is never edited or deleted, locally or in the cloud.
  - For ÖKC payments, the reversal also needs the ÖKC's cancellation or return document; Wasla records the ÖKC result. Where the device returns none, or for a manual record, Wasla records only the staff member's confirmation.
- A check closes only when the sum of its payments minus reversals equals its total. Cash may exceed the total; the difference is change and is recorded.

## UX requirements

The POS uses the Wasla visual direction (warm operational minimalism, foundation tokens in [../frontend/design-system.md](../frontend/design-system.md)). It uses the repository's frontend stack: ASP.NET Core MVC/Razor, Bootstrap 5 and vanilla JavaScript ([../frontend/architecture.md](../frontend/architecture.md)). The pages are served by POS Edge and shown in the Terminal app. It is a separate operational surface from the Orders tenant shell.

**Layout (1024×768 and larger, landscape):**

- Three zones: categories (top or start edge), product grid (center), cart and actions (end edge, about one third of the width). In Arabic the zones mirror.
- Dense, with no decorative whitespace. Text and prices are legible at arm's length (product names at least 16 px, totals at least 24 px).
- The tables screen is a grid of table tiles per area. Both tile colour and a text label show free or occupied; colour is never the only signal.
- Portrait terminals (768 px wide) stack the cart under the products with a cart summary bar. Phone widths are not a target for the MVP.

**Touch targets and taps:**

- Every interactive control is at least 48×48 CSS px, with at least 8 px between targets. Product tiles are at least 96×72 px. WCAG 2.2 requires 24 px; 48 px is the operational target for wet or gloved hands.
- Tap counts:
  - Add a product without modifiers: 1 tap.
  - Quick sale paid with exact cash: **Pay** + **Cash (ÖKC) exact** = 2 taps after the products; the ÖKC then prints the fiscal receipt.
  - Card: **Pay** + **Card (ÖKC)** = 2 taps; the guest completes the payment on the ÖKC and the result arrives by itself.
  - Send to kitchen: 1 tap.
  - Open an occupied table's check from the tables screen: 1 tap.
- No hover-only controls, no long-press-only actions, no drag-and-drop requirement.
- Destructive or money actions (void, cancel, reversal) need a confirmation step; adding lines never does.

**Keyboard and scanner readiness:**

- Every action is keyboard operable, with a visible focus ring:
  - The product search field is reachable with one key (`/` or F2).
  - Enter adds the highlighted product.
  - Amount entry accepts the numeric keypad.
- The sale screen must not swallow fast keyboard input, so that a keyboard-wedge barcode scanner can be added later without a redesign. Barcode lookup itself is not in the MVP.

**Localization:**

- TR (default), EN, AR, RU for every POS string, including the Terminal app, setup console and printed documents. Resource keys go under a POS prefix. There is no hardcoded copy in Razor or JavaScript, and JavaScript messages are passed from Razor, as in Wasla Orders ([20-localization-ui.mdc](../../.cursor/rules/20-localization-ui.mdc)).
- Arabic is right to left: mirrored zones, logical CSS properties, and amounts and check numbers kept left to right inside right-to-left text.
- Amounts are in TRY with `₺`, through the culture's number format, never the culture's own currency. Dates are Gregorian in every culture, as in Orders (WAS-44 for amounts, WAS-130 for dates).
- Product, category and modifier names are restaurant data and are not translated.

**Accessibility:** WCAG 2.2 AA for the POS screens:
- AA contrast in light and dark themes;
- labelled controls;
- status changes (line added, payment recorded, print failed, POS Edge unreachable, sync state) announced through live regions;
- no information by colour alone;
- `prefers-reduced-motion` respected;
- 200 % zoom without loss of function on the back-office screens.

## Non-functional requirements

These are targets for design and tests. None is measured yet.

### Responsiveness

- Tap feedback within 100 ms. Adding or changing a draft line happens on the Terminal and never waits for POS Edge.
- Writes to POS Edge (send to kitchen, save, close) answer within 200 ms at the 95th percentile over the restaurant network. The UI shows progress after 300 ms and never accepts a second identical action while one is in flight.
- A payment waits for the ÖKC and the guest. The Terminal shows the fiscal transaction's state the whole time and never offers a second payment while one is in flight or unknown.
- Nothing a cashier waits for calls the internet.
- The sale screen loads the menu once per sign-in and refreshes it when the menu changes; it does not request the menu per product tap.

### Internet loss

- Selling, printing, history and PIN sign-in on paired terminals continue unchanged. The sync chip shows "Offline since".
- Payments through the ÖKC do not use the Wasla cloud. Card approval needs the ÖKC's own bank connection; whether cash sales work while the ÖKC itself is offline depends on the device.
- Synced facts wait in POS Edge's durable outbox and are uploaded, in order and without duplicates, when the connection returns ([ADR](../architecture/pos-application-boundary.md#synchronization)).
- A Wasla cloud outage is the same as an internet outage for the restaurant.
- The licence lease covers the lease period without contact (provisionally 14 days). Restricted mode, if ever reached, refuses only new checks from the next business day. **An expired lease never blocks completing, printing or reviewing a sale that is already open**: adding lines, sending to the kitchen, paying, closing, printing and history all continue ([ADR](../architecture/pos-application-boundary.md#licensing-while-offline)).

### LAN loss

- A Terminal that cannot reach POS Edge shows "POS Edge unavailable". Draft lines stay editable on that Terminal, and **Send to kitchen**, **Pay** and **Close** are disabled with a localized message.
- Draft lines that were never sent live only on that Terminal. Leaving the check with unsent lines asks for confirmation.
- A request that timed out is retried with the same idempotency key, so a send or payment that reached POS Edge is not duplicated ([below](#idempotency-and-duplicate-protection)).

### ÖKC unavailable

- If POS Edge cannot reach the ÖKC, or the ÖKC reports a problem (paper out, busy, needs a Z report), **Pay** is disabled with the reason. Opening checks, adding lines, sending to the kitchen and pre-bills continue.
- Transactions that were in flight when the connection dropped become [unknown](#7-unknown-ökc-outcome) and are checked on the ÖKC when it is back.
- Using the ÖKC on its own while Wasla cannot reach it is the restaurant's fallback; such sales are not in Wasla unless the [manual record](#unresolved-product-decisions) is kept.

### Edge machine failure

If the Edge machine fails, the restaurant cannot use POS until it is replaced and restored ([ADR](../architecture/pos-application-boundary.md#failure-behavior)):
- **Backups:** automatic backups every 15 minutes, and daily backups kept for 14 days.
- **Restore with cloud recovery.** Restoring a backup is always followed by a cloud recovery ([ADR](../architecture/pos-application-boundary.md#restore-and-recovery-from-the-cloud)).
  - It needs a one-time restore code from Wasla and a short online handshake.
  - It brings back, as local history, every sale, payment, kitchen ticket, receipt and audit record that was synced after the backup.
  - Local history and day totals are therefore complete up to the last synced record.
  - Nothing is uploaded twice. Numbers after a restore carry the epoch as a prefix.
- **What can be lost:**
  - sales committed but not yet synced that are in no surviving backup (the restore report names the last known record);
  - changes to open checks after the backup;
  - unsent Terminal drafts.
- **Terminals.** A replacement machine needs its Terminals re-paired. A restore on the same machine does not.
- **Fallback:** paper and the ÖKC used on its own.
- **Restore target:** within 2 hours with a spare machine.

### Idempotency and duplicate protection

- The Terminal creates the ids of checks, lines and payments (GUIDs) and sends an idempotency key with every write. A repeated request with the same key returns the first result and changes nothing.
- Recording a payment and closing a check are compare-and-set on the check's version: two terminals paying the same table cannot both succeed. The loser sees the check's current state, like Wasla Orders' single-winner order actions (WAS-104).
- A payment that would make the recorded total exceed the check total is refused, except cash, whose excess is recorded as change.
- **ÖKC transactions.** POS Edge creates the fiscal transaction id before sending. It sends one transaction at a time to a device and never a second one for a check while one is in flight or unknown. After a POS Edge restart, transactions that were sent but not finished become unknown and are checked on the ÖKC before anything else.
- A closed or cancelled check accepts no lines, payments or sends.
- A kitchen send sends only lines not yet sent; repeating it creates no second ticket for the same lines.
- **Uploads are idempotent too.** Each synced record has a unique id and a per-installation sequence, so the cloud stores it once however often it is sent.

### Auditability

- An append-only audit record for:
  - sign-in success, failure and lockout;
  - terminal pairing and revocation;
  - send to kitchen;
  - void request and approval;
  - check cancellation;
  - payment, payment reversal and sale cancellation;
  - every fiscal transaction state change, unknown-outcome checks and their resolution (with the approver and reason);
  - ÖKC pairing and connection changes;
  - document reprints;
  - menu price change;
  - staff and PIN changes;
  - licence and emergency-code events;
  - large clock changes;
  - backups, restores and updates.
- Each record has the UTC time, terminal, staff member (and approver when different), check, action, amounts before and after, and reason. No PINs, credentials, codes or secrets.
- Audit records are never updated or deleted, locally or in the cloud.

### Security and tenant isolation

- **One tenant per installation.** One POS Edge serves exactly one tenant and its database holds only that tenant's data. A terminal paired to one installation cannot act on another.
- **Credentials:**
  - Terminals present a device credential over HTTPS, and accept POS Edge only with a server certificate from their pinned installation CA.
  - Staff sign in with a PIN only on a paired terminal.
  - POS Edge authenticates to the cloud with its installation credential. The cloud never trusts a tenant id from a request without that credential.
- **PINs** are hashed with a local secret, rate limited per terminal and staff member, and locked after repeated failures.
- **Network exposure.** POS Edge accepts no unauthenticated requests apart from pairing with a valid one-time code and an anonymous health probe that shows no tenant data. It must not be exposed to the internet.
- POS follows the repository's secrets and logging rules ([AGENTS.md](../../AGENTS.md#8-secrets-credentials-and-logging)).

## Related documents

- [../architecture/pos-application-boundary.md](../architecture/pos-application-boundary.md): installed architecture, data ownership, sync, printing, devices, licence and repository decision
- [pos-first-slice.md](pos-first-slice.md): first vertical slice and backlog
- [product-boundaries.md](product-boundaries.md)
- [roles-and-permissions.md](roles-and-permissions.md): Wasla Orders roles (not POS roles)
- [../integrations/print-bridge.md](../integrations/print-bridge.md)
