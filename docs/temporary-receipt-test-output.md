# Temporary receipt output for manual testing

Status: retained at the user's request on 2026-09-21. Do not remove yet.

`src/Wasla.PrintBridge/Services/PrintBridgeRuntime.cs` contains a marked
`TEMPORARY` block in the `DryRun` path. It saves the formatted receipt as UTF-8
text before the job is marked printed. Normal physical printing is unaffected.

- Folder: `C:\ProgramData\Wasla\PrintBridge\logs\test-receipts`
- File: `receipt-{jobId:N}.txt`
- The application log records the output path, not the receipt body.
- The file contains the actual formatter output, including the selected language
  and visibility settings. It is not a PDF or proof of physical printer layout.
- Only jobs processed after this code was loaded produce a file.
- Dry run still marks the job printed without sending a physical print.
- A file-write failure follows the existing print-failure handling.
- Files may contain customer information enabled in the receipt settings; they
  are local test artifacts and must not be committed or shared as general logs.

The user confirmed that receipt content changes during the manual test. This
does not establish physical printer compatibility or full manual test coverage.

## Removal follow-up

Keep the block while the user continues testing. When the user confirms testing
is complete and requests removal, remove the marked file-output block and its
path log, preserving the existing dry-run behavior and the reprint-template fix.
Review generated test files with the user before deleting them; code removal
does not delete existing files. Update this note when removal is complete.
