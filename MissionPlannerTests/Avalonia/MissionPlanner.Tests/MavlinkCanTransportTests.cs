using System.Text;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

public class MavlinkCanTransportTests {
  private static MAVLink.MAVLinkMessage VehicleFrame(uint id, byte[] data, byte bus) {
    var packet = new MAVLink.mavlink_can_frame_t(id, 255, 190, bus, (byte)data.Length, Pad(data));
    byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
        MAVLink.MAVLINK_MSG_ID.CAN_FRAME, packet, sysid: 1, compid: 1);
    return new MAVLink.MAVLinkMessage(bytes);
  }

  private static byte[] Pad(byte[] data) {
    var padded = new byte[8];
    data.CopyTo(padded, 0);
    return padded;
  }

  private static string ReadPort(MavlinkCanTransport transport) {
    var buffer = new byte[256];
    int count = transport.Port.Read(buffer, 0, buffer.Length);
    return Encoding.ASCII.GetString(buffer, 0, count);
  }

  [Fact]
  public void Received_frames_reach_the_stack_but_are_never_sent_back_to_the_vehicle() {
    var link = new MAVLinkInterface();
    var sent = new List<object>();
    using var transport = MavlinkCanTransport.Open(link, 1, 1, 2, null, out string? conflict, packet => { sent.Add(packet); return true; });
    Assert.NotNull(transport);
    Assert.Null(conflict);

    // ArduPilot sets bit 31 on extended ids, like pydronecan's mavcan driver expects.
    uint id = 0x1E01AAFF | 0x80000000u;
    Assert.True(transport!.HandleMessage(VehicleFrame(id, [0xAA, 0x55], 1)));

    string line = ReadPort(transport);
    Assert.StartsWith("T", line);
    Assert.EndsWith("\r", line);
    Assert.True(DroneCAN.DroneCAN.TryParseSLCAN(line.TrimEnd('\r'), out var frame, out var payload));
    Assert.Equal(127, frame.SourceNode);
    Assert.True(frame.IsServiceMsg);
    Assert.Equal(42, frame.SvcDestinationNode);
    Assert.Equal(new byte[] { 0xAA, 0x55 }, payload.packet_data);
    Assert.Equal(1, transport.ReceivedFrames);
    Assert.Empty(sent);
    Assert.Equal(0, transport.SentFrames);
  }

  [Fact]
  public void Stack_writes_become_vehicle_packets_for_the_selected_bus() {
    var link = new MAVLinkInterface();
    var sent = new List<object>();
    using var transport = MavlinkCanTransport.Open(link, 7, 1, 2, null, out _, packet => { sent.Add(packet); return true; });

    byte[] text = Encoding.ASCII.GetBytes("T1E01AAFF2AA55\rT9E0155FF101\r");
    transport!.Port.Write(text, 0, text.Length);

    Assert.Equal(2, sent.Count);
    var first = Assert.IsType<MAVLink.mavlink_can_frame_t>(sent[0]);
    Assert.Equal(0x1E01AAFFu | 0x80000000u, first.id);
    Assert.Equal(1, first.bus); // CAN2 is bus index 1 on the wire
    Assert.Equal(7, first.target_system);
    Assert.Equal(2, first.len);
    Assert.Equal(new byte[] { 0xAA, 0x55 }, first.data.Take(2).ToArray());
    Assert.Equal(2, transport.SentFrames);
  }

  [Fact]
  public void A_second_session_on_the_other_bus_is_refused_until_the_first_closes() {
    var link = new MAVLinkInterface();
    var other = new MAVLinkInterface();
    Func<object, bool> drop = _ => true;
    var first = MavlinkCanTransport.Open(link, 1, 1, 1, null, out _, drop);
    Assert.NotNull(first);

    Assert.Null(MavlinkCanTransport.Open(link, 1, 1, 2, null, out string? conflict, drop));
    Assert.Contains("CAN1", conflict);

    using var sameBus = MavlinkCanTransport.Open(link, 1, 1, 1, null, out string? none, drop);
    Assert.NotNull(sameBus);
    Assert.Null(none);
    using var otherLink = MavlinkCanTransport.Open(other, 1, 1, 2, null, out string? otherConflict, drop);
    Assert.NotNull(otherLink);
    Assert.Null(otherConflict);

    first!.Dispose();
    sameBus!.Dispose();
    using var afterClose = MavlinkCanTransport.Open(link, 1, 1, 2, null, out string? late, drop);
    Assert.NotNull(afterClose);
    Assert.Null(late);
  }

  [Fact]
  public void Disposed_transport_ignores_vehicle_frames_and_stack_writes() {
    var link = new MAVLinkInterface();
    var sent = new List<object>();
    var transport = MavlinkCanTransport.Open(link, 1, 1, 1, null, out _, packet => { sent.Add(packet); return true; });
    transport!.Dispose();

    Assert.False(transport.HandleMessage(VehicleFrame(0x1E01AAFF, [1], 0)));
    byte[] text = Encoding.ASCII.GetBytes("T1E01AAFF101\r");
    try { transport.Port.Write(text, 0, text.Length); } catch (IOException) { }
    Assert.Empty(sent);
  }

  [Theory]
  [InlineData("")]
  [InlineData("Z")]
  [InlineData("T1E0")]
  [InlineData("T1E01AAFF0")]
  [InlineData("X1E01AAFF2AA55")]
  public void TryParseSLCAN_rejects_lines_that_carry_no_frame(string line) {
    Assert.False(DroneCAN.DroneCAN.TryParseSLCAN(line, out _, out _));
  }

  [Fact]
  public void TryParseSLCAN_does_not_process_or_forward_the_frame() {
    var can = new DroneCAN.DroneCAN();
    int received = 0;
    can.FrameReceived += (_, _) => received++;

    Assert.True(DroneCAN.DroneCAN.TryParseSLCAN("T1E01AAFF2AA55", out var frame, out var payload));
    Assert.Equal(0x7F, frame.SourceNode);
    Assert.Equal(2, payload.packet_data.Length);
    Assert.Equal(0, received);

    can.ReadMessageSLCAN("T1E01AAFF2AA55");
    Assert.Equal(1, received);
  }
}
