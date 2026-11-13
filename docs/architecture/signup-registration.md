# Signup registration drafts and owner phone verification

## Purpose and scope

This document owns the server side of the public registration flow of Wasla Orders up to the payment-pending registration (WAS-192): the browser-bound registration draft, its cookie and lifetimes, mandatory SMS verification of the account owner's mobile phone, the SMS sender modes, the JSON API the signup page calls, and the boundary at which a verified draft becomes a `PendingRegistration`.

It does not own payment ([billing.md](billing.md), WAS-149), the real SMS provider (WAS-193), the registration page itself (WAS-194) or automatic provisioning (WAS-195). Signup access, the ownership cookie of a submitted registration and the existing abuse limits stay in [tenancy.md](tenancy.md#signup-and-checkout-access).

## Status

Implemented (WAS-192): the draft and its cookie, the section saves, owner phone verification with the fake sender, the address check, the gated submit, the SMS sender modes, migration `AddRegistrationDrafts` and the cleanup service. The form `POST /signup` is gone. The signup page's script that uses the API below, and the new resource keys, arrive with WAS-194; on the WAS-192 branch alone the old page's form posts to a `405`, so the two pull requests ship together.

| Layer | Where |
|-------|-------|
| Contracts | `src/Wasla.Application/Abstractions/Onboarding/RegistrationDrafts/` (`IRegistrationDraftService`, `IRegistrationPhoneVerificationService`, models, `SignupErrorCodes`), `src/Wasla.Application/Abstractions/Sms/ISmsSender.cs` |
| Rules | `src/Wasla.Application/Onboarding/PendingRegistrationRequestValidator.cs` (`PendingRegistrationRules` by section), `TurkishMobilePhone.cs` |
| Services | `src/Wasla.Infrastructure/Services/RegistrationDraftService.cs`, `RegistrationPhoneVerificationService.cs`, `RegistrationDraftRecords.cs`, `RegistrationPhoneVerificationSendLock.cs`, `RegistrationDraftCleanup.cs`; `PendingRegistrationService.CreateAsync` (internal) |
| SMS | `src/Wasla.Infrastructure/Sms/` (`DisabledSmsSender`, `FakeSmsSender`, `FakeSmsOutbox`, `SmsMessageTemplates`, `Templates/*.txt`) |
| Registration | `AddWaslaRegistrationDrafts` and `AddWaslaSms` in `src/Wasla.Infrastructure/DependencyInjection/RegistrationDraftServiceCollectionExtensions.cs`, called by Web `Program.cs` |
| Web | `SignupDraftController`, `SignupController` (page), `Models/Signup/SignupDraftApiModels.cs`, `SignupDraftResponses.cs`, `Security/SignupDraftCookie.cs` |
| Tests | `tests/Wasla.UnitTests/Signup/`: `SignupDraftApiTests` and `SignupSmsModeTests` over HTTP, `RegistrationDraftCleanupTests`, `RegistrationPhoneRulesTests`, and on SQL Server `RegistrationDraftSqlServerTests` and `RegistrationDraftMigrationSqlServerTests` ([../operations/testing.md](../operations/testing.md#sql-server-concurrency-tests)); the draft helper `SignupDraftClient.cs` |

## Flow

Plan → Restaurant → Account owner → SMS verification → Review → Secure payment → Verified provider result → Provisioning → Ready.

| Step | Server operation | Writes |
|------|------------------|--------|
| Plan | `PUT /signup/draft/plan` | Draft (created by the first successful save) |
| Restaurant | `PUT /signup/draft/restaurant` | Draft |
| Account owner | `PUT /signup/draft/owner`, validated before any SMS | Draft (password hash only) |
| SMS verification | `POST /signup/draft/phone/send`, `POST …/verify`, `PUT /signup/draft/phone` | Verification rows; the proof on the draft |
| Review | `GET /signup/draft` (`canReview`, price) | Nothing |
| Secure payment | `POST /signup/draft/submit`, then the existing `/signup/pending/{id}` and checkout pages | `PendingRegistration` (`AwaitingPayment`) |
| Provider result, provisioning, ready | Unchanged: Development simulator or manual state; CLI and Central Admin provisioning (WAS-149, WAS-195) | — |

No `PendingRegistration` exists before the submit. The restaurant contact phone (mobile or landline) is a restaurant field; only the owner's mobile phone is verified.

## Draft identity

- Cookie `__Host-Wasla.SignupDraft` (`SignupDraftCookie`): a Data Protection payload (purpose `Wasla.Signup.RegistrationDraft.v1`, time-limited to the draft's expiry) naming one draft id. HttpOnly, `SameSite=Lax`, Secure, host-only, path `/`. The `__Host-` prefix makes the browser accept it only from this host over HTTPS without a Domain, so a cookie planted from a sibling or parent domain (cookie tossing) cannot stand in for it, and only this name is read. Development over plain HTTP, where a prefixed cookie cannot be set, uses `.Wasla.SignupDraft` (not Secure, path `/`). It works across instances only when they share the Data Protection key ring.
- Residual risk: whoever holds a browser's draft cookie holds its draft, including a verified phone proof. Editing the owner's email or password keeps the proof (only a number change clears it), as the issue requires; the cookie is HttpOnly and host-only, and the draft's lifetime bounds the exposure.
- The draft id comes only from this cookie: never from a body, query or route. A tampered, expired or unreadable cookie names no draft.
- The first successful section save of a browser without a draft cookie creates the draft and issues the cookie. Every successful write issues it again with the extended expiry. A write whose cookie names no live draft gets `409 Signup.Draft.Missing` and the cookie is cleared; the next save starts a new draft. Two first saves sent at the same moment create two drafts, and the later cookie wins; the page should finish its first save before sending another write.
- A successful submit clears the draft cookie and grants the existing ownership cookie `.Wasla.SignupRegistration` for the new registration.

## Draft storage, lifetimes and personal data

| Value | Default | Kind | Configuration |
|-------|---------|------|---------------|
| Code length | 6 digits | Approved, fixed | `PhoneVerificationPolicy.CodeLength` |
| Code lifetime | 5 minutes | Approved | `Wasla:Signup:PhoneVerification:CodeLifetime` |
| Resend wait | 60 seconds | Approved | `Wasla:Signup:PhoneVerification:ResendWait` |
| Wrong codes before the lock | 5 | Approved | `Wasla:Signup:PhoneVerification:MaxFailedAttempts` |
| Lock duration | 15 minutes | Tunable | `Wasla:Signup:PhoneVerification:LockoutDuration` |
| Draft idle lifetime (each save extends it) | 24 hours | Tunable | `Wasla:Signup:Draft:IdleLifetime` |
| Draft maximum lifetime | 3 days | Tunable | `Wasla:Signup:Draft:MaxLifetime` |
| Phone proof lifetime | 24 hours | Tunable | `Wasla:Signup:Draft:PhoneProofLifetime` |
| Cleanup interval | 15 minutes | Tunable | `Wasla:Signup:Draft:CleanupInterval` |
| Payment-pending registration validity | 7 days | Existing | `Wasla:TenantOnboarding:PendingRegistrationExpiryDays` |

Approved values are product decisions; change them only with one. The host refuses to start with a value outside these bounds: code lifetime 1–30 minutes, resend wait 10 seconds–10 minutes and shorter than the code lifetime, wrong codes 1–20, lock 1 minute–24 hours, idle lifetime 1 hour–7 days, maximum lifetime from the idle lifetime to 30 days, proof lifetime 10 minutes up to the maximum lifetime, cleanup 1 minute–1 day, send limits 1–100,000.

- **Sections.** A section is stored only after it passed its server-side validation, so a saved section is complete. The rules are those of `PendingRegistrationRequestValidator`, split by section and composed back into the full validator (one rule set, not a parallel one). Every rule carries a SharedResource key as its message, which is the field error `code`. The owner rules now require a Türkiye mobile phone.
- **Türkiye address.** City, district, neighbourhood and street (or another address line) are required; building, floor, door, postal code, note and map link are optional. The neighbourhood is required as text (`Validation.NeighborhoodRequired`): any name is accepted, and `neighborhoodId` stays optional because the catalog does not hold every neighbourhood or village. A draft saved before this rule keeps its restaurant section as saved; the submit validates the stored values again, so it answers `Signup.Draft.ValidationFailed` with the neighbourhood field error and the page asks for it on the restaurant step. Nothing is migrated or filled in.
- **Password.** Hashed at the owner save with the established BCrypt boundary (`BCrypt.Net.BCrypt.HashPassword`, as `PendingRegistrationService` does today). The plaintext exists only in that request: it is not stored, logged, returned or echoed (`RegistrationOwnerInput.ToString` leaves it out, and request bodies are not logged). The draft answers only `passwordSet`. An empty password on a later save keeps the hash.
- **Hand-off.** The submit copies the stored hash into `PendingRegistrations.PasswordHash`; provisioning copies that into the owner `AppUser` as today. Nothing is hashed twice, and no plaintext reaches the registration.
- **After submit.** In the submit transaction the draft's personal data (restaurant, owner, hash, phone) is cleared. The row keeps its id, times and registration id until it expires, so a repeated submit returns the same registration.

## Owner mobile phone

- **Normalization.** Spaces, hyphens, dots and parentheses are removed. An optional leading `+90`, `0090`, `90` (12 digits in total) or `0` (11 digits in total) is removed. What remains must be exactly 10 digits starting with `5`, and is stored in E.164: `+905321234567`. Anything else, including other country codes and landlines, is `Validation.MobilePhoneInvalid`; empty is `Validation.MobilePhoneRequired`.
- **Türkiye only.** Owner numbers outside Türkiye are an open product decision.
- **Display.** `owner.mobilePhone` in the draft is the 10-digit national number (`5321234567`); the page shows the fixed `+90` prefix and `532 123 45 67`. The phone status shows `maskedPhone` `+90 5** *** ** 67`.
- **Restaurant phone.** `Mobile` or `Landline`, separate from the owner's verified mobile and never verified. The page treats both types alike (`signup-phone-format.js`): after the fixed `+90` it groups the number as `212 345 67 89` while it is typed or pasted, removes a `+90`, `0090` or trunk `0` in front once there are more than 10 digits, keeps the caret after the digit it followed, and sends the national digits (`2123456789`), so switching the type changes nothing. Both phone fields (business and the owner's mobile, also in the phone step's "change number") hold at most 10 national digits, counted after the prefix, not in characters (owner's decision): once 10 are there, a digit typed anywhere is refused and no digit already there is pushed out, while deleting, Backspace and a selection replaced by as many digits work as usual; a longer paste keeps its first 10 national digits after its prefix is removed, as asked. The server still refuses a business phone with more than 10 national digits sent to it directly (`Validation.BusinessPhoneTooLong`, `TurkishBusinessPhone`, which reads the number the way the page does) and never shortens it; the owner's mobile keeps its exact rule (`Validation.MobilePhoneInvalid`) before any code is sent. There is no extension field. The landline hint names the chosen city's area code (`Signup.LandlineAreaCodeHint`, "{0} alan kodu: {1}", as in "Ordu alan kodu: 452"); İstanbul lists 212 and 216, because the catalog stores 212 only. The codes stay left to right in Arabic. Before a city is chosen the hint is the localized example (`Signup.LandlinePhoneHint`, "Örnek: +90 212 345 67 89"). The restaurant step asks for the address before the contact details, so the city usually comes first. The hint is only a hint: the number is never changed to match it or checked against the city. The server keeps the value as sent: trimmed, at most 50 characters, and at most 10 national digits. It checks no other format and no minimum for either type (a mobile business number need not start with 5, and a short number such as a 444 line is accepted); this gap is known and left to a product decision.
- **Registration.** `PendingRegistrations.OwnerPhone` (existing column) receives the verified E.164 number.

## Phone verification

- **Before a send.** The owner section (name, email, mobile phone, password) must be saved; otherwise `Signup.Phone.OwnerIncomplete`.
- **Codes.** `RandomNumberGenerator`, 6 digits with leading zeros. Stored only as a Data Protection payload (purpose `Wasla.Signup.PhoneVerificationCode.v1`) that binds the code to its verification row; compared in constant time. Never stored in plaintext, never logged, never returned by an ordinary endpoint.
- **One verification row per draft and number.** It holds the active code, its expiry, the time of the last send, sends, failed attempts, a lock end and the verification time. Changing to an earlier number of the same draft finds that number's row again, with its failures and lock.
- **Resend.** Allowed from 60 seconds after the last send. The new code replaces the old one, which no longer verifies. Failures are kept. A send is refused while the number is locked.
- **Verify.** Each check first reserves one attempt with a conditional increment (`attempts < max`, code active, not expired, not locked) and only then compares. The reservation that reaches the limit sets the lock in the same statement, so no stopped request can leave the attempts used up without a lock. A match stores the proof on the draft (conditional on the draft's current number and on the draft not being submitted) and then consumes the code with an update conditional on the exact stored code (so a resend that replaced it in between wins), in one transaction; a right code checked with the last attempt verifies and lifts the lock, unless a wrong code checked at the same moment has already dropped the code with the lock, in which case the right code is refused and the number stays locked (it fails closed). Concurrent checks can therefore never use more than 5 attempts or consume a code twice. A check that cannot reserve its attempt, finds its code gone after reserving one, or cannot consume its code answers from one read of the stored state, in the same order as a check's first look (draft submitted, verified, locked, then no active code, expired or incorrect), and returns the status built from that read, so the error and the status agree. A check refused around the fifth wrong code, which locks the number and drops the code together, therefore answers `Signup.Phone.Locked`, not `NoActiveCode`; a check whose code another check used answers `Signup.Phone.AlreadyVerified`, never `CodeIncorrect`. When the same right code is checked several times at once, a check beyond the fifth finds the attempts used up and answers `Signup.Phone.Locked` until the winning check has finished; the page then reads the status, which says `verified`.
- **Lock order.** Every write that touches both locks the draft first and then its verification rows (storing a proof, a number change, the submit), so they wait for each other on SQL Server instead of deadlocking.
- **Wrong code.** `Signup.Phone.CodeIncorrect` with `attemptsRemaining`. The 5th wrong code locks the number for the lock duration and clears its code (`Signup.Phone.Locked`). After the lock the next send resets the failures; a code whose attempts are used up after its lock passed is spent (`Signup.Phone.NoActiveCode`, status `notSent`). A row with every attempt used and no lock, which an earlier build could leave, counts like a passed lock. An expired code is `Signup.Phone.CodeExpired` and not counted; a value that is not 6 digits is `Signup.Phone.CodeFormatInvalid` and not counted.
- **Number change** (owner save with another number, or `PUT /signup/draft/phone`). The proof is cleared and every outstanding code of the draft is cleared, so a code sent to the previous number is rejected (`Signup.Phone.NoActiveCode`). The same number changes nothing. Restaurant or other owner edits keep the proof.
- **Replay and foreign codes.** A consumed code is gone (`Signup.Phone.AlreadyVerified` or `NoActiveCode`). A code is only ever compared with the requesting draft's own row for its current number, so another draft's code is simply wrong.
- **Proof.** `PhoneVerifiedNumber` and `PhoneVerifiedAtUtc` on the draft. It counts while the number equals the owner's current number and for the proof lifetime. The client never states that a number is verified.
- **Delivery.** A send is reserved in a short transaction (limits counted, code stored) and the SMS is handed to the sender after the commit, outside any transaction. A refused, failed or timed-out delivery clears that code, keeps the send counted and the resend wait, and answers `502 Signup.Phone.DeliveryFailed`. Adapters report timeouts as results; one that throws instead is handled the same way (a cancellation that is not the request's own as a timeout, anything else as unavailable), never with a `500` that leaves a usable code. A message that was delivered after a timeout carries an unusable code; the user requests a new one.
- **Status state.** `verified` when the proof counts; else `unavailable` when no sender is configured; else `locked` while locked; else `codeSent` while a code is active; else `expired` when the last code expired unused; else `notSent`.

## Limits

| Bound | Default | Scope and store | Over the bound |
|-------|---------|-----------------|----------------|
| Draft writes (section saves, number change) | 60 per 10 minutes | Client network, process memory (`AuthenticationRateLimiting:SignupDraftSave`) | `429 Signup.TooManyAttempts` |
| Draft creation | 20 per hour | Client network, process memory (`SignupDraftCreation`), taken by every draft write without a draft cookie, also one that then fails validation | `429 Signup.TooManyAttempts` |
| Code sends | 10 per hour | Client network, process memory (`SignupPhoneSend`) | `429 Signup.TooManyAttempts` |
| Code checks | 30 per 10 minutes | Client network, process memory (`SignupPhoneVerify`) | `429 Signup.TooManyAttempts` |
| Address checks | 60 per 5 minutes | Client network, process memory (`SignupAddressAvailability`) | `429 Signup.TooManyAttempts` |
| Submit | 10 per hour | Client network, process memory (existing `SignupSubmission`) | `429 Signup.TooManyAttempts` |
| Wrong codes | 5 per number, then the lock | Draft and number, CentralDb | `429 Signup.Phone.Locked` |
| Sends per draft | 6 over the draft's life | CentralDb (`MaxSendsPerDraft`) | `429 Signup.Phone.SendLimitReached` |
| Sends per number | 5 per hour, 10 per 24 hours, across drafts | CentralDb (`MaxSendsPerNumberPerHour`, `MaxSendsPerNumberPerDay`) | `429 Signup.Phone.SendLimitReached` |
| Global SMS volume | 300 per hour, 2,000 per 24 hours | CentralDb (`MaxSendsPerHour`, `MaxSendsPerDay`) | `503 Signup.Phone.CapacityReached`, one warning log per window |
| Active registrations per owner email | 3 (existing) | CentralDb, at submit | `429 Signup.TooManyAttempts` (same answer as the network limit) |

- The client network is the existing trusted-proxy identity (`ClientNetworkPartition`: IPv4 address or IPv6 /64, a forwarded address only from a trusted proxy). Network limits use the existing limiter and are per process, like the other signup limits ([authentication.md](authentication.md#credential-rate-limiting)).
- Draft, number and global limits are counted in CentralDb, so they hold across processes and restarts. On SQL Server a send counts and records itself under the transaction-owned application lock `Wasla.RegistrationPhoneVerification.Send`, released when that short transaction ends, so simultaneous sends cannot exceed them. SQLite tests run without the lock.
- Every value except the approved ones is an operational default to tune. The global volume needs the SMS price and expected signup volume (WAS-193); the per-number values need real delivery data. The per-network limits on code sends and draft creation count an IPv4 address as one client: many users behind one carrier NAT or office router share them, so they need tuning against real traffic.

## SMS sender

| `Wasla:Sms:Mode` | Effect |
|------------------|--------|
| Missing, empty or `Disabled` | A sender that is not available: status `unavailable`, sends `503 Signup.Phone.Unavailable`, so no signup can pass the submit boundary (fail closed) |
| `Fake` | Development only. Messages go to a bounded in-memory outbox; nothing is sent or logged. Outside Development the host refuses to start |
| Any other value | The host refuses to start |

- The real adapter, its mode value, credentials, sender identity, approved message text and WebOTP format are WAS-193. It implements `ISmsSender` in Infrastructure and keeps vendor types and credentials on its side.
- The SMS text is a backend template, embedded like the e-mail templates: `src/Wasla.Infrastructure/Sms/Templates/RegistrationPhoneVerification.{tr,en,ar,ru}.txt`, with `{{Code}}` and `{{Minutes}}` and the request's UI culture. A culture without its own template uses Turkish; without the Turkish template the send fails closed with `Signup.Phone.Unavailable` and logs an error. The wording (Wasla identity, the code, its validity, "do not share") is a draft until WAS-193 approves it, and each template says so in a `#` comment line, which is never sent.
- **Reading the fake code.** In Development with `Fake`, `GET /signup/draft/phone/fake-outbox` returns the messages sent for the browser's own draft, with their codes; each fake message carries its draft as `SmsMessage.Reference`. It answers 404 everywhere else, like the checkout simulator, and while public signup is closed. Web reads it through the Application abstraction `IDevelopmentSmsOutbox`, which only the Development `Fake` mode registers. Tests do not use the endpoint: test hosts register the fake sender and its `FakeSmsOutbox` explicitly and read the outbox through DI, also in a Production-environment host. Configuration can never enable the fake outside Development.
- Logs name the draft, the operation and the outcome category; never the code, the message text or the full number.

## HTTP API

Conventions:

- Base path `/signup` on the marketing host (other hosts redirect there, [tenancy.md](tenancy.md#host-and-path-policy)). JSON with camelCase names; enumerations are strings.
- Writes validate the antiforgery token from the header `RequestVerificationToken` (the page renders the token; the antiforgery cookie comes with the page). A failed check is `400` without a JSON body.
- Every route, the Development fake outbox included, is closed with public signup (`Wasla:TenantOnboarding:PublicSignupEnabled`): `404` without a body.
- Answers are `Cache-Control: no-store`. Request bodies are limited to 16 KB.
- Errors are `SignupApiError`: `{ code, message, fieldErrors, retryAfterSeconds, phone, missingSections, registrationUrl }`. `code` is a stable SharedResource key and `message` its text in the request culture. `429` and `503` answers carry `Retry-After` when waiting helps.

| Method and route | Antiforgery | Network limit | Request | Success | Errors |
|------------------|-------------|---------------|---------|---------|--------|
| `GET /signup/draft` | No | — | — | `200` draft state (`exists: false` without a draft; an invalid cookie is cleared) | — |
| `PUT /signup/draft/plan` | Yes | `SignupDraftSave` (+ `SignupDraftCreation` when creating) | `{ planCode, billingPeriod }` | `200` draft state; creates the draft and its cookie when there is none | `400 Signup.Draft.InvalidRequest`, `400 Signup.Draft.ValidationFailed` (`Validation.PlanInvalid`, `Signup.EnterpriseContactOnly`, `Validation.BillingPeriodInvalid`), `409 Signup.Draft.Missing`, `409 Signup.Draft.Submitted`, `429` |
| `PUT /signup/draft/restaurant` | Yes | As above | `SignupRestaurantSectionRequest` | `200` draft state; keeps the phone proof | As above, with the restaurant field codes; a taken or reserved address is `Signup.SlugUnavailable` on `slug` |
| `PUT /signup/draft/owner` | Yes | As above | `{ fullName, email, mobilePhone, password, confirmPassword }` | `200` draft state; a changed number clears the proof | As above, with the owner field codes |
| `GET /signup/draft/phone` | No | — | — | `200` phone status | `409 Signup.Draft.Missing` |
| `POST /signup/draft/phone/send` | Yes | `SignupPhoneSend` | — | `200` phone status `codeSent` | `409 Signup.Draft.Missing`, `Signup.Draft.Submitted`, `Signup.Phone.OwnerIncomplete`, `Signup.Phone.AlreadyVerified`; `429 Signup.Phone.ResendNotAvailable`, `Signup.Phone.Locked`, `Signup.Phone.SendLimitReached`, `Signup.TooManyAttempts`; `502 Signup.Phone.DeliveryFailed`; `503 Signup.Phone.Unavailable`, `Signup.Phone.CapacityReached` |
| `POST /signup/draft/phone/verify` | Yes | `SignupPhoneVerify` | `{ code }` | `200` phone status `verified` | `400 Signup.Draft.InvalidRequest`, `Signup.Phone.CodeFormatInvalid`, `Signup.Phone.CodeIncorrect`, `Signup.Phone.CodeExpired`; `409 Signup.Draft.Missing`, `Signup.Draft.Submitted`, `Signup.Phone.OwnerIncomplete`, `Signup.Phone.NoActiveCode`, `Signup.Phone.AlreadyVerified`; `429 Signup.Phone.Locked`, `Signup.TooManyAttempts` |
| `PUT /signup/draft/phone` | Yes | `SignupDraftSave` | `{ mobilePhone }` | `200` phone status (`notSent` for a new number) | `400 Signup.Draft.InvalidRequest`, `Signup.Draft.ValidationFailed` (`owner.mobilePhone`); `409 Signup.Draft.Missing`, `Signup.Draft.Submitted`, `Signup.Phone.OwnerIncomplete`; `429` |
| `POST /signup/draft/submit` | Yes | `SignupSubmission` | — (any body is ignored) | `200 { registrationId, redirectUrl }`; ownership cookie granted, draft cookie cleared; a repeated submit returns the same registration | `409 Signup.Draft.Missing`, `Signup.Draft.Incomplete` (`missingSections`), `Signup.Phone.NotVerified`, `Signup.SlugUnavailable` (field `restaurant.slug`); `400 Signup.Draft.ValidationFailed`; `429 Signup.TooManyAttempts`; `500 Signup.ProvisioningFailed` |
| `GET /signup/address-availability?slug=` | No | `SignupAddressAvailability` | query `slug` | `200 { slug, status }`, status `available`, `taken` (also reserved) or `invalid`; advisory only, never a reservation | `429` |
| `GET /signup/draft/phone/fake-outbox` | No | — | — | Development with `Fake` only: `200 { messages: [{ maskedPhone, code, sentAtUtc }] }` | `404` everywhere else |

A draft that is already submitted answers its writes with `409 Signup.Draft.Submitted` and `registrationUrl`. Field errors are `{ section, field, code, message }`, with `section` `plan`, `restaurant` or `owner` and `field` the camelCase request field.

Draft state (`GET /signup/draft`, every successful section save, and the page's initial state):

```json
{
  "exists": true,
  "expiresInSeconds": 86357,
  "submitted": false,
  "registrationUrl": null,
  "plan": { "complete": true, "planCode": "Pro", "billingPeriod": "Monthly" },
  "restaurant": {
    "complete": true,
    "businessName": "Burger House",
    "businessTypeCodes": ["burger"],
    "businessPhoneType": "Landline",
    "businessPhone": "212 555 12 34",
    "businessEmail": null,
    "slug": "burger-house",
    "country": "Türkiye",
    "cityId": 34, "districtId": 451, "neighborhoodId": null, "streetId": null,
    "city": "İstanbul", "district": "Kadıköy", "neighborhood": null,
    "streetAddress": "Moda Cad.", "buildingNumber": "12", "floor": null, "doorNumber": null,
    "addressNote": null, "postalCode": null, "locationUrl": null
  },
  "owner": { "complete": true, "fullName": "Ayşe Yılmaz", "email": "ayse@example.test", "mobilePhone": "5321234567", "passwordSet": true },
  "phone": { "state": "codeSent", "maskedPhone": "+90 5** *** ** 67", "codeLength": 6, "codeExpiresInSeconds": 241, "resendAvailableInSeconds": 0, "lockedForSeconds": null, "attemptsRemaining": 4 },
  "canReview": false,
  "price": { "planCode": "Pro", "billingPeriod": "Monthly", "isSet": true, "amount": 600.00, "currency": "TRY", "vatIncluded": true, "priceText": "600 TL / ay", "vatText": "KDV dahil" }
}
```

Without a draft, `exists` is false, `expiresInSeconds` and `price` are null, every section has `complete: false` and empty values, and `phone` is `notSent` (or `unavailable`). `price` comes from `IBillingPlanPriceReader` (the price in effect, as on the plan cards); without one, `isSet` is false, the amount fields are null and `priceText` is the localized "price not set yet". No VAT amount, total with tax or renewal date is computed.

Phone status (`GET /signup/draft/phone`, every phone answer, `phone` in errors of the phone endpoints). Durations are whole seconds from the server's clock, rounded up:

```json
{ "state": "locked", "maskedPhone": "+90 5** *** ** 67", "codeLength": 6, "codeExpiresInSeconds": null, "resendAvailableInSeconds": null, "lockedForSeconds": 812, "attemptsRemaining": null }
```

`resendAvailableInSeconds` is 0 when a code can be sent now and null when sending is not possible (no number, verified, locked, unavailable). `codeExpiresInSeconds` and `attemptsRemaining` are set only while `codeSent`.

Error:

```json
{
  "code": "Signup.Phone.CodeIncorrect",
  "message": "Kod hatalı. Lütfen tekrar deneyin.",
  "fieldErrors": [],
  "retryAfterSeconds": null,
  "phone": { "state": "codeSent", "maskedPhone": "+90 5** *** ** 67", "codeLength": 6, "codeExpiresInSeconds": 180, "resendAvailableInSeconds": 0, "lockedForSeconds": null, "attemptsRemaining": 2 },
  "missingSections": null,
  "registrationUrl": null
}
```

## Signup page

`GET /signup` stays one Razor page whose steps call the API above. It embeds the browser's draft, so it is sent with `Cache-Control: no-store` (and `Pragma: no-cache`), like the API. Besides the existing fields of `SignupViewModel` (plans, prices, business categories, cities, application domain, simulator flag) it receives, set by the server and never bound:

| Field | Meaning |
|-------|---------|
| `InitialDraft` | The draft state as `GET /signup/draft` returns it, so a reload restores the steps |
| `Endpoints` | `SignupDraftEndpoints`: every API URL above plus the address lookups; `FakeSmsOutbox` is null outside Development with `Fake` |
| `RequestedPlanCode` | The `?plan=` query normalized when it names a known plan, otherwise null |
| `IsPhoneVerificationAvailable` | False without an SMS sender: the page can say up front that signup cannot be completed |
| `PhoneCodeLength` | 6 |
| `SupportEmail` | `Wasla:Signup:SupportEmail`, default `destek@wasla.tr` |

The address lookups `/signup/districts`, `/neighborhoods` and `/streets` stay as they are. After a load the page puts the draft's saved address back through them, for at most 15 seconds. A Continue on the restaurant step waits for that instead of reporting a field that is still empty. If the time runs out, the lookups are cancelled, the page says so, and Continue tries again. From the start of the restore, a street the visitor types or clears is kept, and choosing another city or district drops the saved address and cancels its lookups. A neighbourhood the visitor types is kept too: the city and district they did not touch still come back, the typed neighbourhood gets the id of the district's neighbourhood with that name, if any, and the saved street, which belongs to the saved neighbourhood, is not put under it. A lookup answer is used only for the newest choice of its level, so an answer for an earlier city, district or neighbourhood is ignored. The lists only suggest names: when a neighbourhood or street lookup fails, the saved neighbourhood and street stay in their fields with the ids the server stored for them, and the next save is validated by the server as usual. There is no form `POST /signup` any more (it answers 405 and creates nothing); the page submits only through `POST /signup/draft/submit`. The page that does so is WAS-194's, shipped together with this change.

### Address fields

City, district, neighbourhood and street use one searchable list control (`signup-combobox.js`, with the matching in `signup-address-search.js`), an editable combobox in the WAI-ARIA pattern: focus stays in the field and the active option is named by `aria-activedescendant`.

- **City and district** must be chosen from their lists. Each keeps its native `<select>`, out of sight and out of the tab order, as the value, the client validation and the change events the page uses; focusing the select (an error link, the page's error handling) focuses the visible field. Typed text that is not chosen goes back to the chosen name on Escape, Tab or leaving the field.
- **Neighbourhood and street** are the text fields themselves; the lists only suggest. A name picked from the list keeps its catalog id while that name is in the field. Otherwise the id is that of the catalog entry with exactly the typed name, or else of the one entry with that name in another case or Turkish letter form (`caferaga` for `CAFERAĞA`; the server then saves the catalog's name), or none, so typed text never keeps an id from an earlier choice. While the typed text is no catalog name in that sense, the list ends with a row that keeps it as typed. Another neighbourhood empties the street and loads its own streets; the same catalog neighbourhood again (its `change` after the typing, or its name in another case) keeps the street and its list.
- **Matching** ignores case by the Turkish rules and treats `ç ğ ı ö ş ü â î û` like their plain letters, so `kadikoy` finds `Kadıköy`. Names starting with the query come first, then names with a later word starting with it, then names containing it, then names holding every word of a several-word query; each group keeps the server's order. Labels and ids are never changed.
- **Keyboard.** Typing opens the list; the arrows, Page Up and Page Down move the active option without changing the field; Enter chooses it and never continues the step while the list is open; Escape closes the list; Tab closes it without choosing and moves on. Focus alone opens nothing.
- **States.** The list says when it is loading, when nothing matches, when there is nothing to suggest, and when its lookup failed, with a retry. A failed list also shows a message under its field with the same retry. A failed district list leaves the district field usable and empty, so Continue asks for a district (`Validation.DistrictRequired`) instead of saving without one; the retry loads that city's districts again and selects the district that was wanted, including the saved one during a restore, without touching the neighbourhood and street.
- **Placement.** The list opens under its field with the field's width and shows about six and a half rows, scrolling the rest. With too little room below (including the mobile summary strip, which covers the bottom of the screen), it opens above the field. The list, with its padding, border and state line, stays within the visible room, down to two rows on a very short screen (a high zoom).
- **Browser autofill.** The four fields carry `autocomplete="wasla-list-search"`, a token no browser reads as an address field. Chrome 157 with a saved address ignored `autocomplete="off"` there (and `type="search"`): its own popup, with "Manage addresses…", opened over the list on the city and street fields. With the unrecognised token it stays closed, and filling a saved address from another field, such as the postal code, leaves these fields alone. The token is not valid HTML for `autocomplete`, so a validator or DevTools may flag it; every other field keeps its standard token, so autofill and password managers work as before. Firefox and Safari were not checked.

### Steps and controls

- **Panel address.** Every edit of the business name makes the address again from it with the page's rules (Turkish letters to plain letters, spaces to hyphens), also after the visitor changed the address by hand (owner's decision); an empty name empties it. The visitor may still edit the address; the next name edit replaces it. A changed address drops its old availability answer at once and is checked again after the usual pause; an answer for an earlier address is ignored. A draft put back on load keeps its saved name and address. The server's availability and uniqueness checks at save and submit decide.
- **Verified phone through the account step.** While the number in the owner's field is the one the server says is verified (the same national digits; spacing or a typed `0`/`+90` make no difference), the step's action is "Devam et": it saves the owner's changes, which keep the proof, and opens the phone step without sending a code. That step shows "Telefonunuz doğrulandı", the number, "Özete geç" and "Telefon numarasını değiştir", and stays until the visitor continues. Another number turns the action back into "Doğrulama kodu gönder"; the code goes only by that choice (or the change form's send). A verify answer that comes back after the owner's number changed verifies and moves nothing; the page reads the server's state instead, and the submit stays refused until the current number is verified.
- **Restaurant step.** Business details, the panel address, the address, then the contact details: the city is chosen before the landline, whose hint names its area code. The page order is also the tab and reading order, in Arabic too, and the review lists the address before the phone and email.
- **Plan and period.** The plan, period and phone-type radios have `autocomplete="off"`, which keeps browsers from restoring their checked state. When Chrome reloads the page from its history (without the back/forward cache) it otherwise checks the visitor's last unsaved choice after the script drew the receipt, with no change event: Pro and Yearly checked beside a Starter, monthly receipt and monthly card prices. The saved draft and the server's prices decide them, as on any load.
- **Passwords.** Each password field has one text control at its end, "Göster" or "Gizle" (`Signup.Password.Show`, `Signup.Password.Hide`), named in full for what it does next (`Common.ShowPassword`, `Common.HidePassword`). Switching keeps the value, the caret and the focus: a pointer press leaves the focus in the field, a keyboard press on the control. Edge's own reveal button is hidden so there is only one.
- **After verification.** While the verify request runs, its button shows the usual busy state. An answer that confirms the number keeps the phone step for 1.2 seconds with "Telefon doğrulandı, devam ediliyor…" (`Signup.Phone.VerifiedMoving`), announced once from the step's status region, then opens the review with focus on its heading. Leaving the step, another phone state or the visitor's own "Özete geç" ends the wait; a wait that ended moves nothing, and nothing is sent or verified again. An answer that arrives after the visitor left the step starts no wait, and a draft that comes back verified does not wait either. With reduced motion the small spinner is not shown.

## Submit boundary

`POST /signup/draft/submit` is the only way a signup creates a `PendingRegistration`, and it uses nothing from the request:

1. The draft from the cookie; a submitted draft returns its registration.
2. Every section saved (`Signup.Draft.Incomplete` with `missingSections`), the full validation of the stored values (`Signup.Draft.ValidationFailed`), a self-service plan, the panel address free and not reserved (`Signup.SlugUnavailable`).
3. A current proof: the verified number equals the owner's stored mobile phone and is within the proof lifetime (`Signup.Phone.NotVerified`).
4. The per-owner-email cap and `Signup.TooManyAttempts`, as today.
5. One transaction: a conditional update claims the draft (not yet submitted), the registration is inserted as `AwaitingPayment` with the 7-day validity, the password hash, `OwnerPhone` = the verified E.164 number and `OwnerPhoneVerifiedAtUtc`, and the draft's personal data is cleared. A concurrent second submit waits for the claim and then returns the same registration.
6. The ownership cookie is granted, the draft cookie cleared, and the answer names `/signup/pending/{id}`.

A missing price does not block the submit, as today ([billing.md](billing.md#plan-prices)).

To keep later work from bypassing verification, `IPendingRegistrationService` offers no way to create a registration: `PendingRegistrationService.CreateAsync` is internal to Infrastructure and only the draft submit calls it, with the stored hash and the verified phone. For WAS-149: start a checkout only for a registration with `OwnerPhoneVerifiedAtUtc` set (registrations created before WAS-192 have none), and add the idempotency identity, price pinning and consent, the atomic address reservation (the `Slug` index on `PendingRegistrations` is not unique; the submit's check is a read followed by an insert, as today) and the hosted payment form.

## Provisioning

The tenant owner `AppUser` has no phone field, and `TenantMembership` stores no owner phone. The verified E.164 number and its verification time therefore stay on the `PendingRegistration`, which keeps its `TenantId` after provisioning. Carrying them into the owner account needs a tenant database migration and is WAS-195.

## Persistence

CentralDb only, migration `AddRegistrationDrafts`:

| Table | Holds |
|-------|-------|
| `RegistrationDrafts` | Id, created, updated and expiry times; plan code and period; the restaurant fields (business type codes as one delimited string); owner name, email, E.164 mobile phone and password hash; the time each section was saved; `PhoneVerifiedNumber`, `PhoneVerifiedAtUtc`; `SubmittedAtUtc`, `PendingRegistrationId` (unique when set). Every personal field nullable, cleared at submit. Index on the expiry |
| `RegistrationPhoneVerifications` | One row per draft and number (unique), cascading with the draft and deleted at submit: protected code, issue and expiry times, last send (also the concurrency token of sends), sends, failed attempts, lock end, verification time |
| `RegistrationPhoneVerificationSends` | One row per send for the number and global windows: draft id (no foreign key, so counts survive the draft), E.164 number, time, outcome. Indexes on number and time, and time. Kept for 24 hours |
| `PendingRegistrations` | New nullable `OwnerPhoneVerifiedAtUtc`; check constraint: set only together with `OwnerPhone` |

The migration is additive. `Down` drops the three tables and the column, and refuses (error 50192) while any registration has `OwnerPhoneVerifiedAtUtc` set, because that is verification evidence ([rollback guards](../operations/migrations.md#rollback-guards)). Apply it with `migrate-central` before the new Web starts; the application never applies it. It was verified on throwaway LocalDB databases only (`RegistrationDraftMigrationSqlServerTests`).

## Cleanup

A hosted service in Wasla.Web runs every cleanup interval and deletes, in bounded batches, drafts whose expiry has passed (their verification rows cascade) and send records older than 24 hours. Every read already treats an expired draft as missing, so correctness never waits for cleanup. The deletes are set-based and idempotent, so several Web instances may run them. It runs in Web because Web owns signup and the other hosts are unchanged; it logs counts only.

## Resource keys for the page

New keys returned by the API (the page adds them in every culture):

| Key | Turkish | English |
|-----|---------|---------|
| `Signup.Draft.Missing` | Kayıt oturumunuzun süresi doldu. Güvenliğiniz için girdiğiniz bilgiler silindi; lütfen yeniden başlayın. | Your registration session has expired. For your security, the details you entered were removed; please start again. |
| `Signup.Draft.Submitted` | Bu kayıt zaten gönderildi. | This registration has already been submitted. |
| `Signup.Draft.ValidationFailed` | Lütfen işaretli alanları düzeltin. | Please correct the highlighted fields. |
| `Signup.Draft.Incomplete` | Devam etmeden önce tüm adımları tamamlayın. | Complete every step before you continue. |
| `Signup.Draft.InvalidRequest` | İstek işlenemedi. Sayfayı yenileyip tekrar deneyin. | The request could not be processed. Reload the page and try again. |
| `Signup.Phone.OwnerIncomplete` | Doğrulama kodu istemeden önce hesap sahibi bilgilerini kaydedin. | Save the account owner details before requesting a verification code. |
| `Signup.Phone.AlreadyVerified` | Bu telefon numarası zaten doğrulandı. | This phone number is already verified. |
| `Signup.Phone.NotVerified` | Devam etmek için cep telefonu numaranızı doğrulayın. | Verify your mobile phone number to continue. |
| `Signup.Phone.CodeIncorrect` | Kod hatalı. Lütfen tekrar deneyin. | The code is incorrect. Please try again. |
| `Signup.Phone.CodeExpired` | Kodun süresi doldu. Yeni bir kod isteyin. | The code has expired. Request a new code. |
| `Signup.Phone.CodeFormatInvalid` | 6 haneli kodu girin. | Enter the 6-digit code. |
| `Signup.Phone.NoActiveCode` | Geçerli bir kod yok. Yeni bir kod isteyin. | There is no active code. Request a new code. |
| `Signup.Phone.ResendNotAvailable` | Yeni kod istemeden önce biraz bekleyin. | Please wait before requesting a new code. |
| `Signup.Phone.Locked` | Çok fazla hatalı deneme yapıldı. Biraz bekleyip yeni bir kod isteyin. | Too many incorrect attempts. Wait a while, then request a new code. |
| `Signup.Phone.SendLimitReached` | Çok fazla kod gönderildi. Lütfen daha sonra tekrar deneyin. | Too many codes have been sent. Please try again later. |
| `Signup.Phone.DeliveryFailed` | Kod gönderilemedi. Numaranızı kontrol edip tekrar deneyin. | The code could not be sent. Check your number and try again. |
| `Signup.Phone.Unavailable` | Telefon doğrulaması şu anda kullanılamıyor. Lütfen destek ile iletişime geçin. | Phone verification is not available right now. Please contact support. |
| `Signup.Phone.CapacityReached` | Telefon doğrulaması geçici olarak kullanılamıyor. Lütfen birkaç dakika sonra tekrar deneyin. | Phone verification is temporarily unavailable. Please try again in a few minutes. |
| `Validation.MobilePhoneRequired` | Cep telefonu numarası gereklidir. | Mobile phone number is required. |
| `Validation.MobilePhoneInvalid` | Geçerli bir Türkiye cep telefonu numarası girin, örneğin 532 123 45 67. | Enter a valid Turkish mobile number, for example 532 123 45 67. |

Existing keys the API returns: `Signup.TooManyAttempts`, `Signup.SlugUnavailable`, `Signup.EnterpriseContactOnly`, `Signup.ProvisioningFailed`, `Billing.PriceNotSet`, `Billing.CurrencyTry`, `Billing.VatIncluded`, `Billing.VatExcluded`, `Pricing.PricePerMonth`, `Pricing.PricePerYear`, and the field codes `Validation.*` of the signup validator. The SMS text is not a page resource (see [SMS sender](#sms-sender)).

## Open decisions

- Owner mobile numbers outside Türkiye.
- A "start over" action that deletes an unsubmitted draft; not part of the contract (an expired draft is deleted by cleanup).
- Whether the phone proof lifetime should simply follow the draft's lifetime.
- The operational values marked tunable above.

## Deferred work

| Issue | Left for it |
|-------|-------------|
| WAS-149 | Checkout from the verified registration only, idempotency identity, price pinning and consent, atomic address reservation, the iyzico hosted form |
| WAS-193 | The real SMS provider adapter and mode, sender identity, approved message text and WebOTP format, real tuning of the send limits |
| WAS-194 | The registration page consuming this API |
| WAS-195 | Automatic provisioning and copying the verified owner phone into the owner account |

## Known discrepancies

- WAS-192 assigns the draft → `PendingRegistration` promotion to WAS-149. Replacing the form `POST /signup` needs a gated submit now, so WAS-192 implements the minimal submit above and WAS-149 extends it.
