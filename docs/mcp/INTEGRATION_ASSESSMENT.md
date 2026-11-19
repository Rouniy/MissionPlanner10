# Embedded MCP and external agent feasibility

Assessed 2026-09-16 against product commit
`512d9f7104ccf6e211a698eaa4cf31af78b6a64d` (audit HEAD before this document:
`37b6876add2aa6852e069407a1b2171bcd904292`). This is a design assessment, not an
implemented or runtime-validated feature at that checkpoint. The subsequent implementation
and operating instructions are in [DIAGNOSTICS.md](DIAGNOSTICS.md); current validation
is recorded in [STATUS.md](../STATUS.md).

## Finding

An embedded Streamable HTTP MCP endpoint and a launcher for local external agents fit
the existing Avalonia/net10 application without a second product or executable server.
Transport integration is relatively small; a reliable application operation boundary,
concurrent access, agent lifecycle and cross-platform delivery account for most work.

The assumed agent is a locally installed CLI using the same host network as Mission
Planner. An agent running in a container, remote machine or hosted service cannot use
the desktop's loopback address directly. That deployment needs a separately designed
network route and authentication policy. Model-provider login remains the agent's
responsibility and is distinct from access to Mission Planner's MCP endpoint.

## Repository evidence

- `MissionPlanner.csproj`: one `MissionPlanner` assembly, net10, explicit source
  includes and four release RIDs. No existing MCP or ASP.NET Core host was found in
  the examined main-project package graph.
- `App.axaml.cs`: explicit startup and desktop-exit cleanup hooks. Add a service whose
  asynchronous lifetime does not own or block the Avalonia event loop. Revoke agent
  access and stop accepting MCP work before disposing UI/vehicle services; avoid
  waiting on work that requires a stopped UI dispatcher.
- `AppState.cs` and `Services/MavLinkConnectionManager.cs`: active link and snapshots
  of multiple connections already exist. `AppState.comPort` follows selection, so a
  tool must bind an explicit connection/system/component and connection generation,
  rather than repeatedly resolving the current UI selection during an operation.
  A connection-list snapshot is not an atomic snapshot of all telemetry fields.
- `Services/LocalKmlServer.cs`: an existing bounded loopback server demonstrates local
  hosting and snapshot use, but its custom GET/HEAD TCP implementation is not a
  suitable MCP transport. Keep its existing behavior and host MCP with Kestrel.
- `Plugin/PortableApi/PluginHost.cs` and `Services/PluginService.cs`: useful UI and
  telemetry integration precedents. The plugin API exposes mutable MAVLink objects
  and is not itself a suitable remote tool contract.
- `ViewModels/FlightPlannerViewModel.cs`: mission reads/writes, dialogs, progress and
  selected-link access are interleaved. Directly exposing its private UI commands
  would not provide reliable structured results or concurrency control.
- `Services/VehicleParameterLoadCoordinator.cs`: cancellation and serialization exist
  for parameter reads, but this is not a universal mission/parameter/command scheduler.
- `Services/SitlLauncher.cs`: process launch, redirected output and lifetime tracking
  are established patterns. A new agent launcher should use `ArgumentList`, dedicated
  child environment values and bounded output buffers rather than shell interpolation.
- Linux/Windows/macOS packaging already publishes self-contained. ASP.NET Core adds
  runtime payload; its actual size and package compatibility must be measured.

## Proposed complete workflow

1. On explicit agent launch, start an in-process Kestrel host on
   `127.0.0.1:0`, discover the assigned port, and expose `/mcp`.
2. Create a revocable per-agent credential and server-enforced access scope. Validate
   Host/Origin, bound requests and concurrency, and never use MCP session IDs as
   authentication. Avoid broad CORS. This local credential arrangement is not a claim
   to implement the full MCP OAuth authorization profile for arbitrary remote clients.
3. A provider adapter constructs the CLI's actual MCP configuration, child environment
   and initial task. Merely mentioning a URL in a prompt does not register MCP tools.
4. Start the agent, stream its progress/results into an Agents panel, and expose stop
   and restart. Stop revokes the credential and cancels owned work, with bounded child
   process cleanup. Existing agent authentication is reused without copying secrets.
5. Typed tools call an application facade. Telemetry returns bounded snapshots with
   explicit SI units, altitude frames, target identity and observation age. UI-backed
   reads/edits marshal through the dispatcher; blocking vehicle I/O runs elsewhere.
