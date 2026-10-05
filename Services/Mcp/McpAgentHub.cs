using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner.Utilities;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Services.Mcp;

/// <summary>
/// Application-wide owner of the MCP listeners, launches and sessions. The AI window is only a view of
/// this hub: closing it keeps every agent connected, like hiding X-Office's assistant panel.
/// </summary>
internal sealed class McpAgentHub : IAsyncDisposable {
  internal static McpAgentHub? Current { get; private set; }
  internal static McpAgentHub Attach(MainWindowViewModel main, Func<Window?> owner) => Current ??= new(main, owner);

  private readonly MainWindowViewModel _main;
  private readonly Func<CancellationToken, Task<McpAgent[]>> _discover;
  private readonly List<McpTerminalLaunch> _terminalLaunches = new();
  private readonly CancellationTokenSource _stop = new();
  private readonly StringBuilder _log = new();
  private readonly object _logSync = new();
  private CancellationTokenSource _localOperationsStop = new();
  private readonly object _listeners = new();
  private int _accessGeneration, _disposed;
  private long _traffic;

  internal McpVehicleAccess Vehicles { get; } = new(() => AppState.Connections.Snapshot());
  internal McpLogCatalog Logs { get; } = new();
  internal McpUiHost Ui { get; }
  internal MissionPlannerMcpServer? SessionServer { get; private set; }
  internal MissionPlannerMcpServer? DesktopServer { get; private set; }
  internal IEnumerable<MissionPlannerMcpServer> Servers => new[] { SessionServer, DesktopServer }.OfType<MissionPlannerMcpServer>();
  internal McpAgent[] Agents { get; private set; } = [];
  internal string DesktopState { get; private set; } = "Persistent port closed.";
  internal CancellationToken Stopping => _stop.Token;
  /// <summary>Raised on any thread after a listener, launch or grant changes.</summary>
  internal event Action? StateChanged;
  /// <summary>Raised on the request thread after every MCP HTTP exchange; drives the activity indicator.</summary>
  internal event Action? Traffic;
  /// <summary>Human-readable activity text (any thread).</summary>
  internal event Action<string>? Output;
  internal long TrafficCount => Interlocked.Read(ref _traffic);

  internal McpAgentHub(MainWindowViewModel main, Func<Window?> owner, Func<CancellationToken, Task<McpAgent[]>>? discover = null) {
    _main = main; _discover = discover ?? McpAgentDiscovery.DiscoverAsync;
    Ui = new(main, Logs, owner);
    if (int.TryParse(Settings.Instance["mcpDesktopPort"], out int saved) && saved is >= 1024 and <= 65535) { DesktopPort = saved; }
  }

  private int _desktopPort = McpDesktopRegistration.DefaultPort;
  internal int DesktopPort {
    get => _desktopPort;
    set {
      if (value is < 1024 or > 65535) { throw new ArgumentOutOfRangeException(nameof(value), "Choose a port between 1024 and 65535."); }
      if (DesktopServer != null && value != _desktopPort) { throw new InvalidOperationException("Close the persistent port before changing it."); }
      _desktopPort = value;
    }
  }

  /// <summary>Registered desktop applications connect only while the persistent port is open, so a registration asks for it at startup.</summary>
  internal bool AutoOpenDesktopPort {
    get => Settings.Instance.GetBoolean("mcpDesktopAutoOpen", false);
    set => Settings.Instance["mcpDesktopAutoOpen"] = value ? "True" : "False";
  }
  internal async Task OpenDesktopPortAtStartupAsync() {
    if (!AutoOpenDesktopPort || _stop.IsCancellationRequested) { return; }
    try { await OpenDesktopPortAsync().ConfigureAwait(false); }
    catch (Exception e) when (e is not OperationCanceledException) {
      DesktopState = "Persistent port could not be opened at startup: " + e.Message;
      Log(DesktopState + Environment.NewLine); Changed();
    }
  }

