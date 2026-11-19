# AI agents over MCP

Mission Planner embeds a Streamable HTTP MCP server in the existing Avalonia application.
Open **AI** in the navigation bar, or **AI diagnostics / MCP…** in the tools menu.
The server is off until requested. No separate server installation is needed.

In the connection model the window scans installed agents when it opens and
offers **Launch** per agent. A terminal agent receives a free loopback port and a one-use
bearer token and is fully allowed as soon as it connects. A desktop application is
registered as an MCP client if needed, the fixed port (default 47183) opens without a
token, sessions on it are allowed, and the application is activated. The listeners belong
to an application-wide hub, so the window can be closed at any time while the agent keeps
working; **Stop all connections** or application exit ends every session. While a session
holds a grant the **AI** navigation button is green and brightens briefly on every MCP
request/response.

Full access means the agent can do what the operator can: switch screens and Setup/Config
pages, click any visible control or menu item, type values, build/validate/upload
missions with terrain profiles, read and write parameters, send flight-mode, arming,
guided, calibration and generic MAVLink commands, download and analyze logs, and open
native log graphs. This is intended for autonomous tuning workflows such as choosing
harmonic-notch and gyro/accel filter settings from raw IMU spectra or preparing/replacing an
AUTOTUNE with evidence from logs.

## Use

The AI window is one assistant page: launch buttons, sessions,
the persistent port, an **Advanced** section and the activity log. It contains no log,
parameter or mission selection of its own: an agent reaches logs, parameters, missions and
every screen through the application itself, using whatever is loaded or connected.

1. Open the window with the **AI** button. Discovery runs on opening; **Find agents**
   refreshes it. Supported clients are Codex CLI, Claude Code, Codex/ChatGPT Desktop,
   Claude Desktop and LM Studio. Every installed agent has its own **Launch <agent>**
   button; agents that are not installed have no button. A nonstandard CLI installation
   is launched from **Advanced → Executable… / Launch custom**; its kind follows the
   executable name (`codex*` or `claude*`, native `.exe` on Windows), so a codex binary is
   never started with Claude flags (Codex rejected `--mcp-config` and the terminal closed).
2. **Launch Codex CLI** / **Launch Claude Code** open an interactive CLI in an external
   terminal, using the working directory from **Advanced** and the initial task shown in
   the window. The default task only asks the agent to verify the MCP connection and to
   summarise how many tools are available and what they cover; it does not analyse logs or
   change anything until asked. The agent's existing login, model and normal client
   settings remain in use. Codex receives process-only MCP `-c` overrides under a unique
   session name; Claude Code receives a private MCP JSON through
   `--mcp-config`/`--strict-mcp-config`. No permission-bypass flag is added and neither
   launcher edits global client configuration. Each launch has a private one-use handoff
   and a single-use bearer credential bound to its MCP session. The Codex override that
   disables the persistent desktop entry is added only when that entry exists in
   `config.toml`; an override on a missing table made Codex exit with "invalid transport"
   and the terminal closed immediately. A failed agent start keeps the terminal open with
   the exit code until Enter is pressed.
3. **Register <desktop application>** writes the MCP entry into the application's own
   configuration (Codex/ChatGPT Desktop `~/.codex/config.toml`, Claude Desktop
   `claude_desktop_config.json`, LM Studio `mcp.json`) and switches on **Open at startup**;
   the same button reads **Unregister <application>** once the entry exists. Desktop
   applications read MCP settings when they start, so restart them after registering, and
   they see Mission Planner only while the persistent port is open. **Launch <desktop
   application>** registers if needed, opens the port, allows existing and subsequent
   sessions on it until **Revoke all** or **Close port**, and activates the application.
4. **Open port** independently opens the persistent endpoint (default **47183**,
   configurable). **Open at startup** (set by Register and Launch, clearable here) opens it
   every time Mission Planner starts so a registered desktop application always finds the
   server. Clients connecting themselves initially get read-only diagnostic access.
   The session list shows client-reported names and access state; **Allow**, **Revoke**
   and **Disconnect** affect the selected session. **Allow all** affects currently connected
   clients. **Revoke all** cancels current grants and pending launch credentials without
   closing the listeners.
5. **Advanced → Open session port → Copy connection settings** supplies a temporary URL and
   bearer token for a manually configured client. Such a client waits for **Allow**.
   Configure a 720-second tool timeout for onboard downloads.
