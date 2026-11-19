# Cross-platform port status

The project targets Windows, macOS and Linux. The current synchronization was locally verified on
Linux Mint 22.3 (Ubuntu 24.04 base), x86-64, X11 on 2026-08-23. Self-contained Windows x64, macOS
x64 and macOS arm64 outputs were also cross-published and inspected on Linux. Native macOS CI loads
the IOKit and SimpleBLE dependencies and enumerates the available controllers and Bluetooth
adapters. Windows and macOS remain first-class targets and still require full runtime acceptance on
their native runners.

## Platform matrix

| Target | Packaging | Current verification |
| --- | --- | --- |
| Windows x64 (`win-x64`) | Self-contained folder, PE apphost; bundled libVLC and native SimpleBLE runtime | Cross-publish passed and PE32+ executable/native DLLs inspected; native Windows application and physical BLE-modem acceptance remain pending |
| macOS x64 (`osx-x64`) | Self-contained `.app`, Mach-O/dylibs; bundled official Intel VLC 3.0.23 and pinned x64 SimpleBLE runtime; CI signing/notarization when credentials are configured | Cross-publish passed; the apphost and every bundled native dependency were inspected as x86-64, and all 444 checksummed VLC runtime files, including 343 plugin dylibs, were verified. Full native Intel application and physical-device acceptance remain pending. Also runs on Apple Silicon through Rosetta 2. |
| macOS ARM64 (`osx-arm64`) | Self-contained `.app`, native Apple-Silicon apphost/dylibs; bundled official ARM64 VLC 3.0.23 and pinned ARM64 SimpleBLE runtime; CI signing/notarization when credentials are configured | Cross-publish passed; the apphost and every bundled native dependency were inspected as ARM64, and all 438 checksummed VLC runtime files, including 337 plugin dylibs, were verified. Native ARM64 CI loads libVLC/SimpleBLE/IOKit, enumerates available hardware and decodes the real MJPEG callback/export pipeline. Full application and physical-device acceptance remain pending. |
| Linux x64 (`linux-x64`) | Self-contained ELF/CoreCLR `tar.gz` and FHS-compliant amd64 `.deb` with native dependencies | Current source: Release build and 1129 tests verified; the NV-Modem/portable-plugin-host/legacy-plugin-ABI/HUD-recording/OSD-tlog-video/Grid-v2-editor/Face-Map/Open-Drone-ID/Tracker-Home-module/flight-command-shortcuts/Terrain-DAT-Maker/interactive-gimbal-video/gimbal-video-layouts/all-interface-antenna-tracker/DroneCAN-multicast/direct-SLCAN/session-safety/thread-safe-settings/signed-beta-updates/managed-WebSocket/MicroDrone/device-operations/default-settings/barometer-altitude/MAVLink-serial-TCP-bridge/firmware-archive/camera-overlay/SHP/SHP-to-POLY/DXF/GeoPackage/KML-GroundOverlay/Hong-Kong-NoFly/GeoTIFF/DTED/native-GDAL/airport-alpha/Rally/docking/detachable-Flight-Data-panels/WMS-WMTS/map-tile-import/SSH/SFTP/LogIndex/MagFit/Heli/connection-safety/nonblocking-device-loss/multi-link/Plane-Formation/FollowPath/FollowMe/MovingBase/WaypointLeader/FollowLeader/Sequence/Translation-RESX/Terrain-3D/cross-platform-BLE/macOS-ARM64-video `.deb` is rebuilt and verified after each functional commit; the portable tarball predates the latest rounds |

Speech is implemented per platform: Windows uses `System.Speech` through PowerShell, macOS uses
`say`, and Linux uses `speech-dispatcher` with the real `espeak-ng` output module
(`spd-say -w`, with a Festival fallback). Speech requests are serialized through a bounded,
duplicate-coalescing queue; Planner Settings includes an audible backend test and result status.

## Upstream baseline

This port is concretely based on a Mission Planner git commit, not on an approximate source copy.
The `external/MissionPlanner` gitlink was originally pinned to:

- `14840eb0cd56b6ad824e05475383484d3213678f` (2026-06-19)

The integration branch now pins:

- `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`

That advances the reusable upstream source by ten commits. All changes in referenced ArduPilot,
MAVLink, DroneCAN, DFLog, LogSort and location libraries are compiled directly from the updated
submodule. UI-only changes were translated to Avalonia where applicable:

| Upstream change | Cross-platform port result |
| --- | --- |
| DroneCAN parameter enumeration fix | Compiled directly from upstream |
| MAVLink null handling, CurrentState messages, Locationwp fixes | Compiled directly from upstream |
| DFLog bad/unnamed cache and large `timems` fixes | Compiled directly from upstream |
| Log sorting: `.tlog`, `.rlog`, `.bin`, `.log`, HIL flag | Upstream sorter plus a new Avalonia action in Flight Data |
| Plugin custom flight action hook | Avalonia `RegisterCustomAction` / `UnregisterCustomAction` API added |
| Plugin custom HUD drawing hook | Avalonia HUD `CustomPaint` event added |
| Embedded HTTP server hardening | Server is absent from the Avalonia port; no older vulnerable server was reintroduced |

## Features restored during this synchronization

- Setup contains an **NV Modem** child page immediately below SiK Radio. It ports the dirty
  `NV5Settings` source from AgroSky GTU, including NV4/NV5 discovery, generation-specific parameter
  descriptions, typed MAVLink parameter encoding, live dual-radio status, radio/RTSP presets,
  cross-device channel copying, printable key generation and protected-key handling, RTSP path
  get/set, transmitter diagnostics, reboot, bounded retries and Mission Planner-compatible `.param`
  files. The page does not create a private socket or serial connection: every device identity
  includes the exact already-open MAVLink interface plus system/component IDs, so identical sysids
  on two links remain isolated. Device changes and refreshes immediately discard displayed values;
  no modem parameter list is persisted between application sessions. See [`NV_MODEM.md`](NV_MODEM.md).
- MAVLink signing key management: add/use/delete/disable.
- A visible cross-platform Tools menu exposes MAVLink Inspector, MAVLink Mirror, NMEA output,
  Cursor-on-Target/TAK output, spectrogram, link/connection diagnostics and log tools.
  Upstream-equivalent Ctrl+F/Ctrl+G/Ctrl+L/Ctrl+W
  shortcuts are restored, with Ctrl+I added for direct MAVLink Inspector access.
- The hidden upstream `temp.cs` developer form is replaced by an explicit Avalonia Developer Tools
  page. It ports MAVLink packet and hardware-ID decoders, APJ embedded-defaults editing, DataFlash
  splitting, DashWare CSV, raw GPS-correction extraction, log organization, arbitrary MAVFTP file
  download (including `@SYS/threads.txt`), parameter-recovery restore, QNH, barometric-altitude
  adjustment, forced recovery calibration flags, reboot/DFU/bootloader actions and remote DataFlash
  logging. The altitude adjustment preserves the official pressure-offset formula, uses only the
  selected vehicle's cached `GND_ABS_PRESS`/`BARO1_GND_PRESS` and revalidates the exact live target
  before writing, so an unresponsive or newly selected modem cannot inherit a blocking read or a
  stale write. Vehicle-changing
  actions require a live link, a disarmed vehicle and explicit confirmation where destructive.
  Its parameter-recovery path is additionally cancellable and bound to the exact active link,
  MAVState, system and component. A modem/vehicle switch, link loss or arming event stops the
  workflow between bounded upstream parameter calls before another write. All 67 official click
  handlers are classified in [`TEMP_HANDLER_AUDIT.md`](TEMP_HANDLER_AUDIT.md); none remains open.
- Developer Tools now ports the official hidden `MAVLinkSerialPort` TCP bridge. One sequential TCP
  client can exchange raw bytes with TELEM1/2, GPS1/2, SHELL or SERIAL0-9 through MAVLink
  `SERIAL_CONTROL`; the official TCP port 500, GPS1 and current UART baud are the defaults. The
  listener is localhost-only unless the operator explicitly enables an unauthenticated,
  unencrypted remote listener. Because `SERIAL_CONTROL` has no target-system field, the bridge
  fails closed unless the selected disarmed autopilot is the only MAVLink system on its telemetry
  link. A modem/vehicle/component change, physical link loss, arming or discovery of another system
  stops the listener and releases the exclusive UART without waiting for a TCP client. The same
  release happens between sequential clients and on explicit stop.
- Developer Tools ports the official hidden `rip all fw` workflow as Download Firmware Archive.
  It reads `firmware2.xml` through two HTTPS manifest mirrors, deduplicates its firmware references
  and downloads at most four files concurrently. Because the current official manifest still
  contains legacy unsigned HTTP references and stale third-party files, each HTTP URL is tried over
  HTTPS first and falls back only after a network/HTTP failure and an explicit operator warning.
  The archive records SHA-256 for every saved file, bounds manifest and firmware sizes, retains
  unavailable URLs in the local manifest with a report, supports immediate cancellation and only
  publishes a new directory after all attempts finish. Existing directories are never overwritten.
