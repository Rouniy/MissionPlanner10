using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MissionPlanner.Services.Mcp;

internal sealed class MissionPlannerMcpServer : IAsyncDisposable {
  private WebApplication? _app;
  private readonly SemaphoreSlim _lifecycle = new(1, 1);
  private readonly CancellationTokenSource _stop = new();
  private int _stopped;
  private readonly ConcurrentDictionary<string, McpConnectionSession> _sessions = new();
  private readonly ConcurrentDictionary<string, DateTime> _launchTokens = new();
  private readonly object _admission = new();
  private bool _desktopLaunched;
  private Task? _disposeTask;
  private readonly int _port;
  private readonly bool _ownsLogs;
  internal bool RequiresToken { get; }
  internal Uri? Endpoint { get; private set; }
  internal string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  internal McpVehicleAccess Vehicles { get; }
  internal McpLogCatalog Logs { get; }
  internal CancellationToken Stopping => _stop.Token;
  internal event Action<string>? Activity;
  internal McpUiHost? UiHost { get; init; }
  internal Func<string, CancellationToken, Task>? OpenLogAnalyzer { get; init; }
  internal McpConnectionInfo[] Sessions => _sessions.Values.Select(s => s.Info).OrderBy(s => s.Id).ToArray();
  internal string IssueLaunchToken() {
    lock (_admission) {
      ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
      foreach (var pair in _launchTokens.Where(p => p.Value < DateTime.UtcNow)) { _launchTokens.TryRemove(pair.Key, out _); }
      if (_launchTokens.Count >= 64) { throw new InvalidOperationException("Too many pending agent launches."); }
      string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
      _launchTokens[token] = DateTime.UtcNow.AddMinutes(5); return token;
    }
  }
  internal void CancelLaunch(string token) => _launchTokens.TryRemove(token, out _);
  internal void GrantDesktopLaunch() {
    lock (_admission) {
      ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
      _desktopLaunched = true;
      foreach (var session in _sessions.Values) { session.Allow(); }
    }
  }
  internal void AllowSession(string id) { if (_sessions.TryGetValue(id, out var session)) { session.Allow(); } }
  internal void RevokeSession(string id) { if (_sessions.TryGetValue(id, out var session)) { session.Revoke(); } }
  internal void DisconnectSession(string id) { if (_sessions.TryGetValue(id, out var session)) { session.Disconnect(); } }
  internal void RevokeSessions() {
    lock (_admission) {
      _desktopLaunched = false; _launchTokens.Clear();
      foreach (var session in _sessions.Values) { session.Revoke(); }
    }
  }
  // Close admission on EVERY listener before awaiting any disposal. Safe during a concurrent start.
  internal void RevokeAccess() {
    lock (_admission) {
      _stop.Cancel(); _launchTokens.Clear(); _desktopLaunched = false;
      foreach (var session in _sessions.Values) { session.Disconnect(); }
    }
  }
  internal static bool IsPassiveTool(string? name) => name is
      "diagnostics_info" or "vehicle_health" or "log_overview" or "log_parameters_at" or "log_vibration_report"
      or "list_vehicles" or "telemetry_schema" or "read_telemetry" or "read_parameters" or "read_mission_draft"
      or "list_local_logs" or "log_schema" or "read_log_records" or "log_field_statistics" or "log_spectrum"
      or "log_batch_spectrum" or "log_response" or "parameter_proposals" or "read_vehicle_messages"
      or "telemetry_packet_inventory" or "log_events" or "log_time_series" or "compare_log_parameters"
      or "compare_vehicle_parameters_to_log" or "mission_draft_get" or "mission_command_schema" or "mission_draft_validate"
      or "vehicle_modes" or "terrain_elevation";
  private McpConnectionSession SessionFor(McpServer server) => server.SessionId is string id && _sessions.TryGetValue(id, out var session)
      ? session : throw new McpException("Unknown or disconnected session.");

