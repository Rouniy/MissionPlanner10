using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Comms;

namespace MissionPlanner.ViewModels;

/// <summary>
/// One MAVLink CAN tunnel: MAV_CMD_CAN_FORWARD keeps the autopilot forwarding one physical bus,
/// CAN_FRAME/CANFD_FRAME packets become SLCAN lines on <see cref="Port"/>, and SLCAN lines the
/// DroneCAN stack writes become CAN_FRAME packets for the vehicle.
/// </summary>
/// <remarks>
/// Received frames are never sent back to the vehicle. Earlier code parsed outgoing SLCAN text
/// with <c>ReadMessageSLCAN</c> and forwarded every <c>FrameReceived</c>, so each frame that arrived
/// over MAVLink was transmitted onto the bus a second time and this application processed its own
/// transmissions as if a remote node had sent them.
/// </remarks>
internal sealed class MavlinkCanTransport : IDisposable {
  private static readonly object _forwardLock = new();
  private static readonly Dictionary<MAVLinkInterface, List<MavlinkCanTransport>> _forwarders = new();

  private readonly MAVLinkInterface _link;
  private readonly byte _sysid, _compid, _bus;
  private readonly Func<bool> _isCurrent;
  private readonly Func<object, bool>? _send;
  private int _subId;
  private bool _subscribed, _disposed;
  private long _rxFrames, _txFrames;

  internal CommsInjection Port { get; } = new();
  internal byte Bus => _bus;
  internal long ReceivedFrames => Interlocked.Read(ref _rxFrames);
  internal long SentFrames => Interlocked.Read(ref _txFrames);

  /// <summary>
  /// Opens the tunnel. Returns null with <paramref name="conflict"/> set when this link already
  /// forwards a different bus: ArduPilot forwards one bus per link, so a second session on the other
  /// bus would make MAV_CMD_CAN_FORWARD alternate every second and both sessions would lose frames.
  /// </summary>
  internal static MavlinkCanTransport? Open(MAVLinkInterface link, byte sysid, byte compid, byte bus,
      Func<bool>? isCurrent, out string? conflict, Func<object, bool>? send = null) {
    lock (_forwardLock) {
      if (_forwarders.TryGetValue(link, out var active) && active.Count != 0 && active[0]._bus != bus) {
        conflict = $"MAVLink CAN{active[0]._bus} is already in use on this link (DroneCAN page or Inspector). "
            + "The autopilot forwards one CAN bus at a time: disconnect that session first or use the same bus.";
        return null;
      }
      var transport = new MavlinkCanTransport(link, sysid, compid, bus, isCurrent, send);
      if (active == null) { _forwarders[link] = active = new List<MavlinkCanTransport>(); }
      active.Add(transport);
      conflict = null;
      if (active.Count == 1 && send == null) { transport.StartForwardLoop(); }
      return transport;
    }
  }

  private MavlinkCanTransport(MAVLinkInterface link, byte sysid, byte compid, byte bus, Func<bool>? isCurrent, Func<object, bool>? send) {
    _link = link; _sysid = sysid; _compid = compid; _bus = bus; _send = send;
    _isCurrent = isCurrent ?? (() => true);
    Port.WriteCallback += OnStackWrite;
    if (send == null) {
      _subId = link.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.CAN_FRAME, HandleMessage, sysid, compid);
      _subscribed = true;
    }
  }

  private void StartForwardLoop() {
    var link = _link; var sysid = _sysid; var compid = _compid; var bus = _bus;
    _ = Task.Run(async () => {
      // ArduPilot drops forwarding 5 s after the last request; renew every second while any session
      // on this link still wants this bus.
      while (true) {
        lock (_forwardLock) {
          if (!_forwarders.TryGetValue(link, out var active) || active.Count == 0 || active[0]._bus != bus) { return; }
        }
        try { link.doCommand(sysid, compid, MAVLink.MAV_CMD.CAN_FORWARD, bus, 0, 0, 0, 0, 0, 0, false); }
        catch { }
        await Task.Delay(1000).ConfigureAwait(false);
      }
    });
  }

  /// <summary>Turns a vehicle CAN_FRAME/CANFD_FRAME into the SLCAN line the DroneCAN stack reads.</summary>
  internal static string? ToSlcanLine(MAVLink.MAVLinkMessage m) {
    if (m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.CAN_FRAME) {
      var pkt = (MAVLink.mavlink_can_frame_t)m.data;
      return Line('T', pkt.id, pkt.len, pkt.data);
    }
    if (m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.CANFD_FRAME) {
      var pkt = (MAVLink.mavlink_canfd_frame_t)m.data;
      return Line('B', pkt.id, pkt.len, pkt.data);
    }
    return null;
  }

  private static string Line(char prefix, uint id, byte len, byte[] data) {
    var frame = new DroneCAN.CANFrame(BitConverter.GetBytes(id));
    var payload = new DroneCAN.CANPayload(data);
    return string.Format("{0}{1}{2}{3}\r", prefix, frame.ToHex(), len.ToString("X"),
        payload.ToHex(DroneCAN.DroneCAN.dlcToDataLength(len)));
  }

  /// <summary>Builds the vehicle packet for one SLCAN line written by the DroneCAN stack.</summary>
  internal static object? ToVehiclePacket(string line, byte sysid, byte compid, byte bus) {
    if (!DroneCAN.DroneCAN.TryParseSLCAN(line, out var frame, out var payload)) { return null; }
    uint id = BitConverter.ToUInt32(frame.packet_data, 0) + (frame.Extended ? 0x80000000 : 0);
    byte dlc = (byte)DroneCAN.DroneCAN.dataLengthToDlc(payload.packet_data.Length);
    return payload.packet_data.Length > 8
        ? new MAVLink.mavlink_canfd_frame_t(id, sysid, compid, (byte)(bus - 1), dlc, payload.packet_data)
        : new MAVLink.mavlink_can_frame_t(id, sysid, compid, (byte)(bus - 1), dlc, payload.packet_data);
  }

  internal bool HandleMessage(MAVLink.MAVLinkMessage m) {
    if (_disposed || !_isCurrent()) { return false; }
    string? line = ToSlcanLine(m);
    if (line == null) { return true; }
    Interlocked.Increment(ref _rxFrames);
    try { Port.AppendBuffer(Encoding.ASCII.GetBytes(line)); } catch { }
    return true;
  }

  private void OnStackWrite(object? sender, IEnumerable<byte> bytes) {
    if (_disposed || !_isCurrent()) { return; }
    foreach (string line in Encoding.ASCII.GetString(bytes.ToArray()).Split('\r', StringSplitOptions.RemoveEmptyEntries)) {
      object? packet = ToVehiclePacket(line, _sysid, _compid, _bus);
      if (packet == null) { continue; }
      try {
        if (_send != null) { _send(packet); } else { _link.sendPacket(packet, _sysid, _compid); }
        Interlocked.Increment(ref _txFrames);
      } catch { }
    }
  }

  public void Dispose() {
    if (_disposed) { return; }
    _disposed = true;
    Port.WriteCallback -= OnStackWrite;
    if (_subscribed) { try { _link.UnSubscribeToPacketType(_subId); } catch { } }
    lock (_forwardLock) {
      if (_forwarders.TryGetValue(_link, out var active)) {
        active.Remove(this);
        if (active.Count == 0) { _forwarders.Remove(_link); }
      }
    }
    try { Port.Close(); } catch { }
  }
}