- The official hidden ResEdit workflow is a native Translation / RESX Editor in Developer Tools.
  It scans the selected Mission Planner source tree for the same `Strings.resx`, `.Text`, `.ToolTip`,
  `HeaderText` and `ToolTipText` string set, combines neutral text with an exact selected culture and
  exports the official sparse `<name>.<culture>.resx` layout plus resumable `output.html` and CSV.
  Existing WinForms manifest-resource names in old `output.html` files are mapped back only when the
  source file/key identity is unambiguous. Scans and exports are cancellable; `bin`, `obj`, generated
  `translation`, repository metadata and directory symlinks are excluded. XML DTDs are rejected,
  HTML/CSV values are escaped, output paths remain below the selected root, writes are atomic and
  every replaced RESX/HTML file is copied into a timestamped `.backup` tree first.
- The hidden upstream `DevopsUI` is a native MAVLink Device Operations window reachable from Tools,
  Developer Tools and Ctrl+J. It supports the official SPI/I2C register reads and ICM20948
  write/read developer test, decodes all upstream DEVICE_OP status values and validates the fixed
  MAVLink field sizes. Operations are bound to the exact active modem/system/component session: a
  target change immediately disables the window, discards an in-flight result and requires an
  explicit rebind, including after a rapid switch away and back. The write test is additionally
  blocked while armed and repeats its target/disarm validation after the explicit hazard warning.
- Mission Command List is a native editor for upstream-compatible `PlannerExtraCommand` and
  `PlannerExtraCommandIDs` settings. Custom numeric MAV_CMD values and P1-P7 labels are immediately
  reflected in the Flight Planner command picker and column headers.
- Cursor-on-Target 2.0 output emits every MAVLink system with invariant XML timestamps/coordinates
  over TAK multicast, UDP client/host, TCP client/host or a serial port. UID prefix, callsign,
  event type and update interval are configurable in the native window. Its per-system identity grid
  restores upstream UID, TAKV, contact callsign/endpoint and VMF fields, reads/writes the official
  `CoTUID` settings format and preserves rows for offline systems.
- Warning engine startup, cross-platform speech adapter and warning editor. The advisory
  geomagnetic K-index is fetched from NOAA once per UTC day, cached and exposed through the shared
  `CurrentState` warning surface without delaying application startup.
- Theme selection includes a native custom-palette editor with validated, persisted Avalonia
  colours; the edited palette is applied immediately.
- FFT log analysis window.
- DataFlash spectrogram for ACC/GYR sensors 1–5 with X/Y/Z plots.
- Proximity radar for `DISTANCE_SENSOR` / `OBSTACLE_DISTANCE` telemetry.
- Parameter metadata regeneration from current ArduPilot definitions.
- Log organization for `.tlog`, `.rlog`, `.bin` and `.log`.
- The upstream Log Index is a native, single-instance Avalonia window reachable from Log Browser
  and Developer Tools. It recursively indexes DataFlash and timestamped telemetry logs, displays
  date/frame/sysid/duration/size/Home/time-in-air/distance/CAM metadata, aggregates multi-selection,
  opens a row in Log Browser and creates exact `<source>.jpg` route thumbnails from cache-only map
  tiles. Scans and initial DataFlash reads are cancellable. Permanent deletion is reject-by-default,
  revalidates root/source/paired-rlog identity and never uses the upstream ambiguous same-stem glob.
- Offline magnetometer calibration ports the official MagFit workflow for `.tlog`, `.bin` and `.log`
  files. It separates up to three compasses, preserves the upstream raw/offset convention and
  throttle/bucket filtering, and offers cancellable sphere or full ellipsoid ALGLIB fitting with
  sample count, eight-octant coverage, old/new offsets, DIA/ODI and RMS review. Analysis never needs
  a vehicle connection; applying values is a separate reject-by-default action that prevalidates the
  complete parameter set and requires a connected, disarmed vehicle.
- Tlog conversion now includes streamed CSV and human-readable packet exports, extraction of the
  latest parameter values, and reconstruction of complete QGC WPL mission snapshots in addition to
  KML, GPX and Matlab output.
- Upstream-compatible custom flight actions and HUD drawing extension points.
- The official plugin lifecycle now has a native cross-platform host. Portable DLLs are discovered
  in user and install `plugins` directories, run `Init`/`Loaded`/scheduled `Loop`/bounded `Exit`,
  resolve private managed and native dependencies in collectible load contexts and share the live
  MAVLink/settings contracts. Tools > Plugin Manager and Ctrl+P expose metadata, diagnostics,
  refresh/load and the upstream `DisabledPlugins` enable policy. Plugin actions, connection events
  and HUD overlays are cleaned automatically; one blocking/erroring loop cannot stall the shared
  scheduler, repeated loop faults are stopped, and plugin diagnostics use a bounded rotating log.
  Plugins remain trusted in-process code. A shipped `MissionPlanner.dll` ABI shim also loads
  existing non-visual .NET Framework plugin binaries without recompilation, including official
  lifecycle/metadata fields, active/all-link MAVLink access, settings, device events and local
  mission-list operations. Binaries that call WinForms/GMap UI members still require source-level
  UI adaptation and recompilation against the documented portable API.
- Joystick input now has native backends on every release target: a port-native Linux joydev reader,
  upstream DirectInput on Windows and IOKit HID on macOS through the managed HIDSharp transport.
  The macOS decoder handles descriptor-declared signed/unsigned ranges, Generic Desktop and Flight
  Simulation axes, two sliders, up to 128 buttons, hat switches and discrete D-pads. Enumeration
  rejects unrelated HID collections, gives identical controllers stable privacy-preserving labels
  and isolates unreadable devices; hot-unplug stops the reader and releases active vehicle control.
  Linux retains deterministic lifecycle, raw input preview and Avalonia-safe axis/button detection.
  An application-level 20 Hz sender now mirrors upstream RC override/manual-control behaviour,
  including the native UDP RC path for the built-in SITL and safe release on disable/link loss. It
  remains active after leaving Setup and can be released immediately from Flight Data. Opt-in,
  per-device Linux range calibration expands controllers whose real HID endpoints do not cover
  their advertised range; uncalibrated devices keep the original bit-for-bit mapping. Calibration
  uses a detached read-only joydev session and releases active control first, so endpoint movements
  cannot reach SITL or a real vehicle.
- Simulation now ports all four current official multi-instance launch actions: Copter single-link
  chain and Copter/Plane/Rover multilink swarms. Each instance receives the upstream four-metre home
  offset, instance-specific TCP/RC ports and an isolated `identity.parm` with its own MAV sysid.
  Multilink instances use the shared independent-connection runtime, so parameters and disconnects
  remain scoped to the selected vehicle. A partial process or telemetry-link failure rolls the whole
  launch back instead of leaving an ambiguous half-running swarm, and Stop all removes only links
  owned by that simulation session. A two-Copter Linux acceptance run verified both real SITL
  processes, TCP links and distinct sysids end to end.
- Standalone Antenna Tracker serial output ports all three interfaces from the pinned Mission
  Planner source: Maestro compact binary commands, ArduTracker PWM text commands and DegreeTracker
  tenths-of-a-degree text commands. Both Setup entries share the same tested drivers and settings;
  live tracking follows the currently selected modem/vehicle, reverse and trim changes apply
  immediately, unsafe numeric input is rejected, and serial close is synchronized with in-flight
  writes. Byte-exact protocol, 180-degree Maestro tilt flip, reversal, validation, connect/disconnect
  and concurrent-disposal tests cover the hardware-independent behavior.
- Persisted serial, TCP, UDP client/listener and WebSocket endpoints can auto-connect at normal app
  startup without reopening the endpoint prompt. An interactive network connection now uses the
  single combined Avalonia address/port dialog and suppresses the transport's second upstream prompt.
- WebSocket telemetry now uses a port-owned managed transport instead of the inherited `async void`
  reader. Raw WebSocket endpoints no longer receive a spurious Socket.IO probe, explicit Socket.IO
  endpoints retain the MAVControl handshake and binary prefix, fragmented binary messages remain a
  continuous MAVLink stream, queued input is bounded, writes are serialized, reconnect attempts are
  bounded and a user Disconnect cancels and releases the reader promptly even after physical loss.
- Serial connections restore the exact upstream baud-rate set plus arbitrary custom rates, remember
  rates per physical port and expose the active MAVLink system/component selector. AUTO scan now has
  visible progress and cancellation and records the actual detected port/rate instead of persisting
  the synthetic `AUTO` endpoint.
- Bluetooth LE ports the official Nordic UART transport on all three targets. Linux uses a managed,
  cancellable BlueZ/D-Bus scan; Windows and macOS use the official SimpleBLE 0.7.3 C ABI, with the
  required Windows DLLs retained and checksummed x64/arm64 macOS dylibs fetched from the upstream
  release. The selector exposes stable names with 48-bit addresses or CoreBluetooth UUIDs and opens
  devices as buffered MAVLink serial streams. Scan, connect, read and write operations are bounded;
  GATT writes are serialized and chunked; early notifications are retained; remote disconnect and
  user cancellation wake blocked operations so a missing modem cannot hold the connection UI.
  Linux scans reuse the process-wide adapter subscription rather than leaking D-Bus watchers, while
  native callback ownership and adapter/peripheral handles are released on every SimpleBLE path.