  /// <summary>True while at least one initialized session holds a grant.</summary>
  internal bool IsConnected() => !_stop.IsCancellationRequested && Servers.Any(s => s.Sessions.Any(i => i.Ready && i.Access == "Allowed"));
  internal int SessionCount() => Servers.Sum(s => s.Sessions.Count(i => !i.Access.StartsWith("Disconnected", StringComparison.Ordinal)));
  internal string RecentOutput { get { lock (_logSync) { return _log.ToString(); } } }

  internal CancellationToken LocalOperationToken {
    get {
      if (_localOperationsStop.IsCancellationRequested) { _localOperationsStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); }
      return _localOperationsStop.Token;
    }
  }

  private void Log(string message) {
    lock (_logSync) {
      _log.Append(message);
      if (_log.Length > 65536) { _log.Remove(0, _log.Length - 65536); }
    }
    Output?.Invoke(message);
  }
  private void Changed() => StateChanged?.Invoke();

  internal async Task<McpAgent[]> DiscoverAsync(CancellationToken ct = default) {
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
    var agents = await Task.Run(() => _discover(linked.Token), linked.Token).ConfigureAwait(false);
    Agents = agents; Changed();
    return agents;
  }

  private MissionPlannerMcpServer CreateServer(int port, bool requiresToken) {
    var server = new MissionPlannerMcpServer(Vehicles, Logs, port, requiresToken) { UiHost = Ui, OpenLogAnalyzer = Ui.OpenPath };
    server.Activity += text => {
      Interlocked.Increment(ref _traffic);
      Log((requiresToken ? "CLI: " : "Desktop: ") + text + Environment.NewLine);
      Traffic?.Invoke();
    };
    return server;
  }

  private async Task<object> ReadMissionAsync(CancellationToken ct) => await Dispatcher.UIThread.InvokeAsync(() => {
    ct.ThrowIfCancellationRequested();
    return (object)new { source = "Mission Planner UI draft", activeSystemId = AppState.comPort.MAV.sysid,
      activeComponentId = AppState.comPort.MAV.compid, homeLatitude = _main.FlightPlanner.HomeLat,
      homeLongitude = _main.FlightPlanner.HomeLng, homeAltitude = _main.FlightPlanner.HomeAlt,
      waypoints = _main.FlightPlanner.Waypoints.Take(10000).Select(w => new {
        sequence = w.Seq, command = w.Command, frame = w.Frame, latitude = w.Lat, longitude = w.Lng,
        altitude = w.Alt, p1 = w.P1, p2 = w.P2, p3 = w.P3, p4 = w.P4,
      }).ToArray() };
  });

  /// <summary>Opens the token-protected ephemeral listener used by terminal agents and manual clients.</summary>
  internal async Task<MissionPlannerMcpServer> OpenSessionPortAsync() {
    MissionPlannerMcpServer server;
    lock (_listeners) {
      ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
      if (SessionServer is { Endpoint: not null } ready && !ready.Stopping.IsCancellationRequested) { return ready; }
      server = SessionServer ??= CreateServer(0, true);
    }
    try {
      await server.StartAsync(ReadMissionAsync, _stop.Token).ConfigureAwait(false);
      if (SessionServer != server || server.Stopping.IsCancellationRequested) { throw new OperationCanceledException(); }
      Log("Session port open at " + server.Endpoint!.AbsoluteUri + Environment.NewLine);
      Changed(); return server;
    } catch { lock (_listeners) { if (SessionServer == server) { SessionServer = null; } } await server.DisposeAsync().ConfigureAwait(false); throw; }
  }

  /// <summary>Opens the fixed, tokenless loopback port used by registered desktop applications.</summary>
  internal async Task<MissionPlannerMcpServer> OpenDesktopPortAsync() {
    MissionPlannerMcpServer server;
    int generation, port;
    lock (_listeners) {
      ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
      if (DesktopServer is { Endpoint: not null } ready && !ready.Stopping.IsCancellationRequested) { return ready; }
      generation = _accessGeneration; port = DesktopPort;
      server = DesktopServer ??= CreateServer(port, false);
    }
    try {
      await server.StartAsync(ReadMissionAsync, _stop.Token).ConfigureAwait(false);
      if (DesktopServer != server || generation != _accessGeneration) { throw new OperationCanceledException(); }
      Settings.Instance["mcpDesktopPort"] = port.ToString(CultureInfo.InvariantCulture);
      DesktopState = "Port open. Self-connected sessions start read only until Allow.";
      Log("Persistent port open at " + server.Endpoint!.AbsoluteUri + Environment.NewLine);
      Changed(); return server;
    } catch {
      lock (_listeners) { if (DesktopServer == server) { DesktopServer = null; } }
      await server.DisposeAsync().ConfigureAwait(false); throw;
    }
  }

  internal async Task CloseDesktopPortAsync() {
    MissionPlannerMcpServer? server;
    lock (_listeners) {
      _accessGeneration++;
      server = DesktopServer; DesktopServer = null;
    }
    server?.RevokeAccess();
    DesktopState = "Persistent port closed. Registration retained; the desktop app remains open.";
    Changed();
    if (server != null) { await server.DisposeAsync().ConfigureAwait(false); }
  }

  internal async Task<string?> RegisterDesktopAsync(McpAgent agent) {
    if (!agent.IsDesktop) { throw new InvalidOperationException("Select a desktop application in the agent list."); }
    int port = DesktopPort;
    string? backup = await Task.Run(() => McpDesktopRegistration.Update(agent.Kind, port)).ConfigureAwait(false);
    Settings.Instance["mcpDesktopPort"] = port.ToString(CultureInfo.InvariantCulture);
    AutoOpenDesktopPort = true;
    if (backup != null) { Log("Client configuration backup: " + backup + Environment.NewLine); }
    Changed(); return backup;
  }

  internal async Task<string?> UnregisterDesktopAsync(McpAgent agent) {
    if (!agent.IsDesktop) { throw new InvalidOperationException("Select a desktop application in the agent list."); }
    await CloseDesktopPortAsync().ConfigureAwait(false);
    string? backup = await Task.Run(() => McpDesktopRegistration.Update(agent.Kind, null)).ConfigureAwait(false);
    if (backup != null) { Log("Client configuration backup: " + backup + Environment.NewLine); }
    Changed(); return backup;
  }

  /// <summary>
  /// Launches an installed agent. Terminal agents get a free token-protected port and are fully allowed
  /// as soon as they connect; desktop applications are registered, the fixed port opens without a token,
  /// existing and future sessions on it are allowed, and the application is activated.
  /// </summary>
  internal async Task LaunchAsync(McpAgent agent, string workingDirectory, string prompt) {
    ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
    if (agent.IsDesktop) { await LaunchDesktopAsync(agent).ConfigureAwait(false); return; }
    if (!File.Exists(agent.Executable)) { throw new InvalidOperationException("Find agents or select an installed executable first."); }
    int generation = _accessGeneration;
    var server = await OpenSessionPortAsync().ConfigureAwait(false);
    if (server.Endpoint == null || generation != _accessGeneration) { throw new OperationCanceledException(); }
    string token = server.IssueLaunchToken();
    McpTerminalLaunch? launch = null;
    try {
      if (_terminalLaunches.Count >= 64) { throw new InvalidOperationException("Stop all connections before launching more agents."); }
      launch = new(agent, server.Endpoint, token, workingDirectory, prompt);
      lock (_terminalLaunches) { _terminalLaunches.Add(launch); }
      await launch.LaunchAsync(server.Stopping).ConfigureAwait(false);
      Log($"Launched {agent.Name} in a terminal; its session is allowed on connection.{Environment.NewLine}");
      Changed();
    } catch {
      server.CancelLaunch(token);
      if (launch != null) { lock (_terminalLaunches) { _terminalLaunches.Remove(launch); } launch.Dispose(); }
      throw;
    }
  }

  private async Task LaunchDesktopAsync(McpAgent agent) {
    int generation = _accessGeneration;
    if (McpDesktopRegistration.RegistrationState(agent.Kind, DesktopPort) != "Registered") {
      await RegisterDesktopAsync(agent).ConfigureAwait(false);
    }
    if (_stop.IsCancellationRequested || generation != _accessGeneration) { throw new OperationCanceledException(); }
    var server = await OpenDesktopPortAsync().ConfigureAwait(false);
    if (generation != _accessGeneration) { throw new OperationCanceledException(); }
    server.GrantDesktopLaunch();
    try {
      await McpAgentDiscovery.LaunchDesktopAsync(agent, server.Stopping).ConfigureAwait(false);
      if (DesktopServer == server) { DesktopState = $"{agent.Name} launched. Its sessions are allowed until Revoke or Close."; }
      Log($"Launched {agent.Name}; sessions on port {DesktopPort} are allowed.{Environment.NewLine}");
      Changed();
    } catch { server.RevokeSessions(); Changed(); throw; }
  }

  internal (MissionPlannerMcpServer Server, McpConnectionInfo Info)[] SessionRows() =>
      Servers.SelectMany(server => server.Sessions.Select(info => (server, info))).ToArray();
  private MissionPlannerMcpServer? ServerOf(string sessionId) => Servers.FirstOrDefault(s => s.Sessions.Any(i => i.Id == sessionId));
  internal void AllowSession(string id) { ServerOf(id)?.AllowSession(id); Changed(); }
  internal void RevokeSession(string id) { ServerOf(id)?.RevokeSession(id); Changed(); }
  internal void DisconnectSession(string id) { ServerOf(id)?.DisconnectSession(id); Changed(); }
  internal void AllowAll() {
    foreach (var server in Servers) { foreach (var session in server.Sessions) { server.AllowSession(session.Id); } }
    Changed();
  }
  internal void RevokeAll() {
    foreach (var server in Servers) { server.RevokeSessions(); }
    Changed();
  }

  /// <summary>Revokes every listener synchronously before awaiting any disposal, including blocked requests.</summary>
  internal async Task StopAllAsync() {
    MissionPlannerMcpServer[] servers;
    lock (_listeners) {
      _accessGeneration++;
      servers = Servers.ToArray();
      SessionServer = DesktopServer = null;
    }
    foreach (var server in servers) { server.RevokeAccess(); }
    _localOperationsStop.Cancel();
    DesktopState = "All MCP connections closed. Registration retained; external agents remain open.";
    McpTerminalLaunch[] launches;
    lock (_terminalLaunches) { launches = _terminalLaunches.ToArray(); _terminalLaunches.Clear(); }
    foreach (var launch in launches) { launch.Dispose(); }
    Changed();
    await Task.WhenAll(servers.Select(server => server.DisposeAsync().AsTask())).ConfigureAwait(false);
    Log("All MCP connections closed; session credentials revoked." + Environment.NewLine);
    Changed();
  }

  /// <summary>
  /// Teardown starts on a worker thread. Cancelling on the UI thread runs cancellation callbacks there, and
  /// the request continuations they resume then capture the UI SynchronizationContext; application exit blocks
  /// the UI thread while waiting for this task, so those continuations would never run and exit hit its 5 s cap.
  /// </summary>
  public ValueTask DisposeAsync() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) { return ValueTask.CompletedTask; }
    return new(Task.Run(async () => {
      _stop.Cancel();
      try { await StopAllAsync().ConfigureAwait(false); Logs.Dispose(); }
      catch (Exception e) { Log(e.Message + Environment.NewLine); }
      if (Current == this) { Current = null; }
    }));
  }
}
