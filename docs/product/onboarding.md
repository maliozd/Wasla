# Onboarding: guided setup and operational mode

**Owns:** who may use guided setup, per-user guided setup state, the tenant's operational mode (Setup / Live), how a tenant goes live, and the Development tenant reset's effect on both.

**Does not own:** signup, payment and provisioning (see [../operations/cli.md](../operations/cli.md) and [../architecture/tenancy.md](../architecture/tenancy.md)); what the Live Screen shows during order training ([../orders/live-screen.md](../orders/live-screen.md)); what synchronization does with orders in Setup ([../orders/synchronization.md](../orders/synchronization.md)).

---

## Two separate states

| State | Scope | Stored in | Values |
| --- | --- | --- | --- |
| Guided setup | One user | `UserGuidedSetupStates` (TenantDb), one row per user | NotStarted (no row), InProgress, Completed, Skipped |
| Operational mode | The tenant | `TenantOperationalSettings.OperationalMode` (TenantDb) | Setup, Live |

A user's guided setup is their own choice and position. It never records that a platform or Print Bridge is set up. The operational mode says whether the restaurant is operating yet. It is not derived from any user's row.

| Concern | Decided by | Effect |
| --- | --- | --- |
| Live Screen isolation during order training | The current user being in order training (per user) | Only that user's Live Screen hides real orders and shows their count ([../orders/live-screen.md](../orders/live-screen.md)) |
| Automatic acceptance and automatic receipts | The tenant's operational mode only | Suppressed while Setup ([../orders/synchronization.md](../orders/synchronization.md#operational-mode-setup)); shown as pending, not off ([below](#configured-and-effective-automation)) |

An Owner who trains in an already-live restaurant is isolated on their own Live Screen; the tenant stays Live, synchronization and automation keep running, and every other user sees and handles real orders as usual.

## Who may use guided setup

Guided setup and order training are Owner-only. The `TenantOwner` policy (Owner role only) guards every action of `GuidedSetupController` (Start, Skip, End, Continue, Advance, section Continue, section status, device-guide link, practice order, Complete) and of `GuidedDemoController` (the practice order and its actions). The same policy is exposed to the UI as `TenantNavigationPermissions.CanUseGuidedSetup`; without it the coordinator gives the user no sections, so there is no Dashboard card, section panel or training panel, and every coordinator command returns without writing. Manager, Kitchen, Cashier and Viewer keep their normal pages, including the Live Screen and Help, which stay under their existing policies. A guided-setup row a non-Owner created before this rule is left untouched and has no effect.

`TenantOperationalMode.Live` is stored as 0. The migration `AddTenantOperationalMode` added the column with default 0, so every existing tenant is Live. A tenant without a settings row also reads as Live, and so does a settings row that a settings page creates later for such a tenant. Only provisioning writes Setup: the signup provisioning operations and CLI `add-customer` insert the new tenant's settings row in Setup after creating the Owner. A repeated provisioning run keeps an existing row. CLI `reset-customer-db` / `reset-all-customer-dbs` recreate databases without that row, so those tenants read as Live.

## Transitions

| Command | User | Tenant |
| --- | --- | --- |
| Start | NotStarted → InProgress | Unchanged (stays Setup if it never went live) |
| Complete (order training, practice order delivered) | InProgress → Completed | Setup → Live |
| Skip (first use) | NotStarted → Skipped | Setup → Live |
| End (confirmed, started journey) | InProgress → Skipped | Setup → Live |
| Hide the guide / Escape / "set it up later" | Unchanged | Unchanged |

