# Order reporting

**Owns:** the order report metrics, the shared report filter (periods, platform, time zone, range policy), the daily and platform breakdowns, and which surfaces use them (WAS-182).

**Does not own:** order statuses and transitions ([lifecycle.md](./lifecycle.md)), order times ([lifecycle.md](./lifecycle.md#order-times-was-125)), roles ([../product/roles-and-permissions.md](../product/roles-and-permissions.md)). The report page is WAS-183 and its CSV, PDF and Excel export is WAS-184 ([Export](#export)); both use this contract and add no calculation of their own.

---

## What a report is

A report is an **order-time cohort evaluated with the orders' current stored statuses**. It takes every stored order whose order time falls in the selected local days, and classifies each by the status it has when the report runs. It is not a financial ledger:

- An order placed yesterday and delivered today counts as yesterday's completed order, and only once the report runs after the delivery was stored.
- When an order's status changes later (an operator action or a sync), every later report, including reports of past days, counts it in its new status group. Past figures are therefore not frozen. (The sync merge never moves an order out of `Delivered`, `Cancelled` or `Failed`, see [lifecycle.md](./lifecycle.md#sync-status-merge).)
- Amounts are the provider's order totals (`Order.TotalAmount`), stored as sent. They are **order values, not revenue, settled income, profit, payout or net earnings**: Wasla stores no commissions, platform fees, refunds, partial cancellations or settlements, and the report adds or subtracts nothing (`DeliveryFee` and `ServiceFee` are not added or subtracted, because whether `TotalAmount` already includes them differs by provider and is not documented).

Reporting is deterministic: the same stored orders, filter and clock give the same figures. No language model computes or explains any figure.

## Metric definitions

Every figure comes from one SQL statement that groups the cohort by platform, stored status, local day and how a cancelled order ended (origin and reason code) (`OrderReportService`). Summary, platform rows, day rows, the days per platform and the reason rows are sums of those groups, so they always reconcile with each other.

| Figure | Formula | Statuses | Time | Empty cohort |
| --- | --- | --- | --- | --- |
| `OrderCount` | Number of stored orders in the cohort; each order is one row, so it counts once | Every status, including ones no longer used | `Order.OrderedAt` | 0 |
| `OrderValue` | Sum of `TotalAmount` | Every status | `OrderedAt` | 0.00 |
| `CompletedOrderCount` | Count | `Delivered` | `OrderedAt` (not `DeliveredAt`) | 0 |
| `CompletedOrderValue` | Sum of `TotalAmount` | `Delivered` | `OrderedAt` | 0.00 |
| `AverageCompletedOrderValue` | `CompletedOrderValue / CompletedOrderCount`, rounded to 2 decimals, midpoint away from zero | `Delivered` | `OrderedAt` | **null** when `CompletedOrderCount` is 0 (shown as no value, never 0) |
| `CancelledOrderCount` | Count | `Cancelled`. This includes rejections: Reject stores `Cancelled`, so the figure is "cancelled or rejected". [How these orders ended](#how-cancelled-orders-ended-was-200) splits it as far as Wasla knows | `OrderedAt` (not `CancelledAt`) | 0 |
| `FailedOrderCount` | Count | `Failed` | `OrderedAt` | 0 |
| `InProgressOrderCount` | Count | `New`, `Preparing`, `ReadyForPickup`, `OnTheWay` | `OrderedAt` | 0 |
| `OtherStatusOrderCount` | Count | Any other stored value (the retired value 1, see [lifecycle.md](./lifecycle.md#acceptance-starts-preparation-was-185)) | `OrderedAt` | 0 |
| `CancellationRate` | `CancelledOrderCount / OrderCount`, from 0 to 1 | Numerator `Cancelled` (cancelled or rejected); denominator every status, orders still in progress included | `OrderedAt` | **null** when `OrderCount` is 0 (shown as no value, never 0 %) |

`OrderCount = CompletedOrderCount + CancelledOrderCount + FailedOrderCount + InProgressOrderCount + OtherStatusOrderCount` in every summary, platform row and day row.

- **All-order value and completed-order value are separate figures.** `OrderValue` includes cancelled, failed and unfinished orders; `CompletedOrderValue` only delivered ones. No average is defined over all orders.
- **Order time** is the stored `OrderedAt` column, `COALESCE(CreatedAtPlatform, ReceivedAt)`: the provider's creation time, otherwise when Wasla first stored the order. A recovery import of yesterday's order at 02:19 counts yesterday. Import, update, acceptance, delivery and cancellation times are never used instead.
- **Currency** is `OrderCurrency.Code` (`TRY`). Orders store no currency; the UI culture formats amounts and never changes the currency.
- **Platform rows** list each platform with at least one order in the cohort, by `OrderCount` descending, then platform value ascending.
- **Day rows** list every local day of the period, oldest first, including days without orders (all counts 0, values 0.00, average null). Each row carries its UTC bounds.
- **Days per platform** (`OrderReport.PlatformDays`) repeat the day rows for each platform row, in the same order and from the same groups. A platform's days sum to its platform row, and each day summed over the platforms is that day's row. They are part of the shared result, not of `OrderReportDto`.

### How cancelled orders ended (WAS-200)

Each cancelled or rejected order falls into exactly one group, read from its stored origin ([lifecycle.md](./lifecycle.md#how-an-order-ended-was-200)). In every summary, platform row, day row and day per platform:

`KnownRejectedOrderCount + KnownCancelledOrderCount + UnknownOutcomeOrderCount = CancelledOrderCount`

| Figure | Stored origin | What it says |
| --- | --- | --- |
| `KnownRejectedOrderCount` | `OperatorRejected` | Rejected through Wasla |
| `KnownCancelledOrderCount` | `OperatorCancelled`, `ProviderOrCustomer` | Cancelled through Wasla (nothing writes this yet), or reported by the provider as not coming from the restaurant. It does not say the order customer did it |
| `UnknownOutcomeOrderCount` | none, `RestaurantOnPlatform`, or a stored number with no name | Wasla cannot tell a rejection from a cancellation |
| `RestaurantOriginUnknownOutcomeOrderCount` | `RestaurantOnPlatform` | The part of the unknown count whose **origin** is known to be the restaurant. A known origin is not always a known outcome: the restaurant may have rejected or cancelled it |

A reason describing a cause, such as a customer request or a courier problem, moves an order into no group and never says who pressed cancel.

### Reasons and coverage (WAS-200)

`OrderReport.CancellationReasons` (`OrderReportDto.CancellationReasons`) has one row per platform and stored code, with its order count, ordered by count descending, then platform and code. A code belongs to its platform's own list, so codes of different platforms are never added together. `OperatorRejected` rows hold the code the operator chose and other rows the code the provider reported; both count in the same row of that platform and code.

`CancellationReasonMissingOrderCount` counts cancelled or rejected orders with no stored reason, in every breakdown. For a summary and for each platform:

`sum of its reason rows + CancellationReasonMissingOrderCount = CancelledOrderCount`

Reason rows exist for the whole report and per platform, not per day; the missing count exists per day too.

| Figure | Numerator | Denominator | Empty |
| --- | --- | --- | --- |
| Share of a split group (page) | The group's count | `CancelledOrderCount` of the same breakdown | no value |
| Reason share (page) | The reason's count | **That platform's** `CancelledOrderCount`, orders without a reason included. With the no-reason row, a platform's rows add up to 100% | no value |
| No-reason share (page) | The platform's `CancellationReasonMissingOrderCount` | That platform's `CancelledOrderCount` | no value |
| `CancellationReasonCoverage` | `CancelledOrderCount − CancellationReasonMissingOrderCount` | `CancelledOrderCount` of the same breakdown | **null** when none was cancelled or rejected |

`CancellationRate` keeps its meaning: the combined count over every order. A missing reason does not mean the order had none. Coverage says what Wasla knows; it is not how often orders end with a reason.

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
- The query returns at most one group per platform, status, day, origin and reason, whatever the order volume; no order row, item or payload is loaded. Only `Cancelled` orders can have an origin or reason (`CK_Orders_CancellationMetadata`), so the other statuses keep at most one group per platform and day, and a day's cancelled groups are limited by the origins and the platform's documented codes. 60,000 synthetic orders over 92 days gave 2,322 groups against 552 without the split, with the same plan (clustered scan and hash aggregate on LocalDB), so the split needs no index. That is one measurement on a developer machine, not a capacity claim.

### Partial periods and comparisons

`Period.IsComplete` is false when the period includes today. A report includes no comparison with a preceding period. A consumer that adds one compares only complete periods of equal length (for example yesterday with the day before, both resolved as `custom`), never a partial today with a full yesterday, and shows no percentage change against a zero baseline.

`OrderReportFilters.PrecedingPeriod` gives that period: the same number of days ending the day before, the same platform, resolved by the normal rules. It returns null for a period that includes today, or when the preceding period would start before 2000-01-01.

## Where it is used

| Surface | Uses | Notes |
| --- | --- | --- |
| Web Dashboard (`/dashboard`, `CanViewReports`) | `today` report for its figures | Today's orders = `OrderCount`; Today's order value = `OrderValue` (all statuses, cancellations included); Active = `InProgressOrderCount`; Cancelled = `CancelledOrderCount`; platform list = platform rows. Its average tile divides `OrderValue` by `OrderCount`, as before. Setup guidance and latest orders are unchanged |
| `GET /api/dashboard/summary`, `GET /api/dashboard/today` (`CanViewReports`) | The same `today` figures through `IDashboardService` | Response fields and meanings unchanged: `revenue` / `TodayRevenue` stay the all-order value (`OrderValue`), `pendingCount` is `InProgressOrderCount`. Platform rows are now ordered as above. Sync statistics and latest orders are unchanged |
| `GET /api/dashboard/report` (`CanViewReports`) | The full contract: filter in the query string, `OrderReportDto` out | Invalid filters answer 400 with the error codes by parameter. The data route WAS-184 builds on. WAS-200 only added fields: the split and missing-reason counts in every figures object, and `CancellationReasons` |
| Web report page (`/reports`, `/reports/content`, `CanViewReports`, WAS-183) | `IOrderReportService` directly, with the same filter rules | See [Report page](#report-page) |
| Web report export (`/reports/export`, `CanViewReports`, WAS-184) | One read of `IOrderReportService` per file, with the page's filter rules and default | See [Export](#export) |

## Report page

`/reports` shows Overview, Platforms and Cancellations. `ReportsController` resolves the filter with `OrderReportFilters` and reads `IOrderReportService`; the views scale and format those figures and calculate none of their own.

- **Filter state** is in the query string with the API's parameter names, plus `compare=1` and `section` (`overview`, `platforms`, `cancellations`). The page's default period is the last 30 days, because the Dashboard already covers today. Without a preset or dates the shared contract still means today; only this page passes `last30days`.
- **Canonical form.** Every accepted form of a filter (a preset in any letter case, a platform by name in any letter case or by number) is written back in one form: lowercase preset, platform name, dates only for `custom`, defaults left out. The form comes from the filter `OrderReportFilters` accepted. Each answer carries it (`data-query`), and the browser takes its URL, links and controls from it, so it decides nothing about validity itself.
- **Refused filters** answer 400 and show the state without running a report. A duplicate parameter is refused as on the API. A refused filter is never tidied: its values stay as submitted, repeated ones included, in the URL and in every link that keeps the filter, and it is not remembered for the next visit.
- **Platforms** takes the platform rows and the daily chart per platform from the same report as the summary (`PlatformDays`), so they cannot disagree. One platform filter shows that platform and a link to all.
- **Chart axes** only scale the bars and label the gridlines. They are calculated in decimal, so any stored amount fits, and every figure and tooltip keeps its exact value. An axis widens when its labels need it, up to 45% of the chart.
- **Cancellations** shows `CancelledOrderCount` and `CancellationRate`, labelled "cancelled or rejected", then, from the same report, how those orders ended (the three groups, the restaurant-origin and no-origin parts of the unknown group, and one row per platform when more than one has any), the reasons per platform with their codes and a no-reason row, and the daily chart and table with the split. When any order lacks a classification or reason, a notice says older records may have none and that missing information does not mean it did not happen. With no cancelled or rejected order the split and reasons are replaced by one statement. Labels come from `ProviderCancellationReasons.LabelKey`: a code an operator can also choose keeps its existing label, and a stored code without a label is shown by its number.
- **Comparison** is on Overview only. It reads the report of `PrecedingPeriod` and shows both periods with their dates, the difference and the percentage change. A change against a previous value of 0, or where either average does not exist, is shown as not comparable. A period that includes today is not compared: the page offers the same number of complete days ending yesterday.

Busy hours (WAS-197), products (WAS-198) and durations (WAS-202) are not on the page.

## Export

The page header's **Export** menu (WAS-184) downloads the report the page shows as **CSV**, **PDF** or **Excel (.xlsx)**. `ReportsController.Export` (`GET /reports/export?format=csv|pdf|xlsx` with the page's filter parameters) resolves the filter exactly as the page does, page default (last 30 days) included, reads `IOrderReportService` once, and builds one `ReportExportDocument` (`src/Wasla.Web/Reports/Export`) that the three writers only format. Section and comparison do not apply to a file.

**What a file holds.** The summary, the platform rows and the day rows of the report, each with the same ten figures in this order: order count, delivered, in progress, cancelled or rejected, failed, other, cancellation or rejection rate, completed order value, average completed order value, all order value. Amount headers name the currency (`TRY`). Every value is a field of the report (the averages and rates are the report's own properties), so the file's summary equals the page's, each platform's and each day's rows add up to it, and status counts add up to the order count. Also in every file: the restaurant (the tenant's name), the period selected, first and last day, day count, complete or partial period, platform filter, currency, time zone (IANA id), the generation time (the report's, in that zone), a definition of every figure, and notes: no orders, a partial period, values are platform order totals and not payments received, settled revenue, profit or payouts, local days and order time, current statuses, cancelled and rejected counted together, undefined figures have no value, practice orders are not orders. How cancelled orders ended, reason codes, the days per platform, busy hours, products and durations are not exported (WAS-197, WAS-198, WAS-200, WAS-202).

**No order customer data, no other order detail.** The report holds counts, amounts, platforms, statuses and days only, so no file can contain a customer name, phone, address, credential or item.

### CSV format

- UTF-8 with a byte order mark (so spreadsheet programs read Turkish, Arabic and Russian), CRLF line ends, comma delimiter, RFC 4180 quoting: a field with a comma, double quote or line break is quoted and its quotes doubled.
- Sections in order, separated by an empty line: the title line and the details (`label,value`), `Summary` (`measure,value,definition`), the platform rows, the day rows (oldest first, every local day), then the notes. Section titles, column headers and labels are in the user's language; record lengths differ between sections.
- Values never follow the culture: counts are integers, amounts have two decimals with a dot and no grouping (`2645.05`), a rate is a fraction from 0 to 1 with four decimals (`0.0909`), days are `yyyy-MM-dd` (Gregorian), the generation time is ISO 8601 with its offset (`2026-11-10T14:10:05+03:00`). A figure that is not defined is an empty field, never `0`.
- Text that starts with `=`, `+`, `-`, `@`, a tab or a line break (or the full-width `＝ ＋ － ＠`), after leading spaces, gets a leading apostrophe so no spreadsheet runs it as a formula (OWASP CSV injection guidance). Numbers are written as numbers and never prefixed. Control characters in text (line breaks included) become spaces.
- A spreadsheet program in a culture whose list separator is `;` (Turkish Excel, for example) may not split a comma CSV when the file is opened by double-click; the Excel export is the file for spreadsheets, the CSV for data tools.

### Excel workbook

An Office Open XML workbook written with the Open XML SDK (`ReportXlsxWriter`): sheets **Summary**, **Platforms** and **Daily**, named in the user's language, right to left in Arabic. Counts, amounts and rates are number cells with number formats (`#,##0`, `#,##0.00`, `0.0%`), days and the generation time date cells (`yyyy-mm-dd`, `yyyy-mm-dd hh:mm`, the time zone is a detail row). Every label is an inline string cell in Text format (`@`): there are no formulas, shared strings, macros, external links or hyperlinks. Data sheets have a frozen header row and first column and a filter. Amounts are stored from the exact decimal, but spreadsheet programs hold numbers as doubles, so an amount beyond 15 significant digits is rounded there; the CSV keeps every digit.

### PDF

`ReportPdfWriter` draws an A4 landscape document with QuestPDF in the page's light colours: a header with the title, restaurant, period and platform on every page; the details and notices; the four headline figures; the Overview's daily orders and completed value charts, from the page's own chart model (`ReportPresenter`: the same columns, stacked segments, partial day, axis and day labels), when the period has two days or more; the platform share when every platform is shown; then the summary with definitions, the platform table, the daily table and the notes. Tables repeat their header row on every page they continue on, long labels wrap instead of being cut, and every page has its number ("Page 2 of 6") and the generation time. Arabic is laid out right to left.

- Fonts are bundled, never taken from the server: QuestPDF's Lato for Latin, Turkish and Cyrillic, and Noto Sans Arabic (Regular and Bold, SIL Open Font License 1.1, `src/Wasla.Web/Reports/Export/Fonts`) as the fallback for Arabic. System fonts are off. A character no bundled font has (an emoji in a restaurant name) is drawn as a placeholder instead of failing the export.
- Text is shaped by the library (HarfBuzz) and written as real text: Latin, Turkish and Russian text and every figure can be searched and copied. Arabic is shaped and readable, but Noto Sans Arabic draws dotted letters as a dotless form plus separate dots, which the PDF's character map cannot name, so searching or copying Arabic words with dotted letters is unreliable.
- Report text is only ever text: no markup is parsed, no image, link, script or font is loaded from outside the assembly, and the file has no annotations, actions or forms.

### Requests, limits and failures

- The tenant comes from the session, the role is the page's (`CanViewReports`); the menu is only a shortcut. A refused filter (the [filter](#filter) errors, a repeated parameter, a missing or unknown `format`) answers 400 with its reason as short localized text and runs no report; another role is sent to access denied and a signed-out request to the login page.
- A period has at most 92 days and the report returns grouped figures only, so a file holds at most 92 day rows and three platform rows. One export runs at a time per signed-in user of a tenant and at most four per Web process (`ReportExportGate`); another answers 429 with `Retry-After` at once. The report read must finish within 60 seconds and stops when the browser abandons the request; writing the file is not interruptible, and its work is bounded by those at most 92 day rows. Any failure answers 500 with a short localized text and logs only the format and exception type.
- Files are attachments with ASCII names from the accepted filter (`wasla-orders-report_2026-10-12_2026-11-10[_platform].csv|pdf|xlsx`); every answer of the export action, file or not, is `Cache-Control: no-store` and `X-Content-Type-Options: nosniff` (the redirects to sign in or access denied come from the authorization pipeline before it).
- The page keeps the menu disabled until a report has loaded for an accepted filter, shows "Preparing the PDF file…" while the file is made, then that it was created or why it failed, and does not start a second export while one runs. A lost session reloads the page.

### Dependencies

- **QuestPDF** 2026.9.1 (Web): PDF layout and text shaping, with native Skia libraries for each platform (about 11 MB for `linux-x64` in the release). It is used under the **QuestPDF Community License** (version 3.0, 6 July 2026), which is free only for organisations under USD 1 million annual gross revenue that are not publicly traded or government bodies; otherwise a paid licence is required. It is not open source. `ReportPdfWriter` declares `LicenseType.Community`; whoever runs Wasla confirms that eligibility.
- **DocumentFormat.OpenXml** 3.5.1 (Web, MIT): the Excel workbook.
- **PdfPig** 0.1.16 (tests only, Apache-2.0): reads the PDFs back in the tests.

## Known limits

- **Data boundary of the split and reasons.** Origins and reasons exist only for orders that ended after the tenant migration `AddOrderCancellationOrigin` was applied and a release with it was running. Earlier cancelled orders stay unknown; nothing is inferred or backfilled. A report period that crosses that moment is partly unknown. The production date is not known yet and is recorded here when the release is deployed.
- **Reasons are not live-verified.** Provider codes are documented for Trendyol GO Meal but have not been seen on real orders, so a real package may leave them empty ([lifecycle.md](./lifecycle.md#trendyol-go-meal-provider-reasons-what-is-documented-and-what-is-not)). Only Trendyol GO has a list; other platforms show every order as having no reason.
- Figures follow the current status, so a past day's figures can change when a later sync changes an order's status.
- No per-tenant time zone and no currency other than TRY.
- `DeliveryFee`, `ServiceFee`, payment method and payment status are not reported.
