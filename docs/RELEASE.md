# Cross-platform packaging and releases

Updated: **2026-08-30**.

## Artifact matrix

| Runtime | Human-installable artifacts | In-app update payload | Build host |
| --- | --- | --- | --- |
| `linux-x64` | self-contained `.tar.gz`, amd64 `.deb` | root-relative ZIP | Ubuntu |
| `win-x64` | self-contained portable ZIP, x64 MSI | root-relative ZIP | Windows |
| `osx-x64` | complete `Mission Planner 10.app` ZIP, compressed DMG | the complete app ZIP | macOS |
| `osx-arm64` | complete `Mission Planner 10.app` ZIP, compressed DMG | the complete app ZIP | macOS |

Linux and Windows package entry points are `build/linux/package.sh` and
`build/windows/package.sh`; the root `Makefile` exposes their common targets. The macOS release
job wraps the self-contained publish with `build/macos/make-app.sh`, then signs and archives the
complete app bundle. `build/macos/make-dmg.sh` adds the same signed app and an Applications shortcut
to a compressed filesystem image. The ZIP remains the in-app update payload; the DMG is the
human-installable image. Generated output belongs below `out/`, `dist/` or `upload/` and is never
committed.

All formats are built from the root `MissionPlanner.csproj`. The publish payload includes
`COPYING.txt`, `NOTICE.md` and every file below `LICENSES/`. Windows-only native DLLs are excluded
from Linux/macOS; the Windows package retains the x64 SimpleBLE runtime. macOS publish fetches the
pinned official VLC 3.0.23 and SimpleBLE 0.7.3 assets and rejects a size, SHA-256, architecture or
runtime-layout mismatch.

## Version contract

`build/version.sh` reads the upstream version from `Properties/AssemblyInfo.cs`, the explicit
monotonic local build number from `build/local-build-number.txt`, and the canonical Git commit. It
emits one contract for assemblies, UI, archives, Debian metadata, MSI and updater manifests:

```text
product/file:  1.3.83.1
informational: 1.3.83.1+0123abcd
artifact:      1.3.83.1-0123abcd
release tag:   v1.3.83.1-0123abcd
beta tag:      v1.3.83.1-0123abcd-beta[.N]
Debian:        1:1.3.83.1+0123abcd
MSI:           1.3.1
```

Developer packages made with tracked or untracked source changes append `.dirty`. Ordinary builds
never mutate the source tree. Before a local release, run `make bump-local-build`, review the single
counter-file change, and commit it. The counter is repository-global and must not be reset when the
upstream version changes. Windows Installer accepts only three numeric fields, so its product
version is `major.minor.local-build`; the complete informational version remains visible in package
metadata. macOS uses the upstream `major.minor.patch` as `CFBundleShortVersionString` and the local
build number as `CFBundleVersion`.

The explicit local sequence starts at `1` from the point where this repository adopted the tracked
counter. It intentionally does not inherit the old implicit Git commit count (`7201`) and remains
independent of the upstream version, so it must not be reset when upstream moves to `1.3.84`.

## Local commands

```bash
dotnet build MissionPlanner.slnx -c Release -m:1 --nologo
dotnet test MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release -m:1 --no-build --nologo

make linux-packages
make windows-zip

# Before producing a new local release (not for every developer build):
make bump-local-build
git add build/local-build-number.txt
git commit -m "build: bump local build number"
```

`make windows-msi` and `make windows-packages` require Windows because WiX only supports producing
MSI packages there. `.github/workflows/ci.yml` runs that target on `windows-latest`, expands the
portable ZIP, installs the MSI with `msiexec`, checks the installed launcher, then uninstalls it.
The same workflow validates Linux with `lintian` plus an Xvfb launch and builds both macOS
architectures on native runners. Each macOS job verifies and mounts its DMG read-only, checks the
app executable and Applications shortcut, and detaches it again.

## GitHub release and updater