6. Read the agent's output in its terminal/application; the **Activity** log records
   launches, ports and grants. Allowed sessions apply parameter changes directly through
   `write_parameters` (audited, verified); agents may still submit parameter proposals for
   batches the operator prefers to review, which are listed under **Advanced** with
   **Review / apply selected** and **Export selected…**.
7. **Advanced → Attach flight log…** registers a DataFlash `.bin`/`.log` or telemetry
   `.tlog` with MCP without an aircraft; agents also discover the configured log directory
   themselves. Offline actions never open a listener.

**Close port** closes just the persistent listener. **Stop all connections** revokes both
listeners before waiting for any request shutdown, including a stuck request. Active grants
are cancelled and late tool results are suppressed. Closing the AI window hides it only.
External terminals/apps remain open; permanent registration is retained. Only the persistent
port reopens at application startup, and only while **Open at startup** is set; offline log
actions never open a listener. Listener teardown runs on a worker thread so application exit
stays immediate with open ports and live sessions (it previously waited for a 5 s cap).

The main **AI** button shows a robot whose eyes change shape (circles, vertical bars,
horizontal bars) on every MCP request or response; the button is green while a session
holds a grant and brightens briefly around each exchange.

An example task:

> Analyze the connected vehicle and the attached flight log. Identify firmware, frame,
> payload assumptions and flight-time parameters. Inspect flight segments for vibration,
> clipping, gyro resonance, rate tracking, PID terms, actuator saturation and EKF health.
> Report missing evidence. Propose only changes supported by this flight, with a staged
> validation flight and criteria for keeping or reverting each change.

All endpoints bind only `http://127.0.0.1:<port>/mcp`. Provider login and model billing
belong to the selected client. A tokenless persistent listener accepts local processes;
client-reported names are not authentication. Launch/Allow grants full control of the
application and the connected vehicle. Passive diagnostics are available in read-only
sessions. Revocation removes both passive and elevated access from that session;
reinitialization cannot restore it. A new independent connection to an open persistent
port starts read-only after **Revoke all**. Close the port to prevent all new access.

The transport deliberately uses stateful MCP (negotiated `2025-11-25`) so individual
clients can be listed and disconnected. Session IDs and credentials are checked together;
there are bounded session/request counts, idle expiry, Host/Origin validation and no
background GET/SSE stream. Requests still use standard POST Streamable HTTP responses.
No LAN listener, cloud connector, public tunnel or firewall rule is created.

## Permanent desktop registration

The **Connections** tab is always available, including when no agent is installed.
Linux discovery checks XDG desktop entries and launcher executables; macOS checks
application bundles; Windows queries StartApps, including Store/MSIX installations.
CLI URL handlers are not desktop installations. Discovery never installs software.

**Register selected desktop** can prepare configuration separately without opening a
port. **Remove registration** closes the persistent listener and removes only MP's
owned entry. Configuration locations and transports are:

| Client | Configuration | Transport |
| --- | --- | --- |
| Codex / ChatGPT Desktop | `$CODEX_HOME/config.toml` or `~/.codex/config.toml` | Direct HTTP; shared Codex configuration |
| Claude Desktop | `Claude/claude_desktop_config.json` in the platform application-config directory | `MissionPlanner10 --mcp-stdio --port 47183` bridges local stdio to HTTP |
| LM Studio | `~/.lmstudio/mcp.json` | Direct HTTP |

The stdio bridge does not start a GUI or open a listener: the operator must open the
port in the running Mission Planner. Closing it terminates access even if the client
keeps its bridge process. Moving a portable application requires registering Claude
Desktop again so its executable path is updated.

Registration owns only `missionplanner10_desktop`: a marked TOML section or marked
JSON entry. It preserves other settings, validates the existing document, creates a
unique backup and replaces the file atomically. Repeated registration is idempotent;
changing the port updates the owned entry. Invalid documents, unmanaged collisions,
symlink configuration files and detected concurrent edits fail without replacement.
TOML text/comments are preserved; JSON formatting may be normalized. The sidecar lock
coordinates MP instances, not unrelated editors. Port conflicts fail explicitly with
no silent fallback. Only one Mission Planner instance can own a given port.

Validation uses the official MCP client,
real local HTTP requests and fake CLI processes; it does not invoke paid models or
change the developer's client registrations. Codex CLI arguments were checked against
0.154.0; Claude support is implemented without invoking Claude during development.

## UI, mission and vehicle API

