using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner.Controls;
using MissionPlanner.Services.Mcp;
using MissionPlanner.ViewModels;
using MissionPlanner.Views;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MissionPlanner.Tests;

public sealed class McpUiTests {
  private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, MissionPlannerMcpTools.JsonOptions);
  private static JsonElement Data(CallToolResult value) => JsonDocument.Parse(Assert.IsType<TextContentBlock>(value.Content[0]).Text).RootElement.Clone();

  [Fact]
  public async Task Operation_journal_reserves_before_dispatch_and_recovers_without_duplicate_mutation() {
    var journal = new McpOperationJournal();
    var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    int calls = 0;
    Task<object> Change() { calls++; return release.Task; }
    var first = journal.RunAsync("op-1", "change", new { n = 1 }, Change, default);
    Assert.Equal("running", journal.Get("op-1").Status);
    Assert.Equal("running", (await journal.RunAsync("op-1", "change", new { n = 1 }, Change, default)).Status);
    await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RunAsync("op-1", "change", new { n = 2 }, Change, default));
    release.SetResult(new { changed = true });
    var receipt = await first;
    Assert.Same(receipt, await journal.RunAsync("op-1", "change", new { n = 1 }, Change, default));
    Assert.Equal(1, calls); Assert.Equal("completed", receipt.Status);
    Assert.Equal("unknown_operation", journal.Get("other").Status);
  }
  [Fact]
  public async Task Operation_journal_keeps_cancelled_receipts_and_rejects_capacity_without_eviction() {
    var journal = new McpOperationJournal(); int calls = 0;
    var cancelled = await journal.RunAsync("cancel", "change", new { }, () => { calls++; throw new OperationCanceledException(); }, default);
    Assert.Equal("cancelled", cancelled.Status);
    Assert.Same(cancelled, await journal.RunAsync("cancel", "change", new { }, () => { calls++; return Task.FromResult<object>(new { }); }, default));
    Assert.Equal(1, calls);
    for (int i = 1; i < McpOperationJournal.Capacity; i++) { await journal.RunAsync("op" + i, "change", new { }, () => Task.FromResult<object>(new { }), default); }
    await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RunAsync("overflow", "change", new { }, () => Task.FromResult<object>(new { }), default));
    Assert.Same(cancelled, journal.Get("cancel"));
    using var stop = new CancellationTokenSource(); stop.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new McpOperationJournal().RunAsync("stop", "change", new { }, () => throw new Exception("must not dispatch"), stop.Token));
  }
  [Theory]
  [InlineData(0, 0, 16, 3, true)]
  [InlineData(91, 0, 16, 3, false)]
  [InlineData(0, -181, 16, 3, false)]
  [InlineData(0, 0, 65535, 3, false)]
  [InlineData(0, 0, 16, 1, false)]
  public void Draft_validation_uses_known_commands_and_explicit_global_frames(double lat, double lng, int command, int frame, bool valid) {
    var result = McpMissionDraft.Validate(new(0, 0, 5), [new((ushort)command, (byte)frame, lat, lng, 40)]);
    Assert.Equal(valid, result.Valid); Assert.Contains(result.Warnings, text => text.Contains("Structural checks only"));
  }
  [Fact]
  public void Draft_validation_rejects_nonfinite_and_bad_jump_targets() {
    Assert.False(McpMissionDraft.Validate(new(0, 0, 0), [new(16, 3, 0, 0, double.NaN)]).Valid);
    Assert.False(McpMissionDraft.Validate(new(0, 0, 0), [new(177, 3, 0, 0, 0, P1: 2, P2: 1)]).Valid);
    Assert.False(McpMissionDraft.Validate(new(0, 0, 0), [null!]).Valid);
    Assert.Throws<ArgumentNullException>(() => McpMissionDraft.Validate(null!, []));
  }
  [AvaloniaFact]
  public void Draft_replace_has_compare_and_swap_and_one_native_undo_including_home() {
    var vm = new FlightPlannerViewModel { VerifyHeight = false };
    var priorPlannedHome = AppState.comPort.MAV.cs.PlannedHomeLocation;
    try {
      vm.AddWaypointAt(34, 33); vm.MissionType = "Fence"; vm.AddWaypointAt(35, 32); vm.MissionType = "Mission";
      vm.HomeLat = 34; vm.HomeLng = 33; vm.HomeAlt = 5;
      string original = McpMissionDraft.Revision(vm);
      var result = Data(McpMissionDraft.Replace(vm, original, new(40, 41, 6), [new(16, 3, 40.1, 41.1, 50), new(16, 3, 40.2, 41.2, 60)]));
      Assert.False(result.GetProperty("uploaded").GetBoolean()); Assert.Equal(2, vm.Waypoints.Count);
      Assert.Throws<InvalidOperationException>(() => McpMissionDraft.Replace(vm, original, new(0, 0, 0), []));
      vm.UndoCommand.Execute(null);
      Assert.Equal(original, McpMissionDraft.Revision(vm)); Assert.Single(vm.Waypoints);
      vm.MissionType = "Fence"; Assert.Single(vm.Waypoints);
      Assert.Throws<InvalidOperationException>(() => McpMissionDraft.Replace(vm, McpMissionDraft.Revision(vm), new(0, 0, 0), []));
      var page = Data(McpMissionDraft.Read(vm, int.MaxValue, 200));
      Assert.True(page.GetProperty("complete").GetBoolean()); Assert.Empty(page.GetProperty("items").EnumerateArray());
    } finally { AppState.comPort.MAV.cs.PlannedHomeLocation = priorPlannedHome; }
  }
  [AvaloniaFact]
  public async Task Ui_serialization_has_no_late_queue_and_cancellation_prevents_dispatch() {
    using var logs = new McpLogCatalog(); var host = new McpUiHost(null!, logs, () => null);
    var release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = host.Mutate(() => release.Task, default);
    await Assert.ThrowsAsync<InvalidOperationException>(() => host.Mutate(() => throw new Exception("must not dispatch"), default));
    release.SetResult(new { }); await first;
    using var stop = new CancellationTokenSource(); stop.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Navigate("PLAN", stop.Token));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.OpenLog("invalid", stop.Token));
  }
  [AvaloniaFact]
  public async Task Log_ui_uses_native_plot_and_rejects_stale_hidden_revoked_and_cross_session_controls() {
    string path = McpServerTests.TemporaryLog();
    var logs = new McpLogCatalog(); var host = new McpUiHost(null!, logs, () => null);
    var session = new McpConnectionSession(null!, "", "test", true, true);
    string? viewId = null;
    try {
      string logId = logs.Attach(path).Id;
      var opened = Data(await host.Mutate(() => host.OpenLog(logId, default), default));
      viewId = opened.GetProperty("viewId").GetString()!;
      string revision = opened.GetProperty("revision").GetString()!;
      Assert.DoesNotContain(path, revision);
      var plotted = Data(await host.Mutate(() => host.PlotLog(viewId, revision, [new("PARM", "Value")], 0, 3, default), default));
      Assert.Equal(2, plotted.GetProperty("samples")[0].GetInt32());
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.PlotLog(viewId, revision, [new("PARM", "Value")], 0, 3, default));
      Dispatcher.UIThread.RunJobs();
      var inspected = Data(await host.Inspect(session, null, null, false, default));
      var targets = session.UiSnapshot!.Targets;
      foreach (string name in new[] { "ClearBtn", "ScaleBox", "OffsetBox", "MapToggle" }) { Assert.Contains(targets, t => t.Name == name && t.WindowId == viewId); }
      Assert.Contains(inspected.GetProperty("windows").EnumerateArray(), w => w.GetProperty("windowId").GetString() == viewId);
      var scale = Assert.Single(targets, t => t.Name == "ScaleBox");
      Assert.Equal("number", scale.Kind); Assert.Contains("set_value", scale.Actions);
      string snapshot = session.UiSnapshot.Id;
      var otherSession = new McpConnectionSession(null!, "", "test", true, true);
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetValue(otherSession, snapshot, scale.Id, Data(2), default));
      await host.Mutate(() => host.SetValue(session, snapshot, scale.Id, Data(2), default), default);
      Assert.Equal(2, ((NumericUpDown)scale.Control).Value);
      // Snapshots stay valid for several actions while the layout is unchanged.
      await host.Mutate(() => host.SetValue(session, snapshot, scale.Id, Data(3), default), default);
      Assert.Equal(3, ((NumericUpDown)scale.Control).Value);
      var clear = session.UiSnapshot.Targets.Single(t => t.Name == "ClearBtn");
      clear.Control.IsEnabled = false;
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke(session, snapshot, clear.Id, default));
      clear.Control.IsEnabled = true;
      await host.Inspect(session, null, null, false, default); snapshot = session.UiSnapshot!.Id;
      var again = session.UiSnapshot.Targets.Single(t => t.Name == "ClearBtn");
      Assert.Equal(clear.Id, again.Id);
      session.Revoke();
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke(session, snapshot, clear.Id, default));
      var fresh = new McpConnectionSession(null!, "", "test", true, true);
      await host.Inspect(fresh, viewId, "clear", false, default); snapshot = fresh.UiSnapshot!.Id;
      clear = Assert.Single(fresh.UiSnapshot.Targets, t => t.Name == "ClearBtn");
      await host.Mutate(() => host.Invoke(fresh, snapshot, clear.Id, default), default);
      var window = (LogBrowseWindow)TopLevel.GetTopLevel(clear.Control)!;
      var plot = ((LogBrowseView)window.Content!).FindControl<LivePlot>("Plot")!;
      Assert.Empty(plot.SeriesLabels);
      await host.Inspect(fresh, viewId, "MapToggle", false, default); snapshot = fresh.UiSnapshot!.Id;
      var toggle = Assert.Single(fresh.UiSnapshot.Targets, t => t.Name == "MapToggle");
      await host.Mutate(() => host.SetValue(fresh, snapshot, toggle.Id, Data(true), default), default);
      Assert.True(((ToggleButton)toggle.Control).IsChecked);
      await host.Mutate(() => host.Invoke(fresh, snapshot, toggle.Id, default), default);
      Assert.False(((ToggleButton)toggle.Control).IsChecked);
      await Assert.ThrowsAsync<ArgumentException>(() => host.Capture(viewId + "/plot", 2000, 800, default));
      await Assert.ThrowsAsync<ArgumentException>(() => host.Capture("consent", 800, 600, default));
      var image = await host.Capture("window:" + viewId, 400, 300, default);
      // The headless renderer produces empty PNG streams; real pixels are checked in the Xvfb acceptance run.
      Assert.True(image.Width <= 400 && image.Height <= 300, $"capture {image.Width}x{image.Height}");
      await Assert.ThrowsAsync<ArgumentException>(() => host.Inspect(fresh, "window-missing", null, false, default));
      string other = McpServerTests.TemporaryLog();
      try {
        await ((LogBrowseViewModel)window.DataContext!).LoadFileAsync(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Capture(viewId + "/plot", 800, 600, default));
      } finally { window.Close(); viewId = null; File.Delete(other); }
      Dispatcher.UIThread.RunJobs();
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.Invoke(fresh, snapshot, toggle.Id, default));
    } finally {
      if (viewId != null) { await host.CloseLog(viewId, default); }
      // The catalog keeps the attached log open; Windows cannot delete it until it is disposed.
      logs.Dispose(); File.Delete(path);
    }
  }

  private sealed class CountingCommand : System.Windows.Input.ICommand {
    public int Calls; public object? Parameter;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { Calls++; Parameter = parameter; }
  }

  [AvaloniaFact]
  public async Task Generic_inspection_reads_labels_clicks_buttons_sets_values_and_lists_menus() {
    var command = new CountingCommand(); int clicks = 0, menuClicks = 0;
    var button = new Button { Name = "GoBtn", Content = "Go", Command = command, CommandParameter = "p" };
    button.Click += (_, _) => clicks++;
    var speed = new NumericUpDown { Name = "SpeedBox", Minimum = 0, Maximum = 100, Value = 5 };
    var text = new TextBox { Name = "NameBox", Watermark = "Vehicle name" };
    var secret = new TextBox { Name = "Secret", PasswordChar = '*' };
    var check = new CheckBox { Name = "Check", Content = "Enable" };
    var combo = new ComboBox { Name = "Frame", ItemsSource = new[] { "Quad", "Hexa", "Octo" }, SelectedIndex = 0 };
    var tabs = new TabControl { Name = "Tabs", Items = { new TabItem { Header = "First", Content = new TextBlock { Text = "one" } }, new TabItem { Header = "Second", Content = new TextBlock { Text = "two" } } } };
    var item = new MenuItem { Header = "Do thing" }; item.Click += (_, _) => menuClicks++;
    var menu = new Menu { Items = { new MenuItem { Header = "Tools", Items = { item } } } };
    var window = new Window { Title = "Form", Width = 400, Height = 400, Content = new StackPanel { Children = {
      menu, new TextBlock { Text = "Speed" }, speed, text, secret, check, combo, tabs, button, new TextBlock { Text = "Status: idle" } } } };
    using var logs = new McpLogCatalog(); var host = new McpUiHost(null!, logs, () => window);
    var session = new McpConnectionSession(null!, "", "test", true, true);
    try {
      window.Show(); Dispatcher.UIThread.RunJobs();
      var inspected = Data(await host.Inspect(session, "main", null, true, default));
      var controls = inspected.GetProperty("controls").EnumerateArray().ToArray();
      Assert.DoesNotContain(controls, c => c.GetProperty("name").GetString() == "Secret");
      Assert.Contains(controls, c => c.GetProperty("kind").GetString() == "static" && c.GetProperty("value").GetString() == "Status: idle");
      var targets = session.UiSnapshot!.Targets; string snapshot = session.UiSnapshot.Id;
      Assert.Equal("Speed", targets.Single(t => t.Name == "SpeedBox").Label);
      Assert.Equal("Vehicle name", targets.Single(t => t.Name == "NameBox").Label);
      Assert.Equal("Go", targets.Single(t => t.Name == "GoBtn").Label);
      var menuTarget = Assert.Single(targets, t => t.Kind == "menu");
      Assert.Equal("Tools > Do thing", menuTarget.Label);
      await host.Mutate(() => host.Invoke(session, snapshot, targets.Single(t => t.Name == "GoBtn").Id, default), default);
      Assert.Equal(1, clicks); Assert.Equal(1, command.Calls); Assert.Equal("p", command.Parameter);
      await host.Mutate(() => host.Invoke(session, snapshot, menuTarget.Id, default), default);
      Assert.Equal(1, menuClicks);
      await host.Mutate(() => host.SetValue(session, snapshot, targets.Single(t => t.Name == "SpeedBox").Id, Data(250), default), default);
      Assert.Equal(100, speed.Value);
      await host.Mutate(() => host.SetValue(session, snapshot, targets.Single(t => t.Name == "NameBox").Id, Data("Bravo"), default), default);
      Assert.Equal("Bravo", text.Text);
      await host.Mutate(() => host.SetValue(session, snapshot, targets.Single(t => t.Name == "Check").Id, Data(true), default), default);
      Assert.True(check.IsChecked);
      await host.Mutate(() => host.SetValue(session, snapshot, targets.Single(t => t.Name == "Frame").Id, Data("Octo"), default), default);
      Assert.Equal(2, combo.SelectedIndex);
      await host.Mutate(() => host.SetValue(session, snapshot, targets.Single(t => t.Name == "Tabs").Id, Data("Second"), default), default);
      Assert.Equal(1, tabs.SelectedIndex);
      await Assert.ThrowsAsync<ArgumentException>(() => host.SetValue(session, snapshot, targets.Single(t => t.Name == "Frame").Id, Data("Tricopter"), default));
      await Assert.ThrowsAsync<ArgumentException>(() => host.SetValue(session, snapshot, targets.Single(t => t.Name == "GoBtn").Id, Data(1), default));
      await Assert.ThrowsAsync<ArgumentException>(() => host.CloseWindow("main", default));
      var state = Data(await host.State(default).ContinueWith(t => t.IsFaulted ? (object)new { } : t.Result));
      _ = state;
    } finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
  }

  [AvaloniaFact]
  public async Task Navigation_reaches_every_screen_and_selects_backstage_pages() {
    var main = new MainWindowViewModel();
    using var logs = new McpLogCatalog(); var host = new McpUiHost(main, logs, () => null);
    bool protect = MissionPlanner.Utilities.Settings.Instance.GetBoolean("password_protect", false);
    MissionPlanner.Utilities.Settings.Instance["password_protect"] = false.ToString();
    try {
      foreach (string route in new[] { "PLAN", "SETUP", "CONFIG", "DATA" }) {
        var result = Data(await host.Navigate(route, default));
        Assert.True(result.GetProperty("completed").GetBoolean(), route); Assert.Equal(route, main.ActiveTab);
      }
      await Assert.ThrowsAsync<ArgumentException>(() => host.Navigate("SECRET", default));
      var state = Data(await host.State(default));
      var pages = state.GetProperty("setupPages").GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("header").GetString()!).ToArray();
      Assert.NotEmpty(pages);
      var selected = Data(await host.SelectPage("SETUP", pages[0], default));
      Assert.True(selected.GetProperty("completed").GetBoolean()); Assert.Equal("SETUP", main.ActiveTab);
      await Assert.ThrowsAsync<ArgumentException>(() => host.SelectPage("CONFIG", "No such page", default));
      await Assert.ThrowsAsync<ArgumentException>(() => host.UploadMission("orbit", false, false, default));
      await Assert.ThrowsAsync<InvalidOperationException>(() => host.ElevationProfile(default));
    } finally { MissionPlanner.Utilities.Settings.Instance["password_protect"] = protect.ToString(); main.Dispose(); }
  }

  [Fact]
  public async Task Vehicle_control_validates_arguments_and_targets_before_touching_a_link() {
    var vehicles = new McpVehicleAccess(() => []);
    await Assert.ThrowsAsync<ArgumentException>(() => vehicles.WriteParameters("t", [], "reason text", false, default));
    await Assert.ThrowsAsync<ArgumentException>(() => vehicles.WriteParameters("t", [new("A", 1), new("A", 2)], "reason text", false, default));
    await Assert.ThrowsAsync<ArgumentException>(() => vehicles.WriteParameters("t", [new("A", 1)], "why", false, default));
    await Assert.ThrowsAsync<ArgumentException>(() => vehicles.WriteParameters("missing", [new("A", 1)], "reason text", false, default));
    await Assert.ThrowsAsync<ArgumentException>(() => vehicles.Command("missing", "rtl", null, default));
    await Assert.ThrowsAsync<ArgumentException>(() => vehicles.Command("missing", "explode", null, default));
    Assert.Throws<ArgumentException>(() => vehicles.Modes("missing"));
    Assert.Contains("set_mode", McpVehicleAccess.Commands); Assert.Contains("mavlink_command", McpVehicleAccess.Commands);
    Assert.Throws<ArgumentException>(() => McpVehicleAccess.Terrain([], default));
    Assert.Throws<ArgumentException>(() => McpVehicleAccess.Terrain([new(91, 0)], default));
    foreach (string name in new[] { "write_parameters", "vehicle_command", "mission_upload", "mission_download", "ui_select_page", "ui_close_window" }) { Assert.False(MissionPlannerMcpServer.IsPassiveTool(name)); }
    foreach (string name in new[] { "vehicle_modes", "terrain_elevation" }) { Assert.True(MissionPlannerMcpServer.IsPassiveTool(name)); }
  }

  [AvaloniaFact]
  public async Task Real_http_ui_mutation_requires_allow_replays_once_and_survives_revoke_allow() {
    var main = new MainWindowViewModel();
    var previousHome = AppState.comPort.MAV.cs.PlannedHomeLocation;
    using var logs = new McpLogCatalog(); var host = new McpUiHost(main, logs, () => null);
    await using var server = new MissionPlannerMcpServer(new(() => []), logs) { UiHost = host };
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp }, http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    try {
      var denied = await client.CallToolAsync("ui_get_state", cancellationToken: timeout.Token);
      Assert.True(denied.IsError); Assert.Contains("permission_required", Assert.IsType<TextContentBlock>(denied.Content[0]).Text);
      string id = Assert.Single(server.Sessions).Id; server.AllowSession(id);
      var draft = Data(await client.CallToolAsync("mission_draft_get", cancellationToken: timeout.Token));
      var args = new Dictionary<string, object?> { ["operationId"] = "replace-1", ["expectedRevision"] = draft.GetProperty("revision").GetString(),
        ["home"] = new { latitude = 34, longitude = 33, altitudeMetres = 10 }, ["items"] = new[] { new { command = 16, frame = 3, latitude = 34.1, longitude = 33.1, altitudeMetres = 50 } } };
      var changed = await client.CallToolAsync("mission_draft_replace", args, cancellationToken: timeout.Token);
      Assert.NotEqual(true, changed.IsError); Assert.Equal("completed", Data(changed).GetProperty("status").GetString());
      string changedRevision = McpMissionDraft.Revision(main.FlightPlanner);
      var replay = await client.CallToolAsync("mission_draft_replace", args, cancellationToken: timeout.Token);
      Assert.Equal(Data(changed).ToString(), Data(replay).ToString());
      Assert.Single(main.FlightPlanner.Waypoints);
      server.RevokeSession(id);
      Assert.True((await client.CallToolAsync("mission_draft_replace", args, cancellationToken: timeout.Token)).IsError);
      server.AllowSession(id);
      var status = Data(await client.CallToolAsync("ui_operation_status", new Dictionary<string, object?> { ["operationId"] = "replace-1" }, cancellationToken: timeout.Token));
      Assert.Equal("completed", status.GetProperty("status").GetString());
      var undo = await client.CallToolAsync("mission_draft_undo", new Dictionary<string, object?> { ["operationId"] = "undo-1", ["expectedRevision"] = changedRevision }, cancellationToken: timeout.Token);
      Assert.NotEqual(true, undo.IsError); Assert.Equal(draft.GetProperty("revision").GetString(), McpMissionDraft.Revision(main.FlightPlanner));
      var state = Data(await client.CallToolAsync("ui_get_state", cancellationToken: timeout.Token));
      Assert.Equal("DATA", state.GetProperty("activeScreen").GetString());
    } finally { AppState.comPort.MAV.cs.PlannedHomeLocation = previousHome; }
  }

  [Fact]
  public async Task Official_http_client_reads_embedded_docs_and_discovers_ui_schemas_without_server_internals() {
    await using var server = new MissionPlannerMcpServer(new(() => []));
    await server.StartAsync(_ => Task.FromResult<object>(new { }));
    using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.IssueLaunchToken());
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = server.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp }, http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    Assert.Equal(55, tools.Count);
    var mutation = tools.Single(t => t.Name == "mission_draft_replace");
    Assert.DoesNotContain("server", mutation.JsonSchema.ToString());
    Assert.Contains("expectedRevision", mutation.JsonSchema.ToString());
    var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);
    Assert.Equal(3, resources.Count);
    var document = await client.ReadResourceAsync(McpDocumentation.StartUri, cancellationToken: timeout.Token);
    Assert.Contains("ui_operation_status", Assert.IsType<TextResourceContents>(Assert.Single(document.Contents)).Text);
    foreach (string uri in new[] { "missionplanner://documentation/../../etc/passwd", "missionplanner://documentation/AI_START.md?file=secret", "file:///etc/passwd" }) {
      await Assert.ThrowsAnyAsync<McpException>(async () => { await client.ReadResourceAsync(uri, cancellationToken: timeout.Token); });
    }
    var validation = await client.CallToolAsync("mission_draft_validate", new Dictionary<string, object?> { ["home"] = new { latitude = 0, longitude = 0, altitudeMetres = 0 }, ["items"] = Array.Empty<object>() }, cancellationToken: timeout.Token);
    Assert.True(Data(validation).GetProperty("valid").GetBoolean());
    var unavailable = await client.CallToolAsync("ui_get_state", cancellationToken: timeout.Token);
    Assert.True(unavailable.IsError); Assert.Contains("UI unavailable", Assert.IsType<TextContentBlock>(unavailable.Content[0]).Text);
    foreach (string name in new[] { "ui_get_state", "ui_capture", "ui_inspect", "ui_operation_status", "mission_draft_replace", "mission_draft_undo", "future_unknown_tool" }) { Assert.False(MissionPlannerMcpServer.IsPassiveTool(name)); }
    foreach (string name in new[] { "mission_draft_get", "mission_command_schema", "mission_draft_validate" }) { Assert.True(MissionPlannerMcpServer.IsPassiveTool(name)); }
  }
}
