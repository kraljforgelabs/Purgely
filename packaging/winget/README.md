# Publishing Purgely on winget

Planned for 1.0, after the public beta and once releases are signed (see `SIGNING.md`) – winget's
validation and SmartScreen both go much smoother with a signed `Purgely.exe`.

After a GitHub Release exists (see `.github/workflows/release.yml`):

1. Install the manifest tool: `winget install Microsoft.WingetCreate`
2. Create the manifest from the release asset:
   `wingetcreate new https://github.com/kraljforgelabs/Purgely/releases/download/v0.9.0/Purgely-portable.zip`
   - Package identifier: `KraljForgeLabs.Purgely`
   - Installer type: `zip` with nested `portable` installer `Purgely.exe`, command alias `purgely`
3. `wingetcreate submit` opens a pull request at microsoft/winget-pkgs. Microsoft validates it (malware scan, install test).
4. For later versions: `wingetcreate update KraljForgeLabs.Purgely --version 1.0.0 --urls <zip url> --submit`

`KraljForgeLabs.Purgely.locale.en-US.yaml` in this folder holds the descriptions to paste in.
