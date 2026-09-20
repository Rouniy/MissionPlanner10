# MCP comparison with X-Office — 2026-09-20

Compared Mission Planner baseline `c15c1948227fe06362d08fdbd4b0f0ee14bb2b92`
with X-Office `b534036d80d7baae86ca1917376f3fbadaef63eb`. X-Office has unrelated
user-owned CRM edits; no reference files were changed and no agent/model was launched.
This is a source and local protocol review, not aircraft acceptance.

## Architecture and differences

| Area | Mission Planner | X-Office | Assessment |
| --- | --- | --- | --- |
| Ownership | Application-wide `McpAgentHub`; hiding the window keeps sessions alive | `McpController` owns listeners independently of its panel | Same useful lifetime model; keep it |
| Clients | Interactive CLI with private one-use handoff; desktop registration; stdio-to-HTTP bridge | Interactive CLI; desktop registration; socket/HTTP transports and stdio bridge | Existing MP client support already covers the intended workflow |
| Consent | Launch grants access; self-connected desktop sessions start read-only; Allow/Revoke/Disconnect | Registration also grants automatic access; Revoke suppresses it until explicit reactivation | Different product policy, not a transport defect; retained MP's documented policy |
| Concurrency | Four request slots, 16 TCP connections; shared UI mutation gate | Reserves capacity for control requests while native work is busy | Fixed MP DELETE starvation without increasing tool concurrency |
| Startup | Async server published before bind completes | Synchronous bind before port is reported open | Fixed MP repeat-open readiness and serialized listener creation |
| UI | Dedicated agent window with scrolling content | Embedded assistant panel | MP access controls now remain outside the scroll area |
| Recovery | 256 connection-local UI/mission receipts; vehicle commands have no receipts | Listener-owned document journals survive reconnect; fresh context required for edits | Corrected MP instructions; reconnect-safe vehicle receipts require a separate target/operation design |
| Application → agent tasks | Initial terminal prompt and tools for agents to pull current application state | Bounded assistant request queue, claim/answer/release, server notifications over SSE/socket | Useful future feature once a concrete MP action (e.g. Analyze selected log) defines payload and answer handling |
| Idle sessions | SDK idle timeout (2 h), 64-session cap | Configurable idle cleanup and busy/last-activity display | Possible future UX improvement; no evidence that changing MP timeout now benefits active log analysis |
| Domain tools | Aircraft telemetry/parameters/commands, mission drafts/transfers, terrain, BIN/TLOG analysis, GUI | Documents, database commands and queries, GUI | Reuse lifecycle principles, not office domain APIs |

Reference implementation: X-Office `src/frontend-qt/McpController.cpp`,
`McpHttpTransport.cpp`, `docs/MCP.md`, `docs/work-items/mcp-sessions-task-f.md`,
and `docs/adr/0051-connection-consent-and-full-document-mcp.md`.
Mission Planner entry points: `Services/Mcp/McpAgentHub.cs`,
`MissionPlannerMcpServer.cs`, `McpConnectionSession.cs`, `McpOperationJournal.cs`,
`McpTerminalLaunch.cs`, `McpStdioBridge.cs`, `Views/AgentToolsWindow.cs`.

## Fixes and evidence

- A second open call returned a server with `Endpoint == null`. Tests hold the
  server startup gate, call each hub open method and require it to await readiness.
  Both tests failed on the baseline. Listener creation/detachment now uses a short
  lock, while all callers await the server's existing async startup gate.
- Four blocked HTTP tools caused `DELETE /mcp` to return 429. A real loopback test
  reproduced this; authenticated DELETE now bypasses only the tool-request semaphore.
  Host/Origin, session/credential checks and TCP connection limits still apply.
- The existing minimum-window-size test exposed an inaccessible Stop button.
  Session controls are now fixed beneath the scrollable content.
- AI_START/UI_API incorrectly promised receipts for every mutation. Their scope now
  follows actual schemas: `write_parameters` and `vehicle_command` need readback
  after uncertain responses and must not be blindly replayed.

See the current entry in `Porting/STATUS.md` for final validation and Git state.
No new dependency, model-provider configuration, account login, flight action or
client registration is needed for these fixes.