- The official Connection List workflow is restored for `tcp://host:port`, `udp://host:port`,
  `udpcl://host:port` and `serial:port:baud` files. Independent MAVLink interfaces are opened with
  bounded parallelism, per-line telemetry logs, reader/heartbeat/stream-request lifecycles and
  prompt cancellation when a modem does not answer. The top selector combines systems/components
  from every live line and shows their endpoint; choosing one atomically redirects flight data,
  planner, parameter pages, joystick and traffic uplink. Flight Data keeps the active aircraft red
  and renders the other connected aircraft as grey heading-aware markers. Closing or losing the
  active line falls back to another live line without blocking on the old transport.
- Tools > Swarm Formation restores the official leader/follower Formation workflow on top of that
  multi-link runtime. It discovers every current MAVLink component without changing the globally
  selected vehicle and permits only explicit Plane/Copter/Rover autopilot followers. Copter/Rover
  followers receive yaw-rotated global position plus leader-velocity targets at 10 Hz. The official
  experimental ArduPlane branch is also native: after a separate opt-in it requests 10 Hz position
  and attitude telemetry and sends the upstream approach-geometry, pitch/thrust PID and quaternion
  `SET_ATTITUDE_TARGET` controller with a protocol-valid body-rate mask. The native Avalonia window
  includes a draggable zoomable X/Y grid, precise X/Y/Z table,
  capture-from-current-position, leader rebasing, yaw/gimbal options, live mode/arm/GPS state and
  reject-by-default Arm/Disarm/Takeoff/Land/GUIDED/AUTO actions for the exact checked follower set.
  Link identity is part of every target: reusing the same sysid on another UDP modem cannot inherit
  commands. A closed/replaced link, stale telemetry, missing leader/follower, edited running plan or
  invalid/non-finite offset/output stops before another batch is sent. The confirmation identifies
  each follower's controller and adds a fixed-wing warning; Plane control remains off by default.
- Tools > Swarm Follow Path restores the official beta trail-following workflow for ArduPlane,
  ArduCopter and ArduRover. The native window chooses one exact leader plus an explicit checked
  follower set, assigns unique trail order values and continuously sends 5 Hz GUIDED targets at
  `order × separation` behind the newest leader position. The port interpolates exact points between
  samples and traverses backwards from the current leader; this fixes the upstream implementation's
  oldest-point traversal and sample-spacing errors. No trail or selection is persisted between
  runs. Commands are withheld from the whole group until enough trail exists and every endpoint,
  sysid, component, firmware, position and telemetry timestamp has passed validation. Link changes,
  a replaced modem, plan edits and leader GPS jumps over 500 m stop the stream; LAND and DISARM stop
  it before issuing their bulk command. Plane targets use the official guided mission-item path and
  require at least 30 m separation, while Copter/Rover use global-relative-altitude position targets.
- Tools > Swarm Waypoint Leader restores the larger official beta workflow launched from upstream
  `temp.cs`. One exact ground master is observed without receiving commands; one ArduCopter air
  master supplies the downloaded waypoint/spline profile and leads an explicitly checked,
  uniquely ordered Copter follower set. The native window includes line/V formation and altitude
  interleave settings, the live mission-altitude graph, path/off-path status and the upstream
  staged takeoff → fly-to-user → follow-user → return-along-mission → separated-altitude RTL state
  machine at 10 Hz. It preserves the official `RTL_ALT`/`RTL_ALT_M` and
  `WPNAV_ACCEL`/`WP_ACC` writes, while requiring reject-by-default confirmation before flight and
  before reset, return or abandon actions. Every tick revalidates exact link/sysid/component,
  complete group telemetry, firmware, position and the confirmed mission signature before sending
  a batch; modem replacement, stale telemetry or a mission edit therefore fails closed. The
  upstream 0.1 m path expansion is represented by exact compact segments and interpolation, and
  landing-altitude targets are immutable rather than accidentally accumulating on timer ticks.
- Tools > Swarm Follow Leader ports the official 10 Hz ground-trail controller. One exact ground
  vehicle is observed, while an exact Copter air master flies one separation ahead and up to 21
  explicitly ordered Copter followers occupy the current/recorded trail. It preserves the official
  air/follower velocity factors and near-waypoint turn correction, but adds complete-batch stale
  telemetry, link-replacement and GPS-jump rejection before emitting another setpoint.
- Tools > Swarm Sequence Layout Editor loads and saves the official `Layouts`/`Steps` JSON shape,
  edits east/north/relative-altitude offsets on a draggable native grid, orders steps and maps every
  layout sysid to an exact modem/system/component identity. Run Step preserves the official captured
  origin and zero-velocity position-target behaviour; reset and the official 2 m GUIDED/arm/takeoff
  action are exposed with reject-by-default confirmations. Upstream `DelayStart`/`DelayEnd` values
  round-trip but are not executed because the official controller does not execute them either.
- Parameter lists are deliberately session-only: neither the port nor the compiled upstream
  `MAVState` writes a reusable vehicle-parameter cache. Disconnecting, beginning a new connection or
  selecting another MAVLink system or modem clears both the previous and newly selected
  values/types/count immediately, including the selector's transient empty state. The Raw
  Parameters editor stays empty while `PARAM_VALUE` packets form a partial list and configuration
  fields remain behind the loading overlay until a complete live list arrives; a failed read clears
  any partial values again. Post-connect
  reads are globally serialized and latest-wins cancellable, so a silent UDP modem cannot hold the
  next selected device behind an unbounded retry. Connected configuration view models are recreated
  on target/list changes, preventing controls from retaining another device's values. After a live
  connection, the port refreshes version-specific parameter metadata and independently checks the
  official ArduPilot manifest for a newer stable vehicle firmware without blocking the link UI.
- Survey Grid now generates upstream-style mission commands: optional takeoff, speed, heading hold,
  spline starts, waypoint delay, RTL/land, distance/digicam/repeat-servo/set-servo camera control and
  per-strip trigger start/stop. It also ports the official complete-strip split-flight output with
  `DO_JUMP` selectors and speed restore, exact `GridData` `.grid` persistence, merged built-in/user
  camera XML profiles, sample-JPEG dimensions/focal length and SRTM ground-elevation statistics.
  Its native Mapsui preview mirrors the current Mission Planner controls: draggable red boundary,
  yellow route segments with distance hover, shared numbered waypoint/photo markers, terrain-aware
  camera footprints, the 1–8+ overlap palette and legend, point-start vertex selection and the
  optional distance optimiser. Boundary edits are returned to Flight Planner when the grid is
  accepted. The working geometry modes from the separate upstream Grid v2 plugin are integrated in
  that same preview: draw a rectangular replacement, move the complete boundary or shift the
  nearest edge perpendicular to itself, in addition to dragging individual vertices. SimpleGrid is
  a strict subset of this workflow and calls the same `Utilities.Grid.CreateGrid` implementation.
  Grid v2's displayed aircraft turn radius and maximum flight time never affect its grid or mission,
  while its min/max speed values only populate otherwise unused fields. The six historical 3DR
  profiles merely limit a redundant altitude slider without validating the editable altitude or
  generated mission, so those inert/presentation controls are not presented as missing functionality.
- Face Map is a native `Auto WP` workflow for facade, quarry-wall and multi-bench surveys. It ports
  the official UTM offset/lawnmower geometry, toe passes, face direction, berm/bench offsets,
  gimbal-pitch progression, yaw toward the surface, optional return path and extra turn images.
  Its mission builder supports takeoff, speed, RTL/land, complete-strip split flights with
  `DO_JUMP`, distance/digicam/repeat-servo/set-servo triggers and stop/start boundaries. The
  draggable Mapsui route preview and live bench cross-section replace the WinForms/GMap drawing;
  built-in/user camera profiles, sample-JPEG metadata, persisted `facemap_*` settings and official
  `.facemap` XML files remain interchangeable. Parallel and near-parallel face legs use a bounded
  bevel instead of the official plug-in's invalid UTM-zero intersection, and absolute missions add
  the planned home altitude explicitly rather than emitting relative heights in a global frame.
- The integrated DroneCAN parameter page now has search, favourites, modified-only filtering and
  `.param` import/export; failed writes remain visibly dirty instead of being accepted locally.
  Its MAVLink-CAN session, forwarding packets and every node parameter/firmware operation are bound
  to the exact modem, system ID and component ID captured at Connect. Changing the active modem,
  vehicle or selected DroneCAN node cancels or invalidates outstanding work; node/parameter/log UI
  is cleared immediately and late callbacks cannot repopulate it, including switch-away-and-back
  races. SLCAN initialization runs off the UI thread, and Disconnect remains available while an
  unresponsive operation is winding down. The official direct SLCAN workflow is also ported: a
  native serial-port/host-baud selector opens Lawicel-compatible adapters without a MAVLink link,
  persists stable removable-device paths, performs the upstream C/S/N/V/O/F initialization and
  exposes the same optional `C` command on exit. The host handle is always released even when the
  adapter is deliberately left in SLCAN mode. A selected port cannot be opened while any primary or
  Connection List MAVLink link still owns the same device, including Linux symlink aliases.
  `Prepare autopilot SLCAN` reproduces the official CAN1 parameter sequence
  (`CAN_SLCAN_CPORT`, `CAN_SLCAN_TIMOUT`, `CAN_P1_DRIVER`, optional `CAN_SLCAN_SERNUM=0`) with a
  disarmed check, explicit warning and exact modem/system/component revision binding. Direct-adapter
  node sessions remain independent when the operator switches an unrelated MAVLink modem. The
  official pydronecan multicast modes are ported as well: CAN1/CAN2 join `239.65.82.0/1:57732` on a
  selected, persisted, live multicast-capable IPv4 interface without requiring MAVLink. Their
  little-endian header, extended-ID flag, CAN-FD flag, 64-byte limit and CRC16 are wire-compatible;
  malformed datagrams are discarded. Incoming frames are kept separate from the outbound virtual
  SLCAN path to prevent multicast echo storms, and errors/disconnect always stop the receiver and
  release the UDP socket.
