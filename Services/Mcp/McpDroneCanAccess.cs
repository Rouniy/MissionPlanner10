using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DroneCAN;
using MissionPlanner.ViewModels;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Services.Mcp;

/// <summary>One requested DroneCAN parameter change from an agent.</summary>
internal sealed record McpDroneCanWrite(string Name, JsonElement Value, JsonElement? Expected = null);

/// <summary>
/// DroneCAN node access for agents over the vehicle's MAVLink CAN tunnel. One session per vehicle
/// and bus shares the same <see cref="MavlinkCanTransport"/> as the DroneCAN page and the Inspector,
/// so an agent never flips MAV_CMD_CAN_FORWARD under the operator; idle sessions close themselves.
/// </summary>
internal sealed class McpDroneCanAccess : IDisposable {
  internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
  internal const byte AgentSourceNode = 126;

  private readonly McpVehicleAccess _vehicles;
  private readonly Func<McpTarget, byte, Func<bool>, MavlinkCanTransport?> _openTransport;
  private readonly Func<DroneCAN.DroneCAN> _canFactory;
  private readonly Dictionary<string, Session> _sessions = new();
  private readonly object _sync = new();
  private readonly Timer _reaper;
  private bool _disposed;

  internal McpDroneCanAccess(McpVehicleAccess vehicles,
      Func<McpTarget, byte, Func<bool>, MavlinkCanTransport?>? openTransport = null,
      Func<DroneCAN.DroneCAN>? canFactory = null) {
    _vehicles = vehicles;
    _openTransport = openTransport ?? ((target, bus, current) => {
      var transport = MavlinkCanTransport.Open(target.Connection.Link, target.State.sysid, target.State.compid, bus,
          current, out string? conflict);
      return transport ?? throw new InvalidOperationException(conflict ?? "The MAVLink CAN tunnel is busy.");
    });
    _canFactory = canFactory ?? (() => new DroneCAN.DroneCAN());
    _reaper = new Timer(_ => CloseIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
  }

  internal int OpenSessionCount { get { lock (_sync) { return _sessions.Count; } } }

  private static byte Bus(int bus) => bus is 1 or 2 ? (byte)bus : throw new ArgumentException("bus must be 1 or 2 (the autopilot's CAN port).");

  private Session GetOrOpen(string targetId, int bus) {
    byte port = Bus(bus);
    var target = _vehicles.Resolve(targetId);
    string key = targetId + ":" + port;
    lock (_sync) {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_sessions.TryGetValue(key, out var existing)) {
        if (existing.IsCurrent()) { existing.Touch(); return existing; }
        existing.Dispose(); _sessions.Remove(key);
      }
      var session = new Session(target, port);
      try {
        session.Open(_openTransport, _canFactory);
      } catch { session.Dispose(); throw; }
      _sessions[key] = session;
      return session;
    }
  }

  private void CloseIdle() {
    lock (_sync) {
      foreach (var pair in _sessions.Where(p => !p.Value.IsCurrent() || DateTime.UtcNow - p.Value.LastUsedUtc > IdleTimeout).ToArray()) {
        pair.Value.Dispose(); _sessions.Remove(pair.Key);
      }
    }
  }

  /// <summary>Closes every session, for tests and application shutdown.</summary>
  internal void CloseAll() {
    lock (_sync) {
      foreach (var session in _sessions.Values) { session.Dispose(); }
      _sessions.Clear();
    }
  }

  public void Dispose() {
    lock (_sync) { _disposed = true; }
    _reaper.Dispose();
    CloseAll();
  }

  internal async Task<object> Nodes(string targetId, int bus, double waitSeconds, CancellationToken ct) {
    if (waitSeconds is < 0 or > 15 || double.IsNaN(waitSeconds)) { throw new ArgumentException("waitSeconds must be 0..15."); }
    var session = GetOrOpen(targetId, bus);
    var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
    while (DateTime.UtcNow < deadline) {
      ct.ThrowIfCancellationRequested();
      var snapshot = session.Snapshot();
      if (snapshot.Count != 0 && snapshot.All(n => n.Name != "?") && session.Age > TimeSpan.FromSeconds(1.5)) { break; }
      await Task.Delay(200, ct).ConfigureAwait(false);
    }
    var nodes = session.Snapshot();
    byte other = (byte)(session.Bus == 1 ? 2 : 1);
    string? hint = nodes.Count == 0
        ? $"No DroneCAN traffic on CAN{session.Bus} after {session.Age.TotalSeconds:0.0} s. Check CAN_P{session.Bus}_DRIVER and wiring, or try bus {other}: the autopilot forwards one port at a time."
        : session.AutopilotNodeId is int ap && nodes.All(n => n.Id == ap)
            ? $"Only the autopilot itself (node {ap}) is visible on CAN{session.Bus}. A device wired to the other connector appears only on bus {other}."
            : null;
    return new {
      targetId, bus = session.Bus, autopilotNodeId = session.AutopilotNodeId, sessionAgeSeconds = Math.Round(session.Age.TotalSeconds, 1),
      nodes = nodes.Select(n => n.ToResult()).ToArray(), hint,
      note = "Node ids come from the autopilot's dynamic allocation (CAN_NODE=0) or a fixed CAN_NODE; the uid identifies the physical board. "
          + "Names arrive from GetNodeInfo and may lag NodeStatus by a second.",
    };
  }

