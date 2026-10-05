using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.Views;

/// <summary>
/// View over the application-wide <see cref="McpAgentHub"/>, laid out like X-Office's assistant panel:
/// installed agents with Launch, live sessions with Allow/Revoke/Disconnect, the persistent port and
/// an activity log. Closing this window hides it only; agents keep working until Stop all connections.
/// The agent operates logs, parameters, missions and every screen through the application itself.
/// </summary>
internal sealed class AgentToolsWindow : Window {
  internal const string DefaultTask = "Connect to the Mission Planner MCP server: read the AI_START resource and tools/list, "
      + "then report in one short message that the connection works, how many tools are available and what they cover. "
      + "Do not analyze logs or change anything until asked.";
  internal McpAgentHub Hub { get; }
  private sealed record SessionRow(MissionPlannerMcpServer Server, McpConnectionInfo Info) {
    public override string ToString() => Info.ToString();
  }
  private readonly WrapPanel _agentButtons = new();
  private readonly WrapPanel _desktopButtons = new();
  private readonly CheckBox _autoOpen = new() { Content = "Open at startup", Margin = new Thickness(6, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
  private readonly TextBlock _agentState = new() { TextWrapping = TextWrapping.Wrap };
  private readonly ListBox _sessions = new() { MinHeight = 72 };
  private readonly TextBox _task = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, Text = DefaultTask };
  private readonly NumericUpDown _desktopPort = new() { Minimum = 1024, Maximum = 65535, Increment = 1,
    Value = McpDesktopRegistration.DefaultPort, FormatString = "0", Width = 130 };
  private readonly TextBlock _desktopState = new() { TextWrapping = TextWrapping.Wrap };
  private readonly TextBox _desktopEndpoint = new() { IsReadOnly = true, Watermark = "Persistent port closed" };
  private readonly TextBox _sessionEndpoint = new() { IsReadOnly = true, Watermark = "Session port closed" };
  private readonly TextBox _workingDirectory = new() { Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
  private readonly TextBox _customExecutable = new() { Watermark = "Path to a codex or claude executable that was not found automatically" };
  private readonly ListBox _proposals = new() { MinHeight = 48 };
  private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
  private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Text = "Select an installed agent and Launch. Closing this window keeps agents connected." };
  private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
  private readonly System.Text.StringBuilder _pendingOutput = new();
  private readonly object _outputSync = new();
  private readonly Action<string> _onOutput;
  private bool _closed;
  private int _activeOperations;

  internal AgentToolsWindow(McpAgentHub hub) {
    Hub = hub;
    Title = "AI agents / MCP"; Width = 860; Height = 760; MinWidth = 640; MinHeight = 560;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    _desktopPort.Value = Hub.DesktopPort;
    _desktopPort.ValueChanged += (_, _) => {
      try { Hub.DesktopPort = (int)(_desktopPort.Value ?? McpDesktopRegistration.DefaultPort); }
      catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException) { _status.Text = e.Message; _desktopPort.Value = Hub.DesktopPort; }
      RefreshAgents();
    };
    _autoOpen.IsChecked = Hub.AutoOpenDesktopPort;
    _autoOpen.IsCheckedChanged += (_, _) => { if (_autoOpen.IsChecked is bool wanted && wanted != Hub.AutoOpenDesktopPort) { Hub.AutoOpenDesktopPort = wanted; } };
    var advanced = new Expander { Header = "Advanced", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Spacing = 6, Children = {
      new TextBlock { Text = "Temporary session port with bearer token (for manually configured clients)", FontWeight = FontWeight.Bold },
      new WrapPanel { Children = { Button("Open session port", StartAsync), Button("Copy connection settings", CopyAsync) } }, _sessionEndpoint,
      new TextBlock { Text = "Terminal working directory" }, _workingDirectory,
      new TextBlock { Text = "Custom terminal agent (the kind follows the executable name)" },
      new WrapPanel { Children = { Button("Executable…", ChooseExecutableAsync), Button("Launch custom", LaunchCustomAsync) } }, _customExecutable,
      new TextBlock { Text = "Parameter proposals submitted by agents for operator review", FontWeight = FontWeight.Bold },
      _proposals, new WrapPanel { Children = { Button("Review / apply selected", ApplyAsync), Button("Export selected…", ExportAsync) } },
      Button("Attach flight log…", AttachAsync),
    } } };
    // Access controls stay reachable even when agent details or Advanced need scrolling.
    var sessionActions = new WrapPanel { Children = {
      Button("Allow", () => SessionAction(0)), Button("Revoke", () => SessionAction(1), true),
      Button("Disconnect", () => SessionAction(2), true), Button("Allow all", AllowAllAsync),
      Button("Revoke all", RevokeAllAsync, true), Button("Stop all connections", StopAsync, true),
    } };
    var body = new StackPanel { Spacing = 8, Children = {
      new TextBlock { Text = "AI agent connection", FontSize = 20 },
      new TextBlock { Text = "Launch gives the agent full control of Mission Planner: screens, controls, missions, parameters, logs and vehicle commands, "
          + "using whatever is loaded or connected in the application. Provider login belongs to the agent.", TextWrapping = TextWrapping.Wrap },
      new TextBlock { Text = "Launch an installed agent", FontWeight = FontWeight.Bold },
      _agentButtons, _desktopButtons, _agentState,
      new TextBlock { Text = "Initial task for terminal agents" }, _task,
      new TextBlock { Text = "Sessions — names are reported by the clients", FontWeight = FontWeight.Bold },
      _sessions,

      new TextBlock { Text = "Persistent local port for desktop applications", FontWeight = FontWeight.Bold },
      new WrapPanel { Children = { _desktopPort, Button("Open port", OpenDesktopAsync), Button("Close port", StopDesktopAsync, true), _autoOpen } },
      _desktopEndpoint, _desktopState,
      advanced,
      new TextBlock { Text = "Activity", FontWeight = FontWeight.Bold },
      _output,
    } };
    Content = new Avalonia.Controls.Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8,
      Children = { new ScrollViewer { Content = body }, sessionActions, _status } };
    Avalonia.Controls.Grid.SetRow(sessionActions, 1);
    Avalonia.Controls.Grid.SetRow(_status, 2);
    _output.Text = hub.RecentOutput;
    _onOutput = Output; hub.Output += _onOutput;
    _timer.Tick += (_, _) => Refresh(); _timer.Start();
    Opened += async (_, _) => {
      Refresh();
      try { await FindAgentsAsync(); } catch (OperationCanceledException) { }
      catch (Exception e) { _status.Text = "Agent discovery: " + e.Message; }
    };
    Closed += (_, _) => { _closed = true; _timer.Stop(); hub.Output -= _onOutput; };
  }

  internal async Task FindAgentsAsync() {
    var agents = await Hub.DiscoverAsync();
    if (_closed) { return; }
    RefreshAgents(agents);
    _status.Text = $"Found {agents.Length} installed agent(s). Nothing was launched.";
  }

  /// <summary>One Launch button per installed agent, as in X-Office; agents that are not installed have no button.</summary>
  private void RefreshAgents(McpAgent[]? agents = null) {
    agents ??= Hub.Agents;
    _agentButtons.Children.Clear(); _desktopButtons.Children.Clear();
    var notes = new List<string>();
    foreach (var agent in agents) {
      _agentButtons.Children.Add(Button($"Launch {agent.Name}", () => LaunchAgentAsync(agent)));
      if (!agent.IsDesktop) { continue; }
      string state = McpDesktopRegistration.RegistrationState(agent.Kind, Hub.DesktopPort);
      bool registered = state == "Registered";
      _desktopButtons.Children.Add(Button(registered ? $"Unregister {agent.Name}" : $"Register {agent.Name}",
          () => registered ? UnregisterDesktopAsync(agent) : RegisterDesktopAsync(agent)));
      notes.Add($"{agent.Name}: {state}.");
    }
    _agentButtons.Children.Add(Button("Find agents", FindAgentsAsync));
    if (notes.Count != 0) {
      notes.Add("Desktop applications read MCP settings when they start: restart them after registering. "
          + "They see Mission Planner only while the persistent port is open; Register switches on \"Open at startup\".");
    }
    _agentState.Text = agents.Length == 0
        ? "No installed agents found: install Codex CLI, Claude Code, Codex/ChatGPT Desktop, Claude Desktop or LM Studio, or use a custom executable under Advanced."
        : string.Join(Environment.NewLine, new[] { "Terminal agents open in a new terminal with the initial task below." }.Concat(notes));
    _desktopButtons.IsVisible = _desktopButtons.Children.Count != 0;
    if (_autoOpen.IsChecked != Hub.AutoOpenDesktopPort) { _autoOpen.IsChecked = Hub.AutoOpenDesktopPort; }
  }

  private Task SessionAction(int action) {
    var row = _sessions.SelectedItem as SessionRow ?? throw new InvalidOperationException("Select a session first.");
    if (action == 0) { Hub.AllowSession(row.Info.Id); }
    else if (action == 1) { Hub.RevokeSession(row.Info.Id); }
    else { Hub.DisconnectSession(row.Info.Id); }
    Refresh(); return Task.CompletedTask;
  }
  private Task AllowAllAsync() { Hub.AllowAll(); Refresh(); return Task.CompletedTask; }
  private Task RevokeAllAsync() { Hub.RevokeAll(); Refresh(); return Task.CompletedTask; }
  private async Task RegisterDesktopAsync(McpAgent agent) {
    await Hub.RegisterDesktopAsync(agent);
    RefreshAgents(); Refresh();
    _status.Text = $"{agent.Name} registered on port {Hub.DesktopPort}. Restart it to load MCP settings; the port opens at startup.";
  }
  private async Task UnregisterDesktopAsync(McpAgent agent) {
    await Hub.UnregisterDesktopAsync(agent);
    RefreshAgents();
    _status.Text = $"{agent.Name} registration removed. Restart it to reload its settings.";
  }
  private async Task OpenDesktopAsync() {
    await Hub.OpenDesktopPortAsync();
    _status.Text = "Persistent port open. Self-connected sessions wait for Allow.";
  }
  private async Task StopDesktopAsync() { await Hub.CloseDesktopPortAsync(); Refresh(); }

  private Button Button(string label, Func<Task> action, bool interrupt = false) {
    var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) };
    button.Click += async (_, _) => {
      if (_closed || (_activeOperations != 0 && !interrupt)) { return; }
      _activeOperations++;
      try { await action(); } catch (OperationCanceledException) { _status.Text = "Cancelled."; }
      catch (Exception e) { _status.Text = e.Message; }
      finally { _activeOperations--; }
    };
    return button;
  }

  private void Refresh() {
    var rows = Hub.SessionRows().Select(pair => new SessionRow(pair.Server, pair.Info)).ToArray();
    if (_sessions.ItemsSource is not SessionRow[] currentRows || !currentRows.SequenceEqual(rows)) {
      string? selected = (_sessions.SelectedItem as SessionRow)?.Info.Id;
      _sessions.ItemsSource = rows;
      _sessions.SelectedItem = rows.FirstOrDefault(r => r.Info.Id == selected);
    }
    lock (_outputSync) {
      if (_pendingOutput.Length != 0) {
        string value = (_output.Text ?? "") + _pendingOutput;
        _output.Text = value.Length <= 65536 ? value : value[^65536..];
        _pendingOutput.Clear();
      }
    }
    _sessionEndpoint.Text = Hub.SessionServer?.Endpoint?.AbsoluteUri ?? "";
    _desktopEndpoint.Text = Hub.DesktopServer?.Endpoint?.AbsoluteUri ?? "";
    _desktopState.Text = Hub.DesktopState;
    _desktopPort.IsEnabled = Hub.DesktopServer == null;
    if (_autoOpen.IsChecked != Hub.AutoOpenDesktopPort) { _autoOpen.IsChecked = Hub.AutoOpenDesktopPort; }
    var proposals = Hub.Vehicles.Proposals();
    if (_proposals.ItemsSource is not ParameterProposal[] current || !current.SequenceEqual(proposals)) {
      object? selected = _proposals.SelectedItem;
      _proposals.ItemsSource = proposals; _proposals.SelectedItem = selected;
    }
  }

  private void Output(string message) {
    lock (_outputSync) {
      _pendingOutput.Append(message);
      if (_pendingOutput.Length > 65536) { _pendingOutput.Remove(0, _pendingOutput.Length - 65536); }
    }
  }

  private async Task StartAsync() {
    var server = await Hub.OpenSessionPortAsync();
    _sessionEndpoint.Text = server.Endpoint!.AbsoluteUri;
    _status.Text = "Session port listening on loopback with a temporary token.";
  }

  private async Task StopAsync() {
    await Hub.StopAllAsync();
    _status.Text = "All MCP connections closed; session credentials revoked.";
    Refresh();
  }

  private async Task AttachAsync() {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Attach flight logs", AllowMultiple = true,
      FileTypeFilter = [new FilePickerFileType("Flight logs") { Patterns = ["*.bin", "*.log", "*.tlog"] }],
    });
    if (_closed) { return; }
    foreach (var file in files) {
      if (file.TryGetLocalPath() is string path) {
        var info = Hub.Logs.Attach(path);
        Output($"Attached {info.Name}; log ID {info.Id}\n");
      }
    }
  }

  private async Task CopyAsync() {
    var server = Hub.SessionServer ?? Hub.DesktopServer ?? throw new InvalidOperationException("Open the session port or the persistent port first.");
    if (Clipboard != null) {
      string configuration = "[mcp_servers.missionplanner]\nurl = " + JsonSerializer.Serialize(server.Endpoint!.AbsoluteUri)
          + (server.RequiresToken ? "\nhttp_headers = { Authorization = \"Bearer " + server.Token + "\" }" : "") + "\ntool_timeout_sec = 720\n";
      await Clipboard.SetTextAsync(configuration);
      _status.Text = "Copied connection settings. Stop all connections revokes current access.";
    }
  }

  private async Task ChooseExecutableAsync() {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select native codex or claude executable" });
    if (files.Count > 0 && files[0].TryGetLocalPath() is string path) { _customExecutable.Text = path; }
  }

  private async Task LaunchCustomAsync() {
    string executable = _customExecutable.Text?.Trim() ?? "";
    if (!File.Exists(executable)) { throw new InvalidOperationException("Select an existing codex or claude executable first."); }
    var kind = McpAgentDiscovery.TerminalKind(executable)
        ?? throw new InvalidOperationException("The custom executable must be named codex or claude so the matching launch flags are used.");
    await LaunchAgentAsync(new McpAgent(kind, (kind == McpAgentKind.ClaudeCode ? "Claude Code" : "Codex CLI") + " (custom)", executable, []));
  }
  private async Task LaunchAgentAsync(McpAgent agent) {
    _status.Text = $"Launching {agent.Name}…";
    await Hub.LaunchAsync(agent, _workingDirectory.Text ?? "", _task.Text ?? "");
    Refresh(); RefreshAgents();
    _status.Text = agent.IsDesktop
        ? $"{agent.Name} launched; its sessions on port {Hub.DesktopPort} are allowed. You can close this window."
        : $"{agent.Name} launched in a terminal with full access. You can close this window.";
  }

  private ParameterProposal Selected() => _proposals.SelectedItem as ParameterProposal
      ?? throw new InvalidOperationException("Select a proposal first.");

  private async Task ExportAsync() {
    var proposal = Selected();
    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Export proposed parameters", SuggestedFileName = "agent-proposal.param",
    });
    if (file == null) { return; }
    await using var stream = await file.OpenWriteAsync();
    stream.SetLength(0);
    await using var writer = new StreamWriter(stream);
    await writer.WriteLineAsync("# Proposed values only; review aircraft identity and evidence before applying.");
    foreach (var change in proposal.Changes) {
      await writer.WriteLineAsync(change.Name + "," + change.Proposed.ToString("R", CultureInfo.InvariantCulture));
    }
  }

  private async Task ApplyAsync() {
    var proposal = Selected();
    var cancellation = Hub.LocalOperationToken;
    var target = Hub.Vehicles.Resolve(proposal.TargetId, true);
    McpVehicleAccess.RequireDisarmed(target);
    if (target.Connection.Link.ReadOnly) {
      throw new InvalidOperationException("Applying proposals requires a disarmed vehicle and a writable connection.");
    }
    string summary = $"Vehicle {target.State.sysid}/{target.State.compid} on {target.Connection.Endpoint}\n\n"
        + proposal.Rationale + "\n\n" + string.Join("\n", proposal.Changes.Select(c =>
            $"{c.Name}: {c.Expected.ToString("R", CultureInfo.InvariantCulture)} → {c.Proposed.ToString("R", CultureInfo.InvariantCulture)}\n{c.Reason}"));
    var review = new Window { Title = "Review parameter changes", Width = 780, Height = 640,
      MinWidth = 560, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
    var apply = new Button { Content = "Apply to disarmed vehicle" };
    var progress = new TextBlock { TextWrapping = TextWrapping.Wrap };
    var body = new TextBox { Text = summary, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    var actions = new WrapPanel { Children = { cancel, apply } };
    review.Content = new Avalonia.Controls.Grid { Margin = new Thickness(12), RowSpacing = 8,
      RowDefinitions = new RowDefinitions("*,Auto,Auto"), Children = { body, progress, actions } };
    Avalonia.Controls.Grid.SetRow(progress, 1); Avalonia.Controls.Grid.SetRow(actions, 2);
    bool applying = false;
    cancel.Click += (_, _) => { if (!applying) { review.Close(); } };
    review.Closing += (_, e) => e.Cancel = applying;
    apply.Click += async (_, _) => {
      applying = true; apply.IsEnabled = false; cancel.IsEnabled = false;
      progress.Text = "Applying and verifying. Wait for completion before disconnecting or arming.";
      try { _status.Text = await McpProposalWriter.ApplyAsync(Hub.Vehicles, proposal, cancellation); }
      catch (Exception e) { _status.Text = e.Message; }
      finally { applying = false; review.Close(); }
    };
    // Keep ordinary UI writes unavailable throughout review/application. The core also serializes
    // parameter writers and compares a fresh value immediately before every proposal write.
    Window? mainOwner = Owner as Window;
    bool ownerEnabled = mainOwner?.IsEnabled ?? true;
    try {
      if (mainOwner != null) { mainOwner.IsEnabled = false; }
      await review.ShowDialog(this);
    } finally { if (mainOwner != null) { mainOwner.IsEnabled = ownerEnabled; } }
  }
}
