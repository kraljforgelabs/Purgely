<img src="docs/purgely-icon.png" width="96" align="right" alt="Purgely icon">

# Purgely – the ultimate PC tool

*by KraljForge Labs – Practical tools forged for the future.*

One free tool for Windows 11 that keeps a PC clean and up to date: uninstall programs and Store apps completely,
clean leftovers and installer junk, remove bloatware and Windows ads, update programs and drivers, and control
startup. Every item comes with a plain explanation, almost everything can be undone, and there are no ads.

> **Public beta (0.9).** Purgely works and is tested, but it hasn't met many PCs yet. Please read [BETA.md](BETA.md)
> and tell us what you find.

## Download
1. Open **[Releases](https://github.com/kraljforgelabs/Purgely/releases)** and download `Purgely.exe` (no installation –
   it is a single file).
2. Optional: check the download – `SHA256SUMS.txt` next to it lists the fingerprint
   (`Get-FileHash Purgely.exe` in PowerShell must show the same value).
3. Start it. Windows asks for administrator rights. While Purgely is not code-signed yet, Windows may show
   "Windows protected your PC" – click *More info → Run anyway*.

The [user guide (PDF)](docs/Purgely-User-Guide.pdf) explains every feature. Website:
https://kraljforgelabs.github.io/Purgely/

## Features (0.9)
- **Home screen** with ten live tiles, a Notices panel, **Checkup** (one look at the whole PC, fix what is certain) and
  **Move to new PC** (save your program list, reinstall everything on the new PC with winget)
- **Uninstall** – desktop programs, Microsoft Store apps and portable programs; **several at once** (quiet mode where the
  uninstaller allows it, one combined leftover scan); live uninstall window, force uninstall; deep leftover scan with
  confidence in words and known-program rules; suite cleanup; smart suggestions (unused for months, duplicates)
- **Clean leftovers** – removed programs and a whole-PC sweep for orphaned shortcuts, tasks, services and empty folders
- **Installer junk**, **Dependency Map**, **PC Timeline** with Bundleware Detector
- **Bloatware, ads, AI & privacy** – preinstalled apps with a verdict; ads and suggestions; Recall, Click to Do, Copilot;
  optional diagnostic data, typing data, activity history; **browser extensions** with what they may do and where they
  came from; Purgely tells you when Windows brings something back
- **Install & Track** – checks an installer first (signature, download source, bundled offers, VirusTotal lookup),
  records everything it changes, and undoes it completely later
- **Updates** (winget, hidden, with progress) and **Drivers** (signed only – Microsoft Update Catalog or the PC maker)
- **Startup** manager and **Undo center** for everything Purgely changed
- **Leftover registrations** *(0.9)* – right-click menu entries, file types, Win+R paths, firewall rules and PATH entries
  that point into a removed program's folder
- **Settings** *(0.9)* – update check (once a day, nothing downloaded by itself), Timeline history, Undo storage,
  welcome tour, and **Remove Purgely and its data**
- **Open knowledge base** – `src/Purgely/Knowledge/*.json`, see `CONTRIBUTING.md`

## Code signing policy
Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/) – applied for; until approved, releases are unsigned (check them with
`SHA256SUMS.txt`).

- Committers and reviewers: [KraljForge Labs](https://github.com/kraljforgelabs)
- Approvers: [KraljForge Labs](https://github.com/kraljforgelabs)
- Only builds made by GitHub Actions from this repository are signed – never a build made on someone's PC.
- Privacy: Purgely sends nothing about you; see [PRIVACY.md](PRIVACY.md).

## Trust
Releases are built by GitHub Actions from this source (`.github/workflows/release.yml`) with SHA-256 checksums – nobody
builds them by hand. Code signing through the SignPath Foundation is being applied for (`SIGNING.md`); a winget listing
follows with 1.0 (`packaging/winget`).

Quality: every risky change goes through the council (`D:\AI\council`) and `preflight.bat`.

## For developers

### Build and check
Requirements: .NET 10 SDK.

- `build.bat` – publishes `dist\Purgely.exe` and starts it (asks for administrator rights).
- `preflight.bat` – Release build + unit tests + safety audit. Must end without **MUST FIX** before testing.
- `tools\safety-audit.ps1 -UpdateBaseline` – only after the council has reviewed new destructive calls.
- `sandbox.bat` – opens Purgely in Windows Sandbox with a fake removed program ("Contoso PhotoTool") and traps
  that must survive (your Documents, a junction, another installed Contoso program). After cleaning, run
  `C:\PurgelyTest\check-after-clean.ps1` in the sandbox; the report lands in `sandbox-results\`.
- `test-on-this-pc.bat` – the same trap test on the real PC when Sandbox can't run (restore point first, fake programs
  only, traps point at dummy folders, option 4 removes exactly what it planted). Includes the ads & suggestions
  before/after/Undo check.
- `publish-to-github.bat` – uploads the source to GitHub; GitHub Actions (`.github/workflows/ci.yml`) then builds, tests
  and audits every change on a real Windows machine.
- `make-release.bat` – tags the version from `Purgely.csproj`; `.github/workflows/release.yml` builds `Purgely.exe`,
  checksums and the release (versions before 1.0 are marked *Pre-release*).

### Layout
- `src/Purgely/Core` – engine (inventory, uninstall, scanning, scoring, Undo, Trial Mode, startup)
- `src/Purgely` – WPF user interface (`HomeWindow` is the start screen)
- `tests/Purgely.Tests` – safety regression tests
- `tools` – safety audit and its baseline
- `AGENTS.md` – rules for anyone (human or AI) changing the code

## Privacy and security
Purgely collects and sends nothing about you – see `PRIVACY.md`. Report security problems privately – see `SECURITY.md`.

## Trademarks
Purgely is an independent project and is not affiliated with, endorsed by or sponsored by any company or product it
mentions or detects (for example Microsoft, Autodesk, Intel, NVIDIA or other publishers). Product and company names
are trademarks of their respective owners and are used only to identify software installed on your PC.

## License
MIT – see [LICENSE](LICENSE). Copyright (c) 2026 KraljForge Labs.

Purgely is free and has no ads. It is provided "as is", without warranty – it changes your PC only after you confirm,
and almost everything can be undone from the Undo center.
