using System;
using MissionPlanner;

namespace MissionPlanner.ViewModels;

/// <summary>DroneCAN stack over the active MAVLink link, shared with the DroneCAN page through <see cref="MavlinkCanTransport"/>.</summary>
public sealed class DroneCanBridge : IDisposable {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private DroneCAN.DroneCAN? _can;
  private MavlinkCanTransport? _transport;

  public DroneCAN.DroneCAN? Can => _can;
  public bool IsOpen => _can != null;
  /// <summary>Why the last <see cref="Start"/> failed: no MAVLink link, a bus conflict or a stack error.</summary>
  public string? LastError { get; private set; }

  public bool Start(byte bus) {
    LastError = null;
    if (_comPort.BaseStream?.IsOpen != true) {
      LastError = "Not connected — open the MAVLink link first.";
      return false;
    }

    var transport = MavlinkCanTransport.Open(_comPort, (byte)_comPort.sysidcurrent, (byte)_comPort.compidcurrent,
        bus, null, out string? conflict);
    if (transport == null) {
      LastError = conflict;
      return false;
    }
    var can = new DroneCAN.DroneCAN { SourceNode = 127 };
    _transport = transport;
    _can = can;
    try {
      can.StartSLCAN(transport.Port.BaseStream);
      can.SetupFileServer();
      can.SetupDynamicNodeAllocator();
    } catch (Exception e) {
      LastError = "CAN start failed: " + e.Message;
      Stop();
      return false;
    }
    return true;
  }

  public void Stop() {
    var transport = _transport;
    var can = _can;
    _transport = null;
    _can = null;
    try { transport?.Dispose(); } catch { }
    try { can?.Stop(false); } catch { }
  }

  public void Dispose() => Stop();
}
