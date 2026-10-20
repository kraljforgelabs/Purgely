# Code signing

Unsigned programs trigger Windows SmartScreen ("Windows protected your PC"). Purgely is open source, so it can be
signed for free:

**Status (2026-10-20):** the SignPath Foundation declined the first application – the project is too new (they look
for stars, forks, contributors and outside articles or discussions). Reapply once Purgely is better known. Options until
then: stay unsigned with SHA-256 checksums (current), or a low-cost Certum open-source certificate.

1. **SignPath Foundation** (free code signing for open-source projects): apply at https://signpath.org with the
   repository https://github.com/kraljforgelabs/Purgely. Requirements: OSI license (MIT ✓), public repository,
   builds made by CI (✓ `.github/workflows/release.yml`), no malware or bundled offers (✓).
2. After approval, add the SignPath GitHub Action at the marked "Sign" step in `release.yml`.
3. SmartScreen reputation also grows with downloads – a winget listing (see `packaging/winget`) helps.

Until then: every release lists SHA-256 checksums (`SHA256SUMS.txt`) so anyone can verify the download.

## SignPath application – ready to paste

Apply at https://signpath.org/apply (signed in with the GitHub account `kraljforgelabs`). The repository must be
**public** first. Suggested answers:

- **Project name:** Purgely
- **Repository:** https://github.com/kraljforgelabs/Purgely
- **Homepage:** https://kraljforgelabs.github.io/Purgely/
- **License:** MIT
- **Description:** Purgely is a free, ad-free Windows 11 maintenance tool: it uninstalls programs completely,
  removes leftovers, bloatware and Windows ads, updates programs (winget) and drivers (Microsoft Update Catalog only),
  and manages startup items. Every change is backed up first and can be undone.
- **Why signing matters:** Purgely needs administrator rights. Users should be able to verify that the Purgely.exe they
  run was built from this repository by GitHub Actions and not modified – and SmartScreen warnings on an unsigned admin
  tool teach users to click through warnings.
- **Build:** GitHub Actions on `windows-latest`, `.github/workflows/release.yml`, triggered by version tags; runs unit
  tests and a safety audit before publishing. Artifacts: `Purgely.exe` (self-contained single file),
  `Purgely-needs-dotnet10.exe`, `Purgely-portable.zip`.
- **Team:** KraljForge Labs (maintainer: kraljforgelabs). All changes go through pull requests or reviewed commits.
- **Bundled software / telemetry:** none. Network access only for winget, the Microsoft Update Catalog, an optional daily
  release check against GitHub, and VirusTotal links opened in the browser (see PRIVACY.md).

After approval: SignPath gives an organization ID, a project slug and a signing policy. Add them as repository secrets
and put the `signpath/github-action-submit-signing-request` step at the marked place in `release.yml`.
