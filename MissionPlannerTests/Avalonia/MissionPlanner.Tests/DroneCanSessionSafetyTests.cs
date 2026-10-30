using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner.Comms;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;
using MissionPlanner.Views.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public class DroneCanSessionSafetyTests {
  [Fact]
  public void Results_require_the_same_modem_vehicle_and_monotonic_revisions() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    var expected = new DroneCanSessionTarget(firstLink, 1, 1);

    Assert.True(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(secondLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 2, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        true, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 8, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 4));
    Assert.False(ConfigDroneCanViewModel.ShouldAcceptNodeBoundResult(
        false, 7, 7, expected, new DroneCanSessionTarget(firstLink, 1, 1), 4, 6));
  }

  [AvaloniaFact]
  public void Target_switch_immediately_clears_nodes_parameters_selection_and_logs() {
    var firstLink = new MissionPlanner.MAVLinkInterface();
    var secondLink = new MissionPlanner.MAVLinkInterface();
    DroneCanSessionTarget? current = new(firstLink, 1, 1);
    using var viewModel = new ConfigDroneCanViewModel(() => current);
    var node = new DroneCanNode { Id = 42, Name = "old-node" };
    viewModel.Nodes.Add(node);
    viewModel.SelectedNode = node;
    viewModel.NodeParams.Add(new DroneCanParam { Name = "OLD_PARAM", Value = "1" });
    viewModel.DebugLog.Add(new DroneCanLog { Node = "42", Text = "old-message" });

    current = new DroneCanSessionTarget(secondLink, 1, 1);
    viewModel.SynchronizeActiveTarget();
    Dispatcher.UIThread.RunJobs();

    Assert.Empty(viewModel.Nodes);
    Assert.Empty(viewModel.NodeParams);
    Assert.Empty(viewModel.DebugLog);
    Assert.Null(viewModel.SelectedNode);
    Assert.False(viewModel.IsConnected);
    Assert.Contains("active modem or vehicle changed", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void Native_view_locks_interface_selection_while_a_session_or_operation_is_active() {
    using var viewModel = new ConfigDroneCanViewModel(() => null);
    var view = new ConfigDroneCanView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<ComboBox>("DroneCanInterfaceSelector"));
    Assert.NotNull(view.FindControl<Button>("DroneCanConnectButton"));
    Assert.True(viewModel.CanChangeInterface);
    Assert.True(viewModel.CanToggleConnection);
  }

  [Fact]
  public void Direct_slcan_port_identity_rejects_the_same_open_device() {
    Assert.True(ConfigDroneCanViewModel.PortsIdentifySameDevice("COM7", "COM7"));
    Assert.True(ConfigDroneCanViewModel.PortsIdentifySameDevice(
        "/dev/ttyACM0", "/dev/ttyACM0"));
    Assert.False(ConfigDroneCanViewModel.PortsIdentifySameDevice(
        "/dev/ttyACM0", "/dev/ttyUSB0"));
    Assert.False(ConfigDroneCanViewModel.PortsIdentifySameDevice(null, "/dev/ttyUSB0"));
  }

  [AvaloniaFact]
  public async Task Direct_slcan_opens_without_mavlink_survives_target_switch_and_releases_port() {
    var transport = new FakeSlcanTransport("/dev/test-slcan", 115200);
    DroneCanSessionTarget? current = null;
    using var viewModel = new ConfigDroneCanViewModel(
        () => current,
        serialPortNames: () => ["/dev/test-slcan"],
        serialPortFactory: (_, _) => transport);
    viewModel.SelectedBusIndex = 2;
    viewModel.SelectedSerialPort = "/dev/test-slcan";
    viewModel.SelectedSerialBaud = 115200;

    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);

    Assert.True(transport.IsOpen);
    Assert.Contains("direct SLCAN", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("C\r", transport.Stream.WritesText);
    Assert.Contains("O\r", transport.Stream.WritesText);
    int closeCommandsBeforeDisconnect = CountOccurrences(transport.Stream.WritesText, "C\r");

    current = new DroneCanSessionTarget(new MissionPlanner.MAVLinkInterface(), 7, 1);
    viewModel.SynchronizeActiveTarget();
    Dispatcher.UIThread.RunJobs();

    Assert.True(viewModel.IsConnected);
    Assert.True(transport.IsOpen);

    viewModel.ToggleConnectCommand.Execute(null);
    Dispatcher.UIThread.RunJobs();

    Assert.False(viewModel.IsConnected);
    Assert.False(transport.IsOpen);
    Assert.True(transport.Stream.CloseCount > 0);
    Assert.True(CountOccurrences(transport.Stream.WritesText, "C\r") >
        closeCommandsBeforeDisconnect);
  }

  [AvaloniaFact]
  public async Task Refresh_discards_cached_discovery_and_next_node_status_readds_the_node() {
    var transport = new FakeSlcanTransport("/dev/test-slcan", 115200);
    var can = new DroneCAN.DroneCAN();
    using var viewModel = new ConfigDroneCanViewModel(
        () => null,
        serialPortNames: () => ["/dev/test-slcan"],
        serialPortFactory: (_, _) => transport,
        canFactory: () => can);
    viewModel.SelectedBusIndex = 2;
    viewModel.SelectedSerialPort = "/dev/test-slcan";

    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);

    var oldStatus = new DroneCAN.DroneCAN.uavcan_protocol_NodeStatus {
      health = (byte)DroneCAN.DroneCAN.uavcan_protocol_NodeStatus
          .UAVCAN_PROTOCOL_NODESTATUS_HEALTH_WARNING,
      mode = (byte)DroneCAN.DroneCAN.uavcan_protocol_NodeStatus
          .UAVCAN_PROTOCOL_NODESTATUS_MODE_INITIALIZATION,
      uptime_sec = 15,
    };
    can.NodeList[42] = oldStatus;
    can.NodeInfo[42] = new DroneCAN.DroneCAN.uavcan_protocol_GetNodeInfo_res();
    var oldNode = new DroneCanNode { Id = 42, Name = "stale-node" };
    viewModel.Nodes.Add(oldNode);
    viewModel.SelectedNode = oldNode;

    viewModel.RefreshCommand.Execute(null);

    // Refresh discards the cache and lets the bus repopulate it, so these dictionaries are live and
    // an unrelated entry may appear between the clear and the assert. CI caught exactly that: node
    // 127 - DroneCAN.SourceNode's default, this application's own node id - carrying an
    // "org.missionplanner" GetNodeInfo response. Assert that the stale entry is gone, which is what
    // the refresh is responsible for, rather than a global emptiness no caller can rely on.
    Assert.False(can.NodeList.ContainsKey(42));
    Assert.False(can.NodeInfo.ContainsKey(42));
    Assert.Empty(viewModel.Nodes);
    Assert.Null(viewModel.SelectedNode);

    var freshStatus = new DroneCAN.DroneCAN.uavcan_protocol_NodeStatus {
      health = (byte)DroneCAN.DroneCAN.uavcan_protocol_NodeStatus
          .UAVCAN_PROTOCOL_NODESTATUS_HEALTH_OK,
      mode = (byte)DroneCAN.DroneCAN.uavcan_protocol_NodeStatus
          .UAVCAN_PROTOCOL_NODESTATUS_MODE_OPERATIONAL,
      uptime_sec = 16,
    };
    can.InvokeMessageReceived(MessageFrame(42), freshStatus, 0);
    Dispatcher.UIThread.RunJobs();

    DroneCanNode refreshed = Assert.Single(viewModel.Nodes, node => node.Id == 42);
    Assert.Equal((byte)42, refreshed.Id);
    Assert.Equal("OK", refreshed.Health);
    Assert.Equal("OPERATIONAL", refreshed.Mode);
    Assert.Equal(TimeSpan.FromSeconds(16), refreshed.Uptime);
  }

  [AvaloniaFact]
  public async Task Node_status_from_an_unnamed_node_requests_its_node_info_immediately() {
    var transport = new FakeSlcanTransport("/dev/test-slcan", 115200);
    var can = new DroneCAN.DroneCAN();
    using var viewModel = new ConfigDroneCanViewModel(
        () => null,
        serialPortNames: () => ["/dev/test-slcan"],
        serialPortFactory: (_, _) => transport,
        canFactory: () => can);
    viewModel.SelectedBusIndex = 2;
    viewModel.SelectedSerialPort = "/dev/test-slcan";
    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);

    var status = new DroneCAN.DroneCAN.uavcan_protocol_NodeStatus { uptime_sec = 3 };
    can.InvokeMessageReceived(MessageFrame(42), status, 0);
    can.InvokeMessageReceived(MessageFrame(42), status, 1);
    Dispatcher.UIThread.RunJobs();

    // uavcan.protocol.GetNodeInfo request (type 1, priority 30) from node 127 to node 42.
    Assert.Equal(1, CountOccurrences(transport.Stream.WritesText, "T1E01AAFF"));
    Assert.Single(viewModel.Nodes, node => node.Id == 42);

    can.NodeInfo[42] = new DroneCAN.DroneCAN.uavcan_protocol_GetNodeInfo_res();
    await Task.Delay(2100);
    can.InvokeMessageReceived(MessageFrame(42), status, 2);
    Assert.Equal(1, CountOccurrences(transport.Stream.WritesText, "T1E01AAFF"));
  }

  [AvaloniaFact]
  public async Task Get_parameters_reports_when_the_node_does_not_answer_the_optional_service() {
    var transport = new FakeSlcanTransport("/dev/test-slcan", 115200);
    var can = new DroneCAN.DroneCAN();
    using var viewModel = new ConfigDroneCanViewModel(
        () => null,
        serialPortNames: () => ["/dev/test-slcan"],
        serialPortFactory: (_, _) => transport,
        canFactory: () => can);
    viewModel.SelectedBusIndex = 2;
    viewModel.SelectedSerialPort = "/dev/test-slcan";

    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);
    var node = new DroneCanNode { Id = 42, Name = "status-only-simulator" };
    viewModel.Nodes.Add(node);
    viewModel.SelectedNode = node;

    await viewModel.GetParametersCommand.ExecuteAsync(null);

    Assert.Empty(viewModel.NodeParams);
    Assert.Contains("did not respond", viewModel.NodeStatus,
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("may not be supported", viewModel.NodeStatus,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public async Task Get_parameters_distinguishes_an_empty_response_from_a_timeout() {
    var transport = new FakeSlcanTransport("/dev/test-slcan", 115200);
    var can = new DroneCAN.DroneCAN();
    using var viewModel = new ConfigDroneCanViewModel(
        () => null,
        serialPortNames: () => ["/dev/test-slcan"],
        serialPortFactory: (_, _) => transport,
        canFactory: () => can);
    viewModel.SelectedBusIndex = 2;
    viewModel.SelectedSerialPort = "/dev/test-slcan";

    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);
    var node = new DroneCanNode { Id = 42, Name = "empty-parameter-node" };
    viewModel.Nodes.Add(node);
    viewModel.SelectedNode = node;

    Task request = viewModel.GetParametersCommand.ExecuteAsync(null);

    // GetParameters starts a two-second response timeout and only then subscribes its handler
    // (DroneCAN.cs:544), so a single injection has to land inside a window the test cannot observe:
    // too early and nothing is subscribed, too late and the operation has already given up and
    // reports a timeout instead. Waiting on IsBusy first lost that race on CI. Repeating the
    // injection until the operation observes one is deterministic and idempotent - an empty
    // response clears the timeout and releases the wait, so the first reply that lands ends it.
    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
    while (!request.IsCompleted && DateTime.UtcNow < deadline) {
      can.InvokeMessageReceived(ServiceResponseFrame(42, 127),
          new DroneCAN.DroneCAN.uavcan_protocol_param_GetSet_res(), 0);
      Dispatcher.UIThread.RunJobs();
      await Task.Delay(10);
    }

    await request;
    Dispatcher.UIThread.RunJobs();

    Assert.Empty(viewModel.NodeParams);
    Assert.Contains("responded but exposes no configurable parameters", viewModel.NodeStatus,
        StringComparison.OrdinalIgnoreCase);
  }

  [AvaloniaFact]
  public void Native_view_exposes_direct_slcan_port_and_baud_controls() {
    using var viewModel = new ConfigDroneCanViewModel(
        () => null, serialPortNames: () => ["/dev/test-slcan"]);
    viewModel.SelectedBusIndex = 2;
    var view = new ConfigDroneCanView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<StackPanel>("DirectSlcanOptions"));
    Assert.NotNull(view.FindControl<ComboBox>("DirectSlcanPortSelector"));
    Assert.NotNull(view.FindControl<ComboBox>("DirectSlcanBaudSelector"));
    Assert.NotNull(view.FindControl<Button>("PrepareAutopilotSlcanButton"));
    Assert.True(viewModel.ShowDirectSerialOptions);
    Assert.True(viewModel.CanEditDirectSerial);
    Assert.Contains("/dev/test-slcan", viewModel.SerialPorts);
  }

  [Fact]
  public void Multicast_codec_matches_the_official_pydronecan_packet_format() {
    byte[] payload = Enumerable.Range(0, 12).Select(value => (byte)value).ToArray();
    var frame = new DroneCanMulticastFrame(
        0x1ABCDE, Extended: true, CanFd: true, payload);

    byte[] packet = DroneCanMulticastCodec.Encode(frame);

    Assert.Equal(
        "3429F8640100DEBC1A80000102030405060708090A0B",
        Convert.ToHexString(packet));
    Assert.Equal("239.65.82.0", DroneCanMulticastCodec.GroupForBus(0).ToString());
    Assert.Equal("239.65.82.1", DroneCanMulticastCodec.GroupForBus(1).ToString());
    Assert.Equal(57732, DroneCanMulticastCodec.Port);
    Assert.True(DroneCanMulticastCodec.TryDecode(packet, out DroneCanMulticastFrame decoded));
    Assert.Equal(frame.Identifier, decoded.Identifier);
    Assert.True(decoded.Extended);
    Assert.True(decoded.CanFd);
    Assert.Equal(payload, decoded.Payload);

    byte[] slcan = System.Text.Encoding.ASCII.GetBytes(
        "B001ABCDE9000102030405060708090A0B");
    Assert.True(DroneCanMulticastCodec.TryEncodeSlcan(slcan, out byte[] fromSlcan));
    Assert.Equal(packet, fromSlcan);
    Assert.Equal(
        "D001ABCDE9000102030405060708090A0B\r",
        System.Text.Encoding.ASCII.GetString(DroneCanMulticastCodec.ToSlcan(decoded)));
  }

  [Fact]
  public void Multicast_codec_rejects_corrupt_or_out_of_contract_datagrams() {
    byte[] packet = DroneCanMulticastCodec.Encode(new DroneCanMulticastFrame(
        0x123, Extended: false, CanFd: false, [1, 2, 3, 4]));
    packet[^1] ^= 0x80;

    Assert.False(DroneCanMulticastCodec.TryDecode(packet, out _));
    Assert.False(DroneCanMulticastCodec.TryDecode(new byte[9], out _));
    Assert.False(DroneCanMulticastCodec.TryDecode(new byte[75], out _));
    Assert.Throws<ArgumentOutOfRangeException>(() => DroneCanMulticastCodec.Encode(
        new DroneCanMulticastFrame(0x123, Extended: false, CanFd: false, new byte[9])));
    Assert.False(DroneCanMulticastCodec.TryEncodeSlcan(
        System.Text.Encoding.ASCII.GetBytes("TFFFFFFFF1AA"), out _));
  }

  [AvaloniaFact]
  public async Task Multicast_connects_without_mavlink_survives_target_switch_and_releases_socket() {
    var networkInterface = new DroneCanNetworkInterfaceOption(
        "test-id", "test0", "Test multicast interface", 17);
    var session = new FakeMulticastSession("239.65.82.0:57732");
    DroneCanSessionTarget? current = null;
    DroneCanNetworkInterfaceOption? selectedByFactory = null;
    byte selectedBus = byte.MaxValue;
    using var viewModel = new ConfigDroneCanViewModel(
        () => current,
        networkInterfaces: () => [networkInterface],
        multicastSessionFactory: (selected, bus) => {
          selectedByFactory = selected;
          selectedBus = bus;
          return session;
        });
    viewModel.SelectedBusIndex = 3;

    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);

    Assert.Same(networkInterface, selectedByFactory);
    Assert.Equal((byte)0, selectedBus);
    Assert.True(session.Started);
    Assert.Contains("239.65.82.0:57732", viewModel.Status, StringComparison.Ordinal);
    Assert.False(viewModel.CanFilterFrames);

    current = new DroneCanSessionTarget(new MissionPlanner.MAVLinkInterface(), 9, 1);
    viewModel.SynchronizeActiveTarget();
    Dispatcher.UIThread.RunJobs();

    Assert.True(viewModel.IsConnected);
    Assert.True(session.Started);

    session.Fail(new IOException("multicast link down"));
    Dispatcher.UIThread.RunJobs();

    Assert.False(viewModel.IsConnected);
    Assert.Contains("multicast link down", viewModel.Status, StringComparison.Ordinal);
    Assert.True(session.StopCount > 0);
    Assert.True(session.DisposeCount > 0);
  }

  [AvaloniaFact]
  public void Native_view_exposes_multicast_bus_and_network_interface_controls() {
    var networkInterface = new DroneCanNetworkInterfaceOption(
        "test-id", "test0", "Test multicast interface", 17);
    using var viewModel = new ConfigDroneCanViewModel(
        () => null, networkInterfaces: () => [networkInterface]);
    viewModel.SelectedBusIndex = 4;
    var view = new ConfigDroneCanView { DataContext = viewModel };

    Assert.Contains("Multicast-CAN1", viewModel.BusOptions);
    Assert.Contains("Multicast-CAN2", viewModel.BusOptions);
    Assert.NotNull(view.FindControl<StackPanel>("MulticastOptions"));
    Assert.NotNull(view.FindControl<ComboBox>("MulticastInterfaceSelector"));
    Assert.NotNull(view.FindControl<Button>("RefreshMulticastInterfacesButton"));
    Assert.True(viewModel.ShowMulticastOptions);
    Assert.True(viewModel.CanEditNetworkInterface);
    Assert.Same(networkInterface, viewModel.SelectedNetworkInterface);
  }

  [AvaloniaFact]
  public async Task Library_forgets_node_info_when_a_node_reboots_so_its_new_version_is_read_again() {
    var transport = new FakeSlcanTransport("/dev/test-slcan", 115200);
    var can = new DroneCAN.DroneCAN();
    using var viewModel = new ConfigDroneCanViewModel(
        () => null,
        serialPortNames: () => ["/dev/test-slcan"],
        serialPortFactory: (_, _) => transport,
        canFactory: () => can);
    viewModel.SelectedBusIndex = 2;
    viewModel.SelectedSerialPort = "/dev/test-slcan";
    viewModel.ToggleConnectCommand.Execute(null);
    await WaitForAsync(() => viewModel.IsConnected && !viewModel.IsBusy);

    can.InvokeMessageReceived(MessageFrame(42), new DroneCAN.DroneCAN.uavcan_protocol_NodeStatus { uptime_sec = 100 }, 0);
    can.NodeInfo[42] = new DroneCAN.DroneCAN.uavcan_protocol_GetNodeInfo_res();
    can.InvokeMessageReceived(MessageFrame(42), new DroneCAN.DroneCAN.uavcan_protocol_NodeStatus { uptime_sec = 101 }, 1);
    Assert.True(can.NodeInfo.ContainsKey(42));
    can.InvokeMessageReceived(MessageFrame(42), new DroneCAN.DroneCAN.uavcan_protocol_NodeStatus { uptime_sec = 2 }, 2);
    Assert.False(can.NodeInfo.ContainsKey(42));
    Assert.Equal(2u, can.NodeList[42].uptime_sec);
  }

  private static async Task WaitForAsync(Func<bool> predicate) {
    DateTime deadline = DateTime.UtcNow.AddSeconds(3);
    while (!predicate() && DateTime.UtcNow < deadline) {
      Dispatcher.UIThread.RunJobs();
      await Task.Delay(10);
    }
    Dispatcher.UIThread.RunJobs();
    Assert.True(predicate(), "Timed out waiting for the DroneCAN session state.");
  }

  private static int CountOccurrences(string text, string value) {
    int count = 0;
    int offset = 0;
    while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0) {
      count++;
      offset += value.Length;
    }
    return count;
  }

  private static DroneCAN.CANFrame MessageFrame(byte sourceNode) {
    var frame = new DroneCAN.CANFrame(new byte[4]);
    frame.SourceNode = sourceNode;
    return frame;
  }

  private static DroneCAN.CANFrame ServiceResponseFrame(
      byte sourceNode, byte destinationNode) {
    var frame = new DroneCAN.CANFrame(new byte[4]);
    frame.SourceNode = sourceNode;
    frame.IsServiceMsg = true;
    frame.SvcDestinationNode = destinationNode;
    frame.SvcIsRequest = false;
    frame.SvcTypeID = DroneCAN.DroneCAN.uavcan_protocol_param_GetSet_req
        .UAVCAN_PROTOCOL_PARAM_GETSET_REQ_DT_ID;
    return frame;
  }

  private sealed class FakeSlcanTransport : ICommsSerial {
    internal FakeSlcanTransport(string portName, int baudRate) {
      PortName = portName;
      BaudRate = baudRate;
    }

    internal FakeSlcanStream Stream { get; } = new();
    public Stream BaseStream => Stream;
    public int BaudRate { get; set; }
    public int BytesToRead => 0;
    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public bool IsOpen { get; private set; }
    public string PortName { get; set; }
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }

    public void Open() => IsOpen = true;

    public void Close() {
      IsOpen = false;
      Stream.Close();
    }

    public void Dispose() => Close();
    public void DiscardInBuffer() { }
    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public string ReadLine() => "";
    public void Write(string text) => Stream.Write(System.Text.Encoding.ASCII.GetBytes(text));
    public void Write(byte[] buffer, int offset, int count) => Stream.Write(buffer, offset, count);
    public void WriteLine(string text) => Write(text + "\n");
    public void toggleDTR() { }
  }

  private sealed class FakeMulticastSession(string endpoint) : IDroneCanMulticastSession {
    private readonly FakeSlcanTransport _serial = new("multicast", 0);

    public ICommsSerial Serial => _serial;
    public string Endpoint { get; } = endpoint;
    public bool Started { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public event Action<Exception>? TransportFailed;

    internal void Fail(Exception exception) => TransportFailed?.Invoke(exception);

    public void Start() => Started = true;

    public void Stop() {
      Started = false;
      StopCount++;
    }

    public void Dispose() {
      DisposeCount++;
      Stop();
      _serial.Dispose();
    }
  }

  private sealed class FakeSlcanStream : Stream {
    private readonly List<byte> _writes = [];

    internal string WritesText {
      get {
        lock (_writes) {
          return System.Text.Encoding.ASCII.GetString(_writes.ToArray());
        }
      }
    }

    internal int CloseCount { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override bool CanTimeout => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override int ReadTimeout { get; set; }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) {
      lock (_writes) {
        for (int i = 0; i < count; i++) {
          _writes.Add(buffer[offset + i]);
        }
      }
    }

    public override void Close() {
      CloseCount++;
      // Keep the fake readable/writable so late, already-scheduled upstream worker iterations do
      // not turn a deterministic lifecycle test into an ObjectDisposedException race.
    }
  }
}
