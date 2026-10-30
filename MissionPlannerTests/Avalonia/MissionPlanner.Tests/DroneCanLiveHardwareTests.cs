using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner.Comms;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;
using Xunit.Abstractions;

namespace MissionPlanner.Tests;

/// <summary>
/// Opt-in acceptance against a real autopilot: set MP_LIVE_CAN_PORT (for example /dev/ttyACM0),
/// MP_LIVE_CAN_BUS (1 or 2, default 2) and optionally MP_LIVE_CAN_NODE (expected node id).
/// Without the variables the test returns immediately so the regular suite stays offline.
/// </summary>
public class DroneCanLiveHardwareTests(ITestOutputHelper output) {
  [AvaloniaFact]
  public async Task Live_autopilot_lists_nodes_names_them_and_reads_parameters_over_mavlink_can() {
    string? port = Environment.GetEnvironmentVariable("MP_LIVE_CAN_PORT");
    if (string.IsNullOrEmpty(port)) {
      output.WriteLine("MP_LIVE_CAN_PORT not set; live DroneCAN acceptance skipped.");
      return;
    }
    int bus = int.TryParse(Environment.GetEnvironmentVariable("MP_LIVE_CAN_BUS"), out int b) ? b : 2;
    int? expectedNode = int.TryParse(Environment.GetEnvironmentVariable("MP_LIVE_CAN_NODE"), out int n) ? n : null;

    var link = new MAVLinkInterface { BaseStream = new SerialPort { PortName = port, BaudRate = 115200 } };
    var watch = Stopwatch.StartNew();
    await Task.Run(() => link.Open(getparams: true, skipconnectedcheck: true, showui: false));
    Assert.True(link.BaseStream.IsOpen, "MAVLink link did not open");
    output.WriteLine($"Connected to {link.sysidcurrent}:{link.compidcurrent} in {watch.Elapsed.TotalSeconds:F1} s, {link.MAV.param.Count} parameters");
    var target = new DroneCanSessionTarget(link, (byte)link.sysidcurrent, (byte)link.compidcurrent);
    // The application pumps packets from ConnectionViewModel; subscriptions only fire while something reads.
    using var pump = new CancellationTokenSource();
    var reader = Task.Run(async () => {
      while (!pump.IsCancellationRequested && link.BaseStream.IsOpen) {
        try { await link.readPacketAsync().ConfigureAwait(false); } catch (Exception) when (!pump.IsCancellationRequested) { await Task.Delay(10); }
      }
    });
    try {
      using var viewModel = new ConfigDroneCanViewModel(() => target);
      viewModel.SelectedBusIndex = bus == 2 ? 1 : 0;
      output.WriteLine($"Autopilot node on CAN{bus}: {ConfigDroneCanViewModel.AutopilotNodeId(target, (byte)bus)}");

      watch.Restart();
      viewModel.ToggleConnectCommand.Execute(null);
      await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy, 10);
      try {
        await WaitForAsync(() => viewModel.Nodes.Count >= (expectedNode == null ? 1 : 2)
            && viewModel.Nodes.All(node => node.Name != "?"), 15);
        Assert.DoesNotContain(viewModel.Nodes, node => node.Id == 127);
      } finally {
        output.WriteLine($"Nodes after {watch.Elapsed.TotalSeconds:F1} s: {string.Join(", ", viewModel.Nodes.Select(node => node.Id + " " + node.Name))}; status: {viewModel.Status}");
      }
      output.WriteLine($"Nodes named after {watch.Elapsed.TotalSeconds:F1} s:");
      foreach (var node in viewModel.Nodes) {
        output.WriteLine($"  {node.Id} {node.Name} sw {node.SoftwareVersion} hw {node.HardwareVersion} {node.Health} {node.Mode} up {node.Uptime}");
      }
      Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "node names took too long");
      await Task.Delay(6500);
      Dispatcher.UIThread.RunJobs();
      output.WriteLine("Status: " + viewModel.Status);

      if (expectedNode != null) {
        var device = Assert.Single(viewModel.Nodes, node => node.Id == expectedNode);
        viewModel.SelectedNode = device;
        watch.Restart();
        viewModel.GetParametersCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsBusy && viewModel.NodeParams.Count > 0, 30);
        output.WriteLine($"{viewModel.NodeParams.Count} parameters from node {expectedNode} in {watch.Elapsed.TotalSeconds:F1} s; {viewModel.NodeStatus}");
        Assert.True(viewModel.NodeParams.Count >= 10);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "parameter read too slow");
        Assert.Contains(viewModel.NodeParams, parameter => parameter.Name == "CAN_NODE");
      }

      // The other bus: only the autopilot (or nothing) answers, and the page must say so.
      viewModel.ToggleConnectCommand.Execute(null);
      await WaitForAsync(() => !viewModel.IsConnected, 5);
      viewModel.SelectedBusIndex = bus == 2 ? 0 : 1;
      viewModel.ToggleConnectCommand.Execute(null);
      await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy, 10);
      await Task.Delay(7000);
      Dispatcher.UIThread.RunJobs();
      output.WriteLine($"Other bus nodes: {string.Join(", ", viewModel.Nodes.Select(node => node.Id + " " + node.Name))}");
      output.WriteLine("Other bus status: " + viewModel.Status);
      Assert.Contains($"MAVLink-CAN{bus}", viewModel.Status);
      viewModel.ToggleConnectCommand.Execute(null);
      await WaitForAsync(() => !viewModel.IsConnected, 5);
    } finally {
      pump.Cancel();
      try { link.Close(); } catch { }
      try { await reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
  }

  private static async Task WaitForAsync(Func<bool> predicate, int seconds) {
    DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (!predicate() && DateTime.UtcNow < deadline) {
      Dispatcher.UIThread.RunJobs();
      await Task.Delay(50);
    }
    Dispatcher.UIThread.RunJobs();
    Assert.True(predicate(), $"Timed out after {seconds} s waiting for the live DroneCAN state.");
  }
}