  private async Task RunSessionAsync(HttpContext context, McpServer server, CancellationToken ct) {
    // Capture request data before RunAsync; HttpContext belongs to initialize only.
    string credential = context.Request.Headers.Authorization.ToString();
    McpConnectionSession session;
    lock (_admission) {
      _stop.Token.ThrowIfCancellationRequested();
      if (_sessions.Count >= 64 || server.SessionId == null) { throw new McpException("Session limit reached."); }
      bool launched = credential.StartsWith("Bearer ", StringComparison.Ordinal)
          && _launchTokens.TryRemove(credential[7..], out DateTime expires) && expires >= DateTime.UtcNow;
      // Middleware admission can race another initialize consuming the same one-use token.
      if (RequiresToken && !launched && credential != "Bearer " + Token) { throw new McpException("Launch credential expired or already used."); }
      session = new(server, credential, RequiresToken ? "CLI HTTP" : "Persistent HTTP", !RequiresToken,
          launched || (!RequiresToken && _desktopLaunched));
      if (!_sessions.TryAdd(server.SessionId, session)) { throw new McpException("Duplicate session."); }
    }
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, session.Lifetime.Token);
    try { await server.RunAsync(lifetime.Token).ConfigureAwait(false); }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    finally { session.Disconnect(); _sessions.TryRemove(server.SessionId!, out _); }
  }

  internal MissionPlannerMcpServer(McpVehicleAccess vehicles, McpLogCatalog? logs = null,
      int port = 0, bool requiresToken = true) {
    if (port is < 0 or > 65535 || (!requiresToken && port < 1024)) {
      throw new ArgumentException("Desktop MCP requires an explicit local port between 1024 and 65535.");
    }
    Vehicles = vehicles; Logs = logs ?? new(); _ownsLogs = logs == null;
    _port = port; RequiresToken = requiresToken;
  }

  internal async Task StartAsync(Func<CancellationToken, Task<object>> mission, CancellationToken ct = default) {
    await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
    try {
      ObjectDisposedException.ThrowIf(_stopped != 0 || _stop.IsCancellationRequested, this);
      if (_app != null) { return; }
      var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {
        Args = [], ApplicationName = typeof(MissionPlannerMcpServer).Assembly.GetName().Name,
        ContentRootPath = AppContext.BaseDirectory,
      });
      builder.Configuration.Sources.Clear();
      builder.Configuration.AddInMemoryCollection();
      builder.Configuration["AllowedHosts"] = "127.0.0.1";
      builder.Logging.ClearProviders();
      builder.WebHost.ConfigureKestrel(options => {
        options.Listen(IPAddress.Loopback, _port);
        options.Limits.MaxRequestBodySize = 256 * 1024;
        options.Limits.MaxConcurrentConnections = 16;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
      });
      var toolInstance = new MissionPlannerMcpTools(Vehicles, Logs, mission, OpenLogAnalyzer);
      builder.Services.AddMcpServer(options => { options.ServerInstructions =
          "A session launched by Mission Planner is already allowed with full control of the application and the connected vehicle. If a tool returns permission_required, ask the operator once to Allow this session in AI → Connections. "
          + "Read resources/read at " + McpDocumentation.StartUri + " first, then tools/list for exact schemas. UI changes require operationId; draft/graph changes also require current revisions. "
          + MissionPlannerMcpTools.Instructions; })
          .WithHttpTransport(options => {
            // Explicit session ownership is required for per-client revocation. New clients negotiate the session protocol.
            options.SessionMode = HttpServerSessionMode.Stateful;
#pragma warning disable MCP9006 // Deliberate 2025-11-25 session protocol for visible, individually revocable clients.
            options.MaxIdleSessionCount = 64;
            options.IdleTimeout = TimeSpan.FromHours(2);
#pragma warning restore MCP9006
#pragma warning disable MCPEXP002 // SDK 2.2 lifecycle hook: needed to cancel and remove exactly one session.
            options.RunSessionHandler = RunSessionAsync;
#pragma warning restore MCPEXP002
          })
          .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) => {
            if (context.Server.SessionId is not string id || !_sessions.TryGetValue(id, out var session)
                || !session.TryAccess(IsPassiveTool(context.Params?.Name), out var grant)) {
              throw new McpException("permission_required: Allow this session in Mission Planner's AI Connections tab.");
            }
            using var access = CancellationTokenSource.CreateLinkedTokenSource(ct, grant, _stop.Token, session.Lifetime.Token);
            access.Token.ThrowIfCancellationRequested();
            var result = await next(context, access.Token).ConfigureAwait(false);
            access.Token.ThrowIfCancellationRequested();
            return result;
          }))
          .WithTools(toolInstance)
          .WithTools(new McpVehicleTools(Vehicles))
          .WithTools(new McpUiTools(UiHost, SessionFor))
          .WithResources<McpDocumentation>();
      var app = builder.Build();
      byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + Token));
      var requests = new SemaphoreSlim(4, 4);
      app.Use(async (context, next) => {
        if (_stop.IsCancellationRequested) { context.Response.StatusCode = 503; return; }
        if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != Endpoint?.Port
            || (context.Request.Headers.TryGetValue("Origin", out var origin)
                && origin.ToString() != Endpoint?.GetLeftPart(UriPartial.Authority))) {
          context.Response.StatusCode = 403; return;
        }
        string credential = context.Request.Headers.Authorization.ToString();
        byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        string sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
        bool sessionCredential = _sessions.TryGetValue(sessionId, out var bound)
            && !bound.Lifetime.IsCancellationRequested && CryptographicOperations.FixedTimeEquals(supplied,
                SHA256.HashData(Encoding.UTF8.GetBytes(bound.Credential)));
        bool launchCredential = credential.StartsWith("Bearer ", StringComparison.Ordinal)
            && _launchTokens.TryGetValue(credential[7..], out var expiry) && expiry >= DateTime.UtcNow;
        if ((sessionId.Length > 0 && !sessionCredential)
            || (RequiresToken && sessionId.Length == 0 && !launchCredential && !CryptographicOperations.FixedTimeEquals(expected, supplied))) {
          context.Response.StatusCode = 401; return;
        }
        // No long-lived GET/SSE stream: request/response MCP leaves capacity for cancellation and other clients.
        if (HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        // Session DELETE must be able to cancel work even when every tool slot is occupied.
        // Authentication above still applies, and Kestrel bounds total connections.
        bool usesRequestSlot = !HttpMethods.IsDelete(context.Request.Method);
        if (usesRequestSlot && !await requests.WaitAsync(0, context.RequestAborted).ConfigureAwait(false)) {
          context.Response.StatusCode = 429; return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(12));
        context.RequestAborted = lifetime.Token;
        try { await next(context).ConfigureAwait(false); }
        finally {
          if (usesRequestSlot) { requests.Release(); }
          Activity?.Invoke($"MCP {context.Request.Method} {context.Response.StatusCode}");
        }
      });
      app.MapMcp("/mcp");
      try {
        await app.StartAsync(ct).ConfigureAwait(false);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Endpoint = new Uri(address + "/mcp");
        _app = app;
      } catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    } finally { _lifecycle.Release(); }
  }

  public ValueTask DisposeAsync() {
    // Revocation and shutdown run on a worker so no cancellation callback or request continuation is bound
    // to the caller's SynchronizationContext (the UI thread may block on this task during application exit).
    lock (_admission) { return new(_disposeTask ??= Task.Run(async () => { RevokeAccess(); await DisposeCoreAsync().ConfigureAwait(false); })); }
  }

  private async Task DisposeCoreAsync() {
    if (Interlocked.Exchange(ref _stopped, 1) != 0) { return; }
    _stop.Cancel();
    await _lifecycle.WaitAsync().ConfigureAwait(false);
    try {
      if (_app != null) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
      }
      Endpoint = null;
      if (_ownsLogs) { await Task.Run(Logs.Dispose).ConfigureAwait(false); }
    } finally { _lifecycle.Release(); }
  }
}
