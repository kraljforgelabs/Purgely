# Purgely – instructions for AI agents and contributors

Purgely deletes and changes things on real Windows PCs. A wrong delete can break Windows. Every change must respect these rules.

## Language
- All code, UI text, comments and documents are in **English**. (The owner talks to Claude in Croatian.)

## Priorities
1. Safety of the user's PC  2. Correctness  3. Clear explanations  4. Stability  5. Minimal, focused changes

## Mandatory safety rules
- **Everything destructive goes through `Core/Cleaner.cs`** (or `StartupManager` for on/off switches). UI code never deletes files, registry keys, services or tasks directly.
- **Backup first, then change.** Every delete/overwrite records an Undo action *before or regardless of* success (a partial delete must still be restorable).
- **Never delete structural Windows keys** – `Cleaner.IsCriticalKey` must be checked for every registry key delete. Never delete a key/folder because of name similarity alone when another installed program uses it (`MarkShared`).
- **Registry:** always use the 64-bit view (`RegistryView.Registry64`, `reg.exe … /reg:64`). Remember WOW6432Node links and shared keys are not real "new" keys – Trial Mode trusts only keys whose last-write time is after the trial started.
- **Pre-selection must be safe on its own.** Users click whatever is recommended. Items of a program that is still installed are never pre-selected. Anything uncertain is shown but not checked.
- **Never delete what belongs to the user:** Documents, Desktop folders, Downloads, OneDrive, other drives' project folders, drive roots, `Program Files` itself, the Windows folder, Purgely itself.
- **Disable before delete** for system behaviour (startup items, services): turning off must always be reversible.
- **Progress, always:** anything longer than a few seconds shows what it does, how far it is and the elapsed/estimated time. No blank or frozen-looking windows.
- **No modal pop-ups at start-up.** Alerts go to the home screen "Notices".
- Test destructive features in **Windows Sandbox or a VM** first. Never ask the user to rely on System Restore as the first fix – use Purgely's own Undo.

## Workflow for every change
1. Read the affected code and the spec (Claude project: `claude/uninstaller-spec.md`).
2. Make the smallest coherent change. Reuse existing helpers (PathUtil, RegistryPath, RegValue, Shell, Cleaner, FileMover, Timeline).
3. **Council review – sized to the change** (level 0–3, see `D:\AI\council\COUNCIL.md`; a small fix gets a quick look or nothing, only destructive/safety changes get the full board) (the shared kit in `D:\AI\council` – read its `COUNCIL.md` and `LESSONS.md` first) for anything that deletes, moves, kills, disables or writes system state: independent reviewers (destructive-action safety · beginner user · modern design · compiler-in-the-head when no SDK is available) read the change before the owner tests it. Fix every *Must fix*, then append new lessons to `D:\AI\council\LESSONS.md` and design findings to `TRENDS.md`.
4. Run **`preflight.bat`** – it must end with no *Must fix*.
5. Only then hand over for testing, together with a short note: what changed, what to test, what could go wrong.

## Preflight (`preflight.bat`)
- Release build of `src/Purgely`.
- Unit tests in `tests/Purgely.Tests` (matching logic, critical keys, parsers, suite detection, startup knowledge, registry value round-trips).
- `tools/safety-audit.ps1`: every destructive API call is counted per file and compared with `tools/destructive-calls.json`. A new destructive call → **Check** (needs council review, then update the baseline with `-UpdateBaseline`). A destructive call outside `Core/` → **Must fix**.
- Result: **OK / Check / Must fix**, exit code 0 / 2 / 1.

## Documentation
- The spec in the Claude project is the single source of truth for features and safety rules. Update it when behaviour changes. No patch logs or duplicate notes.

## Safety rules added by the council (2026-10-15)
- `SafePaths` is the only place that decides whether a file/folder may be removed; `Cleaner` re-checks it right before deleting.
- `FileMover` deletes a source file only after its copy was verified; junctions/symlinks are never followed.
- Weak evidence (brand word, common first word, "appeared during the trial") may offer an item but never tick it.
- Trial revert ticks only what is attributable to the new program; nothing under the Windows folder is offered.
- Startup "Optimize" uses only knowledge-base entries marked Safe – never the name heuristic.
- No modal pop-ups at start-up: background findings go to the home screen's Notices panel.

## Rules added with 0.3 (2026-10-17)
- Settings switches (Bloatware "Ads & suggestions") write their Undo actions **before** each registry change, through `UndoStore.SaveActions`.
- Store apps: the package name is validated before it reaches PowerShell; the "Appx" Undo action can only reopen the Store page – say so in the UI.
- Known-program rules (`Core/KnownPrograms.cs`) must never point into the user's own files; user-data paths are offered, never ticked.
- Suggestions are hints only – they never tick or remove anything.

## Rules added with 0.9 (2026-10-19)
- Purgely's shared data (`C:\ProgramData\Purgely`, `X:\Purgely Undo`) must stay administrators-only (`DataFolders.Protect`);
  never follow a link when choosing a backup folder.
- Registrations (right-click menu, file types, App Paths, firewall rules, PATH) are offered only when they point into a
  folder that clearly belongs to the program, or when the name matches **and** the target is gone. Nothing that points
  into Windows is offered. Extension keys (`.mp4`) and the user's default-app choices are never touched.
- PATH: remove one entry, keep the value type (REG_EXPAND_SZ) and every other entry unexpanded; Undo adds the entry back.
- The update check only reads GitHub's public release list, at most once a day, can be turned off, and never downloads
  or runs anything. Only URLs under the project's own releases page are opened.
- "Remove Purgely" deletes only folders with Purgely's exact names (never through a link) and only `Purgely.exe` itself.
