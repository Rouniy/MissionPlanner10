# Dowding plugin audit

Updated: **2026-08-24**.

## Classification

`plugins/Dowding` was an experimental, deployment-specific WinForms plugin. It was not part of the
official Mission Planner application or release build and is not a functional-parity requirement
for the Avalonia application. After this audit closed its manifest rows, the cleanup branch removed
the dormant plugin, generated REST clients, Node test server and plugin-only ONVIF dependency; Git
history and this decision record preserve the upstream implementation for reference.

This conclusion is based on the frozen native baseline `67a3c4f22bd1b38ac499f9756902e04fa4ed8444`:

- `MissionPlanner.csproj` excludes `plugins/**` from compilation.
- `MissionPlanner.sln` gives the Dowding project GUID
  `{FB690197-2695-489C-A47C-439FF3AEF7A8}` Debug/Release `ActiveCfg` entries but no `Build.0`
  entry, so a normal solution build does not compile it.
- Dowding feature work ended at `ea6090fa5` on 2021-06-24; the last later UI change (`2e282c1cf`,
  2024-03-19) only followed a shared TCP transport setting change. There is no default service
  endpoint: operation requires an independently supplied server, account or bearer token.

The three C# rows remain in the frozen inventory for traceability, but are classified as a deliberate
retirement rather than being represented as an unported official feature. The separate
`cleanup/project-audit` branch completed the physical deletion after the final reference and package
graph checks proved that none of these components belonged to the active Avalonia product.

## Function review

The plugin combined four unrelated workflows:

| Legacy workflow | Avalonia decision |
| --- | --- |
| Generic antenna-tracker serial control | Ported as the native Antenna Tracker parameter, serial and live pages, with all three upstream output protocols and lifecycle tests. |
| Official Cursor-on-Target output | Ported as the native Cursor-on-Target / TAK window with multicast, UDP client/host, TCP client/host and serial output. The plugin-specific inbound contact cache belongs to the retired proprietary integration. |
| Display of every connected MAVLink system | Ported in the Flight Data Mapsui map and connection selector without an 8-bit SysID/CompID filter beyond the MAVLink protocol fields themselves. |
| Proprietary Dowding REST/WebSocket feed and its dedicated ONVIF/secondary-tracker forwarding | Retired. It was not built or shipped by the official solution, has no usable default deployment and would require a new authenticated service contract and threat model rather than a mechanical UI port. |

## Native defects not carried forward

The review found several concrete bugs and unsafe assumptions in the dormant upstream plugin:

- tracker and CoT TCP listeners bind `IPAddress.Any` without authentication;
- the WebSocket close callback blocks a callback thread for 60 seconds and then reconnects without
  cancellation or a bounded retry policy;
- the CoT TCP accept path schedules `DoAcceptTcpClientCallback` (the antenna-tracker callback)
  instead of `DoAcceptCoTTcpClientCallback` after the first client;
- the CoT serial branch reads `CMB_serialport.Text` instead of `cmb_cotport.Text`;
- `GLOBAL_POSITION_INT.relative_alt` subtracts a home altitude in metres from an altitude in
  millimetres;
- ONVIF yaw editing can dereference an uninitialized device, while update calls are fire-and-forget
  and can overlap indefinitely;
- password encryption uses a repository-wide static key/IV modified by the first MAC address, and
  the bearer token is stored directly in settings.

Recreating those paths would expose new network, credential and physical pointing-control surface.
Any future Dowding-compatible integration must therefore be a separately specified optional plugin
using the portable plugin API, cancellable networking, OS credential storage and explicit bind/access
controls.
