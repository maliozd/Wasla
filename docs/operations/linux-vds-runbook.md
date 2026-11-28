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
| `/opt/wasla/product-floor` | `root 0644` | Commit of the product floor release, written once, at its first install ([The floor release](#the-floor-release)) |
| `/opt/wasla/product-assignment-status` | `root 0644` | `none` or `assigned`: whether any tenant has ever been assigned to anything but Orders. `none` is decided only at the floor's first install ([Assignment records](#assignment-records)) |
| `/opt/wasla/retired-releases/` | `root 0700` | Releases older than the floor, moved aside before the first assignment other than Orders |
| `/etc/wasla/hold/` | `root 0700` | Restore hold: one empty file per held unit, named after the unit ([Held restore](#held-restore-cases-2-and-3)) |
| `/etc/systemd/system/<unit>.d/50-restore-hold.conf` | `root 0644` | Drop-in that refuses to start the unit, a service or the backup timer, while its hold file exists |
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
9. **Reference data**: `sudo wasla-cli -- seed-address-reference-data` (idempotent; seeds Türkiye's cities and districts and says that no neighbourhood or street was imported). The neighbourhood and street catalog is a separate, owner-approved step with `--source`: see [address-reference-data.md](address-reference-data.md#import-into-a-target-centraldb).
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

1. On a workstation, from the exact commit merged into `master`: `pwsh deploy/linux/publish-release.ps1 -Commit <sha>` (a detached worktree, so local changes never reach the archive). On Windows it also runs in Windows PowerShell 5.1 (`powershell -File deploy/linux/publish-release.ps1 -Commit <sha>`), from any shell including Git Bash: the archive is always written with the Windows `tar.exe` (`%SystemRoot%\System32`), never with a GNU `tar` that comes first on `PATH`. Copy `artifacts/release/wasla-<sha>.tar.gz` to the server and compare its SHA-256.
2. `sudo wasla-backup PRE-<short-sha>`.
3. `sudo wasla-install-release <archive> <sha>`. It installs the release beside the others, stops the services, runs the gate with the new CLI, and only then switches `current` and starts the services, waiting for `/health/ready`. A failed gate leaves the services stopped and `current` unchanged ([deployment.md](deployment.md#release-order-migrate-every-database-first)); fix the failed database and run it again.
4. Verify, then read the logs for a few minutes.
5. Record the commit and the UTC time of the install in the release ticket ([Release history](#release-history)).

## Rollback

```bash
sudo wasla-rollback            # to /opt/wasla/previous-release
sudo wasla-rollback <commit>   # to any installed release
```

It switches `current` back and restarts the services. **It does not roll back database migrations.** Each unit runs the target release's `migration-status --require-current` before it starts, so a release older than the applied schema refuses to start (unknown migrations). Then either keep the target and restore the pre-release backups ([Restore](#restore)), or switch forward again (`sudo wasla-rollback <newer commit>`). Never remove that check to make an old release start. Record the target commit and the UTC time in the release ticket ([Release history](#release-history)).

**When a service does not start**, `wasla-rollback` exits 1 after it has switched: `current` names the target and `previous-release` the release it left. The services keep retrying their start (`Restart=on-failure`, about every 12 seconds in the drill) until systemd's start limit ends the retries with `Start request repeated too quickly` in the journal, and the backup timer keeps running. Before any backup is restored, [Restore](#restore) stops the services and, in cases 2 and 3, holds them: a service that is still retrying starts by itself as soon as the restored schema matches the target, before any restore check. A service stopped by its start limit refuses `systemctl start`, even after `systemctl stop`, until `sudo systemctl reset-failed <unit>`, and `systemctl` then reports only its last failure.

After the first assignment other than Orders, the rollback targets and the backups that may be restored are narrower: see [Product floor and restore reconciliation](#product-floor-and-restore-reconciliation-was-236).

## Backups and restore

`wasla-backup` takes a full backup with `CHECKSUM` of `WaslaProd_Central` and every database owned by `waslaprod_tenant`, verifies each with `RESTORE VERIFYONLY`, and deletes its nightly files older than 14 days. A failure leaves `wasla-backup.service` failed (`systemctl --failed`). SQL Server Express has no Agent and no backup compression, hence a systemd timer (03:30) and plain `.bak` files.

**The backups are local.** They do not survive the loss of the server. Encrypted off-site copies and a restore drill are still open (WAS-79).

Every restore of CentralDb or of a tenant database follows [Restore](#restore). Its case decides whether the units are held and what is checked before they start.

## Product floor and restore reconciliation (WAS-236)

A tenant assigned to anything but Orders ([Product assignment](../architecture/tenancy.md#product-assignment-was-235)) must not be processed by Wasla Orders again. Two things could make Orders process it: a release that predates WAS-235, which treats every active tenant as its own, or a restore that brings back an older assignment.

### The floor release

- The floor is the first production release whose commit contains WAS-235, the `dev` merge `530e2ccde3d0f77739489991aff263da02b0657b`. That merge adds the CentralDb migration `20261125185955_AddTenantProduct`. A commit is the floor or later when `git merge-base --is-ancestor 530e2ccde3d0f77739489991aff263da02b0657b <commit>` succeeds.
- When the floor is installed for the first time, and only then:
  - check that it is the first time: the [release history](#release-history) names no earlier install of the floor or of a later release, the release ticket names no floor, and neither `/opt/wasla/product-floor` nor `/opt/wasla/product-assignment-status` exists. If any of them says otherwise, write neither record and note the conflict in the ticket;
  - record its commit in the release ticket and on the server: `sudo sh -c 'set -C; echo <commit> > /opt/wasla/product-floor'` (`set -C` refuses to overwrite an existing file);
  - start the status record with `none` ([Assignment records](#assignment-records));
  - check that `/opt/wasla/releases/<commit>/RELEASE` names the commit, and that `__EFMigrationsHistory` in `WaslaProd_Central` holds `20261125185955_AddTenantProduct`.
- A later install, a reinstall of the floor or a rollback to it writes neither record again. A rebuilt server gets the floor commit back from the ticket, and the status record only as [Assignment records](#assignment-records) allows.
- The floor runs with every tenant still on Orders before the first assignment other than Orders.

### Allowed versions, rollback targets and backups

| | Until the first assignment other than Orders | From then on |
|---|---|---|
| Releases that may run | Any release the migration gate accepts | The floor and later releases only |
| Rollback target | As in [Rollback](#rollback) | A release that is the floor or later. If the gate refuses it, fix forward, or restore backups taken at or after the floor and reconcile ([Restore](#restore)) |
| Backups that may be restored | Any, followed by the gate | Only backups whose CentralDb holds `AddTenantProduct`. The `PRE-<floor>` backup and every older backup are prohibited |

- **The gate already stops older releases.** Every unit runs the current release's own `migration-status --require-current` before it starts. Every CLI older than the floor reports `AddTenantProduct` as unknown and refuses. This holds while CentralDb is at or after the floor. A pre-floor CentralDb would let an old release start again, which is why those backups are prohibited.
- **Start Web, Api and the Worker only through their units.** A DLL started by hand skips the gate and the restore hold.
- **Keep exactly one Worker** ([Topology](#topology)). No lease enforces it.

### Before the first assignment other than Orders

The cutover (WAS-240) may change an assignment for the first time only when all of these hold:

1. The floor is installed, recorded and has run with every tenant on Orders.
2. The assignment register exists and has been verified, and the status record says `assigned`, on the server and in the release ticket, before the first assignment changes ([Assignment records](#assignment-records)).
3. No release older than the floor can be selected:
   - every pre-floor `/opt/wasla/releases/<commit>` is moved to `/opt/wasla/retired-releases/`, so `wasla-rollback` reports it as not installed;
   - `/opt/wasla/previous-release` names a release that is the floor or later.
4. Every backup taken before the floor is moved out of `/var/opt/mssql/backup/wasla-prod/` into a quarantine folder there (`pre-floor/`, `mssql:mssql 0700`). `wasla-backup` deletes only its `*_FULL_*` files after 14 days, so `PRE-*` files stay until someone moves or deletes them.

### Release history

The release history is the [baseline ticket](#baseline-for-the-installs-before-was-236) for the installs made before WAS-236, followed by one release ticket for every later production install and every rollback, each naming the commit and the UTC time ([Update](#update), [Rollback](#rollback)). It lives outside the server, so a rebuilt server does not lose it. It is trusted only when the baseline has no gap, a ticket exists for every install and rollback since, and every ticket can be read. A gap, a ticket that cannot be read, or a ticket that disagrees with another ticket or with the server makes it untrusted.

#### Baseline for the installs before WAS-236

The installs made before this section existed have no release tickets. Record them once, in a baseline ticket, before the floor is installed:

1. **Collect the existing evidence, read only**, and keep each source's output in the ticket:
   - every directory in `/opt/wasla/releases/` and `/opt/wasla/retired-releases/`, with its `RELEASE` file (`commit=`, `published_utc=`);
   - the target of `/opt/wasla/current` and the commit in `/opt/wasla/previous-release`;
   - the `PRE-<short-sha>` and other named backups in `/var/opt/mssql/backup/wasla-prod/`, with their times;
   - the release notes, tickets, pull requests and deployment messages that name a production install;
   - whether `/opt/wasla/product-floor` and `/opt/wasla/product-assignment-status` exist;
   - the `AddTenantProduct` query from [step 4 of the held restore](#held-restore-cases-2-and-3) in `WaslaProd_Central`.
2. **List every install** that any source names, with its full commit, its time when a source shows it, and the sources. For each commit, record the exit code of `git merge-base --is-ancestor 530e2ccde3d0f77739489991aff263da02b0657b <commit>`.
3. **Record the conclusion with its grounds.** The baseline confirms that the floor was never installed in production only when all of these hold:
   - every listed commit resolves to a full commit and exits 1;
   - the sources do not contradict one another: each `RELEASE` file names its directory's commit, `current` and `previous-release` name listed commits, and no backup or note names a commit missing from the list;
   - neither record file exists, and the query returns 0.

   Then write: "The floor has not been installed in production as of `<UTC time>`", with who checked and the grounds.
4. **A gap stays a gap.** Do not close it by assumption, for example that every install was built from `master`. Record what is missing. The history stays untrusted, so restores fall in case 3, until evidence closes the gap.

### Assignment records

Two records say what the assignments should be. Both live outside CentralDb and outside every backup restored with it, so a restore that rolls back CentralDb does not roll them back. Copy every change to them into the release ticket. A server restored from an image, or rebuilt, loses or rolls back the server copy.

- **The status record**, `/opt/wasla/product-assignment-status`, says whether any tenant has ever been assigned to anything but Orders.
  - `none` is decided once, at the floor's first install ([The floor release](#the-floor-release)), and written then, on the server and in the ticket: `sudo sh -c 'set -C; echo "none since <floor commit> $(date -u +%FT%TZ)" > /opt/wasla/product-assignment-status'`. `set -C` refuses to overwrite an existing record.
  - The cutover (WAS-240) writes `assigned <UTC time>` there, and in the ticket, before it changes the first assignment.
  - A rollback, a restore, a reinstall, a rebuilt server or an image restore never resets `assigned`. When either copy says `assigned`, write `assigned` into the other one too.
  - A rebuild, reinstall, rollback or restore never makes a new `none` decision. A lost server copy of the existing `none` record may be put back only when all of these can be verified:
    - the ticket holds that record, `none since <floor commit> <time>`, written at the floor's first install;
    - the [release history](#release-history) is trusted and names that first install of the same floor commit;
    - no record says `assigned`: not the ticket, not a server copy, not a server image.

    Then copy the ticket's line unchanged, with `set -C` as above, and note in the ticket where it came from. When anything is unclear, the server copy stays missing and restores fall in case 3 ([Restore](#restore)).
- **The assignment register** lists every tenant whose assignment is not Orders, with its value. WAS-165 and WAS-240 define its form and who writes it. It must exist and be verified before the first assignment other than Orders.

The restored CentralDb is what gets checked, never the reference. An expected list is never taken as empty because the restored CentralDb shows every tenant on Orders.

### Restore

Before anything else, decide which case applies, from the release history, the floor record and the status record, on the server and in the release ticket. Never decide it from the backup that is about to be restored. The operator who runs the restore does every step as root and as the SQL administrator, and records the case and each step's output in the incident or release ticket.

**Nothing runs beside a restore.** From the moment the restore starts until the migration check has completed and, in cases 2 and 3, the assignment verification has passed, nobody runs a deploy (`wasla-install-release`), a rollback (`wasla-rollback`), a product cutover or a manual migration. The restore operator runs only what the runbook allows at the current step, such as the migrations in case 2 step 2 and case 3 step 5 and the correction through the cutover procedure (WAS-240) in case 3 step 4. A rollback that belongs to the incident, as in [Rollback](#rollback), is finished before the restore starts: `wasla-rollback` has exited, also when it exited 1, and `current` names its target. The restore's checks then run with that release ([Which release runs the checks](#which-release-runs-the-checks)).

| Case | When | Procedure |
|---|---|---|
| 1. Before the floor | The trusted [release history](#release-history) confirms that the floor was never installed in production, and nothing contradicts it (below) | [Case 1](#case-1-before-the-floor) |
| 2. Floor installed, no assignment other than Orders ever made | The trusted release history names the floor's first install, `/opt/wasla/product-floor` names the same commit, and the status record says `none` both on the server and in the release ticket | [Held restore](#held-restore-cases-2-and-3), then [Case 2](#case-2-no-assignment-other-than-orders-was-ever-made) |
| 3. An assignment was made, or the case is unclear | Every other state: the status record says `assigned`; the release history is untrusted; or the floor record or the status record is missing, cannot be read, or disagrees with another record or with the server | [Held restore](#held-restore-cases-2-and-3), then [Case 3](#case-3-an-assignment-was-made-or-the-case-is-unclear) |

The release history confirms case 1 only when all of these hold:

- It is trusted, and none of the commits it names is the floor or later: `git merge-base --is-ancestor 530e2ccde3d0f77739489991aff263da02b0657b <commit>` exits 1 for each. An exit code above 1, such as for an unknown commit, confirms nothing.
- `/opt/wasla/product-floor` and `/opt/wasla/product-assignment-status` do not exist, and no release ticket names a floor.
- No directory in `/opt/wasla/releases/` or `/opt/wasla/retired-releases/` is named after a commit that is the floor or later.
- When the CentralDb that the restore replaces can still be read, its `__EFMigrationsHistory` has no `20261125185955_AddTenantProduct` row. This reads the migration history, not the `Product` column.

When any of them fails or cannot be checked, the restore is case 3.

#### Which release runs the checks

The migration check and the gate of every case run with the release that serves after the restore. That is the release `current` names; after a rollback, its target ([Rollback](#rollback)). With the services stopped:

1. `readlink -e /opt/wasla/current` names `/opt/wasla/releases/<commit>`, and `<commit>` is the release the ticket says will serve.
2. The check names that release: `sudo wasla-cli --release /opt/wasla/releases/<commit> -- migration-status --require-current`. Where the case applies pending migrations, it does so with `sudo wasla-cli --operator --release /opt/wasla/releases/<commit> --gate`.
3. **No `--gate` over unknown migrations.** When the check reports `has migrations unknown to this build`, the restored databases are newer than that release. Do not run `--gate`: it runs `migrate-central` and `migrate-all-tenants` before its own check, and a release older than the floor migrates every active tenant's database, whatever its assignment. Stop: the units and the backup timer stay stopped and, in cases 2 and 3, held. Record the output in the ticket.

A check run with another release answers for that release. In the drill, after a failed rollback from `578a279c` to `94acc764` and a restore of the `PRE-578a279c` backups, the `94acc764` check exited 0, while the `578a279c` CLI listed five pending migrations and failed with `Invalid column name 'Product'`.

#### Stopping the backups

Every restore stops the backup timer first, then waits for an automatic backup that has already started to finish. A running backup is never stopped.

```bash
sudo systemctl stop wasla-backup.timer
until systemctl show -p ActiveState --value wasla-backup.service | grep -qxE 'inactive|failed'; do sleep 30; done
systemctl status --no-pager wasla-backup.service
```

Save that status in the ticket. The timer starts again only after the migration check has completed and, in cases 2 and 3, the assignment verification has passed. In cases 2 and 3 the hold also keeps it stopped across a reboot.

#### Case 1: before the floor

The existing restore and migration check apply. The `Product` column does not exist yet and is not queried. The units are not held.

1. Stop: `sudo systemctl stop wasla-worker wasla-api wasla-web`.
2. Stop the backups ([Stopping the backups](#stopping-the-backups)).
3. Restore as the SQL administrator: `RESTORE DATABASE [<db>] FROM DISK = N'<file>' WITH REPLACE, CHECKSUM;`. Restore CentralDb the same way. A tenant database restored from an older backup than CentralDb keeps working only while its schema matches.
4. Run the check before starting, with the release that serves ([Which release runs the checks](#which-release-runs-the-checks)): `sudo wasla-cli --release /opt/wasla/releases/<commit> -- migration-status --require-current`. Only when it exits 0: `sudo systemctl start wasla-web wasla-api wasla-worker`, then `sudo systemctl start wasla-backup.timer`. A service stopped by its start limit needs `sudo systemctl reset-failed <unit>` first ([Rollback](#rollback)).

#### Held restore: cases 2 and 3

1. **Hold every unit and the backup timer**, before anything is stopped. A held unit does not start: not after a reboot, not from `wasla-install-release`, `wasla-rollback`, `Restart=`, the timer or a manual `systemctl start`. The drop-ins extend the installed unit files without editing them; the hold files keep the hold on disk.

   ```bash
   sudo install -d -m 0700 /etc/wasla/hold
   for u in wasla-web.service wasla-api.service wasla-worker.service wasla-backup.service wasla-backup.timer; do
     sudo install -d -m 0755 /etc/systemd/system/$u.d
     printf '[Unit]\nAssertPathExists=!/etc/wasla/hold/%s\n' "$u" | sudo tee /etc/systemd/system/$u.d/50-restore-hold.conf >/dev/null
     sudo touch /etc/wasla/hold/$u
   done
   sudo systemctl daemon-reload
   systemctl show -p DropInPaths wasla-web.service wasla-api.service wasla-worker.service wasla-backup.service wasla-backup.timer
   ```

   Hold the POS integration host the same way once it exists. Do not use `systemctl mask`: it refuses a unit whose file `wasla-install-host` installed in `/etc/systemd/system`, and `mask --runtime` does not survive a reboot. The hold has been verified in an Ubuntu 22.04 container with systemd 249, not yet across a reboot of a real server ([Known gaps](#known-gaps)).
2. **Stop the services**, the Worker first: `wasla-worker` (and the POS integration host, once it exists), then `wasla-api`, then `wasla-web`. `systemctl is-active` reports `inactive` for each.
3. **Stop the backups** ([Stopping the backups](#stopping-the-backups)).
4. **Record the current state** while the databases are still readable. This is evidence of what the restore replaces; it never decides the case or the expected assignments.
   - Back up with `sudo wasla-backup PRE-RESTORE`. The script runs directly; only its unit is held.
   - In `WaslaProd_Central`, check the migration first: `SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261125185955_AddTenantProduct';`.
   - When it returns 1, save the output of `SELECT Slug, IsActive, Product FROM dbo.Tenants WHERE Product <> 1 ORDER BY Slug;`.
   - When it returns 0, or CentralDb cannot be read, write that in the ticket and run no `Product` query. A missing column shows nothing about the assignments.
5. **Check the release that serves, before anything is restored**, and save the output:
   - item 1 of [Which release runs the checks](#which-release-runs-the-checks) holds, and `/opt/wasla/releases/<commit>/RELEASE` names `commit=<commit>`;
   - `git merge-base --is-ancestor 530e2ccde3d0f77739489991aff263da02b0657b <commit>`, in a clone of the repository: 0 means the floor or later, 1 a release older than the floor.

   Any other exit code, or a check above that fails, means the release cannot be checked: stop, in both cases. **Case 3 needs the floor or later.** When `current` names an older release, stop as well. Stopping means: restore nothing, do not run `--gate`, keep the units and the backup timer held, and record why in the ticket. Case 2 continues with an older release only as [Case 2](#case-2-no-assignment-other-than-orders-was-ever-made) step 2 allows.
6. **Restore** as the SQL administrator: `RESTORE DATABASE [<db>] FROM DISK = N'<file>' WITH REPLACE, CHECKSUM;`, for CentralDb and each tenant database the incident needs. A tenant database restored from an older backup than CentralDb keeps working only while its schema matches.
7. **Check the restored CentralDb before any migration**, and save the result: the `AddTenantProduct` query from step 4. 1 means the backup was taken at or after the floor, 0 before it.
8. **Continue with case 2 or case 3.** Every unit and the backup timer stay held until [Start after a held restore](#start-after-a-held-restore) allows them.

**No tenant migration before the assignments are verified.** `sudo wasla-cli --operator --gate` and `migrate-all-tenants` choose the tenant databases to migrate by the assignments in the restored CentralDb, so cases 2 and 3 check those assignments first.

#### Case 2: no assignment other than Orders was ever made

1. **Assignment check before any migration**, when step 7 of the held restore returned 1, so the `Product` column exists:
   - the status record still says `none`, both on the server and in the release ticket;
   - `SELECT Slug, IsActive, Product FROM dbo.Tenants WHERE Product <> 1 ORDER BY Slug;` in the restored `WaslaProd_Central` returns no row.

   When step 7 returned 0, the backup predates the floor and has no `Product` column. Check only the status record here, on the server and in the release ticket; the migrations add the column with every tenant on Orders.
2. **Migration check**, with the release that serves ([Which release runs the checks](#which-release-runs-the-checks)).

   **A release older than the floor** may serve until the first assignment other than Orders, for example after a rollback ([Allowed versions](#allowed-versions-rollback-targets-and-backups)). When the `merge-base` check in step 5 of the held restore exited 1, continue only when both of these hold:
   - step 1 verified the status record `none`, on the server and in the release ticket;
   - step 7 of the held restore returned 0, so the restored CentralDb is from before the floor.

   Otherwise stop: do not run `--gate`, keep the units and the backup timer held, and record why in the ticket.

   Then run `sudo wasla-cli --release /opt/wasla/releases/<commit> -- migration-status --require-current`. A backup taken before that release's migrations shows them as pending: apply them with `sudo wasla-cli --operator --release /opt/wasla/releases/<commit> --gate`, then run the check again. When the check reports migrations unknown to that release, do not run `--gate` ([Which release runs the checks](#which-release-runs-the-checks) item 3). The check has completed only when it exits 0. Save that output.
3. **Assignment verification**, after the check has completed: the status record still says `none`, the `Product` query from step 1 returns no row, and the completed check's output has no `Not assigned to Wasla Orders` line. Then the expected list is empty and the restored CentralDb matches it. When the release that serves is older than the floor, the restored CentralDb has no `Product` column and the check prints no such line; the status record is then the whole verification.
4. **When a row or that line appears,** the records and the database disagree. Continue with case 3, from its first check. A row found in step 1 leaves the tenant databases unmigrated.
5. **Start** only as [Start after a held restore](#start-after-a-held-restore) allows.

#### Case 3: an assignment was made, or the case is unclear

Web, Api, the Worker, the POS integration host and the backup timer stay held until step 7 has passed. When the register, or the cutover procedure that a difference needs, does not exist yet, the restore waits with everything held.

1. **The backup holds `AddTenantProduct`:** step 7 of the held restore, run before any migration, returned 1. A 0 means a prohibited pre-floor backup: do not migrate it; restore a backup taken at or after the floor instead.
2. **The register exists and has been verified.** Without it, wait.
3. **Compare the assignments with the register, before any tenant migration.** The `Product` query from case 2, run on the restored CentralDb, lists the same tenants with the same values as the register, in both directions. Check 1 shows that the column exists, so the comparison needs no migration.
4. **Correct a difference only through the cutover procedure (WAS-240),** never by editing `Tenants.Product`. If that procedure does not exist yet, wait. Then repeat check 3, until the assignments match.
5. **Only then, the migration check**, with the release that serves ([Which release runs the checks](#which-release-runs-the-checks)): `sudo wasla-cli --release /opt/wasla/releases/<commit> -- migration-status --require-current`. That release is the floor or later (step 5 of the held restore). Pending migrations are applied with `sudo wasla-cli --operator --release /opt/wasla/releases/<commit> --gate`, which in such a release migrates only the Orders databases of tenants assigned to Orders; then the check runs again. The check has completed only when it exits 0.
6. **Verify the assignments again** after the migrations: check 3 on the migrated CentralDb, and the completed check's `Not assigned to Wasla Orders` line names the same active tenants as the register.
7. **Start** only as [Start after a held restore](#start-after-a-held-restore) allows.

#### Start after a held restore

A unit starts only when both of these have passed, with their output in the ticket:

- **the migration check:** `migration-status --require-current` exited 0 on the restored databases, after every migration applied to them (case 2 step 2, case 3 step 5);
- **the assignment verification** of the case, after that check: case 2 step 3, or case 3 step 6.

Until then every unit and the backup timer stay held. Then remove each hold file just before starting its unit:

1. Web, then Api, each answering `/health/ready`, then the Worker, then the POS integration host once it exists: `sudo rm /etc/wasla/hold/wasla-web.service && sudo systemctl start wasla-web`, and so on. A service stopped by its start limit before it was held needs `sudo systemctl reset-failed <unit>` first ([Rollback](#rollback)).
2. The backups: `sudo rm /etc/wasla/hold/wasla-backup.service /etc/wasla/hold/wasla-backup.timer && sudo systemctl start wasla-backup.timer`.
3. When no hold file is left, delete the `50-restore-hold.conf` drop-ins and run `systemctl daemon-reload`.

## Verification

- `systemctl status wasla-web wasla-api wasla-worker`, `journalctl -u wasla-web -u wasla-api -u wasla-worker --since -10min`
- `curl -fsS http://127.0.0.1:5200/health/ready`, the same on 5201
- `sudo wasla-cli -- migration-status --require-current`
- `ss -tlnp`: only Nginx on 80/443 and SSH publicly; Kestrel and SQL Server on loopback or blocked by UFW (`ufw status`)
- From outside: `https://wasla.tr`, `https://www.wasla.tr` (redirect), `https://app.wasla.tr`, each tenant host, HTTP to HTTPS redirects, an unknown name under `app.wasla.tr`, sign-in for Central Admin and the tenant owner, static files and notification sounds, the SignalR hub (`/hubs/tenant-events`) upgrading to WebSocket
- The server's other sites on their own names, unchanged

## Known gaps

- Off-site backups and a restore drill (WAS-79).
- **The restore hold has not been verified across a real reboot (WAS-236).** The drill of 2026-11-28 ran in an Ubuntu 22.04 container with systemd 249, SQL Server 2022 Express and the units installed by `wasla-install-host`:
  - a held service and the held backup timer refused `systemctl start` (`Assertion failed`);
  - `systemctl mask` refused the installed unit file;
  - removing the hold files let them start;
  - they stayed stopped across a restart of the container.

  A container restart starts systemd again, not the kernel. Before the hold's first production use, check on a non-production server that a held service and the held backup timer stay stopped after a reboot.
- **The assignment register does not exist yet (WAS-165, WAS-240).** The first assignment other than Orders waits for it. Until it exists, a restore that falls in case 3 keeps Web, Api, the Worker and the backup timer held ([Restore](#restore)).
- **The release history baseline has not been recorded yet (WAS-236).** Until it is recorded without a gap ([Baseline for the installs before WAS-236](#baseline-for-the-installs-before-was-236)), every restore falls in case 3. While production is before the floor, case 3 stops before anything is restored ([Held restore](#held-restore-cases-2-and-3) step 5) and refuses every backup (check 1), so no restore can complete. Record the baseline before a restore is needed.
- `Email:Provider` is `Log`: no email is sent (welcome, password reset, contact form notifications) until SMTP credentials are configured. Contact form messages are still stored in CentralDb and listed in Central Admin (`/admin/contact-messages`), marked as not notified.
- Payment and billing are not implemented, so public signup stays closed; tenants are added with the CLI.
- One certificate names each tenant host (no wildcard yet).
- Worker has no health endpoint; its state is in the journal and its log files.