- Full Parameter List includes the upstream-compatible, explicitly confirmed reset-to-default and
  reboot flow. Setup now also exposes the official Mission Planner `DefaultSettings` workflow as a
  dedicated page: it recursively catalogs `.param` profiles below ArduPilot `Tools/Frame_params`,
  caches the list for the session and supports an explicit refresh. Catalog/download requests are
  cancellable and paths are constrained to that upstream tree. Local files and downloaded profiles
  are compared with the live parameter set, with the upstream runtime and barometer-calibration
  exclusions, then selectively staged and reviewed before any write reaches the vehicle. A profile
  comparison is bound to the exact modem/system/component selection revision and is discarded if
  the user switches away while the download or comparison dialog is open.
- Traditional Heli setup now includes the official live visual feedback as native Avalonia controls:
  the four-point Stabilize collective curve, 101-point Acro expo curve, live mapped collective
  cursor, collective/rudder inputs with manual-mode range capture and the three swash-servo position
  needles. Its 100-ms timer follows page activation/deactivation instead of remaining alive after
  navigation, and the writable parameter/manual-servo workflow remains intact.
- ADS-B aircraft received as MAVLink `ADSB_VEHICLE` telemetry or from an external receiver are
  retained with source-aware expiry and rendered as labelled, heading-aware traffic markers.
  External input supports SBS-1, AVR and Beast TCP streams, local dump1090/readsb fallbacks and an
  adsb.lol-compatible HTTP endpoint; the official server/port settings and source-precedence rules
  are preserved. The nearest external targets can be uplinked to the vehicle at the upstream 1 Hz,
  ten-aircraft/10 km limits. MAVLink `OA_DB` obstacles are rendered with their radius, while AIS
  vessels are deduplicated by MMSI, aged independently and shown with distinct vessel markers.
  Explicit collision severity survives subsequent position packets, and unchanged traffic no
  longer rebuilds the layer at every 300 ms map tick.
- `CAMERA_FEEDBACK` telemetry is rendered as numbered photo markers, flags captures below
  `CAM_MIN_INTERVAL` and draws the projected footprints of the latest four images using the
  upstream terrain-aware camera geometry.
- The shared MAVLink runtime now owns every normal and built-in-SITL connection: it continuously
  reads packets, sends the optional GCS heartbeat, requests the configured stream rates, polls
  missing parameters, detects a disarmed silent/dead link and releases joystick control safely.
  An armed radio link is kept open through a telemetry fade so it can recover, as in upstream. It also
  restores GCS sysid, serial reset/ESP settings, refreshes Home on an arm transition and closes
  telemetry logs on an unexpected link loss.
- Physical USB/serial removal and a silent UDP modem can no longer trap the UI inside an upstream
  driver `Close()`. The dead transport is atomically detached, its logical connection and parameter
  state are cleared immediately, and OS cleanup continues against only the captured old stream in
  a dedicated background thread, without starving shared async continuations. Port selection,
  explicit disconnect, multi-link fallback and a new connection all remain available even when that
  old driver never returns; cancelling an open or foreground parameter read also stops waiting for
  its synchronous upstream worker.
- Vehicle terrain serving was re-audited against the pinned source: each opened `MAVLinkInterface`
  instantiates the official `TerrainFollow`, receives `TERRAIN_REQUEST` through the shared reader and
  sends 4×4 `TERRAIN_DATA` grids from the cross-platform SRTM cache. This inherited workflow was
  already active and is no longer incorrectly listed as absent.
- Flight Planner's `Make Terrain DAT` action ports the official `TerrainMakerPlugin` as a native,
  cancellable Avalonia window. It starts from the visible map bounds, expands them to the official
  whole-degree tile names, preserves the upstream grid coordinates, sample order, validity bitmap,
  little-endian header and XMODEM CRC, and uses the same GeoTIFF -> DTED -> SRTM elevation priority.
  Exact tile/block/sample/file-size estimates and an explicit replacement confirmation are shown
  before generation. Each completed tile is flushed and atomically published, while cancellation
  removes only the in-progress temporary tile. The port also corrects a pinned upstream off-by-one:
  the original trailer loop emits 2047-byte records although its offsets and declared format use
  2048-byte records; generated files here contain complete 2048-byte zero-padded blocks.
- Setup > Advanced > Elevation Sources restores the official local DEM workflow and `GDALImageDir`
  setting without requiring a native GDAL installation. It recursively indexes GeoTIFF and
  DTED0/1/2 files in the background at startup, reports per-file coverage/errors, supports progress
  cancellation and preserves the official GeoTIFF -> DTED -> downloaded-SRTM altitude priority.
  Changing an already active DEM directory is staged for restart so stale and new terrain indexes
  cannot be mixed invisibly in one session.
- The same setup page restores the official `GDAL Custom` map provider when a current native GDAL
  runtime is installed. The port discovers GDAL dynamically instead of bundling upstream's obsolete
  Windows-only 2.3.2 package, opens candidate datasets read-only, creates north-up EPSG:3857 warped
  views, and samples only each requested tile. Multiple local rasters retain transparency and are
  overlaid coarse-to-fine on Google satellite imagery; corrupt or unsupported files are isolated and
  reported without disabling the managed elevation path. Startup and manual scans are asynchronous
  and cancellable, and replacing an index closes its dataset handles safely.
- `Load Waypoints on connect` now opens the planner and reads the mission, while `Params Background
  Load` performs a cancellable live parameter-protocol read without holding the connect UI open.
- Flight Planner can read and write mission, fence and rally storage through ArduPilot's MAVFTP
  `@MISSION/*.dat` files, with an automatic fallback to the standard MAVLink mission protocol when
  the firmware or transport does not support MAVFTP mission storage.
- The official six-action Rally Points map submenu is restored: set at the clicked coordinate,
  download, upload, clear, save `.ral` and load `.ral`. Adding or clearing points preserves the
  separate active-mission store and participates in Undo. An online clear writes an empty Rally
  mission before changing the local list; a transport failure retains local data and directs the
  operator to download again to verify potentially partial vehicle state.
- Flight Planner's `Switch Docking` map action reproduces both upstream arrangements: action tools
  on the right with waypoints below the map, or action tools below with waypoints to the right.
  Both splitters remain usable, the bottom toolbar scrolls horizontally, and the exact upstream
  `FP_docking=Right|Bottom` setting restores the layout on the next view instance.
- Planner/Flight Data runtime settings that previously only wrote a preference now take effect:
  heading-up map rotation, track length, distance-to-home visibility, HUD overlay visibility and
  reduced refresh rate on slow machines. Colour-type WarningEngine rules now highlight their
  matching QuickView cell with readable foreground text.
- Map tiles support upstream-style server-only, server-and-persistent-cache and strictly offline
  cache-only modes. Flight Data and Flight Planner share the cross-platform disk cache and apply a
  mode change immediately without mixing providers that happen to use the same z/x/y coordinates.
- Map Tile Cache ports the official hidden `GE Injection` workflow into the normal Ctrl+X window.
  It recursively imports the upstream `Z<zoom>/<row>/<column>.jpg|png` hierarchy into one explicitly
  selected provider cache, validates coordinates and decodes every image before replacing an
  existing tile. Provider caches remain isolated; invalid paths, corrupt/oversized images and
  duplicates are counted without entering the cache, and a long import is cancellable.
- Developer Tools ports the official hidden `Shp to Poly` workflow. It writes the same
  `poly-1.poly`, `poly-2.poly`, … files next to the selected shapefile, preserves SHP feature and
  coordinate order, retains closing vertices and emits invariant `latitude<TAB>longitude` text.
  Case-insensitive `.prj` sidecars are reprojected to WGS84 through the existing managed reader;
  replacement is explicit and atomic instead of silently truncating an existing output.
- The hidden official `OpenGLtest2` developer workflow is now a native 3D Terrain View reachable
  from Tools and Developer Tools. A cancellable SRTM mesh is draped with the selected map imagery
  through the same online/cache policy, while a bounded 64-tile atlas prevents an extreme range or
  zoom from creating an unbounded download. The view follows live MAV roll/pitch/yaw and NED
  velocity, draws the mission plus Guided/Target/MAV markers, supports fog, vertical exaggeration,
  upstream-compatible minimum/maximum imagery zoom, `Lock to MAV`, W/S/A/D/Q/E/R/F free-camera
  controls and terrain ray-picking. As upstream does, a
  terrain click sends a waypoint at the already configured guided altitude without changing mode;
  the port additionally refuses a zero altitude or disconnected link instead of reporting a
  target that the MAVLink library silently discarded.
