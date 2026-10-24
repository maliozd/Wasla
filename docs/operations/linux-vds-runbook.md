# Linux VDS runbook

## Purpose and scope

This runbook installs, updates, backs up and rolls back Wasla Orders on one Ubuntu 22.04 server behind Nginx (WAS-76, WAS-77, WAS-78, first local part of WAS-79). The files it uses live in `deploy/linux/`. Configuration keys and their meaning are in [deployment.md](deployment.md); migrations and the release gate in [migrations.md](migrations.md); CLI commands in [cli.md](cli.md).

It holds no secret value, no deployed commit and no temporary status. Record those in the release ticket.

## Topology

| Piece | Where | Notes |
|-------|-------|-------|
| Nginx | `:80`, `:443`, public | The only public listener. `deploy/linux/nginx/wasla-http.conf` (ACME challenge, redirect to HTTPS) and `wasla-https.conf` |
| Nginx catch-all | `/etc/nginx/sites-available/00-default-reject` | `default_server` on 80 and 443 that closes (444) any host no site names, so no site answers for another domain. The server also hosts unrelated sites with their own server blocks; never make one of them the default |
| `wasla-web.service` | `127.0.0.1:5200` | Marketing, application entry, every tenant host, SignalR hub |
| `wasla-api.service` | `127.0.0.1:5201` | Not published by Nginx: it answers tenant JSON only on `{slug}.app.wasla.tr`, which Web serves, and Print Bridge uses Web. No `api.wasla.tr` |
| `wasla-worker.service` | no listener | Order synchronization; sends live changes to Web on `127.0.0.1:5290` |
| SQL Server | `127.0.0.1:1433` | UFW allows only SSH and HTTP/HTTPS; 1433 is never opened to the Internet |

Run exactly one Web and one Worker ([realtime-scale-out.md](../architecture/realtime-scale-out.md)).

### Hosts

| Host | Served by |
|------|-----------|
| `wasla.tr` | Web, marketing host |
| `www.wasla.tr` | Nginx, permanent redirect to `https://wasla.tr` |
| `app.wasla.tr` | Web, application entry |
| `{slug}.app.wasla.tr` | Web, the tenant whose stored `PrimaryDomain` it is. Web answers every other name under `app.wasla.tr` with tenant-not-found |
| Any other name | Nginx catch-all, connection closed |

DNS: `A` records for `wasla.tr`, `www.wasla.tr`, `app.wasla.tr` and `*.app.wasla.tr` pointing to the server. Leave the domain's mail and verification records alone.

## Paths

| Path | Owner, mode | Content |
|------|-------------|---------|
| `/opt/wasla/releases/<commit>/` | `root:wasla 0750`, files `0640` | One immutable release: `web/`, `api/`, `worker/`, `cli/`, `RELEASE`, `migrate-before-start.sh`, `deploy/linux/` |
| `/opt/wasla/current` | symlink | The release systemd runs. Switched atomically, never built into |
| `/opt/wasla/previous-release` | `root 0644` | Commit of the release before the last switch |
| `/opt/wasla/bin/` | `root 0755` | `wasla-preflight`, `wasla-cli`, `wasla-install-release`, `wasla-rollback`, `wasla-backup` |
| `/etc/wasla/wasla.env` | `root:root 0600` | Non-secret settings ([example](../../deploy/linux/env/wasla.env.example)) |
| `/etc/wasla/secrets.env` | `root:root 0600` | `ENCRYPTION_MASTER_KEY`, runtime CentralDb connection string, internal channel secret ([names](../../deploy/linux/env/secrets.env.example)) |
| `/etc/wasla/operator.env` | `root:root 0600` | Migrator connection string, tenant owner and backup logins; never read by a service ([names](../../deploy/linux/env/operator.env.example)) |
| `/var/lib/wasla/keys/web`, `/var/lib/wasla/keys/api` | `wasla:wasla 0700` | ASP.NET Core Data Protection key rings (separate for Web and Api) |
| `/var/log/wasla/{web,api,worker}` | `wasla:wasla 0750` | Working directories; Api and Worker write `logs/wasla-*-<date>.log` here |
| `/var/opt/mssql/backup/wasla-prod/` | `mssql:mssql 0750` | Local backups |
| `/etc/letsencrypt/live/wasla.tr/` | certbot | TLS certificate and key |

