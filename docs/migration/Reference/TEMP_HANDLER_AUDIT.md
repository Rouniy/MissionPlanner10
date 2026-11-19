# Official `temp.cs` handler audit

This registry covers every click handler in the official Mission Planner developer form at pinned
upstream commit `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`. It prevents experimental developer buttons from
being mistaken for ordinary end-user parity gaps and makes a newly added upstream handler visible
to the test suite.

Status meanings:

- `ported` — the workflow has a native Avalonia entry point.
- `replaced` — the operator outcome is available through a different cross-platform or safer
  workflow.
- `obsolete` — the handler is empty, commented out, a one-off benchmark, or superseded by normal
  supported workflows.
- `unsafe` — the developer-only action intentionally crashes or commands an aircraft and is not an
  acceptable application feature.
- `platform-specific` — the action is Windows operating-system maintenance, not Mission Planner
  flight functionality.

| Handler | Status | Avalonia equivalent or decision |
| --- | --- | --- |
| `BUT_geinjection_Click` | `replaced` | Map Cache imports the official zoom/row/column JPG or PNG directory layout. |
| `BUT_clearcustommaps_Click` | `ported` | Map Cache clears the selected custom provider cache. |
| `BUT_lang_edit_Click` | `ported` | Developer Tools > Translation / RESX Editor. |
| `BUT_georefimage_Click` | `ported` | Flight Data > Geo-reference Images. |
| `BUT_follow_me_Click` | `ported` | Tools > Swarm Follow Me. |
| `BUT_paramgen_Click` | `ported` | Setup > Advanced > Regenerate Parameter Metadata. |
| `myButton1_Click` | `ported` | Developer Tools > MicroDrone Downlink. |
| `but_osdvideo_Click` | `ported` | Developer Tools > OSD Video — Telemetry Overlay. |
| `BUT_swarm_Click` | `ported` | Tools > Swarm Formation. |
| `BUT_outputMavlink_Click` | `ported` | Tools > MAVLink Mirror / serial pass-through output. |
| `BUT_outputnmea_Click` | `ported` | Tools > NMEA Output. |
| `BUT_followleader_Click` | `ported` | Tools > Swarm Follow Path, matching the handler's actual `FollowPathControl` body. |
| `BUT_sorttlogs_Click` | `replaced` | Log Organizer handles telemetry and DataFlash log types together. |
| `BUT_movingbase_Click` | `ported` | Tools > Moving Base. |
| `but_getfw_Click` | `replaced` | Cancellable, atomic Developer Tools > Download Firmware Archive. |
| `button3_Click` | `ported` | Setup > Warning Manager. |
| `but_mavserialport_Click` | `replaced` | Target-safe Developer Tools > MAVLink Serial TCP Bridge. |
| `BUT_magfit2_Click` | `ported` | Developer Tools > Offline Magnetometer Calibration (MagFit). |
| `BUT_shptopoly_Click` | `replaced` | Managed Developer Tools > Convert Shapefile to POLY. |
| `but_gimbaltest_Click` | `replaced` | Flight Data map/video gimbal pointing and camera controls provide the live workflow. |
| `but_maplogs_Click` | `replaced` | Flight Log Index builds map thumbnails while indexing logs. |
| `butlogindex_Click` | `ported` | Developer Tools and Log Browser > Flight Log Index. |
| `but_structtest_Click` | `obsolete` | Internal serialization micro-benchmark with no operator workflow. |
| `but_armandtakeoff_Click` | `unsafe` | Unconditional developer arm/takeoff shortcut intentionally excluded. |
| `but_sitl_comb_Click` | `replaced` | Connection List and native multi-instance SITL avoid global SYSID rewrites and port scanning. |
| `but_injectgps_Click` | `ported` | Setup > GPS Inject. |
| `BUT_fft_Click` | `ported` | Tools > FFT Analysis. |
| `but_reboot_Click` | `ported` | Confirmed, disarmed Developer Tools > Reboot Vehicle. |
| `BUT_QNH_Click` | `ported` | Developer Tools > Set QNH. |
| `but_trimble_Click` | `ported` | Tools > Swarm Sequence. |
| `myButton_vlc_Click` | `replaced` | Flight Data custom libVLC video source. |
| `but_agemapdata_Click` | `ported` | Map Cache removes cached map data older than 30 days. |
| `but_signkey_Click` | `ported` | MAVLink Signing Key management. |
| `but_optflowcalib_Click` | `ported` | PX4Flow live frame/calibration page. |
| `but_gpsinj_Click` | `ported` | Developer Tools > Extract GPS Corrections. |
| `but_followswarm_Click` | `ported` | Tools > Swarm Waypoint Leader. |
| `myButton3_Click` | `ported` | Same upstream action as GDAL; covered by Elevation & Local Raster Sources. |
| `but_GDAL_Click` | `ported` | Managed GeoTIFF/DTED elevation plus optional native GDAL raster-map scanning and overlays. |
| `but_sortlogs_Click` | `ported` | Developer Tools > Organize Log Directory. |
| `but_logdlscp_Click` | `replaced` | Verified-host-key SFTP DataFlash download workflow. |
| `but_td_Click` | `ported` | Developer Tools > Download MAVFTP File. |
| `but_dem_Click` | `ported` | Elevation Sources file grid and coverage view. |
| `but_gsttest_Click` | `obsolete` | Handler body contains only commented experimental code. |
| `but_proximity_Click` | `ported` | Tools > Proximity Radar. |
| `but_dashware_Click` | `ported` | Developer Tools > Create DashWare CSV. |
| `but_mavinspector_Click` | `ported` | Tools > MAVLink Inspector. |
| `BUT_driverclean_Click` | `platform-specific` | Windows driver uninstall UI is delegated to native OS driver management. |
| `but_blupdate_Click` | `replaced` | Confirmed, disarmed Developer Tools > Upgrade Bootloader. |
| `but_3dmap_Click` | `ported` | Developer Tools > 3D Terrain View. |
| `but_anonlog_Click` | `ported` | Setup > Advanced anonymized log export with a privacy warning. |
| `but_messageinterval_Click` | `ported` | Flight Data MAVLink message-interval controls. |
| `BUT_xplane_Click` | `obsolete` | Empty upstream handler; current simulation support is SITL. |
| `but_disablearmswitch_Click` | `ported` | Flight Data safety-switch action. |
| `but_hwids_Click` | `ported` | Developer Tools > Decode Hardware ID. |
| `but_packetbytes_Click` | `ported` | Developer Tools > Decode MAVLink Packet. |
| `but_acbarohight_Click` | `replaced` | Exact-target Developer Tools > Adjust Barometer Altitude. |
| `But_stayoutest_Click` | `obsolete` | One-off mission/fence/rally protocol diagnostic; supported planner workflows cover those stores. |
| `but_lockup_Click` | `unsafe` | Deliberate autopilot lockup command intentionally excluded. |
| `but_hexmavlink_Click` | `ported` | The MAVLink packet decoder accepts compact hexadecimal input. |
| `but_remotedflogger_Click` | `replaced` | Explicit Start/Stop Remote DataFlash Log lifecycle. |
| `but_paramrestore_Click` | `replaced` | Cancellable recovery is bound to one exact modem/MAVState/system/component and stops on target loss. |
| `BUT_CoT_Click` | `ported` | Tools > Cursor-on-Target / TAK Output. |
| `but_ManageCMDList_Click` | `ported` | Setup > Mission Command List. |
| `but_signfw_Click` | `ported` | Developer Tools > Embed Defaults in APJ. |
| `but_dfumode_Click` | `replaced` | Confirmed, disarmed Developer Tools > Reboot to DFU. |
| `BUT_forcecal_accel_Click` | `replaced` | Confirmed recovery-only Developer Tools > Force Accel Calibrated. |
| `BUT_forcecal_mag_Click` | `replaced` | Confirmed recovery-only Developer Tools > Force Compass Calibrated. |

The pinned source contains 67 click handlers and every one appears exactly once above. There are no
open handlers in this registry. Broader format, release-channel and native-platform gaps remain
tracked in [`PORT_STATUS.md`](PORT_STATUS.md).
