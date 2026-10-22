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

**Sources:** `src/Wasla.Domain/Entities/Central/BillingSubscription.cs`, `BillingPayment.cs`, `src/Wasla.Domain/Billing/`, `src/Wasla.Infrastructure/Persistence/Central/Configurations/Billing*Configuration.cs`, `src/Wasla.Infrastructure/Services/BillingSubscriptionStore.cs`.

### What is reused and what is not

| Existing concept | Use |
|------------------|-----|
| `PendingRegistration` | The subscription's origin and owner before a tenant exists. The plan code and billing period are copied from it; checkout may start only while it is `AwaitingPayment` or `PaymentFailed` and not expired |
| `Tenant` | Linked once the registration is provisioned and names that tenant (`LinkTenantAsync`) |
| `SubscriptionStatus` | The subscription state, extended with `Pending` (6) and `ReconciliationRequired` (7). `None` and `Trialing` stay tenant-level values that a billing subscription never holds |
| `WaslaPlanCodes`, `Monthly`/`Yearly` | The plan and period vocabulary of signup |
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
| Billing states only; known provider and environment; Monthly or Yearly; positive amounts; periods end after they start; verified states have a provider reference | `CK_BillingSubscriptions_*`, `CK_BillingPayments_*` |
| Billing history outlives the tenant row and pins its registration | `TenantId` FK `ON DELETE SET NULL`; `PendingRegistrationId` and payment FKs `RESTRICT` |

`delete-tenant` therefore keeps a deleted tenant's subscription and payments (tenant id cleared, registration owner kept). Nothing deletes registrations today.

Snapshot columns (registration, plan, period, amount, currency, created time) and every payment column are marked `PropertySaveBehavior.Throw`: EF refuses to save a change to them. Provider and environment are part of the alternate key and cannot change either. Provider references and the tenant link are set once by the domain.

Sandbox and production identifiers never collide: the same reference string in both environments names two different rows, and every provider-driven write carries the `BillingProviderScope` (provider and environment) of the gateway that verified it. A write whose scope is not the record's returns `ScopeMismatch` and changes nothing.

## Idempotency and concurrency

`IBillingSubscriptionStore` never calls the provider. Every write is idempotent or ends in a deterministic `BillingStoreOutcome`:

| Command | Repeated | Contradicting |
|---------|----------|---------------|
| `StartForRegistrationAsync` | Same scope and price returns the existing id as `Unchanged` | Other scope or price: `Conflict` with the existing id; registration not awaiting payment or expired: `NotEligible` |
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
| Product and pricing plan reference codes | Adapter configuration keyed by `PlanCode` and `BillingPeriod`, per environment (WAS-148). Not stored per subscription |
| Price of the selected pricing plan | `BillingPrice` passed to `StartForRegistrationAsync` and frozen in the snapshot |
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

- Additive: it creates the two tables with their keys, indexes and check constraints. No existing table, column or row changes, and existing tenants, registrations and the simulator behave as before.
- Order: like every CentralDb migration, apply it before the new Web, Api or Worker starts.
- Rollback: `Down` drops both tables, so it refuses with error 50147 and changes nothing while either table holds a row. Billing history must be exported and removed deliberately before a rollback; once real payments exist, roll forward instead.
- The application never applies it. It was verified on throwaway LocalDB databases only.

## Deferred work

| Issue | Left for it |
|-------|-------------|
| WAS-148 | iyzico Subscription API client implementing `IBillingGateway`, plan and pricing-plan mapping per environment, which price is authoritative, secure configuration |
| WAS-149 | Signup and checkout wiring: create the subscription, hosted checkout, callback verification, session-token correlation, provisioning and `LinkTenantAsync`, replacing the simulator in the production flow |
| WAS-150 | Webhook authenticity, a webhook inbox table for event idempotency (its key depends on iyzico's subscription event contract), reconciliation of state and payments |
| WAS-151 | Entitlement policy per state, grace, cancellation and card update actions, owner-only management |
| WAS-152 | Tenant billing page and Central Admin billing view, including a state history if needed |
| WAS-153 | Accounting ledger, refunds, chargebacks and invoice references |
| WAS-154 | End-to-end sandbox verification and the production rollout gate |

No outbox and no state-history table exist: no current flow needs them, and each later issue adds what its flow requires.

## Known discrepancies

- **Plan prices.** The public plan catalog (`WaslaPlanCatalog`: Starter 300, Pro 600, Pro+ 1200 TRY per month) and the simulated checkout pricing (`CheckoutSimulatedPricing`: 499, 999, 1499 TRY, yearly ×10) disagree. WAS-147 does not choose; the price is a parameter of `StartForRegistrationAsync`, decided by WAS-148's plan mapping.
- **Tenant status fields.** `Tenant.SubscriptionStatus` and `Tenant.BillingPaymentStatus` are written at provisioning (`Trialing`, `Paid`) and never read. They are not the billing source of truth.

## Tests

`tests/Wasla.UnitTests/Billing/`: state machine and fail-closed checks (`SubscriptionStatusTransitionTests`), the store over a SQLite CentralDb built from the model, including tenant A/B isolation, sandbox/production separation, duplicates, repeated writes, immutable snapshots and a concurrent write between read and save (`BillingSubscriptionStoreTests`), mapping and migration shape (`BillingPersistenceModelTests`), boundaries and data minimization (`BillingBoundaryTests`), the simulator (`BillingSimulationIsolationTests`), and on a real SQL Server simultaneous writers, database constraints and the rollback guard (`BillingSqlServerTests`, `BillingMigrationSqlServerTests`; see [../operations/testing.md](../operations/testing.md#sql-server-concurrency-tests)).
