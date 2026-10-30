using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MissionPlanner.Services.Mcp;
using ModelContextProtocol.Client;
using Tomlyn;
using Tomlyn.Model;

namespace MissionPlanner.Tests;

public sealed class McpAgentConnectionTests {
  [Theory]
  [InlineData("\n")]
  [InlineData("\r\n")]
  public void Desktop_registration_preserves_other_settings_comments_and_is_idempotent(string newline) {
    string original = string.Join(newline, new[] { "# My settings", "model = 'chosen-model'", "[mcp_servers.other]", "command = 'other-tool'", "# keep this", "" });
    string changed = McpDesktopRegistration.Edit(original, 47183);
    Assert.StartsWith(original, changed);
    Assert.Equal(changed, McpDesktopRegistration.Edit(changed, 47183));
    Assert.Equal(original, McpDesktopRegistration.Edit(changed, null));
    string moved = McpDesktopRegistration.Edit(changed, 47184);
    Assert.DoesNotContain(":47183", moved); Assert.Contains(":47184/mcp", moved);
    var model = TomlSerializer.Deserialize<TomlTable>(moved)!;
    var servers = (TomlTable)model["mcp_servers"]!;
    Assert.Equal(2, servers.Count);
    Assert.False(((TomlTable)servers[McpDesktopRegistration.ServerName]!).ContainsKey("http_headers"));
  }

  [Theory]
  [InlineData("model = [")]
  [InlineData("[mcp_servers.missionplanner10_desktop]\nurl='http://existing/mcp'\n")]
  [InlineData("mcp_servers.missionplanner10_desktop.url='http://existing/mcp'\n")]
  [InlineData("# BEGIN MissionPlanner10 desktop MCP (managed)\nmodel='x'\n")]
  public void Desktop_registration_rejects_malformed_or_unowned_configuration(string original) =>
      Assert.Throws<InvalidOperationException>(() => McpDesktopRegistration.Edit(original, 47183));

  [Fact]
  public void Desktop_registration_does_not_overwrite_user_edits_or_markers_inside_strings() {
    string managed = McpDesktopRegistration.Edit("", 47183);
    Assert.Throws<InvalidOperationException>(() => McpDesktopRegistration.Edit(managed.Replace("enabled = true", "enabled = false"), null));
    string stringMarkers = "description = '''\n" + managed + "'''\n";
    Assert.Throws<InvalidOperationException>(() => McpDesktopRegistration.Edit(stringMarkers, null));
  }

  [Fact]
  public void Desktop_registration_backs_up_original_and_removes_only_owned_section() {
    string root = Path.Combine(Path.GetTempPath(), "mp-registration-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string path = Path.Combine(root, "config.toml");
    byte[] original = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("# Keep\r\nmodel='existing'\r\n")).ToArray();
    File.WriteAllBytes(path, original);
    try {
      string backup = McpDesktopRegistration.Update(path, 47183)!;
      Assert.Equal(original, File.ReadAllBytes(backup));
      Assert.True(McpDesktopRegistration.IsRegistered(path, 47183));
      Assert.False(McpDesktopRegistration.IsRegistered(path, 47184));
      Assert.Null(McpDesktopRegistration.Update(path, 47183));
      McpDesktopRegistration.Update(path, null);
      Assert.Equal(original, File.ReadAllBytes(path));
      Assert.False(McpDesktopRegistration.IsRegistered(path, 47183));
    } finally { Directory.Delete(root, true); }
  }

  [Fact]
  public void Claude_session_config_is_private_temporary_and_never_puts_token_in_arguments() {
    var uri = new Uri("http://127.0.0.1:32123/mcp");
    string directory;
    using (var files = new McpAgentSessionFiles(McpAgentKind.ClaudeCode, uri, "test-private-token")) {
      directory = files.DirectoryPath;
      using var doc = JsonDocument.Parse(File.ReadAllText(files.ClaudeConfigPath));
      var server = doc.RootElement.GetProperty("mcpServers").GetProperty("missionplanner");
      Assert.Equal(uri.AbsoluteUri, server.GetProperty("url").GetString());
      Assert.Equal(720000, server.GetProperty("timeout").GetInt32());
      Assert.Equal("Bearer test-private-token", server.GetProperty("headers").GetProperty("Authorization").GetString());
      if (!OperatingSystem.IsWindows()) {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.ClaudeConfigPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
      }
      var start = McpAgentProcess.BuildStart(OperatingSystem.IsWindows() ? "C:\\agent path\\claude.exe" : "/tmp/agent path/claude",
          uri, "test-private-token", directory, McpAgentKind.ClaudeCode, files.ClaudeConfigPath);
      Assert.Contains("--strict-mcp-config", start.ArgumentList);
      Assert.Contains(files.ClaudeConfigPath, start.ArgumentList);
      Assert.Contains("--no-session-persistence", start.ArgumentList);
      Assert.Contains("dontAsk", start.ArgumentList);
      Assert.Equal("720000", start.Environment["MCP_TOOL_TIMEOUT"]);
      Assert.DoesNotContain("test-private-token", string.Join(" ", start.ArgumentList));
      Assert.DoesNotContain("--dangerously-skip-permissions", start.ArgumentList);
      Assert.Equal("", start.ArgumentList[start.ArgumentList.IndexOf("--tools") + 1]);
    }
    Assert.False(Directory.Exists(directory));
  }

