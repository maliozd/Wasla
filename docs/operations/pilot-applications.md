# Pilot restaurant applications

WAS-217. A restaurant applies to the Wasla pilot from the coming-soon page. The application is stored in CentralDb and the
Wasla team is emailed; the team then calls or writes to arrange a conversation. An application starts no signup,
payment, panel or provisioning, and it is not an acceptance into the pilot.

WAS-222 adds the application's retention: its personal data is deleted 90 days after the last meaningful contact or
status change ([Retention and deletion](#retention-and-deletion)), and Central Admin answers a data subject's request
([Data subject requests](#data-subject-requests)). This is the approved product and technical decision of
22 November 2026; **the legal wording is still under review** by the company's legal adviser.

Design: Figma "Wasla — Web & App", page "WAS-217 · Pilot Başvuru · Claude Alternatifi" (screens S1 to S10, the
localization frames and the accessibility board).

## Pages

| Route | What it is |
|-------|------------|
| `/` (coming-soon page) | While the form is open, its actions are "Become a pilot restaurant" (`/pilot`) and "Contact us" (`mailto:` the published contact address). Otherwise the page keeps its earlier actions (email and Instagram). |
| `GET /pilot` | The form, or the received state once after a stored application (post/redirect/get). |
| `POST /pilot` | Stores the application. Antiforgery, the honeypot field `Website` and two rate limits ([Abuse protection](#abuse-protection)). |
| `GET /pilot/privacy-notice` | The KVKK article 10 notice of the form. The form links to it, in a new tab, before the submit button. |

The pages exist on the marketing host only. On the application entry (`app.wasla.tr`) and on tenant hosts
`TenantResolutionMiddleware` answers 404 for `/pilot` and everything under it, before any tenant lookup
([../architecture/tenancy.md](../architecture/tenancy.md)). They are independent of
`Wasla:MarketingSite:FullSiteEnabled` and of public signup: they open without publishing the rest of the marketing site
(`/contact`, `/privacy-notice` and `/pos` stay closed while the full site is off).

The form asks for the restaurant name, the city (one of the 81 Turkish provinces of the CentralDb city catalog), the
contact person, a Turkish phone number (required, fixed `+90`, the contact form's rules), an email address and an
optional note. There is no password, payment, address, plan or setup field, and no consent or acknowledgement checkbox:
the privacy notice is information, not consent, and the form asks for no marketing permission.

The phone field takes the 10 national digits after the fixed `+90` with the signup page's phone behaviour
(`js/signup/signup-phone-format.js`, bound by `wasla-marketing.js` to inputs marked `data-wm-phone`; no second
formatter): an 11th digit typed is refused, letters and other characters never land, the number is grouped as
`532 418 27 90`, and a paste, or text with more than one digit inserted in one go (a keyboard suggestion, an input
method, autofill), is cleaned of spaces, brackets and dashes, loses a `+90`, `0090` or trunk `0`, and keeps its first 10
national digits. One Backspace right after a group space removes the digit before it (one Delete right before a space,
the digit after it). Only the ASCII digits 0-9 count. The input has `inputmode="numeric"`, `autocomplete="tel-national"` and `maxlength="20"`;
20 fits a number written out in full (`+90 (532) 418-27-90`), so an input method that inserts it as text is not cut
short before the prefix is removed. The server still validates the number (`Validation.PilotPhoneRequired`,
`Validation.ContactPhoneInvalid`); the field's limit is a convenience, not a check.

## When the form is open

The form, its notice and the coming-soon action are published only when all three hold (`PilotApplicationGate`):

1. `Wasla:PilotApplication:Enabled` is true (default `false`).
2. Every required privacy notice fact below is configured, or the host runs in the `Development` environment. Only
   `Development` shows an incomplete notice, as a draft with each missing fact marked "Not configured". Every other
   environment name (Production, Staging, Pilot, Test, or a misspelling) keeps the form closed (404) until the facts are
   set, and Web logs a warning at startup naming the missing keys and the environment.
3. The CentralDb city catalog holds the 81 Turkish provinces: one active row for each plate code `01` to `81`. An empty,
   partial or unreadable catalog keeps the form and the coming-soon action closed, so no visitor sees an empty or
   partial city list. Web logs a warning with the reason (empty, incomplete with the number found, or unreadable with
   the exception type) at startup and whenever the state changes. The catalog is read at most every 10 minutes while
   complete and every minute while not, so an import opens the form within a minute without a restart.

The notice takes the data controller and the legal facts from configuration only, so the code holds no company name,
address or legal decision:

| Key under `Wasla:PilotApplication:PrivacyNotice` | Content |
|---|---|
| `ControllerName` | The data controller's full legal (trade) name |
| `ControllerAddress` | Its registered address |
| `ControllerContact` | Its contact address for the notice (email or KEP) |
| `LegalBasis` | The legal basis under KVKK article 5, as decided by the legal review |
| `Transfers` | Hosting and email providers and whether data goes abroad |
| `Retention` | How deletion is carried out, after the fixed sentence on the period (below) |
| `RequestChannel` | Where a data subject sends a request about their rights |
| `UpdatedOn` | Optional; the notice date, `yyyy-MM-dd` |

The values are shown as configured, in every UI language; write them in Turkish.

The notice's "How long it is kept" section starts with a fixed, translated sentence (`PilotPrivacy.RetentionText`) whose
number of days comes from the rule the cleanup runs (`PilotApplicationRetention.PeriodDays`, 90), so the notice cannot
state a different period. `Retention` then adds what the fixed sentence does not say. An example, to be approved by the
legal adviser before use, not a legal text:

```text
Wasla:PilotApplication:PrivacyNotice:Retention = "Süre dolduğunda başvurudaki kişisel veriler (restoran adı, il, yetkili adı, telefon, e-posta ve not) aktif veritabanından silinir; silme işlemi kişisel veri içermeyen bir kayıtla üç yıl belgelenir. Silinen veriler, yedeklerin saklama süresi dolana kadar korunan yedeklerde kalabilir."
```

### Before production

These are inputs the company must provide or work that must be done; none of them is invented in code or configuration:

1. The data controller's legal name, address and contact (email or KEP) address, the legal basis, the providers and
   transfers, the retention period and the request channel, reviewed by the company's legal adviser. Check whether a
   VERBİS registration is required.
2. A legal review of the fixed notice and form texts in all four UI languages (Turkish, English, Arabic, Russian): the
   notice headings and fixed sentences, the "Personal data" box on the form and the received-state text come from the
   resource files, not from configuration.
3. Retention and data subject requests ([WAS-222](https://linear.app/wasla-food/issue/WAS-222)). **Built:** the 90-day
   rule, the `pilot-applications-retention` command with its destruction records, and the Central Admin request
   procedure ([Retention and deletion](#retention-and-deletion), [Data subject requests](#data-subject-requests)).
   **Still open before production:** the legal adviser's approval of the retention wording in all four languages and of
   the configured `Retention` and `RequestChannel` texts, the request response time, the schedule enabled on the server
   ([Scheduling](#scheduling)), and the backup retention for labelled backups ([Backups](#backups)).
4. A team role address for the notifications (`Wasla:Contact:PilotRecipientEmail`, optional; without it they go to
   `Wasla:Contact:RecipientEmail`), and a sender (`Email:FromEmail`) the SMTP server may send from. Confirm a real
   delivery once in the target environment.
5. The CentralDb city catalog imported (`seed-turkey-reference-data` or `seed-address-reference-data`) with all 81
   provinces.
6. The CentralDb migrations `AddPilotApplications` and `AddPilotApplicationRetention` applied
   ([migrations.md](migrations.md)).

The existing contact form notice at `/privacy-notice` also names the controller only as "Wasla"; that gap is separate
from this form.

## Abuse protection

- Antiforgery on the post, and the hidden honeypot field `Website`. The field is not rendered (`hidden`), so it is out of
  sight, the tab order and the accessibility tree, and browsers and password managers do not fill a field that cannot be
  focused; it also carries the common password-manager ignore attributes. A post that fills it is answered like a stored
  application, and nothing is stored or sent.
- Two rate limits per client network, both answered with the form again, what was typed, the localized message
  `Pilot.TooManyAttempts` and status 429 with `Retry-After`:

| Policy | Counts | Default | Key |
|---|---|---|---|
| `pilot-application` | Every post, valid or not | 30 per hour | `AuthenticationRateLimiting:PilotApplication` |
| `pilot-application-store` | Valid applications about to be stored and emailed, after validation and the honeypot | 10 per hour | `AuthenticationRateLimiting:PilotApplicationStore` |

Why these numbers: a restaurant normally applies once. A visitor who corrects validation errors posts several times,
and several restaurants can share one address behind a carrier-grade NAT or an office network, so a limit on every post
must leave room for many corrections: 30 posts allow about 20 rounds of corrections and still stop automated posting.
The store limit bounds the rows and team emails one network can create in an hour; 10 leaves room for several
restaurants on one network and for a visitor who sends the form again after a save failure, and an automated sender that
passes validation still creates at most 10 rows and 10 emails an hour per network. Validation errors never take a store
permit, so corrections never lock a visitor out of the real application.

## Storage

An application is a `ContactMessages` row with `Topic = PilotApplication` (5), the stored fields of a contact message,
and `CityPlateCode` (`varchar(2)`, `01` to `81`, checked against the active city catalog when stored). The note is the
`Message` column and may be empty. `PrivacyNoticeAcknowledged` is false, because the form asks for no acknowledgement.
No IP address or other request data is stored.

Two columns carry the team's side (WAS-222): `PilotStatus` (`New` 0, `Contacted` 1, `Accepted` 2, `Declined` 3) and
`PilotLastActivityAtUtc`, the last meaningful activity in UTC. Both are null for contact form messages. The
`AddPilotApplicationRetention` migration sets applications stored before it to `New`, last active when they were
received.

The form carries a submission id generated when it is first shown and kept through validation errors and a failed save.
It becomes the row's id: posting the same form again (a double click, a resubmitted page, or several posts at once)
finds the stored row and stores and sends nothing; concurrent posts meet at the primary key. The submit button also
ignores a second click while the form is being sent.

A failed save is logged with the row id, the exception type, the inner exception type and the SQL Server error number,
never with what the visitor typed.

## Notification and its failure

The row is saved first; only a failure of that save reaches the visitor ("Your application could not be saved", with
the typed values kept and a "Send again" button). The team email is sent after the save, outside any transaction, and
its outcome is recorded on the row (`NotificationStatus`, `NotificationAttemptedAtUtc`):

- `Sent`: the SMTP server accepted it. `Failed`: sending threw or did not finish in time; the log names the row id, the
  topic, the failure category and the exception type, never what the visitor typed. `NotConfigured`: `Email:Provider`
  is not `Smtp` (Development, tests); nothing is sent and nothing claims delivery.
- The visitor sees the received state in every case, because the application is stored.

**One attempt at a time.** Each attempt owns the row through `NotificationClaimId`: the stored row carries the first
attempt's claim, and a resend claims a new one in a single conditional update. An attempt records its outcome with a
conditional update that matches its own claim only, so a late outcome of an earlier attempt never overwrites a newer
attempt's state; that late outcome is logged and dropped.

**Send deadline.** One attempt (SMTP connection, sign-in and send) ends after 3 minutes
(`ContactNotificationRetry.SendTimeout`), whether or not the SMTP client stops on its token, and is recorded as
`Failed`. That is far inside the 15-minute window after which an unfinished attempt counts as stuck, so an attempt that
is still running is not taken for a stuck one.

**What this does not guarantee.** The SMTP server accepting an email and the application recording that are two
separate steps, and no protocol makes them one. When an attempt cannot tell whether the server took the email (a
timeout or a lost connection after the message was sent, or a process that stopped before recording the outcome), it is
recorded as failed or stays pending, and a resend may deliver a second copy. The system prevents two attempts from
running at once; it does not make delivery exactly once. Delivery is at least once in those cases.

**Finding notifications that never finished.** If the process stops between storing a row and recording its outcome,
the row stays `Pending`. Central Admin lists such rows, together with failed and unconfigured ones, under the filter
"Notification needs attention" (`/admin/contact-messages?kind=attention`), using the same rule as the resend. Web also
checks at startup and every 15 minutes and logs a warning with the number of notifications that started more than 15
minutes ago without an outcome (`PilotApplicationReadinessCheck`).

Central Admin lists pilot applications at `/admin/contact-messages` (filter "Pilot applications", `?kind=pilot`). The
detail shows the city and the notification state, and offers "Send notification again" when the notification failed,
was not configured, or started more than 15 minutes ago without an outcome (`ContactNotificationRetry`, one rule for the
claim query and the page; a test holds the query and the in-memory form together on SQLite and SQL Server). The resend
claims the row in one conditional update, so two administrators or a double click start one attempt; a notification
already recorded as sent is not offered again. In a Log email setup the page says that nothing was sent.

## Retention and deletion

WAS-222. Approved product and technical decision of 22 November 2026; the legal wording is still under review.

**The rule** (`PilotApplicationRetention`, the only place it is written; Central Admin, the cleanup and the tests use it):
a pilot application's personal data is kept for **90 days after its last meaningful activity** and then deleted. The
same rule holds for every status:

| Status | What happens |
|---|---|
| `New` (unanswered) | Deleted 90 days after it was received, unless the team records activity first |
| `Contacted` | Kept while the team keeps recording contacts; deleted 90 days after the last one |
| `Declined` | Deleted 90 days after it was declined (or after a later recorded contact) |
| `Accepted` | Deleted 90 days after the acceptance or the last recorded contact. The restaurant continues in the onboarding/customer process; data that becomes customer or contract data falls under that process's own, separate retention policy (not defined here) |

- **Meaningful activity** is the receipt of the application, a status change in Central Admin, and "Record a contact"
  (pressed after a call or an email with the restaurant). Each moves `PilotLastActivityAtUtc` to now and restarts the
  period, so an application is never deleted while the team is talking to the restaurant, provided the team records it.
  Setting the status it already has, a correction on request, a notification resend, viewing and downloading are not
  activity and do not extend the period.
- **Times and boundary.** Everything is UTC. An application last active at `T` is kept at every instant before
  `T + 90 days` and may be deleted at `T + 90 days` exactly and after. Central Admin shows the instant as "Deleted on or
  after".
- **Running operations hold it.** An application whose team notification is being sent (a claim taken less than 15
  minutes ago) is not deleted; the next run takes it. An older claim belongs to an attempt that ended at its 3-minute
  deadline and holds nothing back.
- **Contact form messages are out of scope.** The rule selects only `Topic = PilotApplication` rows with a last activity;
  contact form messages are never selected and keep their current handling.
- Personal content is deleted, not anonymised: the whole row goes. No anonymous statistics are kept; nothing needs them
  today.

### The cleanup command

```powershell
dotnet run --project src\Wasla.Cli -- pilot-applications-retention --dry-run
dotnet run --project src\Wasla.Cli -- pilot-applications-retention
```

On the server: `sudo wasla-cli -- pilot-applications-retention [--dry-run]`. It needs `ConnectionStrings:CentralDb` only
(no `ENCRYPTION_MASTER_KEY`, no tenant database) and the application login's `db_datareader` and `db_datawriter` rights.

- `--dry-run` reads the same rows and changes nothing. It prints each expired application as
  `would delete <id> status=<status> last-activity=<UTC> due=<UTC>` and the number of destruction records past their own
  retention. `--batch-size` (default 100, at most 1,000) sets how many rows one round reads.
- A real run reads expired applications in rounds of at most the batch size, oldest activity first, and deletes each in
  its own short transaction: a conditional delete that re-checks the rule inside the database, and only if it removed the
  row, a destruction record. It prints `deleted <id>`, `kept <id>` for a row that changed since it was read (new activity,
  a notification attempt that started, or another run that deleted it first) and `FAILED <id>` on the error stream.
- **Safe to repeat and to overlap.** Two runs at once, or a run and an administrator, delete and record a row once: on
  SQL Server the second delete waits for the first one's row lock and then finds nothing. A rerun finds nothing left to
  delete. A status change or recorded contact that commits before the delete keeps the row; one that comes after finds
  it gone and Central Admin says so.
- **Failures are visible.** A row that cannot be deleted is logged at error level with its id, the exception type and the
  SQL Server error number (never its content), recorded as a failed destruction when possible, and left stored; the run
  continues and exits with code 1. Code 0 means every expired row was deleted or no longer expired; code 2 is an invalid
  option. The next run tries the failed rows again.
- Output and logs carry ids, statuses and UTC times only, never a name, email address, phone number, city or note.

### Destruction records

Every deletion writes a `DataDestructionRecords` row: the destroyed row's id (`SubjectId`) and kind, the UTC time, the
method (`RetentionExpired` or `DataSubjectRequest`), the result (`Deleted` or `Failed`), the actor (`RetentionJob`, or
`CentralAdmin` with the administrator's user id) and `RetainUntilUtc`. It holds no application content. The records are
kept for **three years**, the period required for destruction operation records, and the same command removes the ones
past `RetainUntilUtc`. A destruction record is not a copy of the application; it only shows that and how it was
destroyed.

### Scheduling

The schedule is **not installed or enabled** by this change. To run it daily on the Linux host later, after the legal
approval and the migration, add a oneshot unit and timer next to `wasla-backup` ([linux-vds-runbook.md](linux-vds-runbook.md)):

```ini
# /etc/systemd/system/wasla-pilot-retention.service
[Unit]
Description=Wasla pilot application retention cleanup
After=mssql-server.service
Requires=mssql-server.service

[Service]
Type=oneshot
ExecStart=/opt/wasla/bin/wasla-cli -- pilot-applications-retention
SyslogIdentifier=wasla-pilot-retention

# /etc/systemd/system/wasla-pilot-retention.timer
[Unit]
Description=Daily Wasla pilot application retention cleanup

[Timer]
OnCalendar=*-*-* 04:15:00
Persistent=true
RandomizedDelaySec=300

[Install]
WantedBy=timers.target
```

Run `sudo wasla-cli -- pilot-applications-retention --dry-run` once and check its list before
`systemctl enable --now wasla-pilot-retention.timer`. A failed run leaves the unit failed (`systemctl --failed`,
`journalctl -u wasla-pilot-retention`), as for the backup; no other alerting exists yet. A daily run deletes an
application at most about a day after its 90 days; if runs stop, applications stay stored past their period, so a
failed unit needs attention. Overlapping runs are harmless.

### Backups

Deletion applies to the **active** CentralDb. Deleted applications stay in backups taken before the deletion until those
backups are removed; backup files are never edited. `wasla-backup` keeps its nightly `*_FULL_*` files for 14 days
(`WASLA_BACKUP_RETENTION_DAYS`), so a deleted application leaves the nightly backups within about two weeks. **Labelled
backups (`wasla-backup INITIAL`, `PRE-<sha>`) are not removed by the job**; until a retention for them is decided, they
keep deleted applications indefinitely and must be removed by hand. Backups stay on the protected host
([linux-vds-runbook.md](linux-vds-runbook.md#backups-and-restore)).

After CentralDb is restored from a backup:

1. Run `pilot-applications-retention --dry-run`, then a real run, before the services start again: applications that
   expired since the backup was taken are deleted at once.
2. Applications deleted **on request** after the backup was taken come back with the restore, and their destruction
   records do not. Before restoring, if the current database is still readable, keep the `SubjectId`s of
   `DataDestructionRecords` with `Method = 2` (`DataSubjectRequest`) newer than the backup; otherwise take them from the
   Web log ("deleted on a data subject's request"). After the restore, delete each again in Central Admin.

## Data subject requests

A person may ask what is stored about them, have it corrected or have it deleted (KVKK articles 11 and 13). The request
reaches the team through the configured `RequestChannel`. The answer time is part of the legal review (KVKK article 13
sets at most thirty days). Central Admin (`/admin/contact-messages`, Central Admin sign-in only) offers:

1. **Find.** "Find a person's application" (`/admin/contact-messages/find`) searches pilot applications by the exact email
   address (case ignored) or phone number the person gave; at most 20 results, newest first. The search is posted, so the
   address or number never lands in the browser history or a request log. Contact form messages are not searched.
2. **Verify the identity by hand.** Nothing in the system verifies who is asking. Confirm it before answering, for example
   by calling back the stored number or replying to the stored address, and keep the request with the team's records.
3. **Export.** "Download data (JSON)" on the application's page downloads everything stored about it: the fields, the
   province, the form language, the team's status, the last activity, the deletion date and the notification state. JSON,
   not CSV, so no spreadsheet runs a typed value as a formula. The response is sent with `Cache-Control: no-store` and as
   an attachment, the download is posted with the antiforgery token, and the log names the application and the
   administrator only. Nothing is emailed automatically: send the file to the person through a channel the team chooses.
4. **Correct.** "Correct data" edits the restaurant name, contact person, email address, phone number, province and note
   with the application form's rules. A correction does not restart the retention period.
5. **Delete.** "Delete application" requires ticking "I verified the requester's identity". It deletes the row with the
   same transaction as the cleanup and writes a destruction record with the method `DataSubjectRequest` and the
   administrator's id. It is refused while a team notification is being sent ("try again in a few minutes").

Status changes and recorded contacts are on the same page ("Application status"). All of these are posted with the
antiforgery token and limited to Central Admin; a tenant session or an anonymous visitor is sent to the Central Admin
sign-in.

## Tests

`PilotApplicationModelTests` (the gate, the catalog check, the resend rule), `PilotApplicationServiceTests`,
`ContactNotificationClaimTests` (the send deadline, a late outcome against a newer claim, query and in-memory rule
parity, on SQLite), `PilotPageWebTests` (environments, the city catalog, hosts, both rate limits) and the pilot cases of
`AdminContactMessagesWebTests` in `tests/Wasla.UnitTests/Contact/` and `Admin/`; `Contact/ContactNotificationSqlServerTests`
on SQL Server (the same form posted at once, resends at once, a hanging send past the stuck window, a stuck attempt that
records its outcome late, rule parity); and `Contact/PilotApplicationBrowserTests` (headless Chromium,
`Category=Browser`) for the 1440 and 390 px layouts, Arabic right to left, long Russian text, keyboard order, error
focus, a double click, and the phone field (typed, edited and pasted numbers, then a send; Backspace and Delete next to the group spaces and
text inserted in one go, with the steps the signup business phone runs too, `Signup/TurkishPhoneFieldScenario.cs`). No test sends email or SMS.

Retention and requests (WAS-222): `Contact/PilotApplicationRetentionTests` (SQLite: the inclusive UTC boundary, every
status, recorded activity restarting the period and a correction not, a running notification attempt holding the row,
contact messages untouched, the dry run changing nothing, repeats, more rows than one batch with equal activity times,
a failed deletion logged, recorded and retried, destruction records without content and their three-year removal, query
and in-memory rule parity); `Contact/CentralAdminPilotApplicationServiceTests` (find, export, correct, status, recorded
contact, deletion on request and its failure); `Contact/PilotApplicationRetentionSqlServerTests` on SQL Server (two runs
at once deleting and recording each row once, a held delete making a second run, a recorded contact and a deletion on
request wait, a contact recorded between the run's read and its delete keeping the row, an uncommitted status change
making the run wait and keep it, rule parity) and `PilotApplicationRetentionMigrationSqlServerTests` (earlier rows set
to `New` and their received time, rollback); `Cli/CliPilotRetentionTests` (the command's output without personal data,
its exit codes, no master key); `Admin/AdminPilotApplicationRequestsWebTests` (Central Admin only, antiforgery on every
change, the posted search, the uncached JSON attachment, correction errors, the identity confirmation, the destruction
record naming the administrator, Arabic right to left); `PilotPageWebTests` (the notice's 90-day sentence in every
culture from the rule's constant); and `Admin/AdminRtlLayoutBrowserTests` (`Category=Browser`: the detail, correction and
search pages without sideways overflow in Arabic at 375 px and English at 1,366 px, the controls named by their labels,
and a real click on Delete refused until the identity box is ticked).
