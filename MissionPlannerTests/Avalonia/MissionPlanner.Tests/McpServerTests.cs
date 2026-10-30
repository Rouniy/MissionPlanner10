using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MissionPlanner.Services.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MissionPlanner.Tests;

public sealed class McpServerTests {
  [Fact]
  public async Task Http_client_reads_telemetry_and_opens_the_registered_analyzer_by_handle() {
    using var file = new McpTelemetryLogTests.TlogFixture();
    file.Add(0, MAVLink.MAVLINK_MSG_ID.VIBRATION, new MAVLink.mavlink_vibration_t { vibration_x = 20, clipping_0 = 4 }, 1);
    file.Add(1, MAVLink.MAVLINK_MSG_ID.VIBRATION, new MAVLink.mavlink_vibration_t { vibration_x = 40, clipping_0 = 7 }, 1, signed: true);
    string? opened = null;
    await using var server = new MissionPlannerMcpServer(new McpVehicleAccess(() => [])) {
      OpenLogAnalyzer = (path, _) => { opened = path; return Task.CompletedTask; },
    };
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    string id = server.Logs.Attach(file.Path).Id;
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.IssueLaunchToken());
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp }, http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    var report = await client.CallToolAsync("log_vibration_report", new Dictionary<string, object?> {
      ["logId"] = id, ["startSeconds"] = 0, ["endSeconds"] = 2,
    }, cancellationToken: timeout.Token);
    Assert.NotEqual(true, report.IsError);
    using var data = JsonDocument.Parse(Assert.IsType<TextContentBlock>(report.Content[0]).Text);
    var sensor = data.RootElement.GetProperty("sensors")[0];
    Assert.Equal("1:1", sensor.GetProperty("instance").GetString());
    Assert.Equal(30, sensor.GetProperty("axes").GetProperty("VibeX").GetProperty("mean").GetDouble());
    Assert.Equal(3, sensor.GetProperty("clipping").GetProperty("Clip0").GetProperty("observedIncrease").GetDouble());
    var result = await client.CallToolAsync("open_log_analyzer", new Dictionary<string, object?> { ["logId"] = id }, cancellationToken: timeout.Token);
    Assert.NotEqual(true, result.IsError); Assert.Equal(file.Path, opened);
  }

  [Fact]
  public async Task Real_http_client_discovers_tools_reads_log_and_receives_useful_errors() {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    await using var server = new MissionPlannerMcpServer(new McpVehicleAccess(() => []));
    await server.StartAsync(_ => Task.FromResult<object>(new { draft = true }), timeout.Token);
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.IssueLaunchToken());
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions {
      Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp,
    }, http);
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    Assert.Contains(tools, t => t.Name == "read_parameters");
    Assert.Contains(tools, t => t.Name == "log_batch_spectrum");
    Assert.Contains(tools, t => t.Name == "vehicle_health");
    Assert.Contains(tools, t => t.Name == "log_vibration_report");
    foreach (string name in new[] { "read_vehicle_messages", "telemetry_packet_inventory", "log_events",
        "log_time_series", "compare_log_parameters", "compare_vehicle_parameters_to_log",
        "dronecan_nodes", "dronecan_read_parameters", "dronecan_write_parameters", "dronecan_node_command" }) {
      Assert.Contains(tools, t => t.Name == name);
    }
    Assert.DoesNotContain(tools, t => t.Name.Contains("arm") || t.Name == "apply_parameter_changes");
    var info = await client.CallToolAsync("diagnostics_info", cancellationToken: timeout.Token);
    Assert.NotEqual(true, info.IsError);
    Assert.Contains("MissionPlanner", Assert.IsType<TextContentBlock>(info.Content[0]).Text);
    var error = await client.CallToolAsync("read_telemetry", new Dictionary<string, object?> {
      ["targetId"] = "invalid", ["fields"] = "roll",
    }, cancellationToken: timeout.Token);
    Assert.True(error.IsError);
    Assert.Contains("Unknown target", Assert.IsType<TextContentBlock>(error.Content[0]).Text);

    string path = TemporaryLog();
    try {
      string id = server.Logs.Attach(path).Id;
      var trend = await client.CallToolAsync("log_time_series", new Dictionary<string, object?> {
        ["logId"] = id, ["type"] = "PARM", ["field"] = "Value", ["startSeconds"] = 0, ["endSeconds"] = 3, ["bins"] = 1,
      }, cancellationToken: timeout.Token);
      Assert.NotEqual(true, trend.IsError);
      using var trendJson = JsonDocument.Parse(Assert.IsType<TextContentBlock>(trend.Content[0]).Text);
      Assert.Equal(0.8, trendJson.RootElement.GetProperty("result").GetProperty("series")[0].GetProperty("buckets")[0].GetProperty("max").GetDouble());
      var comparison = await client.CallToolAsync("compare_log_parameters", new Dictionary<string, object?> {
        ["beforeLogId"] = id, ["beforeSeconds"] = 1, ["afterLogId"] = id, ["afterSeconds"] = 2,
      }, cancellationToken: timeout.Token);
      Assert.NotEqual(true, comparison.IsError);
      using var comparisonJson = JsonDocument.Parse(Assert.IsType<TextContentBlock>(comparison.Content[0]).Text);
      Assert.Equal(0.3, comparisonJson.RootElement.GetProperty("comparison").GetProperty("differences")[0].GetProperty("delta").GetDouble(), 8);
      var overview = await client.CallToolAsync("log_overview", new Dictionary<string, object?> {
        ["logId"] = id,
      }, cancellationToken: timeout.Token);
      Assert.NotEqual(true, overview.IsError);
      Assert.Contains("PARM", Assert.IsType<TextContentBlock>(overview.Content[0]).Text);
      var snapshot = await client.CallToolAsync("log_parameters_at", new Dictionary<string, object?> {
        ["logId"] = id, ["atSeconds"] = 1.5,
      }, cancellationToken: timeout.Token);
      Assert.NotEqual(true, snapshot.IsError);
      using var snapshotJson = JsonDocument.Parse(Assert.IsType<TextContentBlock>(snapshot.Content[0]).Text);
      Assert.Equal(0.5, snapshotJson.RootElement.GetProperty("parameters")[0].GetProperty("value").GetDouble());
      var missingVibe = await client.CallToolAsync("log_vibration_report", new Dictionary<string, object?> {
        ["logId"] = id, ["startSeconds"] = 0, ["endSeconds"] = 10,
      }, cancellationToken: timeout.Token);
      Assert.True(missingVibe.IsError);
      Assert.Contains("no VIBE", Assert.IsType<TextContentBlock>(missingVibe.Content[0]).Text);
      var first = await client.CallToolAsync("read_log_records", new Dictionary<string, object?> {
        ["logId"] = id, ["types"] = "PARM", ["count"] = 1,
      }, cancellationToken: timeout.Token);
      using var parsed = JsonDocument.Parse(Assert.IsType<TextContentBlock>(first.Content[0]).Text);
      Assert.Single(parsed.RootElement.GetProperty("rows").EnumerateArray());
      Assert.False(parsed.RootElement.GetProperty("complete").GetBoolean());
      int next = parsed.RootElement.GetProperty("nextLine").GetInt32();
      var second = await client.CallToolAsync("read_log_records", new Dictionary<string, object?> {
        ["logId"] = id, ["types"] = "PARM", ["startLine"] = next,
      }, cancellationToken: timeout.Token);
      using var parsed2 = JsonDocument.Parse(Assert.IsType<TextContentBlock>(second.Content[0]).Text);
      Assert.Single(parsed2.RootElement.GetProperty("rows").EnumerateArray());
      Assert.Equal("0.8", parsed2.RootElement.GetProperty("rows")[0].GetProperty("fields").GetProperty("Value").GetString()!.Trim());
    } finally { await server.DisposeAsync(); File.Delete(path); }
  }

  [Fact]
  public async Task Streamable_http_negotiates_sse_accepts_notifications_and_rejects_unsupported_versions() {
    await using var server = new MissionPlannerMcpServer(new McpVehicleAccess(() => []));
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
    http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    using var init = new StringContent("""
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"wire-test","version":"1"}}}
        """, Encoding.UTF8, "application/json");
    using var response = await http.PostAsync(server.Endpoint, init);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    Assert.Contains("\"protocolVersion\":\"2025-11-25\"", await response.Content.ReadAsStringAsync());
    Assert.True(response.Headers.Contains("Mcp-Session-Id"));
    http.DefaultRequestHeaders.Add("Mcp-Session-Id", response.Headers.GetValues("Mcp-Session-Id").Single());
    http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
    using var notification = new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Encoding.UTF8, "application/json");
    using var accepted = await http.PostAsync(server.Endpoint, notification);
    Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    using var get = await http.GetAsync(server.Endpoint);
    Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    http.DefaultRequestHeaders.Remove("MCP-Protocol-Version");
    http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "not-a-version");
    using var ping = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json");
    using var invalid = await http.PostAsync(server.Endpoint, ping);
    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
  }

  [Fact]
  public async Task Authentication_origin_and_host_are_enforced_and_each_server_gets_an_independent_port() {
    await using var first = new MissionPlannerMcpServer(new McpVehicleAccess(() => []));
    await using var second = new MissionPlannerMcpServer(new McpVehicleAccess(() => []));
    await first.StartAsync(_ => Task.FromResult<object>(new { }));
    await second.StartAsync(_ => Task.FromResult<object>(new { }));
    Assert.NotEqual(first.Endpoint!.Port, second.Endpoint!.Port);
    Assert.NotEqual(first.Token, second.Token);
    using var http = new HttpClient();
    Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(first.Endpoint)).StatusCode);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.Token);
    Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(first.Endpoint)).StatusCode);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Token);
    http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
    Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(first.Endpoint)).StatusCode);
    http.DefaultRequestHeaders.Remove("Origin");
    http.DefaultRequestHeaders.Host = "untrusted.example";
    Assert.Contains((await http.GetAsync(first.Endpoint)).StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden });
    http.DefaultRequestHeaders.Host = null;
    var uri = first.Endpoint;
    await first.DisposeAsync();
    await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(uri));
  }

  [Fact]
  public async Task Catalog_rejects_unknown_handles_and_changed_files() {
    using var catalog = new McpLogCatalog();
    string path = TemporaryLog();
    try {
      var info = catalog.Attach(path);
      await File.AppendAllTextAsync(path, "\nMSG, changed\n");
      await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.Read(info.Id, (log, _) => log.Count, default));
      await Assert.ThrowsAsync<ArgumentException>(() => catalog.Read("../../secret", (log, _) => log.Count, default));
    } finally { File.Delete(path); }
  }

  internal static string TemporaryLog() {
    string path = Path.Combine(Path.GetTempPath(), $"mp-mcp-{Guid.NewGuid():N}.log");
    File.WriteAllLines(path, [
      "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
      "FMT, 130, 40, PARM, QNff, TimeUS,Name,Value,Default",
      "PARM, 1000000, ATC_RAT_RLL_P, 0.5, 0.2",
      "PARM, 2000000, ATC_RAT_RLL_P, 0.8, 0.2",
    ]);
    return path;
  }
}
