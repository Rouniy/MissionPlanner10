using System.Collections.Concurrent;
using System.Linq;
using MissionPlanner;
using MissionPlanner.Comms;

namespace MissionPlanner;

public static class AppState {
  public static MAVLinkInterface comPort => Connections.Active.Link;

  internal static MAVLinkInterface PrimaryComPort => Connections.Primary.Link;

  internal static Services.MavLinkConnectionManager Connections { get; }

  internal static Services.JoystickControlService JoystickControl { get; }

  private static Services.VehicleParameterLoadCoordinator _parameterLoads = null!;

  internal static Services.VehicleParameterLoadCoordinator ParameterLoads =>
      System.Threading.Volatile.Read(ref _parameterLoads);

  internal static Services.TrafficService Traffic { get; }

  internal static Services.LocalKmlServer LocalKml { get; }

  public static event System.Action? ConnectionChanged;

  public static void RaiseConnectionChanged() => ConnectionChanged?.Invoke();

  public static bool IsConnected => Connections.Active.IsOpen;

  // Written from comms threads via the CommsBase.Settings callback.
  public static ConcurrentDictionary<string, string> CommsSettings { get; } = new();

  public static Services.ProgressReporter? ActiveConnectReporter { get; set; }

  static AppState() {

    Services.AppPaths.Initialize();

    // Private NV5 status can arrive as soon as a UDP/TCP/serial reader starts. Register the
    // NV modem dialect before constructing any shared MAVLink interface so those early packets are
    // validated and cached even when the NV Modem setup page has not been opened yet.
    Services.NvModemMavlinkDialect.Register();

    // Replace upstream WinForms UI hooks before constructing or opening any shared
    // MAVLink/communications component.
    global::System.CustomMessageBox.ShowEvent += Services.Dialogs.ShowUpstreamMessage;
    var primary = new MAVLinkInterface();
    Connections = new Services.MavLinkConnectionManager(primary);
    _parameterLoads = new Services.VehicleParameterLoadCoordinator(primary);
    JoystickControl = new Services.JoystickControlService(() => comPort);
    Traffic = new Services.TrafficService(() => comPort, applySavedSettings: true);
    LocalKml = new Services.LocalKmlServer();

    Connections.ActiveChanged += (_, current) => {
      var replacement = new Services.VehicleParameterLoadCoordinator(current.Link);
      var previous = System.Threading.Interlocked.Exchange(ref _parameterLoads, replacement);
      previous.CancelCurrent();
      // Never expose parameters from the last time this link/vehicle was active. The selector will
      // start a fresh read, but all persistent views must see an empty list immediately.
      MAVState selected = current.Link.MAV;
      selected.param.Clear();
      selected.param_types.Clear();
    };
    Connections.Changed += () => {
      Traffic.SetMavlinkSources(Connections.Snapshot().Select(connection => connection.Link));
      RaiseConnectionChanged();
    };

    Services.MavLinkProgressContext.EnsureRegistered();

    CommsBase.Settings += (name, value, set) => {
      if (set) {
        CommsSettings[name] = value;
        return value;
      }
      return CommsSettings.TryGetValue(name, out var v) ? v : "";
    };

    CommsBase.InputBoxShow += Services.Dialogs.ShowUpstreamInput;

    ConnectionChanged += () => {
      var connected = IsConnected;
      JoystickControl.HandleConnectionChanged(connected);
      if (!connected) {
        Services.SitlLauncher.ClearPrimaryConnection();
      }
    };

    ApplyUnits();
  }

  public static void ApplyUnits() {
    try {
      var s = MissionPlanner.Utilities.Settings.Instance;

      if (s["distunits"] != null && System.Enum.TryParse<distances>(s["distunits"], out var d)
          && d == distances.Feet) {
        CurrentState.multiplierdist = 3.2808399f;
        CurrentState.DistanceUnit = "ft";
      } else {
        CurrentState.multiplierdist = 1;
        CurrentState.DistanceUnit = "m";
      }

      if (s["altunits"] != null && System.Enum.TryParse<altitudes>(s["altunits"], out var a)
          && a == altitudes.Feet) {
        CurrentState.multiplieralt = 3.2808399f;
        CurrentState.AltUnit = "ft";
      } else {
        CurrentState.multiplieralt = 1;
        CurrentState.AltUnit = "m";
      }

      if (s["speedunits"] != null && System.Enum.TryParse<speeds>(s["speedunits"], out var sp)) {
        switch (sp) {
          case speeds.fps:
            CurrentState.multiplierspeed = 3.2808399f;
            CurrentState.SpeedUnit = "fps";
            break;
          case speeds.kph:
            CurrentState.multiplierspeed = 3.6f;
            CurrentState.SpeedUnit = "kph";
            break;
          case speeds.mph:
            CurrentState.multiplierspeed = 2.23693629f;
            CurrentState.SpeedUnit = "mph";
            break;
          case speeds.knots:
            CurrentState.multiplierspeed = 1.94384449f;
            CurrentState.SpeedUnit = "kts";
            break;
          default:
            CurrentState.multiplierspeed = 1;
            CurrentState.SpeedUnit = "m/s";
            break;
        }
      } else {
        CurrentState.multiplierspeed = 1;
        CurrentState.SpeedUnit = "m/s";
      }
    } catch {

    }
  }
}
