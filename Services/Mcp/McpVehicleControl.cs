using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services.Mcp;

internal sealed record ParameterWrite(string Name, double Value, double? Expected = null, string? Reason = null);
internal sealed record McpTerrainPoint(double Latitude, double Longitude);

/// <summary>
/// Direct vehicle control for allowed sessions: parameter writes, mode/arm/guided/mission commands,
/// calibration triggers and generic MAV_CMD dispatch through the same MAVLink link the operator uses.
/// Every write is audited and re-validated against the live target immediately before sending.
/// </summary>
internal sealed partial class McpVehicleAccess {
  private static readonly SemaphoreSlim CommandGate = new(1, 1);

  internal async Task<object> WriteParameters(string targetId, ParameterWrite[] writes, string reason, bool allowArmed, CancellationToken ct) {
    if (writes == null || writes.Length is < 1 or > 100 || writes.Any(w => w == null)
        || writes.Select(w => w.Name).Distinct(StringComparer.Ordinal).Count() != writes.Length) {
      throw new ArgumentException("Provide 1..100 unique parameter writes.");
    }
    if (reason == null || reason.Length is < 5 or > 8000) { throw new ArgumentException("Give a reason of 5..8000 characters; it is written to the audit file."); }
    await McpProposalWriter.Gate.WaitAsync(ct).ConfigureAwait(false);
    try {
      var target = Resolve(targetId, true);
      void ValidateTarget() {
        Resolve(targetId, true);
        if (!allowArmed) { RequireDisarmed(target); }
        if (target.Connection.Link.ReadOnly) { throw new InvalidOperationException("The connection is read only."); }
      }
      ValidateTarget();
      var changes = writes.Select(w => {
        var current = target.State.param[w.Name] ?? throw new ArgumentException("Unknown parameter: " + w.Name);
        return new ParameterChange(w.Name, w.Expected ?? current.Value, w.Value, w.Reason is { Length: >= 5 } ? w.Reason : reason);
      }).ToArray();
      foreach (var change in changes) { ValidateChange(target, change); }
      string directory = Path.Combine(AppPaths.StateRoot, "agent-parameter-audit");
      Directory.CreateDirectory(directory);
      string id = Guid.NewGuid().ToString("N"), prefix = Path.Combine(directory, id);
      await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(new { id, targetId, reason, allowArmed,
        systemId = target.State.sysid, componentId = target.State.compid, endpoint = target.Connection.Endpoint,
        createdUtc = DateTime.UtcNow, changes }, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
      await File.WriteAllLinesAsync(prefix + "-before.param", target.State.param.Snapshot().Where(p => !Sensitive(p.Name))
          .Select(p => p.Name + "," + p.Value.ToString("R", CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
      var results = new List<object>();
      string firmware = target.State.cs.firmware.ToString();
      var rebootRequired = new List<string>();
      int applied = 0; string? failure = null;
      foreach (var change in changes) {
        try {
          ct.ThrowIfCancellationRequested(); ValidateTarget(); ValidateChange(target, change);
          bool written = await Task.Run(() => target.Connection.Link.SetParamIfUnchangedAsync(target.State.sysid, target.State.compid,
              change.Name, change.Expected, change.Proposed, ValidateTarget, ct), ct).ConfigureAwait(false);
          double actual = target.State.param[change.Name]?.Value ?? double.NaN;
          var type = target.State.param[change.Name]?.TypeAP;
          double expectedWire = type == MAVLink.MAV_PARAM_TYPE.REAL32 ? (double)(float)change.Proposed : change.Proposed;
          if (!written || actual != expectedWire) {
            throw new InvalidOperationException($"{change.Name}: not verified; the vehicle may have applied a value. Read it again before retrying.");
          }
          applied++;
          if (Metadata(change.Name, firmware)["RebootRequired"].Equals("True", StringComparison.OrdinalIgnoreCase)) { rebootRequired.Add(change.Name); }
          results.Add(new { name = change.Name, previous = change.Expected, value = actual, status = "acknowledged" });
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException or TimeoutException) {
          failure = e.Message; results.Add(new { name = change.Name, previous = change.Expected, value = change.Proposed, status = "failed: " + e.Message });
          break;
        } finally {
          await File.WriteAllLinesAsync(prefix + "-result.txt", results.Select(r => JsonSerializer.Serialize(r)), CancellationToken.None).ConfigureAwait(false);
        }
      }
      return new { targetId, auditId = id, applied, total = changes.Length, completed = failure == null, error = failure, results, rebootRequired,
        note = "Values are verified against the typed acknowledgement. Writes are stored in the vehicle's parameter storage immediately; "
            + "parameters listed in rebootRequired take effect after vehicle_command reboot. No automatic rollback; the before snapshot is " + prefix + "-before.param." };
    } finally { McpProposalWriter.Gate.Release(); }
  }

  internal object Modes(string targetId) {
    var target = Resolve(targetId);
    return new { targetId, firmware = target.State.cs.firmware.ToString(), currentMode = target.State.cs.mode, armed = target.State.cs.armed,
      modes = MissionPlanner.ArduPilot.Common.getModesList(target.State.cs.firmware).Select(m => new { number = m.Key, name = m.Value }).ToArray() };
  }

  private static double Arg(JsonElement? args, string name, double? fallback = null) {
    if (args is { ValueKind: JsonValueKind.Object } element) {
      foreach (var property in element.EnumerateObject()) {
        if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { continue; }
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double number) && double.IsFinite(number)) { return number; }
        if (property.Value.ValueKind == JsonValueKind.True) { return 1; }
        if (property.Value.ValueKind == JsonValueKind.False) { return 0; }
        if (property.Value.ValueKind == JsonValueKind.String && double.TryParse(property.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) { return number; }
        throw new ArgumentException($"Argument {name} must be a finite number.");
      }
    }
    return fallback ?? throw new ArgumentException($"Argument {name} is required.");
  }
  private static string Text(JsonElement? args, string name) {
    if (args is { ValueKind: JsonValueKind.Object } element) {
      foreach (var property in element.EnumerateObject()) {
        if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) {
          return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.ToString();
        }
      }
    }
    throw new ArgumentException($"Argument {name} is required.");
  }

  internal static MAVLink.MAV_CMD ParseMavlinkCommandId(JsonElement? args) {
    if (args is { ValueKind: JsonValueKind.Object } element) {
      foreach (var property in element.EnumerateObject()) {
        if (!property.Name.Equals("commandId", StringComparison.OrdinalIgnoreCase)) { continue; }
        var value = property.Value;
        string text;
        if (value.ValueKind == JsonValueKind.String) {
          text = value.GetString()!.Trim();
          string name = text.StartsWith("MAV_CMD_", StringComparison.OrdinalIgnoreCase) ? text[8..] : text;
          if (Enum.GetNames<MAVLink.MAV_CMD>().Contains(name, StringComparer.OrdinalIgnoreCase)) {
            return Enum.Parse<MAVLink.MAV_CMD>(name, true);
          }
        } else if (value.ValueKind == JsonValueKind.Number) {
          text = value.GetRawText();
        } else {
          throw new ArgumentException("commandId must be an integer in 0..65535 or a known MAV_CMD name.");
        }
        // Parse the original text so a high-precision fraction cannot round into a command ID.
        if (!ushort.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ushort id)) {
          throw new ArgumentException("commandId must be an integer in 0..65535 or a known MAV_CMD name.");
        }
        if (!Enum.IsDefined(typeof(MAVLink.MAV_CMD), id)) {
          throw new ArgumentException("commandId is not a known MAV_CMD.");
        }
        return (MAVLink.MAV_CMD)id;
      }
    }
    throw new ArgumentException("Argument commandId is required.");
  }

  internal static readonly string[] Commands = ["set_mode", "arm", "disarm", "takeoff", "guided_goto", "rtl", "land", "loiter", "mission_start",
    "change_speed", "set_servo", "set_relay", "motor_test", "calibrate", "save_parameters", "reboot", "mavlink_command"];

  /// <summary>Executes one vehicle command on the request thread pool; returns the observed state afterwards.</summary>
  internal async Task<object> Command(string targetId, string command, JsonElement? args, CancellationToken ct) {
    if (string.IsNullOrWhiteSpace(command)) { throw new ArgumentException("Command name required; see vehicle_command description."); }
    string name = command.Trim().ToLowerInvariant();
    if (!Commands.Contains(name)) { throw new ArgumentException("Unknown command. Supported: " + string.Join(", ", Commands) + "."); }
    await CommandGate.WaitAsync(ct).ConfigureAwait(false);
    try {
      var target = Resolve(targetId, true);
      var link = target.Connection.Link;
      if (link.ReadOnly) { throw new InvalidOperationException("The connection is read only."); }
      byte sysid = target.State.sysid, compid = target.State.compid;
      bool armedBefore = target.State.cs.armed; string modeBefore = target.State.cs.mode;
      object detail = new { };
      bool accepted = await Task.Run<bool>(() => {
        ct.ThrowIfCancellationRequested();
        switch (name) {
          case "set_mode": {
            string mode = Text(args, "mode");
            var request = new MAVLink.mavlink_set_mode_t();
            if (!link.translateMode(sysid, compid, mode, ref request)) { throw new ArgumentException($"Mode {mode} is not available for {target.State.cs.firmware}; call vehicle_modes."); }
            link.setMode(sysid, compid, request); return true;
          }
          case "arm": return link.doARM(sysid, compid, true, Arg(args, "force", 0) != 0, TimeSpan.FromSeconds(5));
          case "disarm": return link.doARM(sysid, compid, false, Arg(args, "force", 0) != 0, TimeSpan.FromSeconds(5));
          case "takeoff": {
            double altitude = Arg(args, "altitudeMetres");
            if (altitude is <= 0 or > 10000) { throw new ArgumentException("altitudeMetres must be 0..10000 above home."); }
            var guided = new MAVLink.mavlink_set_mode_t();
            if (link.translateMode(sysid, compid, "GUIDED", ref guided)) { link.setMode(sysid, compid, guided); }
            return link.doCommand(sysid, compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, (float)altitude);
          }
          case "guided_goto": {
            double latitude = Arg(args, "latitude"), longitude = Arg(args, "longitude"), altitude = Arg(args, "altitudeMetres");
            int frame = (int)Arg(args, "frame", 3);
            if (latitude is < -90 or > 90 || longitude is < -180 or > 180 || altitude == 0 || latitude == 0 || longitude == 0) { throw new ArgumentException("guided_goto needs nonzero WGS84 latitude/longitude and a nonzero altitude."); }
            if (frame is not (0 or 3 or 10)) { throw new ArgumentException("frame must be 0 (GLOBAL), 3 (relative to home) or 10 (terrain)."); }
            link.setGuidedModeWP(sysid, compid, new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = latitude, lng = longitude, alt = (float)altitude, frame = (byte)frame });
            detail = new { latitude, longitude, altitude, frame };
            return true;
          }
          case "rtl": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.RETURN_TO_LAUNCH, 0, 0, 0, 0, 0, 0, 0);
          case "land": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.LAND, 0, 0, 0, 0, 0, 0, 0);
          case "loiter": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.LOITER_UNLIM, 0, 0, 0, 0, 0, 0, 0);
          case "mission_start": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.MISSION_START, 0, 0, 0, 0, 0, 0, 0);
          case "change_speed": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_CHANGE_SPEED, (float)Arg(args, "speedType", 1),
              (float)Arg(args, "speedMetresPerSecond"), (float)Arg(args, "throttlePercent", -1), 0, 0, 0, 0);
          case "set_servo": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_SET_SERVO, (float)Arg(args, "channel"), (float)Arg(args, "pwm"), 0, 0, 0, 0, 0);
          case "set_relay": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_SET_RELAY, (float)Arg(args, "relay"), (float)Arg(args, "state"), 0, 0, 0, 0, 0);
          case "motor_test": {
            int motor = (int)Arg(args, "motor"), throttle = (int)Arg(args, "throttlePercent"), seconds = (int)Arg(args, "seconds", 2), count = (int)Arg(args, "motorCount", 0);
            if (motor is < 1 or > 32 || throttle is < 0 or > 100 || seconds is < 1 or > 60) { throw new ArgumentException("motor 1..32, throttlePercent 0..100, seconds 1..60."); }
            RequireDisarmed(target);
            return link.doMotorTest(motor, MAVLink.MOTOR_TEST_THROTTLE_TYPE.MOTOR_TEST_THROTTLE_PERCENT, throttle, seconds, count);
          }
          case "calibrate": {
            string kind = Text(args, "kind").ToLowerInvariant();
            RequireDisarmed(target);
            return kind switch {
              "gyro" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 1, 0, 0, 0, 0, 0, 0),
              "baro" or "barometer" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 1, 0, 0, 0, 0),
              "airspeed" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 2, 0, 0, 0, 0),
              "level" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 0, 0, 2, 0, 0),
              "accel_simple" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 0, 0, 4, 0, 0),
              "compass_start" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_START_MAG_CAL, 0, 1, 1, 0, 0, 0, 0),
              "compass_accept" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_ACCEPT_MAG_CAL, 0, 0, 0, 0, 0, 0, 0),
              "compass_cancel" => link.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_CANCEL_MAG_CAL, 0, 0, 0, 0, 0, 0, 0),
              _ => throw new ArgumentException("kind: gyro, baro, airspeed, level, accel_simple, compass_start, compass_accept or compass_cancel. Interactive six-side accelerometer and radio calibration use the Setup pages through ui_* tools."),
            };
          }
          case "save_parameters": return link.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 1, 0, 0, 0, 0, 0, 0);
          case "reboot": RequireDisarmed(target); return link.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 1, 0, 0, 0, 0, 0, 0);
          case "mavlink_command": {
            var mavCommand = ParseMavlinkCommandId(args);
            float[] p = Enumerable.Range(1, 7).Select(i => (float)Arg(args, "p" + i, 0)).ToArray();
            detail = new { commandId = (ushort)mavCommand, commandName = mavCommand.ToString(), p };
            return Arg(args, "useCommandInt", 0) != 0
                ? link.doCommandInt(sysid, compid, mavCommand, p[0], p[1], p[2], p[3], (int)Arg(args, "p5", 0), (int)Arg(args, "p6", 0), p[6])
                : link.doCommand(sysid, compid, mavCommand, p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
          }
          default: throw new ArgumentException("Unknown command.");
        }
      }, ct).ConfigureAwait(false);
      // Give the heartbeat/mode stream a moment so the reported state reflects the command.
      if (name is "set_mode" or "arm" or "disarm" or "takeoff" or "rtl" or "land" or "loiter") {
        for (int i = 0; i < 15 && (name == "set_mode" ? target.State.cs.mode == modeBefore : name is "arm" or "disarm" && target.State.cs.armed == armedBefore); i++) {
          await Task.Delay(200, ct).ConfigureAwait(false);
        }
      }
      Resolve(targetId);
      return new { targetId, command = name, accepted, detail, mode = target.State.cs.mode, armed = target.State.cs.armed, modeBefore, armedBefore,
        note = "accepted reflects the MAVLink acknowledgement or send; verify effect with read_telemetry/vehicle_health and read_vehicle_messages." };
    } finally { CommandGate.Release(); }
  }

  internal static object Terrain(McpTerrainPoint[] points, CancellationToken ct) {
    if (points == null || points.Length is < 1 or > 500 || points.Any(p => p == null || !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude)
        || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180)) { throw new ArgumentException("Provide 1..500 finite WGS84 points."); }
    var results = new List<object>();
    foreach (var point in points) {
      ct.ThrowIfCancellationRequested();
      var response = srtm.getAltitude(point.Latitude, point.Longitude);
      bool valid = response.currenttype == srtm.tiletype.valid;
      results.Add(new { point.Latitude, point.Longitude, altitudeMetres = valid ? response.alt : (double?)null,
        source = response.altsource, status = response.currenttype.ToString() });
    }
    return new { points = results, note = "Terrain metres AMSL from the configured elevation source (SRTM/GeoTIFF/DTED). Missing tiles return status invalid and may download in the background; retry later. Ocean returns 0." };
  }
}