The server now exposes **55 tools**: 29 diagnostics, 22 UI/draft/mission tools and 4
vehicle tools (`write_parameters`, `vehicle_modes`, `vehicle_command`, `terrain_elevation`).
MP adopts connection-local receipts and snapshots while routing actions through explicit
native adapters. UI receipts remain scoped to a connection, and this reconnect limitation
is documented explicitly.

Agents should read [AI_START](../docs/mcp/AI_START.md) and [UI_API](../docs/mcp/UI_API.md).
The same versioned text is embedded in each binary and exposed through `resources/list`
and `resources/read` at `missionplanner://documentation/AI_START.md`, `UI_API.md` and
`DIAGNOSTICS.md`. Initialize instructions point to the starting resource. Unknown URIs
cannot read files. `tools/list` remains the authoritative machine-readable schema.

Capabilities: navigation to every screen and Setup/Config page; generic inspection of all
visible controls and menu items in every open window with native click/toggle/select and
typed value entry; whole-window and map/plot PNG captures; closing dialogs; map
centering/zoom; live tuning field selection; catalogue-backed native log views and graphs;
Mission draft validation/replacement/Undo with revision checks; terrain elevation and a
mission elevation profile; Mission/Fence/Rally upload and download through the planner's
native transfer; direct verified parameter writes; flight-mode, arming, takeoff, guided,
RTL/land/loiter, speed, servo/relay, motor-test, calibration, storage, reboot and generic
MAV_CMD commands.

UI/vehicle tools require the connection's Allow (automatic for launched sessions). Every
mutation uses operationId for replay recovery; graph/draft edits also check revisions.
Stop/revoke cancels pending work before subsequent UI dispatch. Completed changes are not
rolled back. Consent controls in the AI window, password boxes and desktop-global input are
never exposed. See UI_API for limits and recovery.

## Available tools

| Tools | Data / behavior |
| --- | --- |
| `diagnostics_info`, `list_vehicles` | Workflow, capabilities, firmware, system/component and connection-bound target IDs |
| `telemetry_schema`, `read_telemetry` | Discover and read scalar CurrentState fields, display units and packet freshness |
| `vehicle_health` | Raw HEARTBEAT, SYS_STATUS, GPS, VIBRATION and EKF with separate receipt ages; fixed physical units, missing/unknown values preserved |
| `read_vehicle_messages` | Last 256 exact-component STATUSTEXT packets, severity, original chunk IDs, sequence cursor and dropped-history indicators |
| `telemetry_packet_inventory` | Received packet types and individual receipt ages; no payloads, stream-rate changes or inferred packet-loss numbers |
| `read_parameters`, `refresh_parameters` | Paginated typed parameters, completeness and metadata; refresh requires disarmed aircraft |
| `read_mission_draft` | Mission Planner's UI mission, explicitly distinguished from onboard mission |
| `list_onboard_logs`, `download_onboard_log` | MAVLink log directory and cancellable disarmed log download |
| `open_log_analyzer` | Open a catalogued BIN/LOG/TLOG in the graphical Log Browser, with field graphs and record tables |
| `list_local_logs`, `log_schema`, `read_log_records` | Opaque log handles, all decoded message fields/units, time/instance filters and lossless pagination |
| `log_overview` | Full-log message counts, time bounds and sources; DataFlash boot time or TLOG elapsed receipt time |
| `log_events` | DataFlash flight-event records and TLOG state changes, text/chunks and command acknowledgments, with original evidence and pagination |
| `log_time_series` | Bounded per-source trends: all-sample means, extrema with times, invalid counts and observed gaps; no interpolation |
| `compare_log_parameters`, `compare_vehicle_parameters_to_log` | Recorded-to-recorded and recorded-to-current parameter differences, explicit source/time, missing/unknown/type-change distinctions |
| `log_parameters_at` | Paginated last-known PARM/PARAM_VALUE values at a log time, with source evidence; never uses future or live values |
| `log_vibration_report` | DataFlash VIBE per IMU or TLOG VIBRATION per source: means/maxima, threshold sample counts, clipping increments and resets |
| `log_field_statistics` | Streaming mean, RMS, deviation, extrema, first/last and times for each message/instance/field |
| `log_spectrum` | One-sided Welch PSD of a regularly sampled scalar field |
| `log_batch_spectrum` | Raw ISBH/ISBD IMU batch PSD with actual sample rate, scaling and sequence validation |
| `log_response` | Target/actual RMS error and cross-correlation lag in the same message/instance |
| `propose_parameter_changes`, `parameter_proposals` | Evidence-backed changes and operator review/application status |
| `write_parameters` | Direct verified writes (1..100), before-snapshot and audit files, disarmed unless `allowArmed`, rebootRequired report |
| `vehicle_modes`, `vehicle_command` | Firmware mode list; set_mode/arm/disarm/takeoff/guided_goto/rtl/land/loiter/mission_start/change_speed/set_servo/set_relay/motor_test/calibrate/save_parameters/reboot/mavlink_command |
| `terrain_elevation`, `mission_elevation_profile` | Configured elevation source lookups and a 100 m sampled terrain/planned/clearance profile of the Mission draft |
| `mission_upload`, `mission_download` | Planner-native Mission/Fence/Rally transfer with explicit absolute-altitude and low-altitude acknowledgements |
| `ui_navigate`, `ui_select_page`, `ui_inspect`, `ui_invoke`, `ui_set_value`, `ui_close_window`, `ui_capture` | Full native UI operation of every window |