- RF Propagation restores the upstream Ctrl+W settings and all three operational overlays on both
  Flight Data and Flight Planner maps: SRTM elevation/terrain shading, the `SightGen`-equivalent
  360-degree terrain-intercept contour, and red/orange Home/vehicle battery-distance rings. It
  preserves the official setting keys, rainbow palette and missing/ocean transparency, while
  making the published 0.5-degree azimuth option functional and bounding the upstream zero-degree
  convergence loop. Cancellable generation guards prevent stale Mapsui results from replacing a
  newer viewport; missing DEM sectors are reported instead of being drawn as verified RF coverage.
- Airport overlays reuse the pinned Mission Planner `airports.csv`, `Airports.ReadOurairports`
  filters and 100-km proximity cache. Flight Data honours the upstream-default-on `showairports`
  setting while Flight Planner always shows airports. The Mapsui layer preserves the official
  9-km/5,559-m Australia translucent-red disks without outlines, zoom > 3 gate, read-only behaviour
  and 50-pixel hover names. Its default opaque-white `VectorStyle` is disabled so the exact upstream
  red alpha of 25/255 composites directly over map tiles instead of producing solid pink disks. A
  renderer-level regression verifies the lower layer remains visible through a disk on a contrasting
  background; database loading is shared and asynchronous.
- SITL and normal/auto connection attempts share one serialization gate; delayed auto-scan cannot
  overwrite a concurrently started simulator link. Joystick shutdown also waits for an in-flight
  sender asynchronously, so releasing overrides cannot freeze the Avalonia UI.
- Autopilot and DroneCAN parameter favourites use separate persisted namespaces, with a
  non-destructive migration from the early shared-key build.
- The uAvionix transponder page now displays live maintenance/GPS/TX/airborne fault flags and the
  upstream NIC/NACp accuracy labels decoded by `CurrentState` instead of fixed disabled placeholders.
- Upstream library message/input callbacks are bridged to Avalonia, so recoverable joystick,
  mission-file, GStreamer and transport errors cannot fall through to `ShowEvent Not Set`.
- PX4Flow image assembly is port-native and writes grayscale MAVLink frames directly to an Avalonia
  bitmap; the Windows-only upstream `System.Drawing.Bitmap` path is no longer used by the page.
- Cross-platform telemetry log directory picker.
- Four palette resources use statically typed Avalonia resources.
- The Terminal page now carries all three current workflows: MAVLink NSH over `SERIAL_CONTROL`,
  raw active-link mode for experts, and the official companion-computer SSH path. SSH uses a
  cancellable `xterm` 80×24 shell with interactive text/paste, Ctrl/arrow/Home/End/F-key sequences,
  cursor/screen editing and alternate-screen handling for tools such as `nano`. A selectable native
  renderer preserves standard/bright/256/true-colour SGR foreground/background colours, bold,
  italic, underline, inverse video, erase/move attributes and the remote cursor. Unlike upstream's
  implicit trust, first contact pins the server's SHA-256 host-key fingerprint and a changed key is
  rejected behind a non-default MITM warning. Host, port and username may be remembered; the
  password is never persisted or logged.
- ArduPilot onboard Lua REPL over `MAV_CMD_SCRIPTING` and MAVFTP, separate from the local script console.
- Runtime MAVLink message interval control (`SET_MESSAGE_INTERVAL`) from Flight Data.
- Configurable/persistent QuickView cell count and columns through the upstream-style context menu,
  without permanent layout controls consuming flight-data space. The pre-flight page loads the
  upstream `checklistDefault.xml` schema, evaluates chained CurrentState/parameter conditions,
  retains manual checks and provides a native colour/condition editor with persistent overrides.
- Mission file compatibility for QGC WPL, Mission Planner `.mission`, QGC `.plan`, `.poly`, legacy
  `.fen`/`.ral`, and load-and-append. QGC Plan round-trips mission, polygon/circle fence, the optional
  breach-return point and rally data, with strict coordinate/frame validation and duplicate-safe append.
- Flight Planner imports KML/KMZ LineStrings as missions and Point placemarks as persistent POIs;
  map overlays also accept KMZ and keep upstream-style separate LineString, outer Polygon and
  MultiGeometry routes, point labels, StyleMap line colour/alpha and integer width. Local KML and
  in-memory KMZ `GroundOverlay` images support rotated `LatLonBox` and `gx:LatLonQuad`, bounded
  affine raster warping and path-traversal rejection. As upstream does, a successful KML/KMZ
  overlay import offers to copy its routes/polygons to Flight Data while leaving point labels and
  rasters planner-only. Planner settings, default altitude frame, absolute-altitude write
  confirmation, display-unit conversions and last Flight Data viewport are restored. Portable map
  tools now include place search, arbitrary heading, UTM entry, polygon offset and Tracker Home.
- The official Tracker Home `Obtain From Module` workflow has a portable native replacement for its
  Windows-only Garmin USB/SetupAPI reader. It obtains one checksum-validated NMEA GGA fix from a
  serial device, TCP client/host, UDP listener or GPSD, releases the source on success/cancellation,
  and prefers local GeoTIFF/DTED/SRTM elevation with an explicit GPS mean-sea-level fallback. The
  confirmation identifies the selected modem/vehicle, warns that official Tracker Home state is
  shared globally, and refuses to apply a fix if the selection changes while reading or confirming.
- Planner Settings exposes an opt-in native port of every active command in the official Shortcuts
  plugin: Alt+A AUTO, Alt+G LOITER, Alt+U ALT HOLD, Alt+S STABILIZE, Alt+H RTL, Alt+T TAKEOFF to
  2 m, Alt+L LAND and Alt+0 RC channel 3 minimum. It corrects the upstream `Stabalize` typo, accepts
  the exact Alt modifier only and does not consume shortcuts while a text or terminal input owns
  focus. Enabling the feature and each individual command are reject-by-default confirmations.
  Every command remains bound to the exact modem/system/component and requires fresh telemetry;
  read-only links, target changes and overlapping key repeats are rejected, while takeoff, land and
  minimum-throttle override are additionally blocked when disarmed. The official Alt+F1 handler is
  a commented-out no-op and is therefore not exposed as functionality.
- Flight Planner now ports the active managed SHP/DXF workflows from upstream: point shapefiles load
  as missions with `ELEVATION`/`alt`/geometry-Z precedence and numeric `wp` ordering; shapefiles can
  also replace the drawn polygon or render mixed point/line/polygon overlays. ESRI `.prj` reprojection,
  `.cpg` encodings and case-insensitive Linux sidecars are supported. DXF Line, Polyline, LwPolyline
  and MLine entities render with their source colours in longitude/latitude or a signed UTM zone.
- GeoPackage feature overlays are imported through a managed, cross-platform reader rather than the
  optional native OGR runtime. Standard GeoPackage geometry headers, points, lines, polygons,
  collections, quoted table names and declared coordinate-system reprojection are supported. As in
  official Mission Planner, imported vector geometry appears in both Flight Planner and Flight Data.
  The pinned application's only user-facing OGR call is this GeoPackage import, so the managed reader
  provides that workflow without making an optional native vector library a separate parity gap.
- Flight Planner and Flight Data use the same persisted map provider and update together. Google,
  OpenStreetMap, Esri and Bing have distinct tile sources; the Bing selector no longer silently serves
  Esri imagery.
- The official custom WMS and WMTS providers are available through native asynchronous server and
  layer selectors in Flight Planner, use the compatible `WMSserver`/`WMSLayer` and historical
  `WMSTserver`/`WMSTLayer` settings, and immediately update Flight Data and other shared imagery
  consumers. WMS 1.1/1.3 handles Web Mercator and WGS84 axis order; WMTS capabilities are cached for
  offline startup and restricted to matrix sets whose zoom/addressing matches the shared Web
  Mercator cache. Capabilities downloads are cancellable, capped at 8 MiB and parsed with DTD/entity
  resolution disabled instead of repeating the original synchronous/unbounded XML path.
- Follow Me and Moving Base are available from both Tools and Advanced Tools. Follow Me accepts
  manual or serial NMEA GGA positions and sends confirmed GUIDED targets; Moving Base accepts serial,
  TCP client/host and UDP client/host GGA input, optional confirmed Rally Point 0 updates, raw logging
  and a live map marker. Both workflows bind to the exact modem, system ID and component ID captured
  at start and stop on a target switch; stale/no-fix positions and a busy MAVLink link cannot emit
  Follow Me commands. Valid coordinates on the equator and prime meridian are no longer discarded.
- Open Drone ID ports the official testing plugin into a native Flight Data tab. Its external
  operator GPS accepts serial, TCP client/host and UDP client/host NMEA GGA input, validates the
  checksum/fix and derives the WGS84 altitude from MSL plus geoid separation. The complete Basic ID,
  System, Self ID and Operator ID configuration is sent in the official 2.5-second rotation after an
  `OPEN_DRONE_ID_ARM_STATUS` is detected from the autopilot or any of the three ODID transmitter
  components; fresh operator-position updates use the official 1 Hz path. The tab includes bounded
  raw/status viewers, five-second GPS and transmitter status handling and the same live `BASE` map
  position, plus a visible OK/FAIL/EMERGENCY map control. Starting transmission and declaring an
  emergency are reject-by-default confirmations, and emergency state is deliberately not restored
  after process restart. Every subscription, NMEA transport and outgoing packet is bound to the
  exact selected modem/system/component and stops on target loss or switch. A busy MAVLink owner
  withholds rather than consumes a due message; stale position uses the protocol's zero lat/lon and
  `-1000` unknown geodetic altitude instead of the old plugin's ambiguous zero altitude.
