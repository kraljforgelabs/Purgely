# Contributing to Purgely

Thank you for helping! Purgely deletes and changes things on real PCs, so safety comes first.
Read `AGENTS.md` first; the same rules apply to people and AI tools.

## The easiest way to help: the knowledge base
Everything Purgely "knows" lives in plain JSON files in `src/Purgely/Knowledge/`:

| File | What it is |
|---|---|
| `bloatware.json` | Preinstalled and promotional apps with a verdict: `Remove` (only ticked when `tick` is true), `Check` (never ticked), `Keep` |
| `known-programs.json` | Where popular programs leave things: `programPaths` (may be ticked) and `userDataPaths` (settings, saves – offered, never ticked) |

Rules for a change:
- Explain **why** in the pull request (a link to the publisher's documentation or a screenshot of the folder is ideal).
- Never add a path that holds the user's own files (Documents, Desktop, OneDrive/Dropbox folders, game saves as program paths).
- Use `%LOCALAPPDATA%`, `%APPDATA%`, `%PROGRAMDATA%`, `%PROGRAMFILES%`, `%PROGRAMFILES(X86)%` or `HKCU\…`/`HKLM\…` – never a user name.
- A Store app pattern is the package name (or its prefix), e.g. `Microsoft.BingNews`; `=Name` means exact.
- Every change is reviewed (the "council") and must pass `preflight.bat` / CI before it ships. Purgely never loads
  knowledge from disk at run time, so only reviewed rules ever reach users.

## Code changes
- Small, focused pull requests. English for code, UI and docs.
- Run `preflight.bat` (build, tests, safety audit). New destructive calls need a review and a baseline update.
- Test destructive changes with `sandbox.bat` or `test-on-this-pc.bat` (fake programs and traps).
