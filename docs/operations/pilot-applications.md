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
| `POST /pilot` | Stores the application. Antiforgery, the honeypot field `Website`, and the `pilot-application` rate limit (5 per hour per client network, `AuthenticationRateLimiting:PilotApplication`). |
| `GET /pilot/privacy-notice` | The KVKK article 10 notice of the form. The form links to it, in a new tab, before the submit button. |

The pages are independent of `Wasla:MarketingSite:FullSiteEnabled` and of public signup: they open without publishing
the rest of the marketing site (`/contact`, `/privacy-notice` and `/pos` stay closed while the full site is off).

The form asks for the restaurant name, the city (one of the active Turkish provinces of the CentralDb city catalog),
the contact person, a Turkish phone number (required, fixed `+90`, the contact form's rules), an email address and an
optional note. There is no password, payment, address, plan or setup field, and no consent or acknowledgement checkbox:
the privacy notice is information, not consent, and the form asks for no marketing permission.

## When the form is open

`Wasla:PilotApplication:Enabled` (default `false`) switches the form on. Its privacy notice takes the data controller and
the legal facts from configuration only, so the code holds no company name, address or legal decision:

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

- In Production the form, its notice and the coming-soon action are open only when `Enabled` is true and all seven
  required facts are set. If `Enabled` is true and a fact is missing, they stay closed (404) and Web logs a warning at
  startup naming the missing keys.
- Outside Production `Enabled` alone opens them, and the notice is shown as a draft with each missing fact marked
  "Not configured", so the flow can be developed and tested.

### Before production

These are inputs the company must provide; none of them is invented in code or configuration:

1. The data controller's legal name, address and contact (email or KEP) address, the legal basis, the providers and
   transfers, the retention period and the request channel, reviewed by the company's legal adviser. Check whether a
   VERBİS registration is required.
2. A team role address for the notifications (`Wasla:Contact:PilotRecipientEmail`, optional; without it they go to
   `Wasla:Contact:RecipientEmail`), and a sender (`Email:FromEmail`) the SMTP server may send from. Confirm a real
   delivery once in the target environment.
3. The CentralDb city catalog imported (`seed-turkey-reference-data` or `seed-address-reference-data`); the form lists
   only active provinces with a plate code.
4. The CentralDb migration `AddPilotApplications` applied ([migrations.md](migrations.md)).

The existing contact form notice at `/privacy-notice` also names the controller only as "Wasla"; that gap is separate
from this form.

## Storage

An application is a `ContactMessages` row with `Topic = PilotApplication` (5), the stored fields of a contact message,
and `CityPlateCode` (`varchar(2)`, `01` to `81`, checked against the active city catalog when stored). The note is the
`Message` column and may be empty. `PrivacyNoticeAcknowledged` is false, because the form asks for no acknowledgement.
No IP address or other request data is stored.

The form carries a submission id generated when it is first shown and kept through validation errors and a failed save.
It becomes the row's id: posting the same form again (a double click, a resubmitted page) finds the stored row and
stores and sends nothing. The submit button also ignores a second click while the form is being sent.

## Notification and its failure

The row is saved first; only a failure of that save reaches the visitor ("Your application could not be saved", with
the typed values kept and a "Send again" button). The team email is sent after the save, outside any transaction, and
its outcome is recorded on the row (`NotificationStatus`, `NotificationAttemptedAtUtc`):

- `Sent`: the SMTP server accepted it. `Failed`: sending threw; the log names the row id, the topic, the failure
  category and the exception type, never what the visitor typed. `NotConfigured`: `Email:Provider` is not `Smtp`
  (Development, tests); nothing is sent and nothing claims delivery.
- The visitor sees the received state in every case, because the application is stored.

Central Admin lists pilot applications at `/admin/contact-messages` (filter "Pilot applications", `?kind=pilot`). The
detail shows the city and the notification state, and offers "Send notification again" when the notification failed,
was not configured, or started more than 15 minutes ago without an outcome. The resend claims the row in one
conditional update, so two administrators or a double click send one email; a notification already sent is never sent
again. In a Log email setup the page says that nothing was sent.

## Tests

`PilotApplicationModelTests`, `PilotApplicationServiceTests`, `PilotPageWebTests` and the pilot cases of
`AdminContactMessagesWebTests` in `tests/Wasla.UnitTests/`, and `Contact/PilotApplicationBrowserTests` (headless
Chromium, `Category=Browser`) for the 1440 and 390 px layouts, Arabic right to left, long Russian text, keyboard order,
error focus and a double click. No test sends email or SMS.
