# Privacy

**Purgely collects nothing and sends nothing about you.** There is no account, no telemetry, no analytics, no ads.

## What stays on your PC
- History, settings and notices: `%LOCALAPPDATA%\Purgely` and `C:\ProgramData\Purgely`
- Undo backups: `C:\ProgramData\Purgely\Undo` and a `Purgely Undo` folder on each drive that had something removed

Only administrators can change these folders. **Settings → Remove Purgely…** deletes all of them (and `Purgely.exe` if
you want), so nothing of Purgely remains.

## When Purgely goes online – only when you ask
| When… | Purgely contacts | What is sent |
|---|---|---|
| Once a day at start (turn off in Settings) | `api.github.com` – this project's public release list | Nothing about you or your PC – only a normal web request (your IP address reaches GitHub, as with any website). Nothing is downloaded. |
| Updates / Update all / Move to a new PC | Windows Package Manager (winget) sources | What winget itself sends (package names) |
| Check for newer drivers / Install | `catalog.update.microsoft.com`, Windows Update | Your devices' hardware IDs (to find their drivers) |
| Maker's driver page, Store page, VirusTotal report | Opens the page in your browser | The VirusTotal link contains only the file's SHA-256 fingerprint – the file is never uploaded |

The other rows happen only when you click. Nothing else leaves your PC. The source code is public, so anyone can check this.