PIDR/PIDP/PIDY, RATE, VIBE, IMU, ESC, RCOU, XKF/NKF, PARM, MSG and other available
messages are discoverable through the log schema. The interface is not restricted to a
fixed list of tuning parameters. Sensor instances remain separate, and `PID*.I` is the
integral term, not an instance number.

The server exposes **29 tools** directly backed by Mission Planner's MAVLink connections,
parameter metadata, mission draft, native DataFlash parser and MAVLink telemetry reader.
For a DataFlash investigation (TLOG differences are described below):

1. Obtain a handle with `list_local_logs`; call `log_overview` and `log_schema`.
2. Call `log_events` to find mode/arm/error events and choose a flight segment in seconds since boot.
3. Call `log_parameters_at(logId, atSeconds)` at the segment start. Use PARM records to
   inspect changes during the segment; absent parameters remain unknown.
4. Call `log_vibration_report(logId, startSeconds, endSeconds)`. Modern `VIBE[IMU].Clip`
   and legacy `Clip0/1/2` are both supported. Counts above thresholds are sample counts,
   not durations. Clipping increments exclude the first observed value and reset/wrap
   intervals; they are lower bounds, not lifetime totals. Missing fields are omitted.
5. Use `log_batch_spectrum` or `log_spectrum` for raw IMU resonance and `log_response`
   for rate tracking. Use `log_field_statistics` and records for battery, ESC, actuator
   and estimator evidence. On a connected aircraft, compare `vehicle_health` and
   `read_parameters` before submitting a proposal.

