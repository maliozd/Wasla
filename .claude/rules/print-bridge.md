---
paths:
  - "src/Wasla.PrintBridge/**"
  - "src/Wasla.PrintBridge.Core/**"
  - "src/Wasla.PrintBridge.Service/**"
  - "src/Wasla.PrintBridge.Version.props"
  - "installer/**"
  - "tests/Wasla.PrintBridge.Tests/**"
---

# Print Bridge rules

These rules add detail for the Print Bridge client to `AGENTS.md` section 11.
The canonical documents are `docs/integrations/print-bridge.md`,
`print-bridge-service.md`, `print-bridge-installer.md` and
`print-bridge-updates.md` in the same folder.

## Projects

The project map is in `AGENTS.md` section 6.

- `Wasla.PrintBridge.Core` must not reference WinForms, WPF or WebView2.
- Only `Wasla.PrintBridge.Service` runs the engine. The tray app talks to it
  over the pipe and never starts an engine of its own.

## Names, settings and paths

- The product name is `Wasla Print Bridge`.
- Connection settings live in the `Wasla` section as `ServerUrl` and
  `AgentToken`. Use these names and add no aliases such as `BaseUrl`.
- Runtime data lives under `C:\ProgramData\Wasla\PrintBridge`, resolved through
  `PrintBridgePaths`. Tests use `IsolatedDataRoot` and never read or write the
  real folder.
- Do not hardcode production URLs or secrets.

## Setup and connection

- Manual setup asks only for the Wasla Web Panel URL (`ServerUrl`) and the
  device token. The client adds the API routes itself.
- `ServerUrl` is a primary field of the connection dialog, never hidden as an
  advanced option.
- Validate locally before any request: an empty or malformed URL, a query or
  fragment, an empty token. Trim the values and normalize the URL. When a saved
  connection is being changed, an empty token keeps the saved one.
- Check the candidate URL and token against the server before saving. Save only
  a verified connection, so a rejected candidate never replaces or clears the
  saved token.
- Automatic setup uses setup links (`wasla-printbridge://setup`). Do not add
  token-based server discovery unless a central discovery service is explicitly
  designed.
- The saved token is never loaded into a field or shown again.
- The device name shown is the one assigned in the Wasla Web panel, or "unknown
  device". The desktop app does not show the Windows machine name.
- The polling defaults are Idle 5 seconds, Busy 1 second, Error 15 seconds and
  3 jobs per poll.

## Localization

- Print Bridge text comes from `PrintBridgeResources*.resx` in
  `Wasla.PrintBridge`: the neutral file (Turkish text) and `tr-TR`, `en-US`,
  `ar-SA` and `ru-RU`. Add every new string to all five files.
