# Mission Planner 10 — .NET 10 and Avalonia port of ArduPilot Mission Planner

[![CI](https://github.com/Rouniy/MissionPlanner/actions/workflows/ci.yml/badge.svg)](https://github.com/Rouniy/MissionPlanner/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Rouniy/MissionPlanner/actions/workflows/codeql.yml/badge.svg)](https://github.com/Rouniy/MissionPlanner/actions/workflows/codeql.yml)

**Mission Planner 10** is an AI-assisted, cross-platform port of the official
**ArduPilot Mission Planner** ground control station to **.NET 10** and **Avalonia**, targeting
Linux, Windows and macOS. It runs as a native desktop application on all three platforms, without
Wine or a browser wrapper. The port preserves Mission Planner's reusable flight-planning, MAVLink,
telemetry and device-management backends while replacing the Windows-only UI with Avalonia.

In addition to the cross-platform migration, this fork includes many reliability, safety and
usability fixes, hardening changes and regression tests that have not been incorporated into the
official Mission Planner codebase. AI tools assist with code analysis, migration and review; every
change remains in the normal source history and is validated through automated tests,
cross-platform builds and human review.

The Avalonia application, reusable Mission Planner backends, plugins and migration history live
together in this fork; there is no external source repository or application submodule.

> Independent community port — **not** affiliated with or endorsed by ArduPilot. Based on
> [Mission Planner](https://github.com/ArduPilot/MissionPlanner) (© Michael Oborne), GPLv3. See `NOTICE.md`.

## Project and upstream links

- Avalonia port: [releases](https://github.com/Rouniy/MissionPlanner/releases) and
  [issue tracker](https://github.com/Rouniy/MissionPlanner/issues).
- Official Mission Planner: [website and documentation](https://ardupilot.org/planner/),
  [support forum](https://discuss.ardupilot.org/c/ground-control-software/mission-planner),
  [source](https://github.com/ArduPilot/MissionPlanner) and
  [changelog](https://github.com/ArduPilot/MissionPlanner/blob/master/ChangeLog.txt).
- The official [Windows MSI](https://firmware.ardupilot.org/Tools/MissionPlanner/MissionPlanner-latest.msi)
  belongs to upstream Mission Planner; it is not an installer for this Avalonia port.

Please report Avalonia/Linux/macOS/Windows port problems in this repository. General ArduPilot or
official Mission Planner usage questions belong in the ArduPilot documentation and forum above.

The in-place migration started from native Mission Planner commit
`67a3c4f22bd1b38ac499f9756902e04fa4ed8444`. Source provenance and the remaining native surface are
audited under [`docs/migration/`](docs/migration/README.md); upstream updates can now be merged into
this same tree. The hardware-free acceptance workflow is documented in the
[SITL checklist](SITL-TESTING.md).

The application version combines the upstream value from `Properties/AssemblyInfo.cs`, the tracked
local build number in `build/local-build-number.txt` and the repository commit. For example,
upstream Mission Planner `1.3.83`, local build `1` and commit `c5945b02` produce
`1.3.83.1+c5945b02`; a package made from uncommitted changes adds `.dirty`. Run
`make bump-local-build` and commit the changed counter before producing the next local release.
The counter is global and monotonic: do not reset it when the upstream version changes. The
composite version appears in the window title and Help page and is shared by release archives and
update manifests. Filesystem/release names use `1.3.83.1-c5945b02`, and release tags add a
leading `v`. Debian control metadata adds epoch 1. MSI uses `major.minor.local-build` because
Windows Installer accepts only three numeric product-version fields.
Signed beta tags append `-beta` or `-beta.N`; they are published as GitHub prereleases and are
discovered by the enabled Beta Updates preference; stable and beta channels both read only signed
manifests attached to this repository's GitHub Releases.

See the [status log](docs/STATUS.md) and the pinned
[feature audit](docs/migration/FEATURE_AUDIT.md) for the support matrix and remaining work. The
native [portable plugin host](docs/migration/Reference/PLUGINS.md) documents the official-style
lifecycle, installation paths, Avalonia extension API, binary compatibility for non-visual legacy
plugins and the explicit source-adaptation boundary for legacy plugins that embed WinForms controls.
The application itself has no WinForms source, project, runtime dependency or submodule.

## AI flight diagnostics

The **AI** navigation button opens an embedded Streamable HTTP MCP server and local
Codex/Claude Code CLI launcher with automatic discovery and session-only configuration.
Detected OpenAI desktop apps can register a fixed loopback MCP connection from the **Desktop**
tab; access starts only on request. Agents can inspect connected-vehicle telemetry and parameters, read
DataFlash and TLOG flight logs, inspect warnings/events, compare flight-time parameters and
read compact signal trends. They can download onboard BIN logs and open them in the graphical
analyzer, calculate DataFlash vibration spectra and rate-response metrics, and submit
parameter proposals for review and application in Mission Planner. See the
[operating instructions and tool reference](docs/mcp/DIAGNOSTICS.md).

## Platform targets

Release automation builds self-contained artifacts for Windows x64, macOS x64, macOS ARM64 and
Linux x64. Linux x64 is the platform verified locally in this synchronization; Windows and macOS
remain first-class targets and are built by the cross-platform CI/release workflows.

Set `RID` to the required runtime identifier: `win-x64`, `osx-x64`, `osx-arm64` or `linux-x64`.

```bash
RID=linux-x64
dotnet publish MissionPlanner.csproj \
  -c Release -r "$RID" --self-contained true -m:1 -p:DebugType=none \
  -o "out/$RID"
./build/rename-apphost.sh "out/$RID" "$RID"
```

Both macOS artifacts bundle an architecture-matched VLC 3.0.23 runtime from the corresponding
official VideoLAN application image. The ARM64 artifact and its video pipeline run natively on
Apple Silicon; the x64 artifact remains available for Intel Macs and Rosetta 2. Exact source URLs,
hashes and licenses are recorded in `LICENSES/VLC-3.0.23-NOTICE.txt`. A non-macOS host needs `curl`,
`file` and `7z` when cross-publishing either macOS RID so the pinned official DMG can be verified and
extracted.

Joystick input uses the upstream DirectInput backend on Windows, a port-native joydev backend on
Linux and a native IOKit HID backend on macOS. All three feed the same mapping UI and target-safe
RC/manual-control sender; physical controller acceptance is tracked separately in the port-status
document.

Nordic-UART Bluetooth LE connections use managed BlueZ/D-Bus on Linux and the pinned SimpleBLE
0.7.3 native ABI on Windows and macOS. Windows keeps its upstream SimpleBLE DLLs; checksummed x64
and arm64 macOS dylibs are fetched from the official SimpleBLE release during publish and bundled
with the corresponding artifact. Discovery, connection and I/O are cancellable and bounded;
end-to-end traffic with representative BLE modems remains a native-platform acceptance item.

Setup > NV Modem is a native port of AgroSky GTU's `NV5Settings` for NV4/NV5 radio modems. It
discovers modems and performs parameter, key, RTSP and maintenance operations through the UDP/TCP/
UART MAVLink connections already open in Mission Planner; it never opens a second port. Parameter
values are session-only and cleared on refresh or device change, while the copied parameter
descriptions remain available in the tab. See [NV Modem](docs/NV_MODEM.md).

Inbound UDP listeners use shared-address binding before opening the port. Broadcast or multicast
telemetry can therefore be received by Mission Planner and another local application such as GTU
Hermes at the same time. This does not promise duplicate delivery for ordinary unicast traffic;
use distinct destination ports or a MAVLink router for that case.

## Linux prerequisites

Ubuntu 24.04 / Linux Mint 22 can use the distribution SDK. `global.json` accepts the 10.0.100
feature band and rolls forward, so the distro-provided 10.0.111 SDK is sufficient; a manually
installed 10.0.301 SDK is not required.

```bash
sudo apt-get update
sudo apt-get install dotnet-sdk-10.0 libvlc5 vlc-plugin-base speech-dispatcher-espeak-ng bluez
# Optional official-style GDAL Custom local raster map provider:
sudo apt-get install gdal-bin
sudo usermod -aG dialout "$USER"
```

Log out and back in after adding `dialout`. `libvlc` is needed for video,
`speech-dispatcher-espeak-ng` for spoken warnings and BlueZ for Linux Nordic-UART Bluetooth LE
connections. A current system GDAL runtime enables the optional `GDAL Custom` map provider; the
managed GeoTIFF/DTED elevation path does not require it. Add `xvfb` for headless GUI smoke tests and
`dotnet-sdk-aot-10.0` only when experimenting with NativeAOT.

Video sources may be direct files or libVLC MRLs such as `rtsp://host/path`, `udp://@:5600`,
`rtp://@:5600` and `v4l2:///dev/video0`. The input dialog also accepts an RTP GStreamer pipeline
containing `udpsrc port=...`, `application/x-rtp` and H.264/H.265 depayloading; the port converts it
to a temporary SDP file for libVLC. A bare non-RTP GStreamer pipeline is not interchangeable with a
libVLC MRL and is rejected with an explanatory message.

## Build & run

The repository is self-contained for the Avalonia application and has no source submodules.

```bash
git clone https://github.com/Rouniy/MissionPlanner.git
cd MissionPlanner
dotnet restore MissionPlanner.csproj -m:1
dotnet run --project MissionPlanner.csproj -m:1
```

`-m:1` avoids an intermittent MSBuild task-host failure seen in the large upstream project graph.

## Self-contained artifact details

For the `linux-x64` publish example above, launch `./out/linux-x64/MissionPlanner10`.
The managed assembly remains `MissionPlanner.dll` so existing non-visual plugins keep their native
Mission Planner binary identity; only the user-facing apphost and product are renamed.

The launcher and native libraries are ELF files. The `.dll` files beside them are normal managed
.NET assemblies (portable ECMA-335 bytecode), not Windows native libraries. Windows-only native
`simpleble*.dll` and `libusb-1.0.dll` files inherited from upstream are explicitly removed from
Linux and macOS publish output, while `win-x64` retains the SimpleBLE runtime it needs.

NativeAOT can be produced experimentally with `-p:EnableNativeAot=true`, but it is not a supported
release mode on any target: upstream log4net and several reflection/serialization paths are not
NativeAOT-compatible. Use the self-contained CoreCLR artifacts for operational builds.

## macOS packages

The native release jobs build Intel (`osx-x64`) and Apple Silicon (`osx-arm64`) packages. Each
architecture is published as both a complete portable `.app` ZIP and a compressed DMG containing
the same signed application plus an Applications shortcut. The ZIP is also the in-app updater
payload. When Developer ID and notarization secrets are configured, the app is signed and stapled
before both artifacts are finalized, and the DMG is notarized and stapled separately.

## Windows packages

The Windows target creates both a portable ZIP and an x64 MSI from one self-contained publish:

```bash
make windows-packages
# Individual formats:
make windows-zip
make windows-msi
```

WiX itself must run on Windows, so `windows-zip` can be cross-built on Linux while
`windows-packages`/`windows-msi` are verified on the `windows-latest` CI runner. The MSI installs
the port under `Program Files/MissionPlanner10` and uses a separate upgrade identity and
shortcuts, so it does not overwrite an installed official WinForms Mission Planner. It does not
install the official serial-driver certificate or vendor drivers. The complete artifact matrix,
release-tag format, updater assets and optional signing secrets are documented in
[`docs/RELEASE.md`](docs/RELEASE.md).

## Linux packages

One target publishes the application once and creates both a portable archive and a Debian package:

```bash
make linux-packages
# Individual formats:
make linux-tar
make linux-deb
```

Artifacts are written to `out/packages/`. The `.deb` is intended for Ubuntu 24.04 / Linux Mint 22
and compatible amd64 distributions. It is self-contained, so a .NET runtime is not required on the
target machine. APT installs the native GUI, ICU, OpenSSL and libVLC dependencies declared by the
package; `speech-dispatcher-espeak-ng` and `bluez` are recommended for spoken warnings and optional
BLE connections respectively, while `gdal-bin` is suggested for local GDAL raster maps.

```bash
sudo apt install ./out/packages/missionplanner10_*.deb
missionplanner10
```

The Debian package installs the application under `/usr/lib/missionplanner10`, adds a desktop
entry and exposes `/usr/bin/missionplanner10`. It replaces the previous `missionplanner` package
without reusing its installation directory. Package-managed installs do not overwrite
themselves with the in-app updater; update them through APT. The portable `tar.gz` includes
`install.sh` for a per-user desktop entry. Portable Linux, Windows and macOS builds can use the
in-app updater after the fork owner configures the Ed25519 release-signing secret; downloaded full
bundles are additionally pinned by SHA-256 before extraction.

## Runtime files

The application does not write settings or downloaded data beside the executable. Paths follow the
native convention on every supported platform:

| Purpose | Linux | Windows | macOS |
| --- | --- | --- | --- |
| Configuration | `$XDG_CONFIG_HOME/MissionPlanner10` (default `~/.config`) | `%APPDATA%\MissionPlanner10` | `~/Library/Application Support/MissionPlanner10` |
| Logs and user data | `$XDG_DATA_HOME/MissionPlanner10` (default `~/.local/share`) | `%LOCALAPPDATA%\MissionPlanner10` | `~/Library/Application Support/MissionPlanner10` |
| Download/cache data | `$XDG_CACHE_HOME/MissionPlanner10` (default `~/.cache`) | `%LOCALAPPDATA%\MissionPlanner10\cache` | `~/Library/Caches/MissionPlanner10` |
| Crash/state files | `$XDG_STATE_HOME/MissionPlanner10` (default `~/.local/state`) | `%LOCALAPPDATA%\MissionPlanner10\state` | `~/Library/Logs/MissionPlanner10` |

SRTM terrain, SITL binaries, parameter definitions, log metadata and updater downloads are caches.
Existing files from legacy `Mission Planner` folders are copied on first use without deleting the
old copy.

Map tiles are the deliberate exception to the renamed application paths: they remain in the
official Mission Planner cache (`$XDG_CACHE_HOME/MissionPlanner/map-tiles` on Linux,
`%LOCALAPPDATA%\MissionPlanner\cache\map-tiles` on Windows and
`~/Library/Caches/MissionPlanner/map-tiles` on macOS). They use
`<provider>/<z>/<x>/<y>.tile`; no SQLite tile database is used. GTU/Hermes uses these same platform
paths and provider identifiers
for compatible Google Satellite, Google Hybrid, Bing Satellite and OpenStreetMap tiles, allowing
both applications to reuse downloaded tiles without changing either application's other data.

Portable plugin DLLs live under the user-data `plugins` directory and can be managed from
**Tools > Plugin Manager** (`Ctrl+P`). They run as trusted in-process code with full application
access; install only plugins you trust. See
[portable plugins](docs/migration/Reference/PLUGINS.md) for the API and
platform paths.

The official TerrainMakerPlugin is built into Flight Planner as **Make Terrain DAT…**. It creates
ArduPilot whole-degree DAT tiles from the visible map area using the configured local DEM/SRTM
sources, reports the exact estimated output size, supports cancellation and atomically replaces only
complete tiles.

## Network services and privacy

The port keeps the useful network integrations described by official Mission Planner, but only for
features that are present here. Analytics and the upstream Cloudflare geolocation request are not
implemented. The main runtime network destinations are:

| Service | Purpose | When it is contacted |
| --- | --- | --- |
| This repository's GitHub Releases and API | Signed stable/beta application updates | Portable builds check at startup; manual checks are in Help. Package-managed installs do not self-update. |
| `firmware.ardupilot.org`, ArduPilot GitHub/raw content | Vehicle firmware manifests and files, parameter/frame defaults and SITL assets | When a connected firmware version is checked or the operator starts the corresponding download/tool. |
| Google, Bing, OpenStreetMap, Esri or an operator-supplied WMS/WMTS server | Map imagery | Only for the selected online map provider; cached/local maps remain available offline. |
| Hong Kong CAD eSUA | Official Hong Kong no-fly polygons | Only when **Show NoFly** and the separate Hong Kong option are enabled; results use a 12-hour disk cache. |
| NOAA SWPC | K-index space-weather value | A bounded background refresh at application startup. |
| `adsb.lol` | Optional external ADS-B traffic | Only while the external ADS-B receiver is enabled. |
| ArduPilot documentation, forum, RFDesign or CubePilot | Help pages and optional vendor firmware | Only after an explicit user action, except the connected-firmware availability check noted above. |

Telemetry links configured by the operator (UDP, TCP, serial, Bluetooth, NTRIP, video and similar)
necessarily contact their configured device or server. Portable plugins are trusted in-process code
and may add their own network behavior.

## Offline use

Vehicle connections, cached maps, mission editing, parameter work against a connected vehicle, log
review and local terrain sources do not require Internet access. Data that can be prepared on one
machine and copied to the matching runtime directory includes:

| Data | Offline support |
| --- | --- |
| Map tiles | Use the map cache manager or copy the cache directory. WMS/WMTS and online basemaps need prior cached tiles. |
| Elevation | Cached SRTM, local WGS84/EGM96 GeoTIFF and DTED are supported; local sources take priority. |
| Parameter and log metadata | Bundled fallbacks are included; newer downloaded definitions remain in the cache. Parameter *values* are deliberately never restored from a previous device/session. |
| No-fly zones | Local KML/KMZ files under the user-data `NoFly` directory work offline; the last Hong Kong eSUA response can be used from cache. |
| Firmware and SITL | Previously downloaded cache content can be reused; discovering or downloading new versions requires a network connection. |

Unlike the official Windows MSI, this port's MSI does not install a serial-driver CA certificate. Linux
serial access uses the operating system's device permissions; Windows drivers remain an operating-
system/vendor responsibility.

## Development

```bash
dotnet build MissionPlanner.slnx -c Release -m:1
dotnet test MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj -c Release -m:1
```

The clean solution build is expected to report zero warnings and zero errors. The current warning
audit and exact reproduction commands are in
[`docs/migration/WARNING_AUDIT.md`](docs/migration/WARNING_AUDIT.md). The conservative
file/directory/build-system classification is recorded in
[`docs/migration/PROJECT_CLEANUP_AUDIT.md`](docs/migration/PROJECT_CLEANUP_AUDIT.md); do not delete
excluded native files merely because they are not yet in the transitional Avalonia compile list.

## License

**GPLv3** (see [`COPYING.txt`](COPYING.txt)). This is a derivative work of Mission Planner and
inherits its license. Third-party runtime notices are retained in [`LICENSES/`](LICENSES/).
