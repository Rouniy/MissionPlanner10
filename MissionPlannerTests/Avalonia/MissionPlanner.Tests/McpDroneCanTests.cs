using System.Text;
using System.Text.Json;
using MissionPlanner.Comms;
using MissionPlanner.Services;
using MissionPlanner.Services.Mcp;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

public sealed class McpDroneCanTests {
  private static readonly byte[] NodeName = Encoding.ASCII.GetBytes("org.ardupilot.MatekL431-Periph");

  private sealed class Fixture : IDisposable {
    internal readonly MAVLinkInterface Link = new() { BaseStream = new CommsInjection() };
    internal readonly MavLinkConnection Connection;
    internal readonly McpVehicleAccess Vehicles;
    internal readonly string TargetId;
    internal readonly DroneCAN.DroneCAN Can = new();
    internal readonly List<object> Sent = new();
    internal readonly McpDroneCanAccess DroneCan;

    internal Fixture(bool heartbeat) {
      var state = Link.MAVlist[1, 1];
      state.lastvalidpacket = DateTime.UtcNow;
      Link.MAVlist[1, 1] = state;
      state.param["CAN_P2_DRIVER"] = new MAVLink.MAVLinkParam("CAN_P2_DRIVER", 1, MAVLink.MAV_PARAM_TYPE.INT8);
      state.param["CAN_D1_UC_NODE"] = new MAVLink.MAVLinkParam("CAN_D1_UC_NODE", 10, MAVLink.MAV_PARAM_TYPE.INT8);
      if (heartbeat) {
        state.addPacket(new MAVLink.MAVLinkMessage(new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
            MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t { base_mode = 0 }, false, 1, 1), DateTime.UtcNow));
      }
      Connection = new MavLinkConnection(Link, "test", true, null);
      Vehicles = new McpVehicleAccess(() => [Connection]);
      TargetId = JsonSerializer.SerializeToElement(Vehicles.ListVehicles()).EnumerateArray().Single().GetProperty("targetId").GetString()!;
      DroneCan = new McpDroneCanAccess(Vehicles,
          (target, bus, current) => MavlinkCanTransport.Open(target.Connection.Link, target.State.sysid, target.State.compid, bus, current,
              out string? conflict, packet => { lock (Sent) { Sent.Add(packet); } return true; })
              ?? throw new InvalidOperationException(conflict),
          () => Can);
    }

    internal static DroneCAN.CANFrame Frame(byte source) => new(new byte[4]) { SourceNode = source };

    internal void Status(byte node, uint uptime = 5) =>
        Can.InvokeMessageReceived(Frame(node), new DroneCAN.DroneCAN.uavcan_protocol_NodeStatus { uptime_sec = uptime }, 0);

    internal void Info(byte node) => Can.InvokeMessageReceived(Frame(node), new DroneCAN.DroneCAN.uavcan_protocol_GetNodeInfo_res {
      name = NodeName, name_len = (byte)NodeName.Length,
      software_version = new DroneCAN.DroneCAN.uavcan_protocol_SoftwareVersion { major = 1, minor = 7, vcs_commit = 0x9c6f307f },
      hardware_version = new DroneCAN.DroneCAN.uavcan_protocol_HardwareVersion { major = 4, minor = 38, unique_id = new byte[16] },
    }, 0);

    internal uint[] SentIds() { lock (Sent) { return Sent.OfType<MAVLink.mavlink_can_frame_t>().Select(p => p.id).ToArray(); } }