  [Fact]
  public void Claude_streaming_output_displays_text_and_tool_names_without_duplicating_result() {
    string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Vibration report"},{"type":"tool_use","name":"mcp__missionplanner__log_schema","input":{"secret":"not displayed"}}]}}""";
    string text = McpAgentProcess.FormatLine(line);
    Assert.Contains("Vibration report", text); Assert.Contains("mcp__missionplanner__log_schema", text);
    Assert.DoesNotContain("not displayed", text);
    Assert.Equal("Analysis complete.\n", McpAgentProcess.FormatLine("""{"type":"result","is_error":false,"result":"Vibration report"}"""));
    Assert.Contains("Agent failed", McpAgentProcess.FormatLine("""{"type":"result","is_error":true,"result":"Login required"}"""));
  }

  [Fact]
  public void Discovery_finds_desktop_entries_regardless_of_file_name_case() {
    string dir = Path.Combine(Path.GetTempPath(), "mp-apps-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      string claude = Path.Combine(dir, "com.anthropic.Claude.desktop");
      File.WriteAllText(claude, "[Desktop Entry]\nType=Application\nName=Claude\nExec=/opt/Claude/claude %U\n");
      Assert.Equal(claude, McpAgentDiscovery.FindDesktopFile(dir, "com.anthropic.claude.desktop"));
      Assert.Null(McpAgentDiscovery.FindDesktopFile(dir, "codex.desktop"));
      Assert.Null(McpAgentDiscovery.FindDesktopFile(Path.Combine(dir, "missing"), "codex.desktop"));
      Assert.Contains("com.anthropic.claude.desktop", McpAgentDiscovery.DesktopEntryFiles);
    } finally { Directory.Delete(dir, true); }
  }

  [Theory]
  [InlineData("Claude Code URL Handler", "claude --handle-uri %u", null)]
  [InlineData("Claude", "claude", null)]
  [InlineData("Codex", "codex-desktop %U", "Codex Desktop")]
  [InlineData("ChatGPT", "chatgpt %U", "ChatGPT Desktop")]
  public void Discovery_distinguishes_desktop_apps_from_cli_handlers(string name, string command, string? expected) {
    string entry = $"[Desktop Entry]\nType=Application\nName={name}\nExec={command}\n";
    Assert.Equal(expected, McpAgentDiscovery.DesktopEntryName(entry));
    Assert.Null(McpAgentDiscovery.DesktopEntryName(entry + "Hidden=true\n"));
  }

