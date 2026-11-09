# Order reporting

**Owns:** the order report metrics, the shared report filter (periods, platform, time zone, range policy), the daily and platform breakdowns, and which surfaces use them (WAS-182).

**Does not own:** order statuses and transitions ([lifecycle.md](./lifecycle.md)), order times ([lifecycle.md](./lifecycle.md#order-times-was-125)), roles ([../product/roles-and-permissions.md](../product/roles-and-permissions.md)). The report page is WAS-183 and the CSV export is WAS-184; both use this contract and add no calculation of their own.

---

## What a report is

A report is an **order-time cohort evaluated with the orders' current stored statuses**. It takes every stored order whose order time falls in the selected local days, and classifies each by the status it has when the report runs. It is not a financial ledger:

- An order placed yesterday and delivered today counts as yesterday's completed order, and only once the report runs after the delivery was stored.
- When an order's status changes later (an operator action or a sync), every later report, including reports of past days, counts it in its new status group. Past figures are therefore not frozen. (The sync merge never moves an order out of `Delivered`, `Cancelled` or `Failed`, see [lifecycle.md](./lifecycle.md#sync-status-merge).)
- Amounts are the provider's order totals (`Order.TotalAmount`), stored as sent. They are **order values, not revenue, settled income, profit, payout or net earnings**: Wasla stores no commissions, platform fees, refunds, partial cancellations or settlements, and the report adds or subtracts nothing (`DeliveryFee` and `ServiceFee` are not added or subtracted, because whether `TotalAmount` already includes them differs by provider and is not documented).

Reporting is deterministic: the same stored orders, filter and clock give the same figures. No language model computes or explains any figure.

## Metric definitions

Every figure comes from one SQL statement that groups the cohort by platform, stored status and local day (`OrderReportService`). Summary, platform rows and day rows are sums of those groups, so they always reconcile with each other.

| Figure | Formula | Statuses | Time | Empty cohort |
| --- | --- | --- | --- | --- |
| `OrderCount` | Number of stored orders in the cohort; each order is one row, so it counts once | Every status, including ones no longer used | `Order.OrderedAt` | 0 |
| `OrderValue` | Sum of `TotalAmount` | Every status | `OrderedAt` | 0.00 |
| `CompletedOrderCount` | Count | `Delivered` | `OrderedAt` (not `DeliveredAt`) | 0 |
| `CompletedOrderValue` | Sum of `TotalAmount` | `Delivered` | `OrderedAt` | 0.00 |
| `AverageCompletedOrderValue` | `CompletedOrderValue / CompletedOrderCount`, rounded to 2 decimals, midpoint away from zero | `Delivered` | `OrderedAt` | **null** when `CompletedOrderCount` is 0 (shown as no value, never 0) |
| `CancelledOrderCount` | Count | `Cancelled`. This includes rejections: Reject stores `Cancelled`, so the figure is "cancelled or rejected". The report does not split it yet (see [Known limits](#known-limits)) | `OrderedAt` (not `CancelledAt`) | 0 |
| `FailedOrderCount` | Count | `Failed` | `OrderedAt` | 0 |
| `InProgressOrderCount` | Count | `New`, `Preparing`, `ReadyForPickup`, `OnTheWay` | `OrderedAt` | 0 |
| `OtherStatusOrderCount` | Count | Any other stored value (the retired value 1, see [lifecycle.md](./lifecycle.md#acceptance-starts-preparation-was-185)) | `OrderedAt` | 0 |

`OrderCount = CompletedOrderCount + CancelledOrderCount + FailedOrderCount + InProgressOrderCount + OtherStatusOrderCount` in every summary, platform row and day row.

- **All-order value and completed-order value are separate figures.** `OrderValue` includes cancelled, failed and unfinished orders; `CompletedOrderValue` only delivered ones. No average is defined over all orders.
- **Order time** is the stored `OrderedAt` column, `COALESCE(CreatedAtPlatform, ReceivedAt)`: the provider's creation time, otherwise when Wasla first stored the order. A recovery import of yesterday's order at 02:19 counts yesterday. Import, update, acceptance, delivery and cancellation times are never used instead.
- **Currency** is `OrderCurrency.Code` (`TRY`). Orders store no currency; the UI culture formats amounts and never changes the currency.
- **Platform rows** list each platform with at least one order in the cohort, by `OrderCount` descending, then platform value ascending.
- **Day rows** list every local day of the period, oldest first, including days without orders (all counts 0, values 0.00, average null). Each row carries its UTC bounds.

### What is in the cohort

- **Each stored order once.** Orders are unique by platform and external order id (`IdempotencyKey`, [lifecycle.md](./lifecycle.md#idempotency-and-parent-row)). A repeated webhook or polling delivery updates the same row and never adds a second order.
- **Practice orders are not orders.** Guided setup's practice orders live in `GuidedDemoSessions` and never in `Orders`, so no report can see them. Orders that came through a Mock provider are ordinary stored orders and are counted: nothing stored marks them as test data.
- **One tenant.** The service reads the database of the tenant resolved from the signed-in session; the tenant id never comes from the request.
- **No order customer data.** Report results carry counts, amounts, platforms, dates and statuses only: no customer names, phones, addresses, notes, items or raw payloads.

## Filter

`OrderReportFilters.Resolve` (`src/Wasla.Application/Reports/OrderReportFilters.cs`) turns the request into a period and an optional platform, or into errors. Nothing is guessed or widened: an invalid filter returns errors and no report runs.

| Parameter | Values |
| --- | --- |
| `preset` | `today`, `yesterday`, `last7days`, `last30days`, `custom` (case-insensitive). Without `preset`: `custom` when a date is given, otherwise `today` |
| `startDate`, `endDate` | Local days, invariant Gregorian `yyyy-MM-dd` from 2000-01-01, as the Orders date filter. Required together for `custom`; refused with any other preset |
| `platform` | A defined platform name (`Yemeksepeti`, `GetirYemek`, `TrendyolYemek`, case-insensitive) or its number (1–3). Missing or empty means every platform |

"Empty" means no characters (`platform=`). A value of only spaces or tabs (`platform=%20`, `startDate=+`) is not empty and is refused with that parameter's error code; the Api binds these parameters as submitted (`FromQueryAsSubmittedAttribute`), because MVC's default string binding would turn such a value into a missing one.

| Preset | Local days |
| --- | --- |
| `today` | today |
| `yesterday` | the day before today |
| `last7days` | today and the 6 days before it |
| `last30days` | today and the 29 days before it |
| `custom` | `startDate` through `endDate`, both included |

The rolling presets include today, like the Orders page's default history range, so they are partial until the day ends.

Errors (`OrderReportFilterError`): `UnknownPreset`, `StartDateInvalid`, `EndDateInvalid`, `DatesRequireCustomPreset`, `CustomRangeIncomplete`, `RangeInverted`, `RangeTooLong`, `RangeInFuture`, `PlatformInvalid`, `DuplicateParameter` (API only: a parameter given twice).

### Time zone and day bounds

- Every tenant is treated as Türkiye (`TimeZoneHelper.ResolveTurkeyTimeZone()`, `Europe/Istanbul`) until tenants have a time zone setting ([lifecycle.md](./lifecycle.md#order-times-was-125)). The result names the zone as an IANA id.
- "Today" is the local day of the injected clock (`TimeProvider`) when the filter is resolved.
- Each local day's start (local midnight) is converted to UTC once. The cohort is `startUtcInclusive <= OrderedAt < endUtcExclusive`, where the end is the next local midnight after `endDate`.
- The grouping in SQL converts each order time to its local day with the zone's offset. When the period crosses an offset change (Türkiye has had none since 2016, other zones have daylight saving), the statement uses each stretch's own offset, and a day that is not 24 hours long is matched by its own UTC bounds, so no order changes day.

### Range policy

- At most **92 local days** (`OrderReportFilters.MaxRangeDays`, one quarter) per report, for the page and the export alike. A longer range is refused with `RangeTooLong`, never shortened.
- No day after today (`RangeInFuture`) and none before 2000-01-01 (`StartDateInvalid` / `EndDateInvalid`).
- The query returns at most one group per platform, status and day (3 × 8 × 92 rows today), whatever the order volume; no order row, item or payload is loaded.

### Partial periods and comparisons

`Period.IsComplete` is false when the period includes today. A report includes no comparison with a preceding period. A consumer that adds one compares only complete periods of equal length (for example yesterday with the day before, both resolved as `custom`), never a partial today with a full yesterday, and shows no percentage change against a zero baseline.

## Where it is used

| Surface | Uses | Notes |
| --- | --- | --- |
| Web Dashboard (`/dashboard`, `CanViewReports`) | `today` report for its figures | Today's orders = `OrderCount`; Today's order value = `OrderValue` (all statuses, cancellations included); Active = `InProgressOrderCount`; Cancelled = `CancelledOrderCount`; platform list = platform rows. Its average tile divides `OrderValue` by `OrderCount`, as before. Setup guidance and latest orders are unchanged |
| `GET /api/dashboard/summary`, `GET /api/dashboard/today` (`CanViewReports`) | The same `today` figures through `IDashboardService` | Response fields and meanings unchanged: `revenue` / `TodayRevenue` stay the all-order value (`OrderValue`), `pendingCount` is `InProgressOrderCount`. Platform rows are now ordered as above. Sync statistics and latest orders are unchanged |
| `GET /api/dashboard/report` (`CanViewReports`) | The full contract: filter in the query string, `OrderReportDto` out | Invalid filters answer 400 with the error codes by parameter. The data route WAS-183 and WAS-184 build on |

## Known limits

- Rejections are not yet counted apart from cancellations. Orders now store how they ended ([lifecycle.md](./lifecycle.md#how-an-order-ended-was-200), WAS-200), but no report figure reads it: that slice waits for the WAS-183 report page.
  - **Data boundary.** Origins and reasons exist only for orders that ended after the tenant migration `AddOrderCancellationOrigin` was applied and a release with it was running. Earlier cancelled orders stay unknown; nothing is inferred or backfilled. A report period that crosses that moment is partly unknown, and the production date is recorded when the release is deployed.
  - **For that slice.** Any split must keep `known rejected + known cancelled + unknown = CancelledOrderCount` in every summary, platform row and day row, and must count a null origin as unknown.
- Figures follow the current status, so a past day's figures can change when a later sync changes an order's status.
- No per-tenant time zone and no currency other than TRY.
- `DeliveryFee`, `ServiceFee`, payment method and payment status are not reported.