    public void Dispose() { DroneCan.Dispose(); Link.Dispose(); }
  }

  private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, MissionPlannerMcpTools.JsonOptions);

  [Fact]
  public async Task Nodes_lists_statuses_requests_node_info_marks_the_autopilot_and_explains_an_empty_bus() {
    using var f = new Fixture(heartbeat: false);
    var empty = Json(await f.DroneCan.Nodes(f.TargetId, 2, 0, CancellationToken.None));
    Assert.Equal(0, empty.GetProperty("nodes").GetArrayLength());
    Assert.Contains("No DroneCAN traffic on CAN2", empty.GetProperty("hint").GetString());
    Assert.Equal(10, empty.GetProperty("autopilotNodeId").GetInt32());
    Assert.Equal(1, f.DroneCan.OpenSessionCount);

    f.Status(10);
    var autopilotOnly = Json(await f.DroneCan.Nodes(f.TargetId, 2, 0.3, CancellationToken.None));
    var node = Assert.Single(autopilotOnly.GetProperty("nodes").EnumerateArray());
    Assert.Equal(10, node.GetProperty("nodeId").GetInt32());
    Assert.True(node.GetProperty("isAutopilot").GetBoolean());
    Assert.Equal("?", node.GetProperty("name").GetString());
    Assert.Contains("Only the autopilot itself (node 10)", autopilotOnly.GetProperty("hint").GetString());
    // uavcan.protocol.GetNodeInfo request from node 126 to node 10, priority 30, as an extended CAN_FRAME for bus index 1.
    Assert.Contains(0x9E018AFEu, f.SentIds());
    var request = f.Sent.OfType<MAVLink.mavlink_can_frame_t>().First(p => p.id == 0x9E018AFEu);
    Assert.Equal(1, request.bus);

    f.Status(125);
    f.Info(125);
    var both = Json(await f.DroneCan.Nodes(f.TargetId, 2, 0.3, CancellationToken.None));
    var nodes = both.GetProperty("nodes").EnumerateArray().ToArray();
    Assert.Equal(2, nodes.Length);
    Assert.Equal("org.ardupilot.MatekL431-Periph", nodes[1].GetProperty("name").GetString());
    Assert.Equal("9c6f307f", nodes[1].GetProperty("vcsCommit").GetString());
    Assert.Equal("4.38", nodes[1].GetProperty("hardwareVersion").GetString());
    Assert.False(nodes[1].GetProperty("isAutopilot").GetBoolean());
    Assert.Equal(JsonValueKind.Null, both.GetProperty("hint").ValueKind);
    Assert.Equal(1, f.DroneCan.OpenSessionCount);

    await Assert.ThrowsAsync<ArgumentException>(() => f.DroneCan.Nodes(f.TargetId, 3, 0, CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() => f.DroneCan.Nodes(f.TargetId, 2, 99, CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() => f.DroneCan.Nodes("nope", 2, 0, CancellationToken.None));
  }

  [Fact]
  public async Task Sessions_refuse_a_second_bus_on_the_same_link_and_close_when_the_connection_changes() {
    using var f = new Fixture(heartbeat: false);
    await f.DroneCan.Nodes(f.TargetId, 2, 0, CancellationToken.None);
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.DroneCan.Nodes(f.TargetId, 1, 0, CancellationToken.None));
    Assert.Contains("CAN2 is already in use", error.Message);
    f.Connection.MarkClosed(); f.Connection.MarkOpened();
    await Assert.ThrowsAsync<InvalidOperationException>(() => f.DroneCan.Nodes(f.TargetId, 2, 0, CancellationToken.None));
    f.DroneCan.CloseAll();
    Assert.Equal(0, f.DroneCan.OpenSessionCount);
  }

  [Fact]
  public async Task Reads_require_a_seen_node_and_report_when_the_parameter_service_stays_silent() {
    using var f = new Fixture(heartbeat: false);
    await f.DroneCan.Nodes(f.TargetId, 2, 0, CancellationToken.None);
    var unknown = await Assert.ThrowsAsync<ArgumentException>(() => f.DroneCan.ReadParameters(f.TargetId, 2, 42, CancellationToken.None));
    Assert.Contains("dronecan_nodes", unknown.Message);
    f.Status(125);
    var result = Json(await f.DroneCan.ReadParameters(f.TargetId, 2, 125, CancellationToken.None));
    Assert.False(result.GetProperty("received").GetBoolean());
    Assert.Equal(0, result.GetProperty("count").GetInt32());
    // uavcan.protocol.param.GetSet request (service 11) from node 126 to node 125 was sent at least once.
    Assert.Contains(f.SentIds(), id => (id & 0x00FF80FF) == 0x000B80FE && ((id >> 8) & 0x7F) == 125);
  }

  [Fact]
  public async Task Writes_need_a_reason_a_disarmed_vehicle_a_writable_link_and_an_answering_node() {
    using var armed = new Fixture(heartbeat: false);
    await armed.DroneCan.Nodes(armed.TargetId, 2, 0, CancellationToken.None);
    armed.Status(125);
    var change = new[] { new McpDroneCanWrite("OUT1_FUNCTION", JsonSerializer.SerializeToElement(33)) };
    await Assert.ThrowsAsync<ArgumentException>(() => armed.DroneCan.WriteParameters(armed.TargetId, 2, 125, change, "no", true, false, CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() => armed.DroneCan.WriteParameters(armed.TargetId, 2, 125, [], "map motor 1", true, false, CancellationToken.None));
    var heartbeat = await Assert.ThrowsAsync<InvalidOperationException>(() => armed.DroneCan.WriteParameters(armed.TargetId, 2, 125, change, "map motor 1", true, false, CancellationToken.None));
    Assert.Contains("heartbeat", heartbeat.Message);
    await Assert.ThrowsAsync<InvalidOperationException>(() => armed.DroneCan.NodeCommand(armed.TargetId, 2, 125, "restart", "apply mapping", CancellationToken.None));

    using var f = new Fixture(heartbeat: true);
    await f.DroneCan.Nodes(f.TargetId, 2, 0, CancellationToken.None);
    f.Status(125);
    f.Link.ReadOnly = true;
    var readOnly = await Assert.ThrowsAsync<InvalidOperationException>(() => f.DroneCan.WriteParameters(f.TargetId, 2, 125, change, "map motor 1", true, false, CancellationToken.None));
    Assert.Contains("read only", readOnly.Message);
    f.Link.ReadOnly = false;
    var silent = await Assert.ThrowsAsync<InvalidOperationException>(() => f.DroneCan.WriteParameters(f.TargetId, 2, 125, change, "map motor 1", true, false, CancellationToken.None));
    Assert.Contains("nothing was written", silent.Message);
    await Assert.ThrowsAsync<ArgumentException>(() => f.DroneCan.NodeCommand(f.TargetId, 2, 125, "format", "apply mapping", CancellationToken.None));
  }

  [Fact]
  public void Values_are_typed_range_checked_and_compared_like_the_node_stores_them() {
    var integer = new McpDroneCanParameter("OUT1_FUNCTION", 51d, DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_INTEGER_VALUE, -1, 300, 0);
    var real = new McpDroneCanParameter("ARSPD_PSI_RANGE", 1d, DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_REAL_VALUE, null, null, null);
    var flag = new McpDroneCanParameter("DEBUG", 0d, DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_BOOLEAN_VALUE, null, null, null);
    var text = new McpDroneCanParameter("NAME", "abc", DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_STRING_VALUE, null, null, null);
    static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);
    Assert.Equal(33d, McpDroneCanAccess.ParseValue(integer, J(33)));
    Assert.Equal(33d, McpDroneCanAccess.ParseValue(integer, J("33")));
    Assert.Throws<ArgumentException>(() => McpDroneCanAccess.ParseValue(integer, J(33.5)));
    Assert.Throws<ArgumentException>(() => McpDroneCanAccess.ParseValue(integer, J(301)));
    Assert.Throws<ArgumentException>(() => McpDroneCanAccess.ParseValue(integer, J(-2)));
    Assert.Throws<ArgumentException>(() => McpDroneCanAccess.ParseValue(integer, J("many")));
    Assert.Equal(0.1d, McpDroneCanAccess.ParseValue(real, J(0.1)));
    Assert.Throws<ArgumentException>(() => McpDroneCanAccess.ParseValue(flag, J(2)));
    Assert.Equal(1d, McpDroneCanAccess.ParseValue(flag, J(true)));
    Assert.Equal("xyz", McpDroneCanAccess.ParseValue(text, J("xyz")));
    Assert.Throws<ArgumentException>(() => McpDroneCanAccess.ParseValue(text, J(5)));
    Assert.Throws<ArgumentException>(() => McpDroneCanAccess.ParseValue(integer, J("abc")));
    Assert.True(McpDroneCanAccess.SameValue(real with { Value = (double)(float)0.1 }, 0.1d));
    Assert.False(McpDroneCanAccess.SameValue(integer, 52d));
    Assert.True(McpDroneCanAccess.SameValue(text, "abc"));
    Assert.Equal("integer", McpDroneCanAccess.TypeName(integer.Type));
    Assert.Equal("string", McpDroneCanAccess.TypeName(text.Type));
  }

  [Fact]
  public void Describe_unpacks_values_ranges_and_defaults_from_a_get_set_response() {
    var response = new DroneCAN.DroneCAN.uavcan_protocol_param_GetSet_res {
      name = Encoding.ASCII.GetBytes("OUT_RATE"), name_len = 8,
      value = new DroneCAN.DroneCAN.uavcan_protocol_param_Value {
        uavcan_protocol_param_Value_type = DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_INTEGER_VALUE,
        union = new DroneCAN.DroneCAN.uavcan_protocol_param_Value.unions { integer_value = 50 },
      },
      min_value = new DroneCAN.DroneCAN.uavcan_protocol_param_NumericValue {
        uavcan_protocol_param_NumericValue_type = DroneCAN.DroneCAN.uavcan_protocol_param_NumericValue.uavcan_protocol_param_NumericValue_type_t.UAVCAN_PROTOCOL_PARAM_NUMERICVALUE_TYPE_INTEGER_VALUE,
        union = new DroneCAN.DroneCAN.uavcan_protocol_param_NumericValue.unions { integer_value = 25 },
      },
      max_value = new DroneCAN.DroneCAN.uavcan_protocol_param_NumericValue {
        uavcan_protocol_param_NumericValue_type = DroneCAN.DroneCAN.uavcan_protocol_param_NumericValue.uavcan_protocol_param_NumericValue_type_t.UAVCAN_PROTOCOL_PARAM_NUMERICVALUE_TYPE_INTEGER_VALUE,
        union = new DroneCAN.DroneCAN.uavcan_protocol_param_NumericValue.unions { integer_value = 400 },
      },
    };
    var parameter = McpDroneCanAccess.Describe(response);
    Assert.Equal("OUT_RATE", parameter.Name);
    Assert.Equal(50d, parameter.Value);
    Assert.Equal(25, parameter.Min); Assert.Equal(400, parameter.Max); Assert.Null(parameter.Default);
    var json = Json(parameter.ToResult());
    Assert.Equal("integer", json.GetProperty("type").GetString());
    Assert.Equal(JsonValueKind.Null, json.GetProperty("default").ValueKind);
  }
}