  [Fact]
  public async Task Stopping_a_server_with_a_live_session_and_idle_keep_alive_connections_takes_under_a_second() {
    // Application exit waits for this; a graceful-shutdown wait on idle connections made Mission Planner close slowly.
    var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
    int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
    var server = new MissionPlannerMcpServer(new McpVehicleAccess(() => []), port: port, requiresToken: false);
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions {
      Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp,
    }, http);
    await using var client = await McpClient.CreateAsync(transport);
    Assert.NotEmpty(await client.ListToolsAsync());
    using var idle = new HttpClient();
    Assert.Equal(HttpStatusCode.MethodNotAllowed, (await idle.GetAsync(server.Endpoint)).StatusCode);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await server.DisposeAsync();
    watch.Stop();
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Server stop took {watch.Elapsed.TotalSeconds:F1} s.");
  }

  [Fact]
  public async Task Desktop_http_needs_no_token_and_stopping_it_does_not_revoke_cli_or_dispose_shared_logs() {
    var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
    int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
    using var logs = new McpLogCatalog(); var vehicles = new McpVehicleAccess(() => []);
    await using var desktop = new MissionPlannerMcpServer(vehicles, logs, port, false);
    await using var cli = new MissionPlannerMcpServer(vehicles, logs);
    await desktop.StartAsync(_ => Task.FromResult<object>(new { }));
    await cli.StartAsync(_ => Task.FromResult<object>(new { }));
    Assert.Equal(port, desktop.Endpoint!.Port);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(cli.Endpoint)).StatusCode);
    await using (var transport = new HttpClientTransport(new HttpClientTransportOptions {
      Endpoint = desktop.Endpoint, TransportMode = HttpTransportMode.StreamableHttp,
    }, http)) {
      await using var client = await McpClient.CreateAsync(transport);
      Assert.Contains(await client.ListToolsAsync(), t => t.Name == "log_vibration_report");
    }
    using var rejectHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    rejectHttp.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
    Assert.Equal(HttpStatusCode.Forbidden, (await rejectHttp.GetAsync(desktop.Endpoint)).StatusCode);
    rejectHttp.DefaultRequestHeaders.Remove("Origin"); rejectHttp.DefaultRequestHeaders.Host = "evil.example";
    Assert.Contains((await rejectHttp.GetAsync(desktop.Endpoint)).StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden });
    var desktopUri = desktop.Endpoint;
    await desktop.DisposeAsync();
    using var freshHttp = new HttpClient();
    await Assert.ThrowsAsync<HttpRequestException>(() => freshHttp.GetAsync(desktopUri));
    Assert.Equal(HttpStatusCode.Unauthorized, (await freshHttp.GetAsync(cli.Endpoint)).StatusCode);
    string path = McpServerTests.TemporaryLog();
    try { Assert.NotNull(logs.Attach(path)); } finally { File.Delete(path); }
  }

  [Fact]
  public async Task Occupied_desktop_port_fails_without_falling_back_to_another_port() {
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try {
      int port = ((IPEndPoint)listener.LocalEndpoint).Port;
      await using var server = new MissionPlannerMcpServer(new McpVehicleAccess(() => []), port: port, requiresToken: false);
      await Assert.ThrowsAnyAsync<IOException>(() => server.StartAsync(_ => Task.FromResult<object>(new { })));
      Assert.Null(server.Endpoint);
    } finally { listener.Stop(); }
  }
  [Fact]
  public async Task Failed_process_start_cleans_its_session_directory() {
    string root = Path.Combine(Path.GetTempPath(), "mp-agent-start-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
      await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => McpAgentProcess.RunAsync(
          Path.Combine(root, "missing.exe"), new Uri("http://127.0.0.1:32123/mcp"), "secret", "Analyze logs", _ => { }, default,
          McpAgentKind.ClaudeCode, root));
      Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    } finally { Directory.Delete(root, true); }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Fake_agent_process_receives_session_config_and_cleanup_runs_on_exit_or_cancel(bool cancel) {
    if (OperatingSystem.IsWindows()) { return; } // Unix process fixture; argv/config/HTTP tests run on every OS.
    string root = Path.Combine(Path.GetTempPath(), "mp-agent-process-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string executable = Path.Combine(root, "fake agent");
    File.WriteAllText(executable, "#!/bin/sh\ncat >/dev/null\nprintf '%s\\n' \"$PWD\"\ncat mcp.json\nprintf '\\n'\n" + (cancel ? "sleep 30\n" : "exit 7\n"));
    File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    using var stop = new CancellationTokenSource();
    var working = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
    try {
      Task<int> run = McpAgentProcess.RunAsync(executable, new Uri("http://127.0.0.1:32123/mcp"), "session-secret", "Analyze logs", line => {
        output.Enqueue(line);
        if (line.StartsWith(root, StringComparison.Ordinal)) { working.TrySetResult(line.Trim()); }
      }, stop.Token, McpAgentKind.ClaudeCode, root);
      string directory = await working.Task.WaitAsync(TimeSpan.FromSeconds(10));
      if (cancel) {
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(10)));
      } else { Assert.Equal(7, await run.WaitAsync(TimeSpan.FromSeconds(10))); }
      Assert.False(Directory.Exists(directory));
      Assert.DoesNotContain("session-secret", string.Join("", output));
      if (!cancel) { Assert.Contains("[redacted]", string.Join("", output)); }
    } finally { stop.Cancel(); Directory.Delete(root, true); }
  }

}
