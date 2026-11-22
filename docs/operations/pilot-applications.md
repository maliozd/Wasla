# Pilot restaurant applications

WAS-217. A restaurant applies to the Wasla pilot from the coming-soon page. The application is stored in CentralDb and the
Wasla team is emailed; the team then calls or writes to arrange a conversation. An application starts no signup,
payment, panel or provisioning, and it is not an acceptance into the pilot.

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
`532 418 27 90`, and a paste is cleaned of spaces, brackets and dashes, loses a `+90`, `0090` or trunk `0`, and keeps
its first 10 national digits. The input has `inputmode="numeric"`, `autocomplete="tel-national"` and `maxlength="20"`;
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
| `Retention` | How long an application is kept and how it is deleted or anonymised |
| `RequestChannel` | Where a data subject sends a request about their rights |
| `UpdatedOn` | Optional; the notice date, `yyyy-MM-dd` |

The values are shown as configured, in every UI language; write them in Turkish.

### Before production

These are inputs the company must provide or work that must be done; none of them is invented in code or configuration:

1. The data controller's legal name, address and contact (email or KEP) address, the legal basis, the providers and
   transfers, the retention period and the request channel, reviewed by the company's legal adviser. Check whether a
   VERBİS registration is required.
2. A legal review of the fixed notice and form texts in all four UI languages (Turkish, English, Arabic, Russian): the
   notice headings and fixed sentences, the "Personal data" box on the form and the received-state text come from the
   resource files, not from configuration.
3. Retention, deletion or anonymisation, and data-subject requests for stored applications
   ([WAS-222](https://linear.app/wasla-food/issue/WAS-222)). **The code has no automatic retention today:** a stored
   application stays until it is deleted by hand, and no screen or command deletes, anonymises or exports one. The
   configured `Retention` and `RequestChannel` texts must describe what is actually done.
4. A team role address for the notifications (`Wasla:Contact:PilotRecipientEmail`, optional; without it they go to
   `Wasla:Contact:RecipientEmail`), and a sender (`Email:FromEmail`) the SMTP server may send from. Confirm a real
   delivery once in the target environment.
5. The CentralDb city catalog imported (`seed-turkey-reference-data` or `seed-address-reference-data`) with all 81
   provinces.
6. The CentralDb migration `AddPilotApplications` applied ([migrations.md](migrations.md)).

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

## Tests

`PilotApplicationModelTests` (the gate, the catalog check, the resend rule), `PilotApplicationServiceTests`,
`ContactNotificationClaimTests` (the send deadline, a late outcome against a newer claim, query and in-memory rule
parity, on SQLite), `PilotPageWebTests` (environments, the city catalog, hosts, both rate limits) and the pilot cases of
`AdminContactMessagesWebTests` in `tests/Wasla.UnitTests/Contact/` and `Admin/`; `Contact/ContactNotificationSqlServerTests`
on SQL Server (the same form posted at once, resends at once, a hanging send past the stuck window, a stuck attempt that
records its outcome late, rule parity); and `Contact/PilotApplicationBrowserTests` (headless Chromium,
`Category=Browser`) for the 1440 and 390 px layouts, Arabic right to left, long Russian text, keyboard order, error
focus, a double click, and the phone field (typed, edited and pasted numbers, then a send). No test sends email or SMS.
