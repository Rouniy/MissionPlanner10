using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using MissionPlanner.Services.Mcp;
using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public sealed class McpSessionLifecycleTests {
  private static int Port() { var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); return port; }
  private sealed class Wire : IDisposable {
    private readonly Uri _uri;
    internal HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal string Id { get; private set; } = "";
    internal Wire(Uri uri, string? token = null) {
      _uri = uri; Http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
      Http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
      if (token != null) { Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
    }
    internal Task<HttpResponseMessage> Post(string json) => Http.PostAsync(_uri, new StringContent(json, Encoding.UTF8, "application/json"));
    internal async Task Initialize(string name) {
      using var response = await Post(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize",
        @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name, version = "1" } } }));
      response.EnsureSuccessStatusCode(); Id = response.Headers.GetValues("Mcp-Session-Id").Single();
      Http.DefaultRequestHeaders.Add("Mcp-Session-Id", Id);
      using var notified = await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
      notified.EnsureSuccessStatusCode();
    }
    internal async Task<string> Call(string name, object? arguments = null) {
      using var response = await Post(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name, arguments = arguments ?? new { } } }));
      response.EnsureSuccessStatusCode(); return await response.Content.ReadAsStringAsync();
    }
    public void Dispose() => Http.Dispose();
  }

  [Fact]
  public async Task Clients_have_independent_grants_and_disconnect_does_not_stop_the_listener() {
    await using var server = new MissionPlannerMcpServer(new(() => []), port: Port(), requiresToken: false);
    await server.StartAsync(_ => Task.FromResult<object>(new { evidence = "mission" }));
    using var first = new Wire(server.Endpoint!); using var second = new Wire(server.Endpoint!);
    await first.Initialize("First"); await second.Initialize("Second");
    Assert.NotEqual(first.Id, second.Id); Assert.Equal(2, server.Sessions.Length);
    Assert.Contains("mission", await first.Call("read_mission_draft"));
    Assert.Contains("permission_required", await first.Call("propose_parameter_changes"));
    server.AllowSession(first.Id);
    Assert.DoesNotContain("permission_required", await first.Call("propose_parameter_changes"));
    Assert.Contains("permission_required", await second.Call("propose_parameter_changes"));
    server.RevokeSession(first.Id);
    Assert.Contains("permission_required", await first.Call("read_mission_draft"));
    Assert.Contains("mission", await second.Call("read_mission_draft"));
    server.AllowSession(first.Id); Assert.Contains("mission", await first.Call("read_mission_draft"));
    server.DisconnectSession(first.Id);
    using var denied = await first.Post("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
    Assert.False(denied.IsSuccessStatusCode);
    Assert.Contains("mission", await second.Call("read_mission_draft"));
  }

  [Fact]
  public async Task Launch_credential_is_single_use_bound_to_its_session_and_revoke_survives_initialized() {
    await using var server = new MissionPlannerMcpServer(new(() => []));
    await server.StartAsync(_ => Task.FromResult<object>(new { evidence = "allowed" }));
    string token = server.IssueLaunchToken();
    using var launched = new Wire(server.Endpoint!, token);
    await launched.Initialize("Launched");
    Assert.Contains("allowed", await launched.Call("read_mission_draft"));
    using var replay = new Wire(server.Endpoint!, token);
    using var replayed = await replay.Post("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"Replay","version":"1"}}}""");
    Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
    launched.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
    using var stolen = await launched.Post("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
    Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
    launched.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    server.RevokeSessions();
    using var notify = await launched.Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    Assert.Contains("permission_required", await launched.Call("read_mission_draft"));
    using var manual = new Wire(server.Endpoint!, server.Token);
    await manual.Initialize("Manual");
    Assert.Contains("permission_required", await manual.Call("read_mission_draft"));
    server.AllowSession(manual.Id); Assert.Contains("allowed", await manual.Call("read_mission_draft"));
  }

  [Fact]
  public async Task Desktop_launch_grants_existing_and_new_sessions_until_revoke() {
    await using var server = new MissionPlannerMcpServer(new(() => []), port: Port(), requiresToken: false);
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    using var first = new Wire(server.Endpoint!); await first.Initialize("Existing");
    server.GrantDesktopLaunch();
    using var second = new Wire(server.Endpoint!); await second.Initialize("Launched");
    Assert.All(server.Sessions, row => Assert.Equal("Allowed", row.Access));
    server.RevokeSessions();
    using var third = new Wire(server.Endpoint!); await third.Initialize("After revoke");
    Assert.Equal("Read only", server.Sessions.Single(s => s.Id == third.Id).Access);
    Assert.Contains("permission_required", await first.Call("read_mission_draft"));
  }

  [AvaloniaFact]
  public async Task Global_stop_revokes_both_listeners_before_waiting_for_a_blocked_first_request() {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var first = new MissionPlannerMcpServer(new(() => []));
    await using var second = new MissionPlannerMcpServer(new(() => []), port: Port(), requiresToken: false);
    await first.StartAsync(async _ => { entered.TrySetResult(); await release.Task; return new { }; });
    await second.StartAsync(_ => Task.FromResult<object>(new { }));
    using var wire = new Wire(first.Endpoint!, first.IssueLaunchToken()); await wire.Initialize("Blocked");
    Task<string> pending = wire.Call("read_mission_draft");
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var hub = new McpAgentHub(null!, () => null, _ => Task.FromResult<McpAgent[]>([]));
    var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    typeof(McpAgentHub).GetProperty("SessionServer", flags)!.SetValue(hub, first);
    typeof(McpAgentHub).GetProperty("DesktopServer", flags)!.SetValue(hub, second);
    var secondEndpoint = second.Endpoint!;
    Task stop = hub.StopAllAsync();
    try {
      Assert.True(first.Stopping.IsCancellationRequested);
      Assert.True(second.Stopping.IsCancellationRequested);
      using var http = new HttpClient();
      try { using var response = await http.GetAsync(secondEndpoint); Assert.False(response.IsSuccessStatusCode); }
      catch (HttpRequestException) { }
    } finally {
      release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(10));
      try { await pending; } catch (HttpRequestException) { }
      await hub.DisposeAsync();
    }
  }

  [Fact]
  public async Task Disconnect_remains_available_when_all_tool_request_slots_are_busy() {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int count = 0;
    await using var server = new MissionPlannerMcpServer(new(() => []));
    await server.StartAsync(async ct => {
      if (Interlocked.Increment(ref count) == 4) { entered.TrySetResult(); }
      await Task.Delay(Timeout.Infinite, ct);
      return new { };
    });
    using var wire = new Wire(server.Endpoint!, server.IssueLaunchToken());
    await wire.Initialize("Busy client");
    Task<string>[] calls = Enumerable.Range(0, 4).Select(_ => wire.Call("read_mission_draft")).ToArray();
    try {
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      using var response = await wire.Http.DeleteAsync(server.Endpoint).WaitAsync(TimeSpan.FromSeconds(5));
      Assert.True(response.IsSuccessStatusCode, $"Disconnect returned {response.StatusCode} while requests were busy.");
    } finally {
      server.RevokeAccess();
      foreach (var call in calls) { try { await call; } catch (HttpRequestException) { } }
    }
  }

  [AvaloniaTheory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Opening_an_existing_starting_listener_waits_for_its_endpoint(bool desktop) {
    await using var hub = new McpAgentHub(null!, () => null, _ => Task.FromResult<McpAgent[]>([]));
    await using var server = new MissionPlannerMcpServer(new(() => []), port: desktop ? Port() : 0, requiresToken: !desktop);
    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
    var gate = (SemaphoreSlim)typeof(MissionPlannerMcpServer).GetField("_lifecycle", flags)!.GetValue(server)!;
    await gate.WaitAsync();
    typeof(McpAgentHub).GetProperty(desktop ? "DesktopServer" : "SessionServer", flags)!.SetValue(hub, server);
    Task<MissionPlannerMcpServer> opening;
    try {
      opening = desktop ? hub.OpenDesktopPortAsync() : hub.OpenSessionPortAsync();
      Assert.False(opening.IsCompleted, "An opening listener must not be reported as ready.");
    } finally { gate.Release(); }
    Assert.Same(server, await opening.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert.NotNull(server.Endpoint);
  }

  [Fact]
  public async Task Revoked_listener_cannot_be_started_again() {
    await using var server = new MissionPlannerMcpServer(new(() => []));
    server.RevokeAccess();
    await Assert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync(_ => Task.FromResult<object>(new { })));
    Assert.Null(server.Endpoint);
  }

  [AvaloniaFact]
  public async Task Parallel_open_calls_share_one_ready_listener() {
    await using var hub = new McpAgentHub(null!, () => null, _ => Task.FromResult<McpAgent[]>([]));
    var servers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(hub.OpenSessionPortAsync)));
    Assert.All(servers, server => {
      Assert.Same(hub.SessionServer, server);
      Assert.NotNull(server.Endpoint);
      Assert.False(server.Stopping.IsCancellationRequested);
    });
    await hub.StopAllAsync();
    Assert.All(servers, server => Assert.True(server.Stopping.IsCancellationRequested));
  }

  [Fact]
  public async Task Stdio_bridge_round_trips_real_HTTP_and_ends_its_session_on_EOF() {
    await using var server = new MissionPlannerMcpServer(new(() => []), port: Port(), requiresToken: false);
    await server.StartAsync(_ => Task.FromResult<object>(new { evidence = "bridge-ok" }));
    string input = """
      {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"Bridge","version":"1"}}}
      {"jsonrpc":"2.0","method":"notifications/initialized"}
      {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"read_mission_draft","arguments":{}}}
      """;
    using var output = new StringWriter();
    Assert.Equal(0, await McpStdioBridge.RunAsync(server.Endpoint!.Port, new StringReader(input), output));
    string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(2, lines.Length); Assert.Contains("bridge-ok", lines[1]);
    foreach (string line in lines) { using var doc = JsonDocument.Parse(line); Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString()); }
    for (int i = 0; i < 100 && server.Sessions.Length > 0; i++) { await Task.Delay(10); }
    Assert.Empty(server.Sessions);
  }
}
