# UI, mission and vehicle API

Use `tools/list` for exact parameter names and JSON schemas. UI, mission transfer and
vehicle write tools require session Allow (automatic for sessions launched from the AI
window). Passive tools (`mission_draft_get`, `mission_command_schema`,
`mission_draft_validate`, `vehicle_modes`, `terrain_elevation` and the diagnostics)
also work on self-connected read-only sessions. Unknown future tools require Allow.

## UI operations

| Tool | Purpose and limits |
| --- | --- |
| `ui_get_state` | Active screen, available routes, open windows, Setup/Config page lists, surface IDs, log view IDs/revisions, tuning fields, draft revision, connection state. |
| `ui_navigate` | DATA, PLAN, SETUP, CONFIG, SIMULATION or HELP through the native navigation command. SETUP/CONFIG may show the operator password dialog first; `completed:false` plus a note means a dialog or hidden screen. |
| `ui_select_page` | Select a Setup/Config backstage page by header (from `ui_get_state`); switches the screen if needed. Pages that need a vehicle are hidden until connected. |
| `ui_inspect` | Every visible control in every open window (buttons, checkboxes, radios, toggles, text and number fields, combos, sliders, tabs, lists, expanders, date pickers) plus logical menu items, with kind, name, label, value, options, actions, enabled/visible and window-relative bounds. Optional `windowId`, substring `filter`, `includeStatic` for labels/status text. Up to 2000 controls per call. |
| `ui_invoke` | Click a button or menu item, toggle a checkbox/toggle, select a tab or list item, expand/collapse an expander. Dispatched through the control's native click/selection, so commands, Click handlers and dialogs run as for the operator. |
| `ui_set_value` | String for text boxes, number for numeric fields/sliders (clamped to the control's range), boolean for toggles/expanders, option index or text for combos/tabs/lists, ISO date for date pickers. Commit buttons still need `ui_invoke`. |
| `ui_close_window` | Close a secondary window or dialog by `windowId`; never the main window. |
| `ui_capture` | PNG of `window:<windowId>` (e.g. `window:main`) or a map/plot surface, width 64..1600, height 64..1200, <=2 MiB. |
| `ui_set_map_view` | Surface ID, latitude -85..85, longitude -180..180, integer zoom 3..20; disables auto-pan. |
| `ui_set_tuning_fields` | 1..12 distinct CurrentState field names and enabled flag; current UI aircraft, 30 s graph. |
| `ui_open_log` / `ui_plot_log` / `ui_close_log` | Catalogue-backed native Log Browser windows and graphs; see limits below. |
| `ui_operation_status` | Connection-local receipt for a mutation ID. |

Control IDs are derived from the window, control type, name and layout path, so they stay
the same across inspections while the layout is unchanged. A snapshot remains valid for
several actions; it is invalidated by revoke/allow (access epoch). A control whose window
closed, that became hidden/disabled, or that sits in a window disabled by a modal dialog
returns `stale_control`/`unavailable_control`: inspect again and act on the dialog first.
The AI window (consent controls) and password boxes are never listed. Template parts such
as spinner arrows are skipped.

## Mission draft and transfer

| Tool | Purpose and limits |
| --- | --- |
| `mission_draft_get` | Page 1..200 items at nonnegative offset, with content revision, home, units and Undo availability. |
| `mission_command_schema` | Known native command IDs/names, available labels and frames; does not establish aircraft support. |
| `mission_draft_validate` | Pure structural validation, <=1000 items; finite values, coordinates, frames, command IDs and DO_JUMP references. |
| `mission_draft_replace` | Compare current revision then replace Mission items/home as one native Undo group. |
| `mission_draft_undo` | Compare revision then undo one planner history entry. May undo an operator edit; inspect first. |
| `mission_elevation_profile` | Terrain, planned altitude and clearance in metres every 100 m along the Mission draft (straight legs, linear altitude), plus minimum clearance and missing-tile count. |
| `terrain_elevation` | Terrain metres AMSL for 1..500 points from the configured elevation source; `invalid` while tiles are missing/downloading. |
| `mission_upload` | Write the draft (`missionType` Mission/Fence/Rally) to the vehicle through the planner's native path (MAVFTP or mission protocol). GLOBAL frames need `acceptAbsoluteAltitude`; altitudes below the planner's Alt Warn need `ignoreLowAltitude`. Replaces the onboard list. |
| `mission_download` | Read the vehicle's list into the draft (replacing it; Undo available). |

Example: `mission_draft_get` all pages at one revision → build items → `mission_draft_validate`
→ `mission_elevation_profile` (check clearance) → `mission_draft_replace` with `expectedRevision`
→ `ui_capture window:main` to look → `mission_upload` → `mission_download` to confirm.

Item properties: `command`, `frame`, `latitude`, `longitude`, `altitudeMetres`, `p1`..`p4`.
Home: `latitude`, `longitude`, `altitudeMetres` (AMSL). Frames: 0 absolute AMSL, 3 relative to
home, 10 terrain-relative. Coordinates are WGS84 degrees. The first array element is item 1.

## Vehicle configuration and commands

| Tool | Purpose and limits |
| --- | --- |
| `read_parameters` / `refresh_parameters` | Paged typed values with metadata (units, range, values/bitmask, rebootRequired). |
| `write_parameters` | 1..100 `{name, value, expected?, reason?}` with a batch `reason`. Each write re-reads the value, compares it to `expected` (or the cache), checks type/range/read-only metadata, writes under the shared parameter gate and verifies the typed acknowledgement. Stops at the first failure; no rollback. Disarmed vehicle required unless `allowArmed:true`. Writes a JSON review, a `-before.param` snapshot and a `-result.txt` audit under the state directory. Returns `rebootRequired` names. |
| `propose_parameter_changes` / `parameter_proposals` | Operator-reviewed batch alternative; the operator applies it from the AI window. |
| `vehicle_modes` | Modes for the target's firmware plus current mode/armed state. |
| `vehicle_command` | `set_mode{mode}`, `arm{force?}`, `disarm{force?}`, `takeoff{altitudeMetres}` (sets GUIDED first), `guided_goto{latitude,longitude,altitudeMetres,frame?}`, `rtl`, `land`, `loiter`, `mission_start`, `change_speed{speedType?,speedMetresPerSecond,throttlePercent?}`, `set_servo{channel,pwm}`, `set_relay{relay,state}`, `motor_test{motor,throttlePercent,seconds?,motorCount?}` (disarmed), `calibrate{kind}` (gyro, baro, airspeed, level, accel_simple, compass_start/accept/cancel; disarmed), `save_parameters`, `reboot` (disarmed), `mavlink_command{commandId,p1..p7,useCommandInt?}`. `accepted` is the MAVLink acknowledgement or send; the result also reports mode/armed before and after. |

Interactive Setup flows (six-side accelerometer calibration, radio calibration, ESC
calibration, motor order tests, FFT setup) are driven through their Setup pages with
`ui_select_page`, `ui_inspect`, `ui_invoke` and `ui_set_value`, reading the page's status
text with `includeStatic`.

## Logs and graphs

`ui_plot_log` replaces an MCP log view graph with 1..8 original scalar fields, explicit time
window and `expectedRevision`. Up to 32768 points per field; instance must identify one
sensor or TLOG system:component. Plot times are boot/sample seconds for DataFlash, elapsed
receipt seconds for TLOG. DataFlash physical multipliers are applied; TLOG values retain
native MAVLink units. Narrow the window if the point limit is hit. Map tiles and plots render
asynchronously; capture again if a capture looks partial.

## State, cancellation and recovery

Tools with an `operationId` parameter reserve a receipt **before** dispatch.
`write_parameters` and `vehicle_command` do not accept this parameter and have no
journal/replay protection. After an uncertain response, read back parameters, telemetry
and vehicle messages; do not blindly repeat a vehicle command.

For journaled tools, identical method/typed arguments and operationId return the same receipt, including `running`. Reusing an ID with different
arguments produces `operation_conflict`. A connection holds at most 256 receipts, without
eviction. At capacity, inspect uncertain outcomes before starting a new connection.

Statuses: `running`, `completed`, `rejected`, `cancelled`, `outcome_unknown`;
`ui_operation_status` additionally returns `unknown_operation`. Rejected/cancelled/native
failure does not promise rollback: inspect state before forming a new operation.
Receipts survive revoke/allow on the same session, not disconnect or application restart.
Never blindly replay a mutation on a new session after losing its original connection.

UI mutations are serialized across both HTTP listeners. Concurrent mutation returns
`ui_busy`; no hidden queue of later actions. Revocation or Stop all cancels pending
operations and checks access again before dispatching pending UI changes. An action already
dispatched may complete; cancellation cannot undo native UI changes, parameter writes or
uploads. Closing the AI window does not stop anything; Stop all connections does.
