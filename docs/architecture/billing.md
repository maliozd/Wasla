# Billing

## Purpose and scope

This document owns the provider-neutral billing boundary of Wasla Orders and its CentralDb persistence (WAS-147): which records exist, who owns them, which invariants the database enforces, and how the single iyzico adapter will plug in. Wasla bills its own SaaS fee to tenants; restaurant end-customer payments and Wasla POS payments are out of scope.

What exists today is the model, the store and the contracts. Nothing calls them yet: there is no iyzico client, hosted checkout, callback or webhook endpoint, entitlement enforcement or billing UI. Signup still ends in the Development payment simulator or a manual payment state, as [tenancy.md](tenancy.md#signup-and-checkout-access) describes. The follow-up work is listed under [Deferred work](#deferred-work).

## Schema ownership

CentralDb is the only store of Wasla subscription identity and SaaS billing state. Tenant databases hold restaurant operations only and receive no billing table, column or copy.

| Table | Owner of each row | Holds |
|-------|-------------------|-------|
| `BillingSubscriptions` | The `PendingRegistration` the checkout started from (required, unique), and after provisioning also the `Tenant` that registration became (unique when set) | One Wasla subscription: Wasla id, provider and environment, plan/period/price snapshot, state, provider references, concurrency revision |
| `BillingPayments` | Its `BillingSubscription` (required) | One provider-verified paid period: provider payment reference, period, charged amount and currency, verification time |
| `BillingPlanPrices` | Wasla (platform data, not a tenant's); each row records the Central Admin who set it | Wasla's authoritative price for one plan and billing period from an effective time: plan code, period, amount, currency, whether VAT is included. Immutable; a new price is a new row |

**Sources:** `src/Wasla.Domain/Entities/Central/BillingSubscription.cs`, `BillingPayment.cs`, `BillingPlanPrice.cs`, `src/Wasla.Domain/Billing/`, `src/Wasla.Infrastructure/Persistence/Central/Configurations/Billing*Configuration.cs`, `src/Wasla.Infrastructure/Services/BillingSubscriptionStore.cs`.

### What is reused and what is not

| Existing concept | Use |
|------------------|-----|
| `PendingRegistration` | The subscription's origin and owner before a tenant exists. The plan code and billing period are copied from it; checkout may start only while it is `AwaitingPayment` or `PaymentFailed` and not expired |
| `Tenant` | Linked once the registration is provisioned and names that tenant (`LinkTenantAsync`) |
| `SubscriptionStatus` | The subscription state, extended with `Pending` (6) and `ReconciliationRequired` (7). `None` and `Trialing` stay tenant-level values that a billing subscription never holds |
| `WaslaPlanCodes`, `Monthly`/`Yearly` | The plan and period vocabulary of signup, and the key of `BillingPlanPrices` |
| `WaslaPlanCatalog` | Plan names, features, order, recommendation and device limits only. It holds no self-service price; Enterprise keeps its contact-sales "starting from" label |
| `TenantMembership` | Not reused for billing. It requires a tenant (none exists during checkout), it is deleted with the tenant (billing evidence must survive) and it has no environment, provider references or price snapshot. It keeps its current role |
| `Tenant.SubscriptionStatus`, `Tenant.BillingPaymentStatus` | Unchanged. Provisioning sets `Trialing` and `Paid`; nothing reads them. Whether entitlement projects the billing state onto them is WAS-151 |

## States

| From | Allowed to |
|------|------------|
| `Pending` | `Active`, `Cancelled`, `Expired`, `ReconciliationRequired` |
| `Active` | `PastDue`, `Cancelled`, `Expired`, `ReconciliationRequired` |
| `PastDue` | `Active`, `Cancelled`, `Expired`, `ReconciliationRequired` |
| `Cancelled` | `Expired` |
| `ReconciliationRequired` | `Active`, `PastDue`, `Cancelled`, `Expired` |
| `Expired` | none (terminal) |

Source: `SubscriptionStatusTransitions`. Every other move, including any to `None`, `Trialing` or an undefined value, is refused and changes nothing.

- **Verified observations only.** A state comes from a server-side provider read or an authenticated provider event, with the time it was observed. Browser redirects and client values never set a state.
- **Ordering.** An observation older than the stored `StatusObservedAtUtc` is `Stale`. The same state seen later only moves that time forward, so a late older observation is still recognised. A different state at exactly the stored time is `Stale` too.
- **Provider reference.** `Active` and `PastDue` require a provider subscription reference (domain rule and check constraint).
- **Fail closed.** `IsVerifiedActive(now)` is true only when the subscription is `Active` **and** one of its verified payments covers `now`: a paid period is half-open, `[PeriodStartUtc, PeriodEndUtc)`. `Pending`, `PastDue`, `Cancelled`, `Expired` and `ReconciliationRequired` are false. A provider state the adapter cannot map is reported as `ReconciliationRequired`, which grants nothing and leaves only through a newer verified state. Grace periods and what each state means for login and features are WAS-151.
- **No single "current period".** Coverage is read from the payments, never from one stored period. So a paid period that starts in the future does not cover today, nor does one on the far side of an unpaid gap; and a later payment never hides an earlier period that still covers today (a period recorded out of order counts the same). `BillingSubscriptionView` carries every verified period that had not ended when it was read (`UnexpiredPaidPeriods`, `ReadAtUtc`), so a view evaluated a little later still sees an already-paid next period, and asking about a moment before `ReadAtUtc` is false.
- **Payments.** A verified payment must match the subscription's price snapshot and needs a provider reference; recording one does not change the subscription row. A payment reported late, even after expiry, is still recorded as evidence.
- **For WAS-151.** Entitlement asks `IsVerifiedActive` at the moment of the request (reading the view per request or within a short cache is fine: periods that had not ended are kept). Grace for `PastDue` or access until the end of a cancelled period are policy decisions WAS-151 adds on top, reading the same paid periods; they are not in this check.

## Plan prices

Two different mappings are involved, and only the first is Wasla's:

| Mapping | Owner | Where |
|---------|-------|-------|
| Wasla plan + billing period → amount + currency, from an effective time | Wasla; authoritative for what a checkout charges | CentralDb `BillingPlanPrices` (WAS-147) |
| Wasla price row → the provider's product and pricing-plan references, per provider environment | Provider configuration; created or looked up in iyzico | Keyed by `PlanPriceId` and `BillingEnvironment` (WAS-148; not stored by WAS-147) |

- **One rule, one source.** The price that applies to a plan and period at a moment is the row with the latest `EffectiveFromUtc` not after that moment (`BillingPlanPriceQueries`). Checkout, the checkout summary (`/checkout/review`), the public pricing section, the signup plan summary and the Central Admin page all use it, through `IBillingPlanPriceReader` for the public pages. No price lives in code or resources: monthly and yearly are separate rows, and a yearly price is never derived from a monthly one.
- **Customer-facing pages.** The marketing pricing cards and the signup summary show each self-service plan's monthly and yearly price in effect with its VAT choice, worded by one presenter (`PlanPriceText`); on the marketing cards a Monthly / Yearly tab control (monthly by default, and the only one shown without JavaScript) switches every card between the two server-rendered periods, so a card never shows the other period's price. A scheduled price appears only from its effective time. A plan or period without a price reads "price not set yet"; there is no fallback amount. A yearly saving is stated only when both prices exist with the same currency and VAT choice and the yearly price is below twelve monthly payments, and then with the exact amount. Enterprise keeps its contact-sales label and flow. If the prices cannot be read, the marketing page still renders and shows them as not set.
- **Checkout price.** `StartForRegistrationAsync` takes no price from its caller. It prices the subscription from the `BillingPlanPrices` row for the registration's plan and period with the latest `EffectiveFromUtc` not after now, and the subscription keeps that row's id (`PlanPriceId`) and amount and currency as its immutable snapshot. A later row prices only later checkouts; an existing subscription, including a repeated start for the same registration, keeps its price. The provider must charge exactly the snapshot (`RecordVerifiedPaymentAsync` refuses another amount).
- **No price, no checkout.** Without a row in effect the start is `PriceNotConfigured` and writes nothing, and `/checkout/review` says that the price has not been set instead of showing an amount. Signup itself is not blocked: registering and paying stay separate steps.
- **The summary shows what checkout charges.** `/checkout/review` uses `BillingPlanPriceQueries.ForRegistrationAsync`: once the registration's subscription has started, its stored snapshot (amount, currency, plan and period), with the VAT choice of the very row it was priced from (`PlanPriceId`); before that, the price in effect. A later price, even with another VAT choice, never changes what an existing subscription shows or is charged, which is also what a repeated `StartForRegistrationAsync` keeps. The summary and checkout read the same clock.
- **VAT.** Each row states whether its amount includes VAT (`VatIncluded`, a required choice). The amount charged is the stored amount either way; Wasla does not add or remove VAT. Tax treatment of the payment is WAS-153.
- **Changes.** Rows are immutable (`PropertySaveBehavior.Throw`), unique per plan, period and effective time, and cannot be deleted while a subscription references them. A price change is a new row with a later effective time.
- **Current state: no rows.** The migration creates the table empty and nothing seeds it, so every checkout start returns `PriceNotConfigured` until a Central Admin sets prices (see [Price decision required](#price-decision-required)). Nothing calls the start yet (WAS-149), so no current flow changes.

### Central Admin plan prices page

`/admin/billing/plan-prices` (`BillingPlanPricesController`, `IBillingPlanPriceAdminService`) is where prices are set. Like every Central Admin page it needs the Central Admin cookie, revalidated against CentralDb on every request ([authentication.md](authentication.md#central-admin-session-revalidation)); there are no Central Admin roles, so every active Central Admin may set prices. A tenant session never reaches it, and every POST validates the antiforgery token.

- **Shows** every plan that can be bought online (the self-service plans of `WaslaPlanCatalog`; never Enterprise) with each period: the price in effect, or "no price — checkout is blocked", and the next scheduled price. Below, every price with its state: in effect, scheduled or replaced.
- **Schedules** a price for a plan and period:
  - The amount is a whole number of kuruş, from 1 to 1,000,000,000. That cap is an input guard against typing errors, not a commercial limit. The amount is stored exactly as TRY with two decimals.
  - The VAT choice is required, with no default.
  - The date is a Türkiye calendar date, today or later. The price takes effect at 00:00 Türkiye time on that date (`BillingPlanPriceInput.EffectiveFromUtc`), so today's date takes effect immediately.
  - Every invalid input gets its own localized message and stores nothing, and a second price for the same plan, period and date is refused (also by the unique index when two admins save at once).
  - The row records the acting admin's id (`CreatedByCentralAdminUserId`), and the log line carries ids, plan, period and effective time.
- **Withdraws** a scheduled price that has not taken effect, in one conditional statement, so a price that has taken effect (and may be referenced) is never removed. A price in effect is replaced by scheduling a new one.

## Invariants the database enforces

These hold even for a writer that bypasses the application.

| Rule | Constraint |
|------|------------|
| One subscription per registration | `IX_BillingSubscriptions_PendingRegistrationId` (unique) |
| One subscription per tenant | `IX_BillingSubscriptions_TenantId` (unique, filtered `TenantId IS NOT NULL`) |
| A provider subscription reference names one Wasla subscription per provider and environment | `IX_BillingSubscriptions_ProviderReference` on `(Provider, Environment, ExternalSubscriptionReference)` (unique, filtered not null) |
| A provider payment reference is recorded once per provider and environment | `IX_BillingPayments_ProviderReference` on `(Provider, Environment, ExternalPaymentReference)` (unique) |
| One payment per subscription period start | `IX_BillingPayments_SubscriptionId_PeriodStartUtc` (unique) |
| A payment has its subscription's provider and environment | `FK_BillingPayments_BillingSubscriptions` on `(SubscriptionId, Provider, Environment)` to `AK_BillingSubscriptions_Id_Provider_Environment` |
| One plan price per plan, period and effective time | `IX_BillingPlanPrices_PlanCode_BillingPeriod_EffectiveFromUtc` (unique) |
| Billing states only; known provider and environment; Monthly or Yearly; positive amounts; periods end after they start; verified states have a provider reference | `CK_BillingSubscriptions_*`, `CK_BillingPayments_*`, `CK_BillingPlanPrices_*` |
| Billing history outlives the tenant row and pins its registration and its price row | `TenantId` FK `ON DELETE SET NULL`; `PendingRegistrationId`, `PlanPriceId` and payment FKs `RESTRICT` |

`delete-tenant` therefore keeps a deleted tenant's subscription and payments (tenant id cleared, registration owner kept). Nothing deletes registrations today.

Snapshot columns (registration, plan price, plan, period, amount, currency, created time), every payment column and every plan price column are marked `PropertySaveBehavior.Throw`: EF refuses to save a change to them. Provider and environment are part of the alternate key and cannot change either. Provider references and the tenant link are set once by the domain.

Sandbox and production identifiers never collide: the same reference string in both environments names two different rows, and every provider-driven write carries the `BillingProviderScope` (provider and environment) of the gateway that verified it. A write whose scope is not the record's returns `ScopeMismatch` and changes nothing.

## Idempotency and concurrency

`IBillingSubscriptionStore` never calls the provider. Every write is idempotent or ends in a deterministic `BillingStoreOutcome`:

| Command | Repeated | Contradicting |
|---------|----------|---------------|
| `StartForRegistrationAsync` | Same scope returns the existing id, with its original price, as `Unchanged` | Other scope: `Conflict` with the existing id; registration not awaiting payment or expired: `NotEligible`; no plan price in effect: `PriceNotConfigured` |
| `BindProviderReferencesAsync` | `Unchanged` | Other reference, or one held by another subscription: `Conflict` |
| `ApplyProviderStatusAsync` | `Unchanged` | Older: `Stale`; not allowed: `InvalidTransition` |
| `RecordVerifiedPaymentAsync` | Identical payment: `Unchanged` | Same reference with other data or on another subscription, a second payment for the same period start, a price other than the snapshot: `Conflict` |
| `LinkTenantAsync` | `Unchanged` | Registration not provisioned or naming another tenant: `NotEligible`; tenant already linked elsewhere: `Conflict` |

`Revision` is an optimistic-concurrency token raised by every change. A write that loses to a concurrent one (stale revision or a unique index) is evaluated again against the stored row, up to three times, and otherwise returns `ConcurrencyConflict` without writing. Other database errors are not reclassified. Retrying the same provider command (for example a second hosted checkout for one subscription) is the adapter's concern; `BillingCheckoutRequest.IdempotencyKey` is derived from the Wasla subscription for that purpose.

## Data minimization

Stored: Wasla ids, provider and environment, plan code, period, amount and currency, states and their times, opaque provider subscription, subscriber and payment references, verified paid-period boundaries per payment.

Not stored: card numbers, CVV, expiry, holder name or any card field (checkout and card update are provider-hosted pages), owner name, email, phone or address (they stay on the registration and are passed to the provider only inside `BillingSubscriberContact`), hosted-session tokens, provider request or response payloads, credentials and signatures. The store logs Wasla ids, operations and outcomes, never provider references, amounts or contact data. `BillingBoundaryTests` fails if a billing record gains a field named like card, contact, token, secret or payload data.

## Application boundary

| Contract | Role |
|----------|------|
| `IBillingGateway` (`src/Wasla.Application/Abstractions/Billing/`) | The provider as Wasla needs it: `StartCheckoutAsync`, `GetCheckoutResultAsync`, `GetSubscriptionAsync`, and the two management actions of the roadmap, `CancelSubscriptionAsync` and `StartCardUpdateAsync`. `Scope` names the provider account it is configured for |
| `IBillingSubscriptionStore` | CentralDb persistence above; registered in Infrastructure DI |
| `BillingPrice` (`src/Wasla.Domain/Billing/`) | Exact amount (positive, at most two decimals) and ISO 4217 code |

There is one implementation to come and no provider router, provider selection, plugin mechanism or second adapter. The domain and application assemblies reference no EF, Infrastructure or provider assembly, and every type in the billing contracts is a Wasla or BCL type (`BillingBoundaryTests`).

## How the iyzico adapter maps (WAS-148 onward)

The adapter lives in Infrastructure, implements `IBillingGateway`, and keeps every iyzico DTO, status string, endpoint and credential on its side. The provider's exact field names, status vocabulary and hosted-form shape are verified against the current iyzico documentation in WAS-148; the mapping below is the intent, not a verified contract.

| iyzico concept | Wasla |
|----------------|-------|
| Sandbox or live API base URL and keys | `BillingEnvironment` of the gateway's `Scope`; keys in secure configuration, never CentralDb |
| Product and pricing plan reference codes | Provider mapping of a Wasla price row: `BillingCheckoutRequest.PlanPriceId` per environment (WAS-148). The iyzico pricing plan's amount must equal that row's; a new Wasla price row needs its own pricing plan |
| Price of the selected pricing plan | Never the source: the amount comes from `BillingPlanPrices` and is frozen in the snapshot |
| Subscription checkout form initialize: token, form content or page | `BillingHostedSession` (`SessionToken`, `RedirectUrl` or `EmbeddedFormContent`, expiry). How the token correlates a callback to the subscription is WAS-149 |
| Checkout form result / subscription details | `BillingProviderSubscription`, then `BindProviderReferencesAsync`, `ApplyProviderStatusAsync`, `RecordVerifiedPaymentAsync` |
| Subscription reference code | `ExternalSubscriptionReference` |
| Reference of the paying subscriber | `ExternalSubscriberReference` (card update) |
| Payment or order reference of a paid period | `ExternalPaymentReference` |
| Subscription status | `SubscriptionStatus`; anything the adapter does not recognise is `ReconciliationRequired` |
| Cancel subscription, card update form | `CancelSubscriptionAsync`, `StartCardUpdateAsync` |

A second provider would add an enum value to `BillingProvider` (and its check constraint) and its own gateway; signup, provisioning and the subscription state machine stay as they are.

## Development payment simulation

The checkout simulator (`POST /checkout/simulate-success|simulate-failed/{id}`, WAS-26/WAS-39) is unchanged and stays Development-only (`CheckoutPaymentSimulator`). It changes only `PendingRegistrations` and writes no billing row. A registration it marked `PaymentSucceeded` can no longer start a billing subscription (`NotEligible`), so a simulated payment can never become, or be mistaken for, provider-verified billing state (`BillingSimulationIsolationTests`). Replacing it in the production flow is WAS-149.

## Migration and rollback

Migration `AddBillingSubscriptions` (CentralDb only; apply with `migrate-central`, see [../operations/migrations.md](../operations/migrations.md)):

- Additive: it creates the three tables with their keys, indexes and check constraints, and inserts no rows. No existing table, column or row changes, and existing tenants, registrations and the simulator behave as before.
- Order: like every CentralDb migration, apply it before the new Web, Api or Worker starts.
- Rollback: `Down` drops the three tables, so it refuses with error 50147 and changes nothing while any of them holds a row (plan prices included). Billing history must be exported and removed deliberately before a rollback; once real payments exist, roll forward instead.
- The application never applies it. It was verified on throwaway LocalDB databases only.

## Deferred work

| Issue | Left for it |
|-------|-------------|
| WAS-148 | iyzico Subscription API client implementing `IBillingGateway`, the provider product and pricing-plan references for each `BillingPlanPrices` row per environment, secure configuration |
| WAS-149 | Signup and checkout wiring: create the subscription, hosted checkout, callback verification, session-token correlation, provisioning and `LinkTenantAsync`, replacing the simulator in the production flow |
| WAS-150 | Webhook authenticity, a webhook inbox table for event idempotency (its key depends on iyzico's subscription event contract), reconciliation of state and payments |
| WAS-151 | Entitlement policy per state, grace, cancellation and card update actions, owner-only management |
| WAS-152 | Tenant billing page and Central Admin billing view, including a state history if needed |
| WAS-153 | Accounting ledger, refunds, chargebacks and invoice references |
| WAS-154 | End-to-end sandbox verification and the production rollout gate |

No outbox and no state-history table exist: no current flow needs them, and each later issue adds what its flow requires.

## Price decision required

No price is seeded and none lives in code. Until a Central Admin enters the decided prices on the plan prices page, every self-service plan and period shows "price not set yet" on the pricing section, signup and the checkout summary, and no checkout can start. The decision needed, from the product/commercial owner:

1. The charge amount for each self-service plan (Starter, Pro, Pro+) and each period (Monthly, Yearly), set separately. Before WAS-147 the public pages showed 300/600/1,200 TL monthly and a yearly price computed as 10 × monthly with a "2 months" note; those built-in amounts and the note are removed.
2. Whether each amount includes VAT, since the provider charges exactly the stored amount.
3. The date from which they apply.

Once entered, every customer-facing page shows them without a code change. The Development payment simulator still marks a registration paid without a price; it writes no billing row and charges nothing.

## Known discrepancies

- **Tenant status fields.** `Tenant.SubscriptionStatus` and `Tenant.BillingPaymentStatus` are written at provisioning (`Trialing`, `Paid`) and never read. They are not the billing source of truth.

## Tests

`tests/Wasla.UnitTests/Billing/`: state machine and fail-closed checks (`SubscriptionStatusTransitionTests`), the store over a SQLite CentralDb built from the model, including plan price resolution, tenant A/B isolation, sandbox/production separation, duplicates, repeated writes, immutable snapshots and a concurrent write between read and save (`BillingSubscriptionStoreTests`), mapping and migration shape (`BillingPersistenceModelTests`), boundaries and data minimization (`BillingBoundaryTests`), paid-period coverage including future periods, gaps and out-of-order payments (`BillingVerifiedPeriodTests`), plan price input rules, effective dates, checkout and withdrawal (`BillingPlanPriceAdminServiceTests`), the Central Admin page over real HTTP: access, antiforgery, every validation message, duplicates and withdrawal (`Admin/AdminBillingPlanPricesWebTests`), the rendered checkout summary (`Signup/CheckoutReviewPriceRenderedTests`), the public pricing section, signup and checkout summary following Central Admin prices, including future prices, separate monthly and yearly prices, missing prices in every culture and a started subscription keeping its price and VAT after a later price (`Signup/PublicPlanPricesRenderedTests`), the price wording and the yearly-saving rule (`Web/PlanPriceTextTests`, `Signup/signup-plan-price.test.js`), the simulator (`BillingSimulationIsolationTests`), and on a real SQL Server simultaneous writers, database constraints and the rollback guard (`BillingSqlServerTests`, `BillingMigrationSqlServerTests`; see [../operations/testing.md](../operations/testing.md#sql-server-concurrency-tests)).
