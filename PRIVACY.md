# Privacy

**Purgely collects nothing and sends nothing about you.** There is no account, no telemetry, no analytics, no ads.

## What stays on your PC
- History, settings and notices: `%LOCALAPPDATA%\Purgely` and `C:\ProgramData\Purgely`
- Undo backups: `C:\ProgramData\Purgely\Undo` and a `Purgely Undo` folder on each drive that had something removed
Delete these folders (or use Discard in the Undo center) and nothing of Purgely remains.

## When Purgely goes online – only when you ask
| You click… | Purgely contacts | What is sent |
|---|---|---|
| Updates / Update all / Move to a new PC | Windows Package Manager (winget) sources | What winget itself sends (package names) |
| Check for newer drivers / Install | `catalog.update.microsoft.com`, Windows Update | Your devices' hardware IDs (to find their drivers) |
| Maker's driver page, Store page, VirusTotal report | Opens the page in your browser | The VirusTotal link contains only the file's SHA-256 fingerprint – the file is never uploaded |

Nothing else leaves your PC. The source code is public, so anyone can check this.