6. Initial tools can list vehicles, read telemetry and cached parameters, inspect the
   mission draft and return analysis/recommendations. Distinguish unavailable/stale
   parameters from an empty vehicle configuration. This is an independently useful
   complete read/analysis feature, without requiring later write tools to work.

For writes, extract shared operations used by both UI and MCP. A semaphore used only
by MCP would not prevent conflicts with the operator. Bind each proposal to the exact
target, connection generation and data revision; validate again before execution.
Parameter batches need per-item verified outcomes because MAVLink does not provide
an atomic multi-parameter transaction. Mission replacement, arm/mode changes and
other physical actions require a separately specified permission/confirmation policy.
Long operations need cancellation and bounded progress; a disconnect or timeout must
not automatically retry a physical action whose result is unknown.

## SDK and agent compatibility

The official [C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) provides
`ModelContextProtocol.AspNetCore`, HTTP transport registration and `MapMcp`. Inspected
release [v2.2.0](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0)
supports mixed protocol revisions with its HTTP session modes. Its
[project](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/src/ModelContextProtocol.AspNetCore/ModelContextProtocol.AspNetCore.csproj)
targets net8/net9/net10 and references `Microsoft.AspNetCore.App`. Pin the selected
package centrally and validate its version/session configuration against supported
clients; do not hand-code JSON-RPC or assume every agent supports notifications.

The [Streamable HTTP specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
documents the deployed 2025-11-25 transport and Origin/loopback requirements. Use SDK
negotiation for client compatibility, not a hard-coded assumption of one revision.
Streamable HTTP does not itself launch agents or deliver their conversational output.
For the initial tools, bounded request/response queries are sufficient; telemetry
subscriptions and higher-rate feeds need their own client-compatibility validation.

As one verified launcher target, installed `codex-cli 0.154.0` exposes `exec`, per-run
`-c` overrides and noninteractive execution. Official
[MCP documentation](https://developers.openai.com/codex/mcp) confirms HTTP `url` and
`bearer_token_env_var`; [noninteractive documentation](https://developers.openai.com/codex/noninteractive)
describes JSONL output. A proposed invocation, not executed during this assessment:

```text
codex exec --json --skip-git-repo-check
  -c 'mcp_servers.missionplanner.url="http://127.0.0.1:<assigned-port>/mcp"'
  -c 'mcp_servers.missionplanner.bearer_token_env_var="MP_MCP_TOKEN"'
  "Inspect the connected vehicle and report its current state."
```

Pass `MP_MCP_TOKEN` only through the child environment; construct each argument
separately in C#. The launcher must check installed-version capabilities, MCP startup
failure, login state and output parsing. MCP access scopes do not sandbox an agent's
other capabilities; filesystem/shell access is a separate launcher policy. Additional
providers need their own configuration/output adapters. Claude remains disabled and
was not invoked; enabling its adapter is outside this assessment.

## Effort and acceptance

These are engineering estimates for one developer, assuming a local agent with working
provider login; they are not measured implementation times or fixed commitments.

| Deliverable | Estimated effort |
| --- | --- |
| Transport proof: host, a few read tools, one CLI connection | 1–2 working days |
| Complete local read/analysis feature: launch/stop UI, scoped credentials, useful tools, tests and four-RID packaging | 5–8 working days total |
| Shared mission/parameter write operations, proposal review, multi-agent conflicts and SITL coverage | 2–4 weeks total, depending on tool scope |
| Remote/hosted agents, OAuth/public endpoint, broad flight-control surface | Separate estimate after deployment and action requirements |

Acceptance should exercise a real MCP client, wrong/expired credentials, Origin/Host
checks, multiple app instances, vehicle switching/disconnection during calls, a failed
or stopped agent, UI shutdown, bounded telemetry/log responses and one end-to-end
installed-agent task. Write acceptance additionally needs concurrent UI activity,
stale proposals, partial failures and SITL verification. Rebuild/package linux-x64,
win-x64, osx-x64 and osx-arm64 after adding the ASP.NET Core dependency.

No server, listener, agent task or model request was started. No packages were added.
Local application builds/tests were not run; `dotnet` is absent from the current PATH.
The next implementation step is a disposable, non-merged transport/package spike to
validate SDK hosting and one Codex round trip, followed by one complete read/analysis
feature rather than committing unused server plumbing alone.