- Developer Tools now ports the official `SerialOutputMD` workflow as MicroDrone Downlink. It emits
  the same `#1` and `#4`-`#9` record families at 10 Hz, with the official baud choices, decimal
  additive/XOR checksum and legacy ECEF conversion. Numeric formatting is culture-independent and
  GPS time uses UTC. Output is bound to the exact active modem, system ID and component ID; changing
  any of them closes the serial output and requires an explicit restart instead of silently sending
  another vehicle's telemetry. Serial errors and window shutdown also cancel and close the sender.
- Planner map tools can prefetch either the visible area or a buffered waypoint path across a selected
  zoom range into the shared offline cache. Auto-WP Text uses the cross-platform vector path backend
  and selected distance/altitude units instead of requiring the Windows-only stick font/GDI path.
- Flight Planner now keeps a bounded undo history across Mission/Fence/Rally edits, home changes,
  map operations, generated patterns and direct grid edits. Ctrl+Z, file open/save and normal/fast
  mission-transfer shortcuts mirror upstream, while the existing KML overlay loader is reachable
  from the map context menu.
- Flight Data restores upstream Ctrl+1…Ctrl+0 action-tab selection and tlog playback/speed keyboard
  controls; the log context action is labelled for its actual convert/extract feature set.
- Flight Data's instrumentation and map columns now have a draggable main splitter. Its clamped
  position is persisted as the upstream-compatible `FlightSplitter` setting, allowing either side
  to be enlarged without collapsing the other into an unusable width.
- Flight Data's HUD and Quick panels can be undocked into separate owned windows like the official
  application. HUD double-click and both panels' context actions move the existing live controls
  rather than copies; closing a window, docking explicitly or leaving the page safely restores each
  panel with its bindings and runtime state intact.
- Flight Data now renders the loaded mission and current waypoint, Home, inclusion/exclusion fence
  polygons and circles, rally points, Guided target, POIs, camera feedback, the live terrain-projected
  gimbal target and a live mission-distance progress strip. Its context menu restores POI
  add/delete/clear, coordinate-based POI creation, Point Camera Coords and the upstream opt-in photo
  overlap-count layer with its exact 1–8 palette, 0.0001-degree lattice and fixed legend.
- The main menu restores upstream auto-hide with a persistent top-edge hover target. Flight Data HUD
  choices now survive restart (icons, Russian layout, ground palette, battery cell count, swap,
  individual indicators and custom fields/prefixes), and the map menu can import/export both legacy
  three-column and altitude-preserving four-column POI files and switch directly to Flight Planner.
- The complete 19-item upstream flight-action selector and Simple Actions tab are present. Command
  implementations match upstream for calibration, safety, engine, scripting, high-latency, ADS-B
  IDENT and system time; flight termination and SD-card formatting add explicit destructive-action
  warnings. Ground-station action output and connection status/progress are now visible, and
  rejected commands are no longer reported as successfully sent.
- Flight Data home and EKF-origin actions use terrain-backed AMSL altitudes with the upstream
  ocean/terrain safety policy. Legacy mount control uses upstream centidegree conversion, preserves
  the final slider position under throttling and resets into MAVLink targeting mode.
- Mount configuration supports both legacy `MNT_*` and current `MNT1_*` parameter schemas, including
  their different angle units, and refreshes from a thread-safe parameter snapshot after loading.
  Stabilization and camera-shutter controls are shown only when the vehicle exposes them.
- Aux Function is a port of the seven upstream `DO_AUX_FUNCTION` presets with Low/Middle/High
  switch levels. It no longer misinterprets that tab as an editor for `RC7_OPTION`…`RC13_OPTION`.
- Fence inclusion/exclusion polygons and circles can be created from the Avalonia planner.
- libVLC startup now resolves versioned Linux `libvlc.so.5`, reports live playback errors, retains
  media for its full native lifetime, and accepts direct MRLs plus common RTP/GStreamer input.
  Announced MAVLink camera streams can be selected and remembered, while the payload page exposes
  MAVLink photo, recording and zoom commands and a video snapshot action.
- Flight Data's HUD menu now separately records the rendered HUD to the upstream-compatible
  timestamped MJPEG/AVI file in the log folder at 25 fps. Start/stop state is explicit, each
  checkpoint leaves a playable partial file, resizing the Flight Data splitter cannot corrupt the
  fixed-size AVI stream, and leaving the page safely closes it. Avalonia capture stays on the UI
  thread while JPEG encoding and disk writes use a bounded background pipeline; when a machine
  cannot keep up, frames are skipped and the wall-clock timeline is preserved instead of building
  an unbounded queue or freezing the interface. This remains distinct from recording the incoming
  libVLC camera stream.
- Tools and Developer Tools now expose the official OSDVideo workflow without its DirectShow,
  GDI+ or Win32 dependencies. A source video is decoded through libVLC while the matching `.tlog`
  is replayed into the upstream `CurrentState` model at 100 ms buckets; the real Avalonia HUD is
  then composited over each visible frame and written as a silent MJPEG AVI. The upstream ±900 s
  synchronization offset, preview-size/default and source-resolution option are preserved. New
  output names never overwrite input or existing files, sensitive export requires explicit
  confirmation, cancellation checkpoints a playable partial AVI, and padded decoder dimensions
  plus libVLC repeat frames are normalized without stretching or lengthening the result.
- The upstream combined `GimbalVideoControl` workflow is ported into the libVLC popup. It overlays
  camera point/rectangle tracking status on the decoded image; supports mouse pan/tilt, Ctrl+click
  ROI and Alt+click/drag object tracking; and restores W/A/S/D slew, Q/E continuous zoom,
  slow/normal/fast modifiers, lock/follow, retract, neutral, point-down, home, photo and camera
  recording actions. Gimbal ID, speeds and FOV policy are configurable and persistent. Active
  camera/gimbal resolution follows vehicle switches, while every held-rate stop packet retains the
  originating link, system, component and gimbal ID so switching UDP modems cannot redirect it.
- Flight Data also restores the official Gimbal Video `Full Sized`, `Mini` and `Pop Out`
  presentations. One live video/control surface moves between the map and owned window without
  restarting the stream or losing gimbal input state. Full Sized moves the current live map into
  the upstream-style optional 30% mini-map, Mini restores the full map with video in the corner,
  and the video context menu provides show-map, swap, presentation and close actions. Closing the
  window, changing the view model or leaving Flight Data restores the map and releases video input.
- Speech event announcements are functional: mode and waypoint changes speak through the upstream
  `CurrentState` hooks, and a cross-platform announcer covers arm/disarm, battery, custom, low
  altitude and low speed alerts with upstream-compatible templates and thresholds.
- Audio Vario is available from Planner Settings and consumes the same `climbrate` telemetry and
  tone/cadence formulas as upstream. A cancellable single-loop controller replaces the unsafe
  `async void` lifecycle, while an in-memory PCM/WAV backend uses the packaged libVLC audio output
  on Linux, macOS and Windows without `Console.Beep`; shutdown and device-failure state are bounded
  and reflected in the UI.
- Basic, Advanced and Custom DisplayView profiles now use the upstream JSON/XML contract, including
  migration from legacy `advancedview` and early Avalonia enum-caption settings. Profile changes are
  applied live to the main Simulation/Help navigation, Setup/Config backstage pages, Flight Data
  tabs and editing locks, and Flight Planner fence/rally/menu controls. User-customized hidden tabs
  remain a separate preference, so a profile switch cannot accidentally hide a tab permanently.
- DataFlash Log Browse now evaluates upstream graph expressions instead of relying on
  `DataTable.Compute`: indexed fields, mixed-message chronological state, `wrap_360`, `degrees`,
  `atan2`, `lowpass`, `delta` and the upstream DFLogScript vector helpers are supported. Every
  alternative in `mavgraphs.xml` is parsed and the best compatible one is selected; MODE overlays
  preserve decoded names and use the upstream firmware-specific numeric-mode resolver as fallback.
- Password protection of the Setup and Config screens is enforced: enabling the option prompts for
  a password (stored as the upstream salted hash) and both screens require it once per session.
- The `ShowNoFly` planner option now auto-loads every KML/KMZ from the `NoFly/` folder in the
  user data directory on both operational maps. The official Hong Kong CAD eSUA GeoJSON feed is
  available through a separate explicit network opt-in and uses the same translucent-blue fill and
  purple outline as pinned Mission Planner. The port does not make upstream's hidden Cloudflare IP
  geolocation request. Downloads are cancellable and size/feature/coordinate bounded; Polygon,
  MultiPolygon and interior rings are validated before an atomic 12-hour XDG-cache replacement,
  with a previously valid stale cache retained across network failures. The live feed check on
  2026-08-23 returned 284 features and 71,220 coordinate tuples.
- The serial link and telemetry logs are closed on application exit, so the tlog tail is no longer
  lost when the window is closed while connected.
