using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

public sealed class SerialPassThroughViewModelTests {
  private const string TcpHost = "TCP Host - 14550";

  private static int FreePort() {
    var tcp = new TcpListener(IPAddress.Loopback, 0);
    tcp.Start();
    int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
    tcp.Stop();
    return port;
  }

  private static SerialPassThroughViewModel StartTcpHost(MAVLinkInterface link, int port, bool writeBack = false) {
    var vm = new SerialPassThroughViewModel(link, port) { SelectedPort = TcpHost, AllowWriteBack = writeBack };
    vm.ToggleConnectCommand.Execute(null);
    return vm;
  }

  [AvaloniaFact]
  public void Failed_start_on_a_busy_port_keeps_the_running_mirror_attached() {
    using var link = new MAVLinkInterface();
    int port = FreePort();
    using var first = StartTcpHost(link, port);
    var mirror = Assert.Single(link.Mirrors);
    var stream = mirror.MirrorStream;

    using var second = StartTcpHost(link, port);

    Assert.StartsWith("Error connecting", second.Status);
    Assert.False(second.IsRunning);
    Assert.True(first.IsRunning);
    Assert.Same(mirror, Assert.Single(link.Mirrors));
    Assert.Same(stream, mirror.MirrorStream);
  }

  [AvaloniaFact]
  public void Each_window_owns_its_mirror_entry_and_stop_removes_only_its_own() {
    using var link = new MAVLinkInterface();
    using var first = StartTcpHost(link, FreePort(), writeBack: true);
    var mirror = Assert.Single(link.Mirrors);
    var stream = mirror.MirrorStream;

    using var second = StartTcpHost(link, FreePort());
    Assert.Equal(2, link.Mirrors.Count);
    Assert.Same(stream, mirror.MirrorStream);

    second.AllowWriteBack = true;
    second.AllowWriteBack = false;
    Assert.True(mirror.MirrorStreamWrite);

    second.ToggleConnectCommand.Execute(null);
    Assert.False(second.IsRunning);
    Assert.Same(mirror, Assert.Single(link.Mirrors));
    Assert.Same(stream, mirror.MirrorStream);
  }

  [AvaloniaFact]
  public void Write_back_toggle_on_a_stopped_window_leaves_other_mirrors_alone() {
    using var link = new MAVLinkInterface();
    using var idle = new SerialPassThroughViewModel(link, FreePort()) { AllowWriteBack = true };
    Assert.Empty(link.Mirrors);

    using var running = StartTcpHost(link, FreePort(), writeBack: true);
    var mirror = Assert.Single(link.Mirrors);
    idle.AllowWriteBack = false;

    Assert.True(mirror.MirrorStreamWrite);
    Assert.Same(mirror, Assert.Single(link.Mirrors));
  }
}