systemd reads the environment files as root and starts the processes as `wasla`, so the account cannot read the files themselves. `sudo wasla-cli` parses them (it never sources them) and runs the CLI as `wasla`. Values are written `KEY="value"` without `$`, backquote, `"` or `\`.

## Accounts and SQL identities

| Identity | Rights | Used by |
|----------|--------|---------|
| Linux `wasla` | System account, no shell, owns only its key and log folders | Web, Api, Worker, CLI |
| SQL `waslaprod_migrator` | Owner (`db_owner`) of `WaslaProd_Central`; `CREATE ANY DATABASE` only for the first `migrate-central` | Release gate and `migrate-central`, via `wasla-cli --operator` |
| SQL `waslaprod_app` | `db_datareader` and `db_datawriter` in `WaslaProd_Central` | Web, Api, Worker and CLI as `ConnectionStrings__CentralDb` |
| SQL `waslaprod_tenant` | Creates and owns every tenant database; `CREATE ANY DATABASE` only while a tenant is provisioned | `add-tenant --sql-auth` via `wasla-cli --tenant-sql-auth`; its credential is stored, encrypted, as each tenant's connection string, so Web, Api, Worker and the CLI use it for tenant databases |
| SQL `waslaprod_backup` | `db_backupoperator` in every Wasla database; `CREATE ANY DATABASE`, which `RESTORE VERIFYONLY` requires | `wasla-backup` |
| SQL administrator | sysadmin | Server administration only. `sa` stays disabled and is never in an application connection string |

A tenant's runtime access is its owner credential because the application stores one connection string per tenant ([tenancy.md](../architecture/tenancy.md#tenantdbcontextfactory-and-secrets)); a separate least-privilege runtime login for tenant databases would need an application change.

## Fail-closed configuration

The application already refuses to start, outside Development, without the marketing and application domains or with a Development-only domain, without `Platforms:ProviderMode`, without `ENCRYPTION_MASTER_KEY` (Web, Api, Worker) and, for Worker and Api, without `Realtime:Internal`; the CLI refuses without `ConnectionStrings:CentralDb` ([deployment.md](deployment.md)). These cases fall back silently in the application and are refused by `wasla-preflight`, which every unit runs before it starts:

| Fallback in the application | Preflight check |
|-----------------------------|-----------------|
| Web, Api and Worker use the committed LocalDB `CentralDb` | `ConnectionStrings__CentralDb` set, a SQL login, not LocalDB, not Windows authentication, not `sa` |
| Data Protection keys in a default or in-memory store | `DataProtection__KeyPath` set (Web, Api), an existing `0700` folder owned by the account |
| Trendyol GO stage endpoint when `appsettings.Production.json` is not loaded | `Platform__TrendyolGo__BaseUrl` is `https://api.tgoapis.com`; the release's Production file names no stage endpoint |
| `Mock` from the committed base files | `Platforms__ProviderMode` is `Real` |
| Clients seen as the proxy, HTTPS seen as HTTP | `TrustedProxies__KnownProxies__0` set (Web, Api) |
| `localhost` (`Wasla:ApiBaseUrl`), `wasla.local`, PFX or Kestrel certificates | No Wasla setting names any of them; `ASPNETCORE_URLS` is one `http://127.0.0.1:<port>` address |
| Public signup open although no payment can be taken | `Wasla__TenantOnboarding__PublicSignupEnabled` is `false` |

The preflight names keys only. `deploy/linux/bin/wasla-preflight.test.js` covers it.

## First deployment

