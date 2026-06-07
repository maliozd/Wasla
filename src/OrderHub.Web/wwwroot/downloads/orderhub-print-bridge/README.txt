OrderHub Print Bridge — Placeholder Package
==========================================

This is a placeholder download package. The full Windows installer packaging
is not finalized yet.

Contents:
- README.txt (this file)
- appsettings.sample.json (configuration template without secrets)

Included plan note:
- Up to 3 active Print Bridge devices are included by default.
- Additional printer/device connections may require a paid add-on later.

Next steps:
1. Open Print Bridge setup in OrderHub (/print-bridge/download).
2. Install the printer driver and confirm Windows sees the printer.
3. Generate a Print Bridge token from the Devices page (/print-bridge).
4. Install and run OrderHub Print Bridge. It appears in the Windows system tray.
5. Open the app from the tray icon and enter BaseUrl, AgentToken, and PrinterName in Settings.
   Settings are stored at C:\ProgramData\OrderHub\PrintBridge\appsettings.json

Do not put real tokens in files you share or commit to source control.