- The inherited global `MissionPlanner.Utilities.Settings` store is safe under concurrent UI and
  background-service access. Singleton initialization and file writes are serialized, key
  enumeration uses concurrent snapshots, and assigning `null` retains the upstream reset semantics
  by removing the setting. This fixes the intermittent collection-corruption failures reproduced
  by post-merge CI without modifying the pinned upstream submodule.
- Write Fast performs the upstream pipelined `MISSION_ITEM_INT` upload with
  MISSION_REQUEST/MISSION_ACK resynchronization instead of duplicating the normal write.
- Geo-referencing writes GPS EXIF into geotagged image copies through the upstream
  `GeoRefImageBase`/ExifLibrary path, in addition to location.txt and location.kml. CAM, TRIG and
  EXIF-time/GPS-offset matching modes are available, including automatic offset estimation,
  configurable shutter lag, AMSL/GPS altitude selection and an explicit metre-based base-altitude
  adjustment; TRIG deliberately requires equal photo/event counts so a missing capture cannot
  silently shift every subsequent coordinate. The portable static `location.kml` report replaces
  the upstream loopback network link, which depended on the intentionally removed embedded server.

Existing port functionality includes serial/TCP/UDP/UDP-client/WebSocket connections, flight data,
mission planning, parameter pages, firmware/log tools, simulation launcher, NMEA/mirroring tools,
maps/KML, DroneCAN over MAVLink, direct SLCAN or pydronecan multicast, joystick mapping on all three
release targets, and libVLC video. These code paths compile; hardware-specific paths still need
native-platform acceptance testing.

## Linux verification details

- Distribution SDK: `/usr/bin/dotnet` 10.0.111.
- `global.json`: 10.0.100 with `latestFeature`, so the distribution SDK is accepted.
- Release build: succeeds with `-m:1`.
- Automated tests: 1129 passed, 0 failed, including NV4/NV5 custom-dialect parsing, typed parameter
  encoding, multi-link isolation, protected key writes, RTSP/preset staging and silent-device timeout;
  legacy Mission Planner plugin binary loading,
  lifecycle/host/mission-list ABI, BLE endpoint parsing, native ABI layout,
  platform-backend selection and HID descriptor decoding for signed and unsigned
  axes, Flight Simulation controls, dual sliders, buttons, hats and D-pads; signed beta manifest
  discovery, HTTPS-only bundle download, Ed25519/SHA-256 tamper rejection, extraction and atomic
  rollback; Settings concurrency/null-reset stress,
  WMS/WMTS capabilities and tile addressing, raw/Socket.IO WebSocket protocol, fragmentation,
  reconnect and bounded-close integration, real
  loopback-UDP Moving Base input, blocking serial cancellation, exact multi-modem target isolation
  and reject-by-default command/rally/Open-Drone-ID starts. Open Drone ID tests additionally cover
  every wire payload, fixed ASCII fields, WGS84 conversion, protocol unknowns, official scheduling,
  ARM-status gating, busy-link withholding, all four component subscriptions and target-switch
  shutdown, plus the native tab/map integration. Live HUD/Quick undock and close-to-redock behavior
  is also covered.
  Tracker Home tests exercise real loopback TCP client/host, UDP and GPSD input, cancellation and
  port release, local-terrain/GPS-altitude selection, coordinate validation, official shared-state
  semantics and the native official-style submenu/dialog.
  Flight-command-shortcut tests cover all eight upstream actions, exact modifier matching, the
  upstream Alt+F1 no-op, opt-in UI, corrected STABILIZE mode and exact-target execution. They also
  verify confirmation rejection and fail-closed handling for stale telemetry, read-only links,
  target switches and disarmed takeoff/land/minimum-throttle requests.
  Map-cache tests cover the official row/column order, path/range rejection, real JPEG/PNG decoding,
  duplicate handling, replacement in BruTile's persistent cache and the bound Avalonia controls.
  SHP-to-POLY tests cover official multi-feature naming/layout, closed rings, WGS84 reprojection,
  locale-independent coordinates, atomic replacement and the visible Developer Tools action.
  Barometer-altitude tests cover the exact upstream conversion, range/finite checks, pressure
  bounds, zero-offset handling and reject-by-default link/MAV target identity guards.
  MAVLink serial-bridge tests use a real loopback TCP socket for bidirectional and multi-chunk byte
  transfer, then verify bounded explicit stop, target-loss shutdown, UART release between clients,
  exact link/vehicle/component identity and single-system enforcement.
  Video tests also validate the complete bundled macOS libVLC layout and require native ARM64 CI
  to load VLC 3.0.23 and execute the real MJPEG decode/callback/export path.
  Firmware-archive tests cover mirror fallback, exact-URL deduplication, bounded parallelism,
  HTTPS-first legacy handling, partial availability, hashes, XML/path hardening, size limits,
  non-overwrite behavior, cancellation cleanup, atomic publication and strictly ordered progress
  reporting under parallel downloads.
  Hong Kong NoFly tests cover Polygon/MultiPolygon parsing, holes, WGS84 validation, the official
  alpha/color style, fresh/stale caches, network failure, cancellation cleanup, atomic publication,
  concurrent-load serialization and layer replacement on both Flight Planner and Flight Data.
  Parameter-recovery tests cover exact link/system/component/MAVState identity, the official
  ENABLE-first and `_ID` reset order, explicit cancellation, target loss and rejected values. The
  `temp.cs` registry test proves that all 67 pinned click handlers have exactly one closed status.
  Native-GDAL tests cover platform library discovery, raster intersection and alpha compositing;
  the installed GDAL 3.8.4 runtime also opens, warps and renders a generated EPSG:3857 GeoTIFF.
- Clean self-contained `linux-x64` publish: 173 MB including the pinned airport database.
- Headless Xvfb startup: reaches the normal application event loop; the packaged Ctrl+X action opens
  the bound Map Tile Cache import UI and Ctrl+F shows the native SHP-to-POLY, Adjust Barometer
  Altitude, MAVLink Serial TCP Bridge and Download Firmware Archive actions while the title reports the exact
  upstream/date/commit version. The packaged serial-bridge window was opened from that action and
  its safe defaults were visually verified.
- The production multicast transport simultaneously joined CAN1 and CAN2 on a real active IPv4
  interface and released both reused UDP 57732 sockets cleanly.
- The `.deb` target is rebuilt from the current 1129-test source on 2026-08-23. Package metadata,
  launcher, desktop entry, icon, man page, native dependencies and required checklist/parameter/log
  resources were verified; every packaged-file checksum matches after extraction, including the
  portable plugin API, HIDSharp/BLE dependency licenses, the SimpleBLE and VLC source/license
  notices and byte-for-byte pinned 8,443,722-byte `airports.csv`.
- `lintian --fail-on error,warning` passes without diagnostics. The extracted x86-64 ELF apphost
  reaches the normal event loop under Xvfb and has no unresolved direct library dependencies.
  Complete and checkpoint-only HUD AVI samples are recognized as 25 fps MJPEG by `ffprobe`.
  The bundled optional .NET LTTng trace provider targets the older `liblttng-ust.so.0` ABI; it is
  not loaded during normal startup and EventPipe diagnostics are unaffected. Target-aware filtering
  removes the upstream Windows-native SimpleBLE/libusb binaries from non-Windows packages while
  retaining them in `win-x64`.
- The smoke routes downloaded parameter/log/SRTM data and application settings to isolated XDG
  roots; the extracted application tree remains byte-for-byte unchanged. A portable test plugin
  and its private managed dependency were loaded from the isolated user directory and completed
  `Init`, `Loaded` and `Loop`; repeated failures in a second plugin disabled only that loop.
- System runtime integrations installed: libVLC, speech-dispatcher-espeak-ng, BlueZ and serial
  `dialout` membership, plus GDAL 3.8.4 for optional local raster maps. The real Bluetooth adapter
  completed three consecutive managed D-Bus LE scans; no Nordic UART modem was in range for a
  traffic test.

Release package names and application metadata embed the upstream Mission Planner version, UTC
build date and short source commit. Debian versions additionally use epoch 1 and the repository
revision count, preserving upgrade ordering from the earlier CalVer packages and between same-day
builds. Exact artifact paths, sizes and SHA-256 values are reported with each build instead of being
pinned here, because a documentation-only commit would immediately make such a record stale. The
apphost is an x86-64 ELF PIE, native libraries are ELF `.so` files and the `.dll` files are managed
assemblies.

## Runtime path behavior

The port now separates configuration, persistent user data, downloaded caches and state files using
XDG on Linux, AppData on Windows and Library folders on macOS. SRTM, SITL, parameter definitions,
log metadata and update downloads never belong in the installation directory. Legacy files are
copied to the appropriate new location without deleting the source. A build-only compatibility
override fixes upstream `Settings.GetDataDirectory()`, which otherwise resolves to unwritable
`/usr/share/Mission Planner` under modern CoreCLR Linux.

