OrderHub Print Bridge — Placeholder Package
==========================================

This is a placeholder download package. The full Windows installer packaging
is not finalized yet.

Contents:
- README.txt (this file)
- appsettings.sample.json (safe empty template; no real customer values)

Configuration rules:
- Repo/installer appsettings.json must stay empty for BaseUrl, AgentToken, and PrinterName.
- Real customer values are entered in the Print Bridge tray app Settings UI.
- Runtime config is stored at:
  C:\ProgramData\OrderHub\PrintBridge\appsettings.json
- Logs are stored at:
  C:\ProgramData\OrderHub\PrintBridge\logs
- AgentToken is never logged.

Config loading order (first run):
1. Load ProgramData config if it exists.
2. If not, load exe-local appsettings.json as sample/default.
3. Create ProgramData config from that sample/default.
4. All runtime saves go to ProgramData only (not Program Files).

Included plan note:
- Up to 3 active Print Bridge devices are included by default.
- Additional printer/device connections may require a paid add-on later.

Next steps:
1. Open Print Bridge setup in OrderHub (/print-bridge/download).
2. Install the printer driver and confirm Windows sees the printer.
3. Generate a Print Bridge token from the Devices page (/print-bridge).
4. Install and run OrderHub Print Bridge. It appears in the Windows system tray.
5. Open the app from the tray icon and enter BaseUrl, AgentToken, and PrinterName in Settings.

Do not put real tokens in files you share or commit to source control.
