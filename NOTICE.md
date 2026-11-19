# NOTICE

This repository integrates a native cross-platform Avalonia user interface directly into a fork of
**ArduPilot Mission Planner**.

## Upstream credit

- Based on **Mission Planner** © Michael Oborne and the ArduPilot project.
  <https://github.com/ArduPilot/MissionPlanner>
- Mission Planner is licensed under the **GNU General Public License v3.0** (see `COPYING.txt`).
- The cross-platform application and the reusable Mission Planner libraries now share this native
  source tree. The in-place migration baseline is commit
  `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`; the migration provenance is recorded under
  `docs/migration/`.

## What this project changes

- The root `MissionPlanner.csproj` builds the application with **Avalonia (.NET 10)** for macOS,
  Linux and Windows. Replaced WinForms application, plugin and UI-library sources have been removed
  after their native replacements or deliberate retirement decisions were recorded under
  `docs/migration/`.
- Flight, protocol, log, parameter and mission backends are reused from the native tree and receive
  cross-platform fixes directly in the same history.
- See `docs/migration/README.md` for the migration record and its acceptance gates, and
  `docs/STATUS.md` for the current status log.

## BLE transport dependencies

- `Linux.Bluetooth` © 2024 Xeno Innovations, Inc. is used for the Linux BlueZ/D-Bus
  transport under Apache License 2.0; see `LICENSES/Apache-2.0.txt`.
- `Tmds.DBus` © Tom Deseyn and Alp Toker is used by that transport under the MIT
  License; see `LICENSES/MIT-Tmds.DBus.txt`.
- Unmodified `SimpleBLE` 0.7.3 provides the official Mission Planner-compatible Windows transport
  and the macOS CoreBluetooth transport under GPLv3; exact source, release assets and pinned hashes
  are recorded in `LICENSES/SimpleBLE-0.7.3-NOTICE.txt`.

## macOS joystick dependency

- `HIDSharp` © 2010–2025 James F. Bellinger is used for the macOS IOKit HID joystick
  transport under Apache License 2.0; see `LICENSES/Apache-2.0.txt`.

## Video runtime dependency

- `LibVLCSharp` provides the managed video API and `VLC media player` 3.0.23 provides the native
  runtime. Windows uses the official VideoLAN NuGet; macOS Intel and Apple-Silicon artifacts bundle
  the corresponding unmodified official VideoLAN runtime. Exact binary/source URLs, sizes, hashes
  and upstream GPL/LGPL notices are recorded in `LICENSES/VLC-3.0.23-NOTICE.txt`.

## Python scripting dependencies

- `IronPython` 3.4.2 provides the embedded cross-platform Python runtime under Apache License 2.0;
  see `LICENSES/Apache-2.0.txt`.
- `IronPython.StdLib` 3.4.2 packages the Python 3.4 standard library. Its complete PSF, BeOpen,
  CNRI and CWI license history is retained in `LICENSES/Python-3.4-StdLib.txt`.

## MCP diagnostics dependencies

- `ModelContextProtocol`, `ModelContextProtocol.Core` and `ModelContextProtocol.AspNetCore`
  2.2.0 © Model Context Protocol a Series of LF Projects, LLC provide the embedded
  Streamable HTTP server under Apache License 2.0; see `LICENSES/Apache-2.0.txt`.
  Source: <https://github.com/modelcontextprotocol/csharp-sdk>, release commit
  `6fa3825973949a9c4f0cd8af344e15a8db09dc35`.

## License of this project

Because this work links Mission Planner's GPLv3 code, the combined work is a **derivative work and is
also licensed under GPLv3** (see `COPYING.txt`). You may copy, modify and redistribute it under those
terms, with source available and notices preserved.

## Not affiliated

This is an independent community port. It is **not** affiliated with, endorsed by, or supported by the
ArduPilot project or the original Mission Planner authors.