Package-managed Linux installs contain a marker and launch-time environment flag. The in-app updater
is disabled for those installs and directs the user to APT; portable Linux, Windows and macOS builds
retain their existing signed update flow.
The Beta Updates preference is operational rather than decorative: startup/manual checks discover
the latest GitHub prerelease carrying platform-specific signed manifest assets, verify the same
Ed25519 trust key as stable, then verify and atomically install its SHA-256-pinned full bundle. Help
also exposes a one-shot beta check. Stable tags continue to deploy the loose-file GitHub Pages feed;
`-beta`/`-beta.N` tags publish prerelease assets without replacing that stable site.

## Managed DLL versus platform-native libraries

Managed .NET assemblies normally use the `.dll` suffix on Windows, macOS and Linux. They contain
portable ECMA-335 IL and are not evidence of an unfinished platform port. Native files remain
platform-specific: PE DLLs on Windows, dylibs/Mach-O on macOS, and ELF `.so` files on Linux.

The executable project, resolved NuGet graph and Linux runtime output were also audited for
WinForms: they contain no `System.Windows.Forms.dll`, `MissionPlanner.Controls.dll`,
`UseWindowsForms` setting or direct `System.Windows.Forms` source reference. Portable upstream
libraries retain a UI callback contract; it is now handled by Avalonia rather than by the original
WinForms host.

Earlier non-Windows publishes also contained three genuinely Windows-native PE DLLs copied by upstream:
`simpleble-c.dll`, `simpleble.dll`, and `libusb-1.0.dll`. They were packaging pollution, not managed
assemblies. Target-aware publish filtering now removes them from Linux and macOS. File-type inspection
confirms that every remaining Linux `.dll` is managed and every `.so` is ELF. The `win-x64` build
retains the upstream SimpleBLE DLLs required by its native BLE backend; macOS receives architecture-
matched official dylibs instead.

## Remaining cross-platform parity and release work

The current solution/project audit leaves no concrete portable upstream user workflow to adapt from
source. The permanent binary UI-compatibility boundary is described below. The handler-level audit
of the hidden developer form is complete and enforced against the pinned upstream source by a test.
Sample/test plugins and workflows already ported natively are not counted as gaps. Historical
online-service integrations and deliberately retired/unsafe paths are listed separately below.
NativeAOT is tracked separately as an optional runtime experiment and is not counted as a Mission
Planner functional-parity gap.

| Area | Affected targets | Current state and direction |
| --- | --- | --- |
| Legacy WinForms plugin UI compatibility | All | Portable DLL discovery, dependency loading, `Init`/`Loaded`/`Loop`/`Exit`, enable/disable UI, current/all-link MAVLink access, settings/device events, mission-list calls, Flight Data actions and HUD overlays are operational. A version-tolerant `MissionPlanner.dll` shim now runs old non-visual .NET Framework binaries without rebuilding. Direct binary compatibility for `System.Windows.Forms`, Windows GMap controls and the original visual `MainV2` surface is not possible in Avalonia; those UI calls must be adapted from source. Loose `.cs` runtime compilation is intentionally not treated as DLL compatibility. |

## Optional runtime experiment

| Area | Current state |
| --- | --- |
| NativeAOT runtime | Linux links to a 66 MB ELF but fails in log4net `Assembly.GetCallingAssembly()`; MAVLink/XML/fastJSON also require dynamic code. Supported releases use self-contained CoreCLR, so NativeAOT is not counted as an upstream feature gap. |

## Intentionally disabled or replaced

| Area | Decision |
| --- | --- |
| Embedded Mission Planner HTTP/KML/MJPEG server | Not compiled on any target. Do not restore the older server implementation; any replacement needs the current authentication and anti-DoS model. GeoRef emits a portable static `location.kml` instead of the old loopback network link. |
| TFR download/overlay | Current Mission Planner disabled its Jepptech-backed background download on 2020-09-30 when the service ended (`b26095f9a`) and later removed the parser/Flight Data handler. The port retains the compatible `showtfr` preference but does not invent a replacement feed and incorrectly present it as upstream parity. |
| European dynamic no-fly feed | The pinned Mission Planner `eunfz.cs` implementation has an empty download URL and is not operational upstream. [EASA currently directs operators](https://www.easa.europa.eu/en/light/topics/geo-zones-know-where-fly-your-drone) to each national aviation authority's geographical-zone source, so the port does not invent a unified feed and present it as official parity. |
| Support Proxy | Not ported on any target until authentication, explicit consent and networking are designed and reviewed. |
| Original WinForms/Windows driver-install UI | Not part of the Avalonia UI. Use native OS driver handling and add board-specific cross-platform DFU implementations where required. |
| Legacy CLI firmware/log paths and AC3.3-era terminal flows | Obsolete for supported firmware and intentionally not exposed. |
| DroneCAN file browser | The official control is unreachable from the current Mission Planner UI and remains half-stubbed. Node parameters and firmware upload are ported; a general browser will be reconsidered when upstream exposes a complete operational workflow. |
| X-Plane/FlightGear legacy HIL and Ateryx-specific pages | Superseded by SITL and not restored without a current user and maintenance case. |
| IronPython script host | The portable local console uses MoonSharp Lua. Old Python scripts would require a large legacy runtime and are not enabled by default. |
| Historical third-party service plugins | AltitudeAngel, DigitalSky, AirMarket and similar integrations require current API, authentication and privacy review before any port. |
| DirectShow device enumeration | Replaced by libVLC video input so the feature can share one API across Windows, macOS and Linux. |
| GDI+ HUD renderer switch | Not applicable to Avalonia: the port uses the cross-platform Skia renderer on every target, so the inherited GDI+ option is shown disabled. |
| Usage analytics | This port has no analytics sender; the Config page states that analytics are disabled rather than presenting a preference with no runtime consumer. |
| Windows SimpleBLE/libusb DLLs in non-Windows packages | Explicitly filtered from Linux and macOS; Windows retains its native BLE runtime and macOS receives matching official dylibs. |
| NativeAOT as release format | Disabled by default on every target. Supported releases are self-contained CoreCLR builds. |
| Developer crash/flight commands | The upstream developer form's autopilot lockup and automatic arm/takeoff shortcuts remain intentionally absent. The normal Flight Data action selector's upstream flight-termination command is ported, but only behind an explicit irreversible-action warning. |

## Native-platform acceptance still required

A Flysky FS-i6XCN was used for a Linux joydev smoke: the port-native reader opened it through
`/dev/input/by-id`, reported 16 buttons, returned live raw axis/button state and shut down cleanly.
Its advertised `-127..127` HID axes only reached about `-90..90` physically, producing roughly
`7431..58105` instead of the full unsigned range; the new per-device range calibration specifically
covers this mismatch and its endpoint/clamping/monotonicity behaviour is unit-tested.
The 20 Hz MAVLink and built-in-SITL packet paths are unit-tested; a two-instance Linux Copter swarm
has also been started and connected over two real MAVLink TCP links with distinct sysids. Hands-on
joystick auto-detect/mapping and RC output to a live vehicle or SITL still need acceptance.
The macOS IOKit backend is descriptor-tested, cross-publishes for x64 and arm64, and is exercised by
a native ARM64 CI enumeration smoke; controller discovery, live axes/buttons and unplug behavior
still need acceptance with representative macOS USB and Bluetooth hardware. The same native CI
loads the bundled Apple-Silicon VLC 3.0.23 runtime and decodes/exports a real MJPEG pipeline; visual
playback, capture devices and network video sources still need hands-on application acceptance.
The Nordic UART BLE stream is unit-tested and all platform artifacts contain the intended backend.
Linux BlueZ scans completed against a real adapter, and native macOS CI loads SimpleBLE and
enumerates adapters. End-to-end scan, connect, MAVLink traffic, physical loss and reconnect still
need acceptance with representative Nordic UART modems on Linux, macOS and Windows.
Camera-footprint projection is
unit-tested, but a live `CAMERA_FEEDBACK` source is still required for acceptance. No USB flight
controller, CAN adapter, camera or live vehicle was attached during this verification. Each release
target still needs acceptance tests with its native serial/USB permissions, video/audio stack and
representative hardware. The port-native PX4Flow frame path is unit-tested but still needs a live
sensor acceptance run. ArduPlane Formation quaternion/PID generation and exact-link packet routing
are unit-tested, but the experimental controller still requires ArduPlane SITL and live-aircraft
acceptance before operational use. Full ArduPilot SITL mission execution testing is still pending;
macOS SITL currently has no prebuilt launcher binary in this port.

## Security dependency overrides

The root build overrides vulnerable versions inherited from the upstream project without modifying
the submodule: log4net 3.3.2, SharpCompress 0.48.0 and SkiaSharp/SkiaSharp native assets 2.88.6.
The SSH port uses SSH.NET 2026.0.0 and BouncyCastle.Cryptography 2.7.0 instead of upstream's
vulnerable SSH.NET 2020.0.2 dependency. Linux.Bluetooth's vulnerable Tmds.DBus 0.20.0 declaration
is explicitly overridden with the compatible CVE-2026-39959-fixed 0.92.0 release. A current
`dotnet list package --vulnerable
--include-transitive` audit reports no vulnerable package in the Avalonia application graph.
The seven CodeQL findings reviewed in this synchronization and their code-level mitigations or
reachability decisions are recorded in [`CODEQL_TRIAGE.md`](CODEQL_TRIAGE.md). Two unused-web-sample
alerts closed through precise analysis scoping; the other five remain visible and no alert was
dismissed merely to make the dashboard green.
