using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MissionPlanner.Services.Mcp;

/// <summary>Vehicle write tools available to allowed sessions: parameters, commands and terrain lookups.</summary>
[McpServerToolType]
internal sealed class McpVehicleTools(McpVehicleAccess vehicles) {
  private static string Json(object value) => JsonSerializer.Serialize(value, MissionPlannerMcpTools.JsonOptions);
  private static async Task<string> Guard(Func<Task<object>> action) {
    try { return Json(await action().ConfigureAwait(false)); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or TimeoutException or System.IO.IOException) { throw new McpException(e.Message); }
  }

  [McpServerTool(Name = "write_parameters", ReadOnly = false, Destructive = true), Description("Write 1..100 parameters to one exact vehicle immediately, verified against the typed acknowledgement, with a before-snapshot and audit file. Optional expected per change rejects writes if the value changed since analysis. Requires a disarmed vehicle unless allowArmed=true (in-flight tuning only when the operator expects it). Check metadata ranges and rebootRequired first with read_parameters.")]
  public Task<string> Write(string targetId, ParameterWrite[] changes, string reason, CancellationToken cancellationToken, bool allowArmed = false) =>
      Guard(() => vehicles.WriteParameters(targetId, changes, reason, allowArmed, cancellationToken));

  [McpServerTool(Name = "vehicle_modes", ReadOnly = true), Description("Flight modes available for the target's firmware plus current mode and armed state. Use before vehicle_command set_mode.")]
  public string Modes(string targetId) { try { return Json(vehicles.Modes(targetId)); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new McpException(e.Message); } }

  [McpServerTool(Name = "vehicle_command", ReadOnly = false, Destructive = true), Description("Send one vehicle command through the operator's MAVLink link. command: set_mode{mode}, arm{force?}, disarm{force?}, takeoff{altitudeMetres}, guided_goto{latitude,longitude,altitudeMetres,frame?}, rtl, land, loiter, mission_start, change_speed{speedType?,speedMetresPerSecond,throttlePercent?}, set_servo{channel,pwm}, set_relay{relay,state}, motor_test{motor,throttlePercent,seconds?,motorCount?}, calibrate{kind: gyro|baro|airspeed|level|accel_simple|compass_start|compass_accept|compass_cancel}, save_parameters, reboot, mavlink_command{commandId,p1..p7,useCommandInt?}. commandId accepts a known MAV_CMD integer in 0..65535 or a case-insensitive name such as CONDITION_YAW or MAV_CMD_CONDITION_YAW. Arguments are a JSON object. Confirm intent with the operator for arming, takeoff, motor tests and reboots; verify results with telemetry and messages.")]
  public Task<string> Command(string targetId, string command, CancellationToken cancellationToken, JsonElement? arguments = null) =>
      Guard(() => vehicles.Command(targetId, command, arguments, cancellationToken));

  [McpServerTool(Name = "terrain_elevation", ReadOnly = true), Description("Terrain height in metres AMSL for 1..500 WGS84 points from the configured elevation source (SRTM/GeoTIFF/DTED), with source and availability. Missing tiles may download in the background; retry. Use for terrain-safe mission altitudes together with mission_elevation_profile.")]
  public Task<string> Terrain(McpTerrainPoint[] points, CancellationToken cancellationToken) =>
      Guard(() => Task.Run(() => McpVehicleAccess.Terrain(points, cancellationToken), cancellationToken));
}
