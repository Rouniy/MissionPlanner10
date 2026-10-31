using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner.Views;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Tests;

public sealed class McpLayoutTests {
  internal static AgentToolsWindow Window(Func<CancellationToken, Task<McpAgent[]>> discover) =>
      new(new McpAgentHub(null!, () => null, discover));
  internal static async Task CloseWindowAsync(AgentToolsWindow window) {
    // Closing the window keeps the hub alive by design; tests dispose the hub explicitly so
    // listener/catalogue teardown finishes before the headless dispatcher is reset.
    window.Close();
    await window.Hub.DisposeAsync();
    Dispatcher.UIThread.RunJobs();
  }
  private static readonly McpAgent[] ThreeAgents = [
    new(McpAgentKind.CodexCli, "Codex CLI", "/fake/codex", []),
    new(McpAgentKind.ClaudeCode, "Claude Code", "/fake/claude", []),
    new(McpAgentKind.OpenAiDesktop, "Codex Desktop", "/fake/desktop", []),
  ];
  private static Button ButtonNamed(AgentToolsWindow window, string content) =>
      window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == content);

  [AvaloniaFact]
  public async Task Application_exit_disposes_a_hub_with_open_ports_and_a_live_session_within_the_exit_budget() {
    // App exit blocks the UI thread in hub.DisposeAsync().Wait(5 s); a slow teardown made Mission Planner close slowly.
    var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); reservation.Start();
    int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
    var hub = new McpAgentHub(null!, () => null, _ => Task.FromResult<McpAgent[]>([])) { DesktopPort = port };
    var desktop = await hub.OpenDesktopPortAsync();
    await hub.OpenSessionPortAsync();
    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    await using var transport = new ModelContextProtocol.Client.HttpClientTransport(new ModelContextProtocol.Client.HttpClientTransportOptions {
      Endpoint = desktop.Endpoint!, TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
    }, http);
    await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(transport);
    Assert.NotEmpty(await client.ListToolsAsync());
    Assert.Equal(1, hub.SessionCount());
    var watch = System.Diagnostics.Stopwatch.StartNew();
    bool finished = hub.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    watch.Stop();
    Assert.True(finished && watch.Elapsed < TimeSpan.FromSeconds(1.5), $"Hub teardown took {watch.Elapsed.TotalSeconds:F1} s (finished: {finished}).");
    Dispatcher.UIThread.RunJobs();
  }
  [AvaloniaFact]
  public async Task Closing_the_agent_window_keeps_the_hub_and_its_listeners_alive() {
    var window = Window(_ => Task.FromResult<McpAgent[]>([]));
    try {
      window.Show(); Dispatcher.UIThread.RunJobs();
      var server = await window.Hub.OpenSessionPortAsync();
      window.Close(); Dispatcher.UIThread.RunJobs();
      Assert.False(server.Stopping.IsCancellationRequested);
      Assert.Same(server, window.Hub.SessionServer);
      Assert.NotNull(server.Endpoint);
      await window.Hub.StopAllAsync();
      Assert.Null(window.Hub.SessionServer);
      Assert.True(server.Stopping.IsCancellationRequested);
    } finally { await CloseWindowAsync(window); }
  }
  [AvaloniaFact]
  public async Task Agent_window_buttons_fit_at_minimum_size_including_advanced_section() {
    var window = Window(_ => Task.FromResult(ThreeAgents));
    try {
      window.Show(); window.Width = window.MinWidth; window.Height = window.MinHeight;
      await window.FindAgentsAsync();
      Dispatcher.UIThread.RunJobs();
      window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
      Dispatcher.UIThread.RunJobs();
      var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
      Assert.True(buttons.Length >= 12, "expected the agent, session, port and advanced buttons");
      foreach (var button in buttons) {
        var start = button.TranslatePoint(new Point(), window)!.Value;
        Assert.True(start.X >= -1, $"{button.Content} starts outside window.");
        Assert.True(start.X + button.Bounds.Width <= window.ClientSize.Width + 1, $"{button.Content} overflows width.");
      }
      // Primary actions are reachable without scrolling at the minimum height.
      foreach (string primary in new[] { "Launch Codex CLI", "Find agents", "Allow", "Stop all connections" }) {
        var button = ButtonNamed(window, primary);
        var start = button.TranslatePoint(new Point(), window)!.Value;
        Assert.True(start.Y + button.Bounds.Height <= window.ClientSize.Height + 1, $"{primary} needs scrolling at minimum size.");
      }
    } finally { await CloseWindowAsync(window); }
  }
  [AvaloniaFact]
  public async Task One_launch_button_per_installed_agent_and_none_for_missing_agents() {
    McpAgent[] found = ThreeAgents;
    var window = Window(_ => Task.FromResult(found));
    try {
      window.Show(); await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      foreach (string name in new[] { "Launch Codex CLI", "Launch Claude Code", "Launch Codex Desktop", "Find agents" }) {
        Assert.True(ButtonNamed(window, name).IsEffectivelyVisible, name);
      }
      Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => (b.Content as string) is "Register Codex Desktop" or "Unregister Codex Desktop");
      // No agent list, editable executable path or agent-kind selector: the buttons are the agents.
      Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "/fake/codex" || t.Text == "/fake/claude");
      Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(), c => c.Items.OfType<string>().Contains("Claude Code"));
      Assert.Contains("Do not analyze logs", AgentToolsWindow.DefaultTask);
      // The initial task is an Advanced setting, like X-Office's panel keeps the main view to agents, sessions and the port.
      Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text == AgentToolsWindow.DefaultTask);
      window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true; Dispatcher.UIThread.RunJobs();
      Assert.Equal(AgentToolsWindow.DefaultTask, window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == AgentToolsWindow.DefaultTask).Text);
      // Each installed agent is a row with its location, not an editable path.
      Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("/fake/codex", StringComparison.Ordinal) == true);
      found = [ThreeAgents[1]]; await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      var launches = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string)
          .Where(c => c?.StartsWith("Launch ", StringComparison.Ordinal) == true && c != "Launch custom").ToArray();
      Assert.Equal(new[] { "Launch Claude Code" }, launches);
    } finally { await CloseWindowAsync(window); }
  }
  [AvaloniaFact]
  public async Task Register_and_unregister_buttons_follow_the_desktop_registration_and_ask_for_the_port_at_startup() {
    string codexHome = Path.Combine(Path.GetTempPath(), "mp-codex-home-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(codexHome);
    string? previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
    var window = Window(_ => Task.FromResult<McpAgent[]>([new(McpAgentKind.OpenAiDesktop, "Codex Desktop", "/fake/desktop", [])]));
    try {
      window.Hub.AutoOpenDesktopPort = false;
      window.Show(); await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      Assert.True(ButtonNamed(window, "Register Codex Desktop").IsEffectivelyVisible);
      Assert.Contains("Desktop application · Not registered ·", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Single(t => t?.StartsWith("Desktop application ·", StringComparison.Ordinal) == true));
      Assert.False(window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Open at startup").IsChecked);
      await window.Hub.RegisterDesktopAsync(window.Hub.Agents[0]);
      await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      Assert.True(McpDesktopRegistration.HasDesktopEntry(Path.Combine(codexHome, "config.toml")));
      Assert.True(ButtonNamed(window, "Unregister Codex Desktop").IsEffectivelyVisible);
      Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Register Codex Desktop");
      Assert.True(window.Hub.AutoOpenDesktopPort);
      window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true; Dispatcher.UIThread.RunJobs();
      Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Open at startup").IsChecked);
      await window.Hub.UnregisterDesktopAsync(window.Hub.Agents[0]);
      await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      Assert.True(ButtonNamed(window, "Register Codex Desktop").IsEffectivelyVisible);
    } finally {
      window.Hub.AutoOpenDesktopPort = false;
      await CloseWindowAsync(window);
      Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
      Directory.Delete(codexHome, true);
    }
  }
  [AvaloniaFact]
  public async Task Startup_opens_the_persistent_port_only_when_a_registration_asked_for_it() {
    var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); reservation.Start();
    int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
    var hub = new McpAgentHub(null!, () => null, _ => Task.FromResult<McpAgent[]>([])) { DesktopPort = port };
    try {
      hub.AutoOpenDesktopPort = false;
      await hub.OpenDesktopPortAtStartupAsync();
      Assert.Null(hub.DesktopServer);
      hub.AutoOpenDesktopPort = true;
      await hub.OpenDesktopPortAtStartupAsync();
      Assert.Equal(port, hub.DesktopServer!.Endpoint!.Port);
    } finally { hub.AutoOpenDesktopPort = false; await hub.DisposeAsync(); Dispatcher.UIThread.RunJobs(); }
  }
  [AvaloniaFact]
  public async Task Port_and_session_controls_remain_available_if_all_agents_disappear() {
    McpAgent[] found = [new(McpAgentKind.OpenAiDesktop, "Codex Desktop", "/fake/app", [])];
    var window = Window(_ => Task.FromResult(found));
    try {
      window.Show(); await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      found = []; await window.FindAgentsAsync(); Dispatcher.UIThread.RunJobs();
      Assert.True(ButtonNamed(window, "Open port").IsEffectivelyVisible);
      Assert.True(ButtonNamed(window, "Allow all").IsEffectivelyVisible);
      Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Launch Codex Desktop");
      Assert.Contains("No installed agents found", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Single(t => t?.StartsWith("No installed", StringComparison.Ordinal) == true));
    } finally { await CloseWindowAsync(window); }
  }
}
