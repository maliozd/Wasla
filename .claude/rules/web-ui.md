---
paths:
  - "src/Wasla.Web/**"
---

# Wasla.Web UI rules

These rules add detail for Wasla.Web to `AGENTS.md` sections 6, 12 and 13. The
canonical documents are `docs/frontend/architecture.md`,
`docs/frontend/design-system.md`, `docs/orders/live-screen.md` and
`docs/architecture/signup-registration.md`.

## Localization

- New Web strings go into `SharedResource` with dotted keys (`Area.Key`, for
  example `Orders.NoOrdersFound` or `Common.Save`).
- Add each new key to the four culture files,
  `SharedResource.{tr-TR,en-US,ar-SA,ru-RU}.resx`, with a non-empty value and
  the placeholders of the `en-US` text. `SharedResourceParityTests` enforces
  this.
- Also add the key to the neutral `SharedResource.resx` with the Turkish text;
  some feature tests require it there. The parity test lets the neutral file be
  a subset, so older keys missing from it are not a defect to fix in unrelated
  work.
- JavaScript receives its messages from Razor through a localized config object
  (for example `window.waslaOrdersOptions.messages`). Scripts never embed UI
  sentences.
- Arabic uses the existing right-to-left layout (`dir="rtl"`, `rtl.css`).

## UI conventions

- Keep controls compact, aligned and consistent, and the tenant UI simple and
  operational.
- Clickable text is a real `<a href>` when it navigates and a `<button>` when it
  acts, never a styled `span` or `div`.
- Notification Settings is one shared modal (`_NotificationSettingsModalShell`)
  with one save path (`/notification-settings`). It opens from the Live Screen
  settings menu, the Live Screen order-training panel and Order Settings
  (`/settings/orders`), not from the Orders page header or the account
  dropdown. Do not add a second modal or save path.
- Browser notification permission is requested only when the user turns
  browser notifications on. Opening the modal, testing the sound and saving
  never request it.
- The Orders page is search and history: it starts no live polling, sound or
  browser notifications. Platform and status filters apply on change; date
  filters keep their explicit Filter and Clear buttons.
- Turkish phone inputs (signup and contact) show a fixed `+90` prefix and group
  the national number as `532 123 45 67`. What is stored follows each field's
  contract in `docs/architecture/signup-registration.md`.

## Hosts and links

- Build tenant, sign-in and central homepage links with `TenantWelcomeUrlBuilder`
  and canonical public URLs with `PublicSiteUrls`. Do not duplicate host,
  scheme or port logic in Razor, and do not hardcode production URLs.
- The builders keep a non-default request port only in Development and Staging.

## Signup and registration status pages

- The signup page uses the public layout. Registration status and checkout
  pages use the minimal `_RegistrationStatusLayout`: logo and language menu,
  no public header or footer.
- A tenant host shows the registration's status page by state: no known
  registration shows tenant-not-found; awaiting payment shows the pending page
  (checkout only for the browser that owns the registration); payment succeeded
  shows that the panel is being prepared; provisioned shows that the panel is
  ready, with sign-in.
- "Back to homepage" on a status page served from a tenant host points to the
  central homepage, not to `/` on the tenant host.
