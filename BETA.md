# Purgely public beta (0.9)

Thank you for testing Purgely! The beta is for finding what the developers' own PCs can't show: other Windows editions,
other PC makers, other programs. Everything you report makes 1.0 safer for everyone.

## Before you start
- Use a PC you can afford a hiccup on – or at least have your important files backed up (that's good advice anyway).
- Purgely makes a backup before it removes anything, and the **Undo center** puts it back. Still, please don't test
  the first time on a machine you need for work tomorrow.
- Download only from this repository's **Releases** page. Check the fingerprint if you like:
  `Get-FileHash .\Purgely.exe` in PowerShell must match `SHA256SUMS.txt`.
- Windows SmartScreen may warn ("Windows protected your PC") because the beta isn't code-signed yet:
  *More info → Run anyway*.

## What to try (15–30 minutes)
1. **Home screen** – do all ten tiles finish counting? Anything that says "Couldn't count"?
2. **Checkup** – what does it find? Does anything ticked look wrong to you?
3. **Uninstall a program you don't need** (or install a small free one first, e.g. a text editor, and remove it again).
   Look at the leftover list: is everything ticked really from that program?
4. **Several at once** – tick 2–3 programs and use *Uninstall N ticked*.
5. **Bloatware** – the three tabs (apps, ads & suggestions, browser extensions). Turn something off, then restore it in
   the Undo center.
6. **Program updates** and **Driver updates** – do they find what other tools find?
7. **Undo center** – restore one package. Did everything come back?
8. **Settings** – *Check now* for a new version, the welcome tour.
9. **Repair** – does it list devices with problems correctly? "Find them" under devices that aren't connected
   (just look – forgetting is optional). "Test the connection".

## What to report
Open an issue with **Report a problem** (Settings) or at
https://github.com/kraljforgelabs/Purgely/issues/new/choose and include:
- Purgely version (bottom right of the home screen) and Windows version (`winver`)
- what you did, what you expected, what happened – a screenshot helps a lot
- **most important:** if Purgely ticked or removed something that wasn't a leftover – say what it was and whether
  the Undo center brought it back

Security problems: please report them privately – see [SECURITY.md](SECURITY.md).

## Known limits of the beta
- Not code-signed yet – check the download with `SHA256SUMS.txt` (see Before you start).
- English only.
- If you start Purgely from a standard account with another administrator's password, your own AppData and settings
  can't be cleaned from there – the home screen tells you.
- Firewall rules removed by Purgely disappear after the next restart.
- The update check needs the repository to be reachable at GitHub; offline it simply stays quiet.