1. **Host.** As root on the server, from an extracted release archive: `deploy/linux/bin/wasla-install-host`. It creates the account and folders and installs scripts, units and Nginx files; it enables nothing.
2. **Catch-all and port 80.** Enable `00-default-reject` and `wasla-http`, then `nginx -t` and `systemctl reload nginx`. Check that the server's other sites still answer on their own names.
3. **TLS certificate** (see [below](#tls-certificate)).
4. **Database names.** List the existing databases and logins first and choose names that collide with none; never attach, rename, overwrite or drop an existing database.
5. **SQL identities.** As the SQL administrator create the four logins above with generated passwords (written straight into the environment files on the server, never printed), grant `waslaprod_migrator` `CREATE ANY DATABASE`.
6. **Secrets.** Generate `ENCRYPTION_MASTER_KEY` (`openssl rand -base64 32`) and the internal channel secret on the server into `secrets.env`; keep an offline copy of the master key with the backup recovery material. Losing it makes every tenant database unreachable.
7. **Install the release without starting it**: `sudo wasla-install-release --no-start <archive> <commit>`. On a fresh server the gate's `migrate-central` creates `WaslaProd_Central`; `migrate-all-tenants` finds no tenant.
8. **CentralDb rights.** Map `waslaprod_app` (`db_datareader`, `db_datawriter`) and `waslaprod_backup` (`db_backupoperator`) in `WaslaProd_Central`; revoke `CREATE ANY DATABASE` from `waslaprod_migrator`.
9. **Reference data**: `sudo wasla-cli -- seed-address-reference-data` (idempotent; seeds Türkiye's cities and districts; neighborhood and street files are not in the repository, so those import 0).
10. **Central Admin**: `sudo wasla-cli --prompt --password "Central Admin password" -- add-central-admin --email <email> --display-name "<name>"`. The password is typed on the terminal and reaches the CLI through a response file in `/run` that only `wasla` can read and that is deleted on exit.
11. **Pilot tenant** ([Add a tenant](#add-a-tenant)).
12. **Gate again**: `sudo wasla-cli --operator --gate`, which ends with `migration-status --require-current`.
13. **Backups** before the first start: `sudo wasla-backup INITIAL`; then `systemctl enable --now wasla-backup.timer`.
14. **Start**: `systemctl enable --now wasla-web wasla-api wasla-worker`; check `/health/ready` on both ports; enable `wasla-https` and reload Nginx.
15. **Verify** ([Verification](#verification)).

## Add a tenant

Signup cannot provision while no payment can be taken, so tenants are added with the CLI:

1. Grant: `GRANT CREATE ANY DATABASE TO waslaprod_tenant;`
2. `sudo wasla-cli --tenant-sql-auth --prompt --admin-password "Owner password" -- add-tenant --name "<name>" --slug <slug> --domain <slug>.app.wasla.tr --admin-email <email> --admin-name "<name>" --sql-server 127.0.0.1,1433`. Use a lowercase slug that starts with a letter or digit and is not reserved (`admin`, `api`, `app`, `docs`, `help`, `partners`, `pos`, `status`, `www`): the CLI checks only the character set. Optionally add `--business-phone`, `--city`, `--country`.
3. Revoke: `REVOKE CREATE ANY DATABASE FROM waslaprod_tenant;`
4. In the new tenant database: `CREATE USER waslaprod_backup FOR LOGIN waslaprod_backup; ALTER ROLE db_backupoperator ADD MEMBER waslaprod_backup;` Without it the nightly backup fails, by design.
5. Add `<slug>.app.wasla.tr` to the certificate (below).
6. `sudo wasla-cli --operator --gate`.

## TLS certificate

The DNS provider has no API for DNS-01 validation, so the certificate is issued over HTTP-01 from `/var/www/letsencrypt` and names each host: `wasla.tr`, `www.wasla.tr`, `app.wasla.tr` and every tenant host. Issue, or extend for a new tenant, with the full list:

```bash
sudo certbot certonly --webroot -w /var/www/letsencrypt --cert-name wasla.tr -d wasla.tr -d www.wasla.tr -d app.wasla.tr -d <slug>.app.wasla.tr --deploy-hook "systemctl reload nginx"
```

Renewal runs from the certbot timer and reloads Nginx. Unknown tenant names under `app.wasla.tr` are not on the certificate, so browsers show a certificate error instead of the tenant-not-found page. Moving DNS to a provider with an API and a `*.app.wasla.tr` wildcard removes the per-tenant step.

## Update

1. On a workstation, from the exact commit merged into `master`: `pwsh deploy/linux/publish-release.ps1 -Commit <sha>` (a detached worktree, so local changes never reach the archive). Copy `artifacts/release/wasla-<sha>.tar.gz` to the server and compare its SHA-256.
2. `sudo wasla-backup PRE-<short-sha>`.
3. `sudo wasla-install-release <archive> <sha>`. It installs the release beside the others, stops the services, runs the gate with the new CLI, and only then switches `current` and starts the services, waiting for `/health/ready`. A failed gate leaves the services stopped and `current` unchanged ([deployment.md](deployment.md#release-order-migrate-every-database-first)); fix the failed database and run it again.
4. Verify, then read the logs for a few minutes.

## Rollback

```bash
sudo wasla-rollback            # to /opt/wasla/previous-release
sudo wasla-rollback <commit>   # to any installed release
```

It switches `current` back and restarts the services. **It does not roll back database migrations.** Each unit runs the target release's `migration-status --require-current` before it starts, so a release older than the applied schema refuses to start (unknown migrations). Then either restore the pre-release backups (below) and roll back, or switch forward again (`sudo wasla-rollback <newer commit>`). Never remove that check to make an old release start.

## Backups and restore

`wasla-backup` takes a full backup with `CHECKSUM` of `WaslaProd_Central` and every database owned by `waslaprod_tenant`, verifies each with `RESTORE VERIFYONLY`, and deletes its nightly files older than 14 days. A failure leaves `wasla-backup.service` failed (`systemctl --failed`). SQL Server Express has no Agent and no backup compression, hence a systemd timer (03:30) and plain `.bak` files.

**The backups are local.** They do not survive the loss of the server. Encrypted off-site copies and a restore drill are still open (WAS-79).

Restore a tenant database (services stopped; as the SQL administrator): `RESTORE DATABASE [<db>] FROM DISK = N'<file>' WITH REPLACE, CHECKSUM;`. Restore CentralDb the same way. A tenant database restored from an older backup than CentralDb keeps working only while its schema matches; run the gate before starting.

## Verification

- `systemctl status wasla-web wasla-api wasla-worker`, `journalctl -u wasla-web -u wasla-api -u wasla-worker --since -10min`
- `curl -fsS http://127.0.0.1:5200/health/ready`, the same on 5201
- `sudo wasla-cli -- migration-status --require-current`
- `ss -tlnp`: only Nginx on 80/443 and SSH publicly; Kestrel and SQL Server on loopback or blocked by UFW (`ufw status`)
- From outside: `https://wasla.tr`, `https://www.wasla.tr` (redirect), `https://app.wasla.tr`, each tenant host, HTTP to HTTPS redirects, an unknown name under `app.wasla.tr`, sign-in for Central Admin and the tenant owner, static files and notification sounds, the SignalR hub (`/hubs/tenant-events`) upgrading to WebSocket
- The server's other sites on their own names, unchanged

## Known gaps

- Off-site backups and a restore drill (WAS-79).
- `Email:Provider` is `Log`: no email is sent (welcome, password reset) until SMTP credentials are configured.
- Payment and billing are not implemented, so public signup stays closed; tenants are added with the CLI.
- One certificate names each tenant host (no wildcard yet).
- Worker has no health endpoint; its state is in the journal and its log files.