`.github/workflows/release.yml` builds all artifacts on a `v*` tag. A tag is rejected unless its
upstream version, local build number and eight-character hash match the tagged commit and tracked
counter. It publishes flat release assets plus `SHA256SUMS` and, for each RID, these updater assets:

```text
<rid>-manifest.json
<rid>-manifest.sig
MissionPlanner10-<artifact>-<rid>-update.zip  # Linux and Windows
MissionPlanner10-<artifact>-<rid>.zip         # complete macOS app
MissionPlanner10-<artifact>-<rid>.dmg         # human-installable macOS image
```

A manual workflow dispatch performs the same package and updater-signature work without publishing
a GitHub Release. This makes the updater key and platform OpenSSL behavior testable before a tag is
created.

The application queries `Rouniy/MissionPlanner` GitHub Releases directly. Stable updates select a
non-prerelease; Beta Updates select a prerelease. Both require an Ed25519-signed manifest and a
SHA-256-pinned full bundle. Debian installs contain `.package-managed` and deliberately defer
updates to APT instead of overwriting package-owned files.

The first Linux/Windows update bundle after the Mission Planner 10 rename also carries a
release-only legacy apphost alias. This lets a pre-rename portable installation apply the update;
normal TAR/DEB/ZIP/MSI packages expose only `MissionPlanner10`. Renaming a macOS `.app` bundle
changes the bundle path itself, so an existing `Mission Planner.app` requires one manual upgrade
to `Mission Planner 10.app`.

The required repository secret is `UPDATE_SIGNING_KEY`, containing an unencrypted PKCS#8 Ed25519
private key. Only its public half is committed in `build/update-public-key.txt` and embedded in the
updater. The release job derives the public half from the secret and refuses to publish if it does
not match. Never commit, print or upload the private key outside GitHub Secrets.

Optional platform signing uses these repository secrets:

- Windows: `WINDOWS_SIGNING_PFX` (base64 PFX) and `WINDOWS_SIGNING_PASSWORD`.
- macOS: `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `MACOS_SIGN_IDENTITY`, plus
  `MACOS_NOTARY_KEY`, `MACOS_NOTARY_KEY_ID` and `MACOS_NOTARY_ISSUER` for notarization.

Without those optional secrets, CI still produces a functional unsigned Windows package and an
ad-hoc-signed macOS preview. A public production release should be treated as unsigned until the
corresponding Authenticode/Developer ID identities are configured and verified.

## Verification record

On 2026-08-24, before publishing the packaging commit:

- solution Release build: `0 warnings / 0 errors`;
- tests: `1266 passed / 0 failed / 0 skipped`;
- `linux-x64`: `.tar.gz` and `.deb` produced; `lintian` clean; extracted DEB reached the normal
  Avalonia event loop under Xvfb; Windows SimpleBLE/libusb binaries absent;
- `win-x64`: portable ZIP passed CRC/extraction checks and contained x64 PE launcher/SimpleBLE
  binaries plus the complete license set;
- `osx-x64` and `osx-arm64`: both self-contained publishes passed; apphost, SimpleBLE and VLC
  binaries matched their requested Mach-O architecture; complete `.app` ZIPs and compressed DMGs
  were generated, mounted read-only, inspected and detached on native macOS runners;
- GitHub workflow YAML passed PyYAML parsing and `actionlint` 1.7.12; packaging shell passed
  `bash -n`, ShellCheck and `git diff --check`.

PR #9 CI run `32730683963` passed native Linux package smoke, real Windows MSI
install/validation/uninstall, and both macOS DMG mount checks; CodeQL run `32730683985` passed.
Release workflow dry-run `32730719818` independently produced all four platform artifact bundles.
The repository currently has the updater signing secret, but not the optional Windows
Authenticode or macOS Developer ID/notarization secrets, so Windows packages are unsigned and the
macOS app/DMG are ad-hoc signed until those identities are configured.
