# Code signing

Unsigned programs trigger Windows SmartScreen ("Windows protected your PC"). Purgely is open source, so it can be
signed for free:

1. **SignPath Foundation** (free code signing for open-source projects): apply at https://signpath.org with the
   repository https://github.com/kraljforgelabs/Purgely. Requirements: OSI license (MIT ✓), public repository,
   builds made by CI (✓ `.github/workflows/release.yml`), no malware or bundled offers (✓).
2. After approval, add the SignPath GitHub Action at the marked "Sign" step in `release.yml`.
3. SmartScreen reputation also grows with downloads – a winget listing (see `packaging/winget`) helps.

Until then: every release lists SHA-256 checksums (`SHA256SUMS.txt`) so anyone can verify the download.