  internal async Task<object> ReadParameters(string targetId, int bus, int nodeId, CancellationToken ct) {
    var session = GetOrOpen(targetId, bus);
    byte node = session.RequireNode(nodeId);
    var (parameters, received) = await session.ReadAsync(node, ct).ConfigureAwait(false);
    return new {
      targetId, bus = session.Bus, nodeId = node, node = session.Snapshot().FirstOrDefault(n => n.Id == node)?.ToResult(),
      received, count = parameters.Count,
      parameters = parameters.Select(p => p.ToResult()).ToArray(),
      note = received ? "Values are live from uavcan.protocol.param.GetSet. Ranges are the node's own min/max when it reports them."
          : "The node did not answer the parameter service within the timeout; it may not support parameters or the bus is busy.",
    };
  }

  internal async Task<object> WriteParameters(string targetId, int bus, int nodeId, McpDroneCanWrite[] changes, string reason,
      bool saveToFlash, bool restart, CancellationToken ct) {
    if (changes == null || changes.Length is < 1 or > 50 || changes.Any(c => c == null || string.IsNullOrWhiteSpace(c.Name))
        || changes.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != changes.Length) {
      throw new ArgumentException("Provide 1..50 unique parameter changes.");
    }
    if (reason == null || reason.Length is < 5 or > 8000) { throw new ArgumentException("Give a reason of 5..8000 characters; it is written to the audit file."); }
    var session = GetOrOpen(targetId, bus);
    byte node = session.RequireNode(nodeId);
    void Validate() {
      _vehicles.Resolve(targetId, true);
      McpVehicleAccess.RequireDisarmed(session.Target);
      if (session.Target.Connection.Link.ReadOnly) { throw new InvalidOperationException("The connection is read only."); }
      if (!session.IsCurrent()) { throw new InvalidOperationException("The DroneCAN session ended; call dronecan_nodes again."); }
    }
    Validate();
    await McpProposalWriter.Gate.WaitAsync(ct).ConfigureAwait(false);
    try {
      var (before, received) = await session.ReadAsync(node, ct).ConfigureAwait(false);
      if (!received) { throw new InvalidOperationException($"Node {node} did not answer the parameter service; nothing was written."); }
      var planned = changes.Select(c => {
        var current = before.FirstOrDefault(p => p.Name == c.Name) ?? throw new ArgumentException($"Node {node} has no parameter {c.Name}.");
        if (McpVehicleAccess.Sensitive(c.Name)) { throw new ArgumentException($"{c.Name} looks like a credential and is not written by agents."); }
        object value = ParseValue(current, c.Value);
        if (c.Expected is JsonElement expected && !SameValue(current, ParseValue(current, expected))) {
          throw new InvalidOperationException($"{c.Name} is {current.Value} on the node, not the expected value. Read it again before retrying.");
        }
        return (Parameter: current, Value: value);
      }).ToArray();
      string directory = Path.Combine(AppPaths.StateRoot, "agent-dronecan-audit");
      Directory.CreateDirectory(directory);
      string id = Guid.NewGuid().ToString("N"), prefix = Path.Combine(directory, id);
      await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(new { id, targetId, bus = session.Bus, nodeId = node,
        nodeName = session.Snapshot().FirstOrDefault(n => n.Id == node)?.Name, reason, saveToFlash, restart, createdUtc = DateTime.UtcNow,
        changes = planned.Select(p => new { name = p.Parameter.Name, previous = p.Parameter.Value, value = p.Value }) },
        new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
      await File.WriteAllLinesAsync(prefix + "-before.param", before.Select(p => p.Name + "," + Convert.ToString(p.Value, CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
      var results = new List<object>();
      int applied = 0; string? failure = null;
      foreach (var (parameter, value) in planned) {
        try {
          ct.ThrowIfCancellationRequested(); Validate();
          bool acknowledged = await Task.Run(() => session.Can.SetParameter(node, parameter.Name, value, parameter.Type), ct).ConfigureAwait(false);
          if (!acknowledged) { throw new InvalidOperationException($"{parameter.Name}: the node did not acknowledge the write."); }
          applied++;
          results.Add(new { name = parameter.Name, previous = parameter.Value, value, status = "acknowledged" });
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException or TimeoutException) {
          failure = e.Message; results.Add(new { name = parameter.Name, previous = parameter.Value, value, status = "failed: " + e.Message });
          break;
        }
      }
      var (after, _) = await session.ReadAsync(node, ct).ConfigureAwait(false);
      var verified = planned.Take(applied).Select(p => {
        var now = after.FirstOrDefault(a => a.Name == p.Parameter.Name);
        return new { name = p.Parameter.Name, value = now?.Value, verified = now != null && SameValue(now, p.Value) };
      }).ToArray();
      bool saved = false, restarted = false;
      if (failure == null && saveToFlash) { saved = await Task.Run(() => session.Can.SaveConfig(node), ct).ConfigureAwait(false); }
      if (failure == null && restart) { restarted = await Task.Run(() => session.Can.RestartNode(node), ct).ConfigureAwait(false); }
      await File.WriteAllLinesAsync(prefix + "-result.txt", results.Select(r => JsonSerializer.Serialize(r))
          .Concat(verified.Select(v => JsonSerializer.Serialize(v))), CancellationToken.None).ConfigureAwait(false);
      return new { targetId, bus = session.Bus, nodeId = node, auditId = id, applied, total = planned.Length, completed = failure == null, error = failure,
        results, verified, savedToFlash = saved, restartRequested = restarted,
        note = (saveToFlash ? "Values were committed with uavcan.protocol.param.ExecuteOpcode SAVE. " : "Values live in RAM until saved to flash. ")
            + "Some AP_Periph settings (CAN_NODE, OUTx_FUNCTION, GPS/compass drivers) apply after a node restart. Before snapshot: " + prefix + "-before.param" };
    } finally { McpProposalWriter.Gate.Release(); }
  }

  internal async Task<object> NodeCommand(string targetId, int bus, int nodeId, string command, string reason, CancellationToken ct) {
    if (reason == null || reason.Length is < 5 or > 8000) { throw new ArgumentException("Give a reason of 5..8000 characters."); }
    // Argument errors come before vehicle-state errors, as in WriteParameters: an agent fixing
    // its call must not be told about stale telemetry or an armed vehicle first.
    string action = (command ?? "").Trim().ToLowerInvariant();
    if (action is not ("save" or "restart" or "erase")) { throw new ArgumentException("command must be save, restart or erase."); }
    var session = GetOrOpen(targetId, bus);
    byte node = session.RequireNode(nodeId);
    _vehicles.Resolve(targetId, true);
    McpVehicleAccess.RequireDisarmed(session.Target);
    if (session.Target.Connection.Link.ReadOnly) { throw new InvalidOperationException("The connection is read only."); }
    bool ok = action switch {
      "save" => await Task.Run(() => session.Can.SaveConfig(node), ct).ConfigureAwait(false),
      "restart" => await Task.Run(() => session.Can.RestartNode(node), ct).ConfigureAwait(false),
      "erase" => await Task.Run(() => session.Can.ExecuteOpCode(node,
          (byte)DroneCAN.DroneCAN.uavcan_protocol_param_ExecuteOpcode_req.UAVCAN_PROTOCOL_PARAM_EXECUTEOPCODE_REQ_OPCODE_ERASE), ct).ConfigureAwait(false),
      _ => throw new ArgumentException("command must be save, restart or erase."),
    };
    return new { targetId, bus = session.Bus, nodeId = node, command = action, acknowledged = ok,
      note = action == "erase" ? "Parameters return to defaults after the node restarts." : action == "restart"
          ? "The node drops off the bus for a few seconds and may receive a new dynamic id only if CAN_NODE is 0 and the allocation table was cleared."
          : "Saved to the node's non-volatile storage." };
  }

  internal static object ParseValue(McpDroneCanParameter parameter, JsonElement json) {
    if (parameter.Type == DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_STRING_VALUE) {
      if (json.ValueKind != JsonValueKind.String) { throw new ArgumentException($"{parameter.Name} is a string parameter."); }
      string text = json.GetString() ?? "";
      if (text.Length > 128 || text.Any(ch => ch > 127)) { throw new ArgumentException($"{parameter.Name}: strings are ASCII, at most 128 characters."); }
      return text;
    }
    double number = json.ValueKind switch {
      JsonValueKind.Number => json.GetDouble(),
      JsonValueKind.True => 1, JsonValueKind.False => 0,
      JsonValueKind.String when double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
      _ => throw new ArgumentException($"{parameter.Name}: value must be a number."),
    };
    if (double.IsNaN(number) || double.IsInfinity(number)) { throw new ArgumentException($"{parameter.Name}: value must be finite."); }
    if (parameter.Type == DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_INTEGER_VALUE
        && Math.Abs(number - Math.Round(number)) > 1e-9) { throw new ArgumentException($"{parameter.Name} is an integer parameter."); }
    if (parameter.Type == DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_BOOLEAN_VALUE
        && number is not (0 or 1)) { throw new ArgumentException($"{parameter.Name} is a boolean parameter (0 or 1)."); }
    if (parameter.Min is double min && number < min) { throw new ArgumentException($"{parameter.Name}: {number} is below the node's minimum {min}."); }
    if (parameter.Max is double max && number > max) { throw new ArgumentException($"{parameter.Name}: {number} is above the node's maximum {max}."); }
    return number;
  }

  internal static bool SameValue(McpDroneCanParameter parameter, object value) {
    if (parameter.Value is string text) { return value is string other && text == other; }
    double current = Convert.ToDouble(parameter.Value, CultureInfo.InvariantCulture);
    double wanted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
    bool real = parameter.Type == DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_REAL_VALUE;
    return real ? (float)current == (float)wanted : current == wanted;
  }

  internal static string TypeName(DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t type) => type switch {
    DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_INTEGER_VALUE => "integer",
    DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_REAL_VALUE => "real",
    DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_BOOLEAN_VALUE => "boolean",
    DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_STRING_VALUE => "string",
    _ => "empty",
  };

  internal static McpDroneCanParameter Describe(DroneCAN.DroneCAN.uavcan_protocol_param_GetSet_res response) {
    static double? Numeric(object? value) => value is string or null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    object raw = response.value.GetValue() ?? "";
    object value = raw is string s ? s : raw is bool b ? (b ? 1 : 0) : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
    return new McpDroneCanParameter(Encoding.ASCII.GetString(response.name, 0, response.name_len), value,
        response.value.uavcan_protocol_param_Value_type, Numeric(response.min_value.GetValue()), Numeric(response.max_value.GetValue()),
        response.default_value.GetValue() is string or null ? null : Numeric(response.default_value.GetValue()));
  }

  /// <summary>One bus session: the DroneCAN stack, the shared tunnel lease and what the bus reported so far.</summary>
  private sealed class Session : IDisposable {
    private readonly ConcurrentDictionary<byte, McpDroneCanNode> _nodes = new();
    private readonly ConcurrentDictionary<byte, DateTime> _infoRequests = new();
    private readonly DateTime _openedUtc = DateTime.UtcNow;
    private long _lastUsedTicks = DateTime.UtcNow.Ticks;
    private MavlinkCanTransport? _transport;
    private volatile bool _disposed;

    internal Session(McpTarget target, byte bus) { Target = target; Bus = bus; }

    internal McpTarget Target { get; }
    internal byte Bus { get; }
    internal DroneCAN.DroneCAN Can { get; private set; } = null!;
    internal int? AutopilotNodeId { get; private set; }
    internal DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
    internal TimeSpan Age => DateTime.UtcNow - _openedUtc;

    internal void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
    internal bool IsCurrent() => !_disposed && Target.Connection.IsOpen && Target.Connection.Generation == Target.Generation;

    internal void Open(Func<McpTarget, byte, Func<bool>, MavlinkCanTransport?> openTransport, Func<DroneCAN.DroneCAN> canFactory) {
      Can = canFactory();
      Can.SourceNode = AgentSourceNode;
      AutopilotNodeId = ConfigDroneCanViewModel.AutopilotNodeId(new DroneCanSessionTarget(Target.Connection.Link, Target.State.sysid, Target.State.compid), Bus);
      Can.NodeAdded += (id, status) => { if (id != Can.SourceNode) { Upsert(id, status); } };
      Can.MessageReceived += (frame, msg, _) => {
        if (frame.SourceNode == Can.SourceNode) { return; }
        if (msg is DroneCAN.DroneCAN.uavcan_protocol_NodeStatus status) { Upsert(frame.SourceNode, status); RequestInfo(frame.SourceNode); }
        else if (msg is DroneCAN.DroneCAN.uavcan_protocol_GetNodeInfo_res info && _nodes.TryGetValue(frame.SourceNode, out var node)) { node.Apply(info); }
      };
      _transport = openTransport(Target, Bus, IsCurrent) ?? throw new InvalidOperationException("The MAVLink CAN tunnel is busy.");
      Can.StartSLCAN(_transport.Port.BaseStream);
    }

    private void Upsert(byte id, DroneCAN.DroneCAN.uavcan_protocol_NodeStatus status) {
      var node = _nodes.GetOrAdd(id, key => new McpDroneCanNode(key) { IsAutopilot = key == AutopilotNodeId });
      node.Apply(status);
    }

    private void RequestInfo(byte node) {
      if (Can.NodeInfo.ContainsKey(node)) { return; }
      DateTime now = DateTime.UtcNow;
      if (now - _infoRequests.GetOrAdd(node, DateTime.MinValue) < TimeSpan.FromSeconds(2)) { return; }
      _infoRequests[node] = now;
      try { Can.WriteToStreamSLCAN(Can.PackageMessageSLCAN(node, 30, Can.TransferID++, new DroneCAN.DroneCAN.uavcan_protocol_GetNodeInfo_req())); } catch { }
    }

    internal List<McpDroneCanNode> Snapshot() => _nodes.Values.OrderBy(n => n.Id).ToList();

    internal byte RequireNode(int nodeId) {
      Touch();
      if (nodeId is < 1 or > 127) { throw new ArgumentException("nodeId must be 1..127."); }
      if (!_nodes.ContainsKey((byte)nodeId)) { throw new ArgumentException($"Node {nodeId} has not been seen on CAN{Bus}; call dronecan_nodes first."); }
      return (byte)nodeId;
    }

    internal async Task<(List<McpDroneCanParameter> Parameters, bool Received)> ReadAsync(byte node, CancellationToken ct) {
      Touch();
      var result = await Task.Run(() => {
        var list = Can.GetParameters(node, out bool received);
        return (list.Select(Describe).Where(p => p.Name.Length != 0).ToList(), received);
      }, ct).ConfigureAwait(false);
      Touch();
      return result;
    }

    public void Dispose() {
      if (_disposed) { return; }
      _disposed = true;
      try { Can?.Stop(false); } catch { }
      try { _transport?.Dispose(); } catch { }
    }
  }
}

internal sealed record McpDroneCanParameter(string Name, object Value,
    DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t Type, double? Min, double? Max, double? Default) {
  internal object ToResult() => new { name = Name, value = Value, type = McpDroneCanAccess.TypeName(Type), min = Min, max = Max, @default = Default };
}

internal sealed class McpDroneCanNode(byte id) {
  internal byte Id { get; } = id;
  internal string Name { get; private set; } = "?";
  internal bool IsAutopilot { get; init; }
  internal string Health { get; private set; } = "";
  internal string Mode { get; private set; } = "";
  internal uint UptimeSeconds { get; private set; }
  internal string SoftwareVersion { get; private set; } = "";
  internal uint VcsCommit { get; private set; }
  internal string HardwareVersion { get; private set; } = "";
  internal string Uid { get; private set; } = "";
  internal DateTime LastSeenUtc { get; private set; }

  internal void Apply(DroneCAN.DroneCAN.uavcan_protocol_NodeStatus status) {
    Health = status.health switch { 0 => "OK", 1 => "WARNING", 2 => "ERROR", 3 => "CRITICAL", _ => status.health.ToString() };
    Mode = status.mode switch { 0 => "OPERATIONAL", 1 => "INITIALIZATION", 2 => "MAINTENANCE", 3 => "SOFTWARE_UPDATE", 7 => "OFFLINE", _ => status.mode.ToString() };
    UptimeSeconds = status.uptime_sec;
    LastSeenUtc = DateTime.UtcNow;
  }

  internal void Apply(DroneCAN.DroneCAN.uavcan_protocol_GetNodeInfo_res info) {
    Name = Encoding.ASCII.GetString(info.name, 0, info.name_len);
    SoftwareVersion = info.software_version.major + "." + info.software_version.minor;
    VcsCommit = info.software_version.vcs_commit;
    HardwareVersion = info.hardware_version.major + "." + info.hardware_version.minor;
    Uid = string.Concat(info.hardware_version.unique_id.Select(b => b.ToString("x2")));
  }

  internal object ToResult() => new { nodeId = Id, name = Name, isAutopilot = IsAutopilot, health = Health, mode = Mode, uptimeSeconds = UptimeSeconds,
    softwareVersion = SoftwareVersion, vcsCommit = VcsCommit == 0 ? null : VcsCommit.ToString("x8"), hardwareVersion = HardwareVersion, uid = Uid,
    lastSeenUtc = LastSeenUtc == default ? (DateTime?)null : LastSeenUtc };
}
