# Tenant Operations Center (central Admin)

## Purpose and scope

The Tenant Operations Center is the read-only operational view of the central Admin area for Wasla Orders. It answers which tenants exist, which are unhealthy, which registrations wait for payment or provisioning, and, for one selected tenant, whether its database, providers, orders and Print Bridge devices are working.

It owns the `/admin`, `/admin/customers` and `/admin/customers/{id}` pages. It does not own signup, provisioning, migrations, provider credentials or Print Bridge setup, and it is not Wasla POS.

## Pages

| Page | Reads | Notes |
|------|-------|-------|
| `/admin` overview | CentralDb only | Tenant counts, signup pipeline, Print Bridge fleet presence, paid registrations awaiting provisioning, recent tenants |
| `/admin/customers` list | CentralDb only | Search, filters, sort, server-side pagination |
| `/admin/customers/{id}` detail | CentralDb + that one tenant database | Identity, provisioning, database and migrations, mode and automation, providers, orders, Print Bridge, next steps |

The existing activate/deactivate posts on the detail page predate this feature and are unchanged. The feature adds no write endpoint.

## Data access boundaries

- **No fan-out.** The overview and the list never open a tenant database. A tenant whose database is down does not affect them.
- **Query budget (CentralDb).** Overview: four aggregate queries plus the attention list (five). List: count, page and per-page device counts (three), plus one existence check only when a filtered result is empty. Detail: four. Each request also makes the one `CentralAdminUsers` read of Central Admin session revalidation.
- **Selected-tenant reads only.** The detail page opens exactly one tenant database, resolved through `ITenantDbContextFactory` by the id of the CentralDb row just read. Nothing from the request (database name, connection string, sort field) reaches a query unvalidated.
- **Tenant read budget.** One explicit connection open, then thirteen read-only commands in total: the migration history (existence check and read), settings, guided setup, users, connections, recent sync failures, four order counts and two print-job counts. No tracking, no writes.
- **Timeouts.** The whole tenant read is bounded at 5 seconds (`TenantOperationalHealthOptions.Timeout`), with the same command timeout and a hard stop even if the driver ignores cancellation. The probe connection disables SqlClient connection retries and uses a connect timeout one second under the deadline, set on that context only; the stored connection string and normal application connections are unchanged.
- **Concurrency.** Concurrent requests for the same tenant share one in-flight read. Nothing is cached after it finishes. A caller that cancels stops waiting; the shared read ends at its own deadline.

## Health states

| Database state | Meaning |
|----------------|---------|
| Reachable | The database answered; sections were read |
| Not configured | The CentralDb row has no stored connection details |
| Unavailable (`Unreachable`) | Server or database could not be reached, including a missing database (SQL error 4060) |
| Timed out | The read did not finish within the deadline |
| Connection details unreadable | The stored value could not be decrypted or parsed (for example a different `ENCRYPTION_MASTER_KEY`) |
| Unknown (`Failed`) | Unexpected failure; see server logs |

Migrations compare the database's `__EFMigrationsHistory` with the migrations this build ships: Up to date, Pending (with count), Database newer than app, or Unknown. A section that cannot be read (typically because a pending migration has not created a column) is shown as unreadable while the rest of the page renders.

Provider connections: Healthy, Disabled, Sync off (tenant Order Sync off), Paused after repeated failures (circuit open), Failing (consecutive failures), Not synced yet, Sync overdue (no successful sync for more than 10 minutes; an Admin presentation threshold, not a Worker setting). Mock/Real is the process's `Platforms:ProviderMode` and the registered client per platform.

Print Bridge presence uses the canonical `PrintBridgeConnectionStatusCalculator`: Online when the last heartbeat is at most 60 seconds old, Stale up to 5 minutes, Offline after that; Never connected and Disabled are shown separately. Removed devices are excluded.

## Secret redaction

- Queries project only displayed columns. Encrypted connection strings, token hashes, password hashes, provider API keys and secrets, supplier ids, executor e-mails, IP addresses, installation ids and print payloads are never selected. The connection-details check returns a boolean computed in the database.
- `LastMigrationResult`, sync error messages and integration error messages contain raw exception text and are never shown; only their outcome (succeeded/failed/not recorded) is.
- Logs for failed health reads contain the tenant id, the state, the exception type name and the SQL error number. Never the message.
- Tests plant marker secrets in every sensitive column and assert they appear in no rendered page, response model or selected SQL.

## Authorization

The pages use the existing central-admin attribute `[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]`. Anonymous requests and tenant sessions (even with a forged role claim) are redirected to `/admin/login`. Unknown ids, registration ids and malformed ids return 404.

The Central Admin session is revalidated against CentralDb on every request. A deactivated, deleted or password-changed admin is signed out on their next request. See [Central admin session revalidation](../architecture/authentication.md#central-admin-session-revalidation).

Both CentralDb failure cases answer HTTP 503 and keep the session cookie:

- The localized load-error panel inside the Admin layout covers a failure of the pages' own CentralDb reads.
- If CentralDb cannot be read at all, the request already fails during session revalidation. The global error page then answers, with no Admin content.

## Troubleshooting

| Observation | Next step |
|-------------|-----------|
| Database unavailable or timed out | Check SQL Server, that the tenant database exists and accepts the configured login; run `migration-status` |
| Connection details unreadable | Check that this server uses the `ENCRYPTION_MASTER_KEY` that provisioned the tenant |
| Migrations pending | Apply with `migrate-customer` / `migrate-all-customers` following [deployment.md](deployment.md#release-order-migrate-every-database-first) |
| Sync overdue or never synced | Check that `Wasla.Worker` is running |
| Paused after repeated failures | Check the platform's status; the owner verifies the connection's credentials |
| Print Bridge offline with queued jobs | The restaurant checks the Print Bridge computer; jobs are picked up on reconnect |

The page only reads. It never migrates, repairs, retries, provisions or changes payment, provider or device state.

## Source map

| Concern | Location |
|---------|----------|
| Contracts | `src/Wasla.Application/Abstractions/Admin/TenantOperationsContracts.cs` |
| Rules (migrations, provider health, guidance) | `src/Wasla.Application/Admin/TenantOperationsRules.cs` |
| CentralDb reads | `src/Wasla.Infrastructure/Services/CentralAdminTenantOperationsService.cs` |
| Tenant health read | `src/Wasla.Infrastructure/Services/TenantOperationalHealthReader.cs` |
| Controllers and views | `src/Wasla.Web/Areas/Admin/` |
| Admin layout RTL corrections | `src/Wasla.Web/wwwroot/css/wasla-admin-layout.css` |