- Setup → Live happens in `GuidedSetupService`, in the same TenantDb transaction as the user's Completed or Skipped write: a user is never recorded as finished while the tenant stays in Setup. It is a conditional update (`WHERE OperationalMode = Setup`), so it is idempotent and concurrent finishes all succeed.
- No production flow moves a tenant from Live back to Setup. Once the tenant is Live, another Owner's Start, Skip, End or Complete only changes that Owner's own row.
- Only an Owner can take a Setup tenant live; a request from any other role is refused before it reaches guided-setup state.
- Skip, End and Complete open the normal Live Screen (`/orders/live-display`) when the user may view it, otherwise the user's home page. Platform readiness is not required.
- If no Owner ever decides, the tenant stays in Setup. In Setup, orders are still synchronized and visible, but nothing is accepted or printed automatically (see [../orders/synchronization.md](../orders/synchronization.md#operational-mode-setup)).

## Practice order countdown

After the Owner marks the practice order ready, it moves on by itself, like a real order moved by the platform's courier: ReadyForPickup → OnTheWay → Delivered, and then the delivered practice order leaves the Live Screen. Each of these three stages lasts `GuidedDemoTiming.StageDuration` (20 seconds), counted from when the practice order entered its status (its `UpdatedAt`, which every status change sets; for Delivered also `CompletedAtUtc`). No new column is needed.

- **Worker is authoritative.** Only `Wasla.Worker` moves Ready and OnTheWay, with a conditional update, so concurrent runs and Worker instances move it once. The delivered practice order leaves the Live Screen exactly at its deadline, when `/orders/live-data` stops including it. That is a read rule, not a status change.
- **On time, without a faster order sync.** The 15-second order-sync cycle is unchanged. For each active tenant it also runs `AdvanceDueAndPlanAsync`, which reads the open practice orders and updates only when a stage is due. It then hands the tenant's next deadline to `GuidedDemoScheduler`, a second hosted service in the Worker that keeps an in-memory list (`GuidedDemoSchedule`) and sleeps until the earliest entry. At the deadline it moves that tenant's practice order and plans the next stage.
  - Query load:
    - **Idle:** with no open practice order, the scheduler holds no timer and runs no query. The cycle's read is one statement per active tenant per cycle, through the filtered open-session index; it replaces the two update statements the cycle used to run.
    - **Active:** a tenant whose practice order still waits for the user's own steps (New, Accepted, Preparing) is read every `GuidedDemoTiming.UserStepCheckInterval` (5 s), so Mark ready is found well before its 20-second deadline. Each automatic deadline costs one read plus one conditional update. Only tenants with an open practice order are in the list, and each such order expires after two hours.
  - A Worker restart loses the list. Its first cycle advances any overdue stage by one step only (never straight to Delivered) and plans the rest again.
  - A tenant database error is logged and left to the next cycle, never retried in a loop.
- **Display only in the browser.** `/orders/live-data` puts `demoAutomation: { action, dueAtUtc, durationSeconds }` on the Owner's own practice order (null on every real order). The card's countdown (`orders-demo-countdown.js`) measures `dueAtUtc` against the snapshot's `serverTimeUtc`, so the browser's clock offset does not matter. A reload, a second tab or a background tab continues from the real remaining time, and a repeated or stale snapshot never restarts or lengthens a stage.
- **Seeing each stage start near 20 seconds.**
  - A successful Mark ready releases the Live Screen store's action guard, which asks for one snapshot at once, so Ready appears at 19–20 s.
  - 400 ms after a stage's deadline, the countdown asks the store for one read-only refresh (`requestRefresh`, the same GET the poll uses, coalesced with it, never two at once). While the stage has not moved it retries once a second, at most 8 times per stage, and only while the page is visible, the practice order is on the screen and the countdown is not disposed. The ordinary 10-second poll continues throughout.
  - **Worst-case delay (healthy Worker):** a transition is visible about 0.4 s + one round trip after the deadline. A Worker that is late by *d* seconds is seen within about 1 s of its change. Either way the new stage first shows 18–20 s.
  - **Stopped Worker:** the card stays at zero with "Platformdan güncelleme bekleniyor…" until a poll shows a change. The countdown never posts, changes a status or raises a new-order effect.
- **Training goes on.** When the delivered practice order leaves the screen, guided setup stays InProgress at Step 7 and "Eğitimi tamamla" still works: completion checks the persisted delivery (`GetLatestAsync`), not the screen. The practice order never appears in Orders or history.
- **Real orders are unaffected.** Real delivered orders stay on the Live Screen for `LiveScreenVisibility.RecentDeliveredWindow` (about two minutes).

## Print Bridge section: connected

The Print Bridge section panel (`_GuidedSetupSectionPanel`) shows its connected state only when the server confirms a connected device: on page load, or when its read-only `GET /guided-setup/section-status` check confirms it. The panel runs that check after the automatic flow's verified success (`wasla:print-bridge-setup-completed`), or repeatedly while a manual connection waits for the device's first heartbeat (`wasla:print-bridge-manual-setup-started`). The connected state also includes a panel fixed to the bottom of the viewport ("Print Bridge bağlandı" / "Cihazını tanı"), so a user far down the page sees how to continue. Its link is the same `GET /guided-setup/print-bridge/device` as the panel's own link (both carry `data-guided-setup-continue` and share one click guard). It has no close button or timer, never scrolls or navigates on its own, and moves nothing on: the section Continue stays in the device guide. It disappears with the panel when the section is no longer the Owner's current one. The panel's status line is the single polite announcement of the connection.

## Configured and effective automation

The mode never changes the saved order settings. Screens show both what is configured and what is effective (`TenantAutomationStatus`, the same rule the automation services apply):

| Saved setting | Tenant in Setup | Tenant Live |
| --- | --- | --- |
| Order sync on | Active (Setup does not stop ingestion) | Active |
| Auto-approve or automatic receipts off | Off | Off |
| Auto-approve or automatic receipts on | PendingSetup: “active once setup is complete” | Active |

Completing or skipping guided setup makes a PendingSetup automation Active by itself, with nothing to change in settings. It applies only to triggers after that moment (see [../orders/synchronization.md](../orders/synchronization.md#operational-mode-setup)). The Live Screen shows the effective states and refreshes them on every poll ([../orders/live-screen.md](../orders/live-screen.md)); the order and receipt settings pages show the saved controls with a pending note.

`ITenantOperationalModeService.GetAutomationStatusAsync` reads the mode and the saved flags from the settings row in one query and has no write. Its consumer is the Live Screen poll. The order-settings endpoints already read that row through `ITenantOrderSettingsService`, which returns the mode with the saved values, so they apply the same rule without a second query.

## Development tenant reset

The temporary Development-only tool (`POST /development-tools/tenant-reset`; Development environment, `DevelopmentTools:EnableTenantReset`, tenant Owner, typed slug) returns the whole test tenant to its post-provisioning state. It deletes every user's guided-setup row and replaces the settings row with the new-tenant row, so the tenant is in Setup again. It is the only operation outside provisioning that writes Setup, and it affects only the tenant it was run for. There is no per-user reset.

## Source map

| Concern | Location |
| --- | --- |
| Enum | `src/Wasla.Domain/Enums/TenantOperationalMode.cs` |
| Read service, seed and activation | `src/Wasla.Infrastructure/Services/TenantOperationalModeService.cs` |
| Configured versus effective rule | `src/Wasla.Application/Abstractions/Setup/TenantAutomationStatus.cs` |
| Activation with the user's change | `src/Wasla.Infrastructure/Services/GuidedSetupService.cs` |
| Commands, Owner-only plan, per-user isolation and redirects | `src/Wasla.Web/GuidedSetup/GuidedSetupCoordinator.cs` |
| Owner-only endpoints | `GuidedSetupController`, `GuidedDemoController` (`TenantPolicies.TenantOwner`) |
| Migration | `src/Wasla.Infrastructure/Persistence/Tenant/Migrations/*_AddTenantOperationalMode.cs` |
| Practice-order timing and Worker scheduling | `src/Wasla.Application/Demos/GuidedDemoTiming.cs`, `src/Wasla.Infrastructure/Services/GuidedDemoDeliverySimulator.cs`, `GuidedDemoSchedule.cs`, `GuidedDemoScheduler.cs` (hosted only by `src/Wasla.Worker/Program.cs`) |
| Practice-order countdown and deadline refresh | `src/Wasla.Web/wwwroot/js/orders/orders-demo-countdown.js` |
