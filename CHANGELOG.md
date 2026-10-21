# Changelog

## 0.9.4 (beta) – 2026-10
- A small "Purgely – Starting…" window appears as soon as Purgely starts, so a slow first start no longer looks
  like nothing happened
- If Purgely can't start, it now says why and writes the details to `%LOCALAPPDATA%\Purgely\crash.log` (to send with a
  problem report) instead of closing without a word
- Starting Purgely again while a copy without a window is still running (stuck, or still starting) now says so and
  explains how to end it, instead of doing nothing
- A short start-up log (`%LOCALAPPDATA%\Purgely\startup.log` – only steps and times) helps trace "nothing happens"
  reports
- One download: `Purgely.exe` has everything inside – no .NET or anything else to install. The smaller
  "needs .NET 10" file is gone

## 0.9.3 (beta) – 2026-10
- **Uninstall by icon** – drop a program's icon (desktop shortcut, Start menu shortcut, .exe or folder) on Purgely,
  drag **Find by window** onto a program's window, or turn on **"Uninstall with Purgely"** in the right-click menu
  (Settings – off until you turn it on, for your account only, removed again with Purgely). A shortcut that starts a
  game inside an emulator or launcher (e.g. a GameLoop game) is explained instead of removing the whole emulator by
  surprise. When Purgely can't tell which program it is, it says so and lets you pick in the list
- Only one Purgely runs at a time
- Clearer, more natural wording throughout the app, the user guide and the website

## 0.9.2 (beta) – 2026-10
- **Epic Games games** are now in the program list. The Epic Games Launcher doesn't register its games with Windows,
  so they were missing. Uninstall opens the launcher (as you, not as administrator), and once it has removed the game,
  Purgely scans for what was left behind (saves, settings, shader cache)

## 0.9.1 (beta) – 2026-10
- **Repair** page (button on the home screen): devices that don't work explained in plain words (restart, reinstall,
  turn on, look for a driver); devices Windows remembers but that aren't connected (offered, never ticked); restart
  Windows Audio; internet test, refresh and reset; missing Microsoft runtimes (Visual C++, DirectX, .NET) installed
  with Windows Package Manager instead of single DLLs from websites
- Driver updates: busy chips with hundreds of catalog packages (e.g. Intel) are now also searched newest-first, so a
  new driver isn't hidden on page 40; devices whose maker app is installed (Intel DSA, NVIDIA app) say so
- Releases are not code-signed (the SignPath Foundation wants a better-known project first), with checksums as before

## 0.9 (public beta) – 2026-10
- **Settings** page (gear button on the home screen): update check, Timeline history length, Undo storage, help links
- **New version notice** – once a day Purgely asks GitHub for a newer release and shows a notice. Nothing is downloaded
  or installed by itself, and dismissing the notice skips that version (can be turned off)
- **Welcome tour** on first start (three steps, built into the home screen rather than a pop-up)
- **Remove Purgely and its data** – shows exactly what will be removed (backups, history, settings, optionally Purgely.exe) and
  warns when cleanups could still be undone
- **More leftovers found** – right-click menu entries and shell extensions, file types and "Open with" entries, Win+R
  program paths, firewall rules and PATH entries. Offered only when they point into the program's own folder, or when
  their name matches and the target is gone; one PATH entry is removed without touching the rest; all undoable
- Security: Purgely's data folders (`C:\ProgramData\Purgely`, `X:\Purgely Undo`) are now writable by administrators only,
  so a standard user can't plant a fake Undo package; a linked "Purgely Undo" folder is never used
- A notice when Purgely runs under a different account than the signed-in user (UAC with another admin's password)
- Releases before 1.0 are marked as Pre-release on GitHub

## 0.4 – 2026-10
- **Uninstall several programs at once** – queue, quiet mode (Windows Installer, Inno Setup, NSIS, quiet uninstall
  strings), skip a stuck one, one combined leftover scan
- **Checkup** – one look at the whole PC, fixes only what is certain, everything undoable
- **Move to a new PC** – save your program list, reinstall everything with winget
- **AI & privacy** switches – Recall, Click to Do, Copilot, Copilot in Edge, diagnostic data, typing data, activity history
- **Browser extensions** – where each came from and what it may do
- **Portable programs** are listed and can be removed (never pre-ticked)
- **Install & Track** – checks an installer first (signature, source, bundled offers, VirusTotal lookup)
- **Open knowledge base** – bloatware and known-program rules as JSON (`CONTRIBUTING.md`)
- Fixed: driver updates with the newer Microsoft Update Catalog title style were missed
- Safety: folders named after a company (e.g. `AppData\Roaming\Autodesk`) are offered but never pre-ticked
- Release workflow with SHA-256 checksums, winget manifest, user guide (PDF)

## 0.3 – 2026-10
- Renamed to **Purgely – the ultimate PC tool**; ten home tiles, app icon, in-app dialogs
- **Bloatware & Windows clutter** with Undo and "Windows brought it back" notices; **Microsoft Store apps**
- **Known-program rules** (OneDrive, Teams, Chrome, Steam, Adobe…), **smart suggestions**, confidence in words

## 0.2 – 2026-10
- Home screen, one-window navigation, guided and force uninstall, recently removed programs, PC sweep
- winget updates without a console window, hardware overview and signed driver installs, Startup manager, Trial Mode
