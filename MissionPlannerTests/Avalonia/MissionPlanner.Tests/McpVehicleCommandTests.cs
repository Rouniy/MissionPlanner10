using System.Text.Json;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpVehicleCommandTests {
  [Theory]
  [InlineData("115", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("201", MAVLink.MAV_CMD.DO_SET_ROI)]
  [InlineData("115.0", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("1.15e2", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("11500e-2", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("115.00000000000000000000000000000", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("\"115\"", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("\"201\"", MAVLink.MAV_CMD.DO_SET_ROI)]
  [InlineData("\"CONDITION_YAW\"", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("\"MAV_CMD_CONDITION_YAW\"", MAVLink.MAV_CMD.CONDITION_YAW)]
  [InlineData("\"DO_SET_ROI\"", MAVLink.MAV_CMD.DO_SET_ROI)]
  [InlineData("\"MAV_CMD_DO_SET_ROI\"", MAVLink.MAV_CMD.DO_SET_ROI)]
  [InlineData("\" mav_cmd_condition_yaw \"", MAVLink.MAV_CMD.CONDITION_YAW)]
  public void Command_id_accepts_known_integers_and_names(string json, MAVLink.MAV_CMD expected) {
    using var args = JsonDocument.Parse("{\"commandId\":" + json + "}");
    Assert.Equal(expected, McpVehicleAccess.ParseMavlinkCommandId(args.RootElement));
  }

  [Fact]
  public void Command_id_accepts_every_defined_ushort_value() {
    foreach (var command in Enum.GetValues<MAVLink.MAV_CMD>()) {
      var args = JsonSerializer.SerializeToElement(new { commandId = (ushort)command });
      Assert.Equal(command, McpVehicleAccess.ParseMavlinkCommandId(args));
    }
  }

  [Theory]
  [InlineData("0", "known MAV_CMD")]
  [InlineData("65535", "known MAV_CMD")]
  [InlineData("-1", "integer in 0..65535")]
  [InlineData("115.5", "integer in 0..65535")]
  [InlineData("115.000000000000000001", "integer in 0..65535")]
  [InlineData("115.00000000000000000000000000001", "integer in 0..65535")]
  [InlineData("114.99999999999999999999999999999", "integer in 0..65535")]
  [InlineData("201.00000000000000000000000000001", "integer in 0..65535")]
  [InlineData("1.1500000000000000000000000000001e2", "integer in 0..65535")]
  [InlineData("65536", "integer in 0..65535")]
  [InlineData("65651", "integer in 0..65535")]
  [InlineData("4294967411", "integer in 0..65535")]
  [InlineData("1e100", "integer in 0..65535")]
  [InlineData("1e-100", "integer in 0..65535")]
  [InlineData("-1e-100", "integer in 0..65535")]
  [InlineData("\"-1\"", "integer in 0..65535")]
  [InlineData("\"115.5\"", "integer in 0..65535")]
  [InlineData("\"115.00000000000000000000000000001\"", "integer in 0..65535")]
  [InlineData("\"114.99999999999999999999999999999\"", "integer in 0..65535")]
  [InlineData("\"201.00000000000000000000000000001\"", "integer in 0..65535")]
  [InlineData("\"1.1500000000000000000000000000001e2\"", "integer in 0..65535")]
  [InlineData("\"1e-100\"", "integer in 0..65535")]
  [InlineData("\"-1e-100\"", "integer in 0..65535")]
  [InlineData("\"65536\"", "integer in 0..65535")]
  [InlineData("\"NaN\"", "integer in 0..65535")]
  [InlineData("\"Infinity\"", "integer in 0..65535")]
  [InlineData("\"UNKNOWN_COMMAND\"", "known MAV_CMD")]
  [InlineData("\"MAV_CMD_UNKNOWN_COMMAND\"", "known MAV_CMD")]
  [InlineData("\"WAYPOINT,LOITER_UNLIM\"", "known MAV_CMD")]
  [InlineData("true", "integer in 0..65535")]
  [InlineData("false", "integer in 0..65535")]
  [InlineData("null", "integer in 0..65535")]
  [InlineData("[]", "integer in 0..65535")]
  public void Command_id_rejects_invalid_values_with_a_clear_argument_error(string json, string message) {
    using var args = JsonDocument.Parse("{\"commandId\":" + json + "}");
    var error = Assert.Throws<ArgumentException>(() => McpVehicleAccess.ParseMavlinkCommandId(args.RootElement));
    Assert.Contains("commandId", error.Message);
    Assert.Contains(message, error.Message);
  }

  [Fact]
  public void Command_id_is_required_and_property_name_is_case_insensitive() {
    Assert.Contains("commandId is required", Assert.Throws<ArgumentException>(
        () => McpVehicleAccess.ParseMavlinkCommandId(null)).Message);
    Assert.Contains("commandId is required", Assert.Throws<ArgumentException>(
        () => McpVehicleAccess.ParseMavlinkCommandId(JsonSerializer.SerializeToElement(new { }))).Message);
    var args = JsonSerializer.SerializeToElement(new { CommandId = 115 });
    Assert.Equal(MAVLink.MAV_CMD.CONDITION_YAW, McpVehicleAccess.ParseMavlinkCommandId(args));
  }
}