The vibration thresholds follow [ArduPilot's measurement guidance](https://ardupilot.org/copter/docs/common-measuring-vibration.html).
They are context for investigation, not an automatic flight-safety classification.
Timestamp reversals within parameter history or an IMU stream are rejected by these
summaries; separate logs from different boots before quantitative analysis.

HTTP uses the official SDK's stateless **Streamable HTTP** transport at `/mcp`:
POST requests (token-authenticated for CLI, explicitly unauthenticated for desktop) can receive SSE responses, notifications receive HTTP 202,
and standalone GET streams return HTTP 405. No transport session ID or resumable event
history is advertised. This is distinct from the obsolete separate `/sse` + `/messages`
transport; clients should use the displayed `/mcp` URL. Wire behavior is tested against
the [MCP transport specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
and with the official C# MCP client, including unsupported-version rejection.

## Download, open and analyze a flight log

The operator workflow lives in the application, not in the AI window: the DATA screen's
**DataFlash Logs** and **Telemetry Logs** tabs download onboard logs and open local files,
and the Log Browser graphs message fields, shows record tables, inspects messages and
parameters and exports data. The aircraft must remain disarmed during an onboard download;
**Stop all connections** cancels an agent-initiated transfer. Agent downloads are saved in
the configured log directory's `agent-downloads` folder and registered with MCP
automatically; **Advanced → Attach flight log…** registers existing `.bin`, `.log` and
`.tlog` files.

An agent can perform the same sequence with `list_onboard_logs`, `download_onboard_log`,
`log_overview`, `log_schema`, `read_log_records` and `open_log_analyzer`. The last tool
opens the actual Mission Planner viewer, not a separate server-side application.

[DataFlash logs are recorded onboard](https://ardupilot.org/planner/docs/common-downloading-and-analyzing-data-logs-in-mission-planner.html),
whereas [TLOGs are normally recorded by the ground station](https://ardupilot.org/planner/docs/mission-planner-telemetry-logs.html).
The onboard MAVLink log protocol downloads BIN; it does not turn those files into TLOG.
Use a completed local TLOG (disconnect/finish recording first if the active writer locks it).

For TLOG, `log_overview`/`log_schema` enumerate MAVLink message/source keys such as
`ATTITUDE[1:1]` and their wire units/descriptions. `read_log_records` accepts these keys
or a message name with `instance="1:1"`. Pagination counts decoded packets. Records include
UTC receipt timestamps; time-window arguments mean seconds from the first receipt, **not**
seconds since vehicle boot. `log_field_statistics` keeps each system/component separate.
`log_parameters_at` decodes PARAM_VALUE using preceding source-specific heartbeat/capability
evidence and returns null for unknown integer encoding. `log_vibration_report` processes
VIBRATION envelopes and clipping counters per source. Binary payload fields remain arrays.

The graphical TLOG viewer also separates types by system/component and supports scalar
graphs, text/array record tables, STATUSTEXT, source-selected parameter history and a map
track (first GLOBAL_POSITION_INT source, named in the summary). Values use MAVLink wire
units; graph scale/offset controls can convert them. DataFlash expression presets and raw
IMU FFT/response tools continue to require BIN/LOG: packet receipt intervals do not establish
the sensor's true sampling rate. The TLOG reader checks CRC and supports signed MAVLink 2,
but does not authenticate signatures offline. Corrupt/truncated/unsupported-dialect records
produce explicit errors rather than silently dropping evidence.
Receipt-clock reversals are also rejected so a negative elapsed time cannot silently
exclude packets; split such recordings before time-window analysis. Generic field
statistics retain protocol sentinel values (for example an unknown battery value);
consult the field descriptions returned by `log_schema` before interpreting them.

## Interpretation and write behavior

- Establish frame, firmware, payload and flight conditions first. Current parameters may
  differ from flight-time PARM records. Mechanical vibration, clipping, estimator problems
  or actuator limits can invalidate a PID-only diagnosis.
- DataFlash log times are seconds since boot; TLOG uses elapsed receipt time. CurrentState values use configured display units and
  are a best-effort snapshot; last-packet time does not establish each field's freshness.
- Record values are decoded DataFlash values before optional FMTU display multipliers.
  Schema reports these multipliers; numerical tools apply them once. Unknown multipliers
  remain explicitly unknown. Inspect units before comparing fields.
- PSD removes each window's mean, uses a Hann window with 50% overlap and returns field
  units squared/Hz. Scalar FFT refuses gaps, nonmonotonic timestamps, mixed instances or
  more than 32,768 samples. Use a shorter interval instead of decimating. Raw batch FFT
  reads one complete batch; missing/out-of-order data is rejected. A low-rate VIBE envelope
  is not raw gyro data suitable for identifying a notch frequency.
- Correlation lag is an observation, not causal radio/control latency, a settling-time
  measurement or an automatic PID design. Weak/constant signals and a search-boundary
  peak produce no lag estimate. Inspect angular wrap, modes, excitation and saturation.
- `write_parameters` and proposals validate expected values, finite numbers, MAVLink type
  limits, read-only metadata and available parameter ranges. Writes verify connection
  generation, fresh telemetry and (unless `allowArmed`) a fresh disarmed heartbeat read
  directly from the packet cache, plus writability. Each parameter is read again under the
  shared write gate, compared to its expected value, written and checked against its typed
  acknowledgement. State and cancellation are rechecked before sending/retrying a write.
- A JSON review, nonsecret `-before.param` snapshot and incremental `-result.txt` audit are
  saved under the application's state directory in `agent-parameter-audit`. Partial failure
  stops the batch. A lost acknowledgement may mean the value was applied; read it again.
  There is no automatic rollback, reboot or claim that a batch is atomic.
- Arbitrary filesystem access, code execution and credential APIs are not exposed. Live
  parameters with KEY/PASS/SECRET/TOKEN in their names are omitted. Vehicle commands are
  serialized and re-resolve the target immediately before sending; motor tests, calibration
  triggers and reboots require a disarmed vehicle.
  Attached flight logs are shared as data, including their messages and PARM history;
  do not attach logs containing information you do not want the chosen agent to receive.

## Additional diagnostic context

These tools address six gaps in the original 23-tool surface: text warnings, telemetry availability,
flight-event selection, compact trends, comparisons between flights and configuration drift since a flight.
They are read-only and available through both CLI and desktop connections without new UI controls.

- **Why does arming fail?** Call `read_vehicle_messages(targetId)` and `vehicle_health`.
  Message severity follows [MAVLink STATUSTEXT](https://mavlink.io/en/messages/common.html#STATUSTEXT):
  zero is most severe, seven includes debug. Continue with `afterSequence=nextSequence`.
  `historyTruncated` means older packets have been evicted; `missedSinceCursor` means a polling
  client has fallen behind. The native cache retains 256 packets per component independently of
  the UI message queue. Receipts may predate MCP startup and include MAVLink 2 fragments; IDs and
  chunk sequence numbers are retained instead of claiming a complete reconstructed message.
- **Is a telemetry value current?** `telemetry_packet_inventory` lists actual received packet
  types and receipt ages. Its five-second freshness marker describes receipt only. An absent
  packet is unknown; the inventory neither measures stream rates nor requests them.
- **What happened during this flight?** `log_events` returns DataFlash MSG/MODE/EV/ERR/ARM/FAIL
  evidence, or TLOG first-observed/changed HEARTBEAT and landed states, STATUSTEXT and COMMAND_ACK.
  Firmware-specific DataFlash codes remain raw. A first observed state is not evidence that a
  transition occurred then. Pagination reconstructs earlier TLOG state before filtering, so
  unchanged heartbeats do not turn into artificial events on the next page. Untimed DataFlash
  messages retain null time and are included only for windows starting at zero. Text is data,
  never agent instructions. See the [ArduPilot log-analysis workflow](https://ardupilot.org/copter/docs/common-downloading-and-analyzing-data-logs-in-mission-planner.html).
- **Where are the short peaks or gaps?** `log_time_series(logId, type, field, startSeconds,
  endSeconds, bins)` scans all samples into at most 512 equal-duration bins per source (up to
  16 sources; use `instance` for more). It returns counts, mean, first/last and min/max with
  occurrence times. Empty bins are omitted, invalid values are counted and maximum observed gap
  is null when fewer than two timestamped records exist. Means are sample-weighted. DataFlash
  uses the native parser's scaling once; TLOG keeps MAVLink wire units and sentinel values.
  These summaries are for trends, never FFT or response-lag calculations.
- **What changed between flights or since this flight?** `compare_log_parameters` accepts two
  log handles and two times, including two times in one log. `compare_vehicle_parameters_to_log`
  compares a historical snapshot against one target's current cache and reports cache completeness
  and capture time. Neither tool infers that a log belongs to the target, stages proposals or
  writes parameters. TLOG comparisons require explicit `systemId:componentId` sources from
  `log_schema`; DataFlash source arguments must be omitted. Each log uses its own time origin.
  Values are last recorded at or before each requested time, with line/time evidence. Exact
  comparisons distinguish changed, unchanged, typeChanged, onlyBefore, onlyAfter and unknown;
  missing means unrecorded, not deleted. Unknown TLOG encodings stay null. Secret parameter names
  are omitted. Results are paginated to 200 entries; snapshots are bounded to 16,384 names.

Parameter reads, log download/analysis, vibration/PSD/response analysis and operator-reviewed
parameter proposals were already present. Flight commands, calibration triggers, mission upload
and direct parameter writes were added with the full-access model; firmware flashing and
arbitrary file/code access remain outside MCP (firmware pages are reachable through UI tools).

## Limits and validation

This is a complete local analysis/proposal/review workflow, not an autonomous flight-tuning
certification. Representative aircraft, simulator and flight acceptance remain necessary.
No model invocation or physical-vehicle write was performed during automated validation.
Tests use the official MCP client against real loopback Kestrel, synthetic DataFlash logs,
known signals, injected MAVLink exchanges and Avalonia layout at the minimum window size.
See [STATUS.md](STATUS.md) for exact results and packaging limitations.

The DataFlash parser is the existing managed DFLogBuffer. Initial indexing is synchronous on a
worker thread and cannot be interrupted mid-constructor; very large logs can delay shutdown.
One indexed DataFlash reader is cached per server; TLOG analysis scans packets with cancellation
and bounded result pages rather than loading the whole recording into memory. TLOG graph/map
materialization is capped at two million points and schema at 4,096 message/source combinations.
Discovery is bounded to 500 files/five directory levels; explicit attachments can select
other logs, up to 1,000 handles per session. HTTP requests have size/concurrency limits.
Existing transport busy guards and parameter/log gates prevent overlapping supported
operations, but this is not a repository-wide transaction system for every legacy plugin.

The C# MCP SDK is pinned to 2.2.0. ASP.NET Core is included in self-contained distributions;
a framework-dependent developer run also needs the ASP.NET Core 10 runtime.
