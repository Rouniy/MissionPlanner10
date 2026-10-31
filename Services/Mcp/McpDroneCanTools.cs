using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MissionPlanner.Services.Mcp;

/// <summary>DroneCAN node tools for allowed sessions: enumerate, read and configure peripherals over the vehicle's CAN tunnel.</summary>
[McpServerToolType]
internal sealed class McpDroneCanTools(McpDroneCanAccess droneCan) {
  private static string Json(object value) => JsonSerializer.Serialize(value, MissionPlannerMcpTools.JsonOptions);
  private static async Task<string> Guard(Func<Task<object>> action) {
    try { return Json(await action().ConfigureAwait(false)); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or TimeoutException or System.IO.IOException or ObjectDisposedException) { throw new McpException(e.Message); }
  }

  [McpServerTool(Name = "dronecan_nodes", ReadOnly = true), Description("List DroneCAN nodes on one of the autopilot's CAN ports (bus 1 or 2) through the MAVLink CAN tunnel: id, name, health, mode, uptime, firmware, hardware and uid, plus which node is the autopilot itself. Opens a session shared with the DroneCAN page; the autopilot forwards one bus per link, so the other bus is refused while a page uses it. Waits up to waitSeconds (default 4) for names.")]
  public Task<string> Nodes(string targetId, int bus, CancellationToken cancellationToken, double waitSeconds = 4) =>
      Guard(() => droneCan.Nodes(targetId, bus, waitSeconds, cancellationToken));

  [McpServerTool(Name = "dronecan_read_parameters", ReadOnly = true), Description("Read all parameters of one DroneCAN node (uavcan.protocol.param.GetSet) with type, value and the node's min/max/default when reported. Call dronecan_nodes first; takes a few seconds per node.")]
  public Task<string> Read(string targetId, int bus, int nodeId, CancellationToken cancellationToken) =>
      Guard(() => droneCan.ReadParameters(targetId, bus, nodeId, cancellationToken));

  [McpServerTool(Name = "dronecan_write_parameters", ReadOnly = false, Destructive = true), Description("Write 1..50 parameters to one DroneCAN node, verify by reading back, and by default commit them to the node's flash (saveToFlash). Optional expected per change rejects stale writes. restart=true reboots the node afterwards (needed for CAN_NODE, OUTx_FUNCTION and driver changes). Requires a disarmed vehicle and a reason; an audit file with a before snapshot is written. Confirm intent with the operator before changing node ids, output functions or sensor drivers.")]
  public Task<string> Write(string targetId, int bus, int nodeId, McpDroneCanWrite[] changes, string reason, CancellationToken cancellationToken,
      bool saveToFlash = true, bool restart = false) =>
      Guard(() => droneCan.WriteParameters(targetId, bus, nodeId, changes, reason, saveToFlash, restart, cancellationToken));

  [McpServerTool(Name = "dronecan_node_command", ReadOnly = false, Destructive = true), Description("Send one maintenance command to a DroneCAN node: save (commit parameters to flash), restart (reboot the node) or erase (reset parameters to defaults; takes effect after restart). Requires a disarmed vehicle and a reason.")]
  public Task<string> Command(string targetId, int bus, int nodeId, string command, string reason, CancellationToken cancellationToken) =>
      Guard(() => droneCan.NodeCommand(targetId, bus, nodeId, command, reason, cancellationToken));
}
