using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlanner.Views;

namespace MissionPlanner.ViewModels;

public sealed record MavSystemChoice(
    MAVLinkInterface Link, byte SysId, byte CompId, string Endpoint, string Label) {
  public override string ToString() => Label;
}

internal enum ConnectionInitializationStage {
  OpeningTransport,
  LoadingParameters,
  Connected,
}

internal sealed class ConnectionInitializationState {
  private int _stage = (int)ConnectionInitializationStage.OpeningTransport;

  internal ConnectionInitializationStage Stage =>
      (ConnectionInitializationStage)Volatile.Read(ref _stage);

  internal void OpenTransport(Action openTransport, Func<bool> isTransportOpen) {
    openTransport();

    // This transition must happen on the opening worker before its Task completes. Otherwise a
    // user can cancel the parameter dialog after MAVLink.Open returned but before the awaiting UI
    // continuation runs, and the already-open transport is incorrectly treated as half-open.
    if (isTransportOpen()) {
      Volatile.Write(ref _stage, (int)ConnectionInitializationStage.LoadingParameters);
    }
  }

  internal void MarkConnected() =>
      Volatile.Write(ref _stage, (int)ConnectionInitializationStage.Connected);
}

public partial class ConnectionViewModel : ViewModelBase, IDisposable {
  private readonly MAVLinkInterface _primaryPort = AppState.PrimaryComPort;
  private MAVLinkInterface _comPort => _primaryPort;
  private readonly CancellationTokenSource _lifetimeCts = new();
  private readonly Task _lifetimeTask;
  private readonly Services.MavLinkTransportRelease _transportRelease = new();
  private int _disposeState;

  internal event Action? Connected;

  public ObservableCollection<string> Ports { get; } = [];
  public ObservableCollection<int> Bauds { get; } =
      [
        1200, 2400, 4800, 9600, 19200, 38400, 57600, 111100, 115200, 230400,
        460800, 500000, 625000, 921600, 1000000, 1500000,
      ];

  public ObservableCollection<MavSystemChoice> VehicleChoices { get; } = [];

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditBaud))]
  private string? _selectedPort;

  [ObservableProperty]
  private int _selectedBaud = 115200;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanEditConnection))]
  [NotifyPropertyChangedFor(nameof(CanEditBaud))]
  [NotifyPropertyChangedFor(nameof(VehicleSelectorVisible))]
  private bool _isConnected;

  [ObservableProperty]
  private MavSystemChoice? _selectedVehicle;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(VehicleSelectorVisible))]
  private bool _hasVehicleChoices;

  public bool CanEditConnection => !IsConnected;
  public bool CanEditBaud => !IsConnected && IsSerialEndpoint(SelectedPort) &&
      !IsBleEndpoint(SelectedPort);
  public bool VehicleSelectorVisible => HasVehicleChoices;

  [ObservableProperty]
  private string _connectText = "CONNECT";

  [ObservableProperty]
  private string _status = "";

  [ObservableProperty]
  private int _progress = -1;

  [ObservableProperty]
  private bool _readOnly;

  [ObservableProperty]
  private bool _autoConnect;

  partial void OnReadOnlyChanged(bool value) => _comPort.ReadOnly = value;

  partial void OnAutoConnectChanged(bool value) =>
      Settings.Instance["autoconnect"] = value.ToString();

  partial void OnSelectedPortChanged(string? value) {
    if (_initializing) {
      return;
    }
    SelectedBaud = SavedBaudForPort(value, SelectedBaud);
  }

  partial void OnSelectedBaudChanged(int value) {
    if (_initializing || value <= 0 || !IsSerialEndpoint(SelectedPort) ||
        IsBleEndpoint(SelectedPort)) {
      return;
    }
    Settings.Instance[PortBaudKey(SelectedPort!)] = value.ToString();
  }

  partial void OnSelectedVehicleChanged(MavSystemChoice? oldValue, MavSystemChoice? newValue) {
    if (_updatingVehicleChoices) {
      return;
    }

    AppState.ParameterLoads.CancelCurrent();
    // A parameter list belongs to one explicit selection only. Discard both the previous target
    // and any cached values on the target being selected before changing the active MAV pointer.
    // Clearing the old target also means returning to it always requires a new live read.
    ResetParameterSelection(oldValue, newValue);
    AppState.RaiseConnectionChanged();
    if (newValue == null) {
      Status = "No vehicle selected. Parameter values are hidden.";
      return;
    }
    if (newValue.Link.BaseStream?.IsOpen != true) {
      Status = $"Connection {newValue.Endpoint} is no longer available.";
      return;
    }

    Interlocked.Exchange(ref _selectionSwitchInProgress, 1);
    try {
      if (!AppState.Connections.SetActive(newValue.Link)) {
        Status = $"Connection {newValue.Endpoint} is no longer available.";
        return;
      }
    } finally {
      Interlocked.Exchange(ref _selectionSwitchInProgress, 0);
    }
    newValue.Link.sysidcurrent = newValue.SysId;
    newValue.Link.compidcurrent = newValue.CompId;
    LoadSelectedVehicleParameters(newValue);
  }

  internal static void ResetParameterSelection(
      MavSystemChoice? previous, MavSystemChoice? next) {
    if (previous != null) {
      ResetSelectedVehicleParameters(
          previous.Link.MAVlist[previous.SysId, previous.CompId]);
    }
    if (next != null && (previous == null ||
        !ReferenceEquals(previous.Link, next.Link) ||
        previous.SysId != next.SysId || previous.CompId != next.CompId)) {
      ResetSelectedVehicleParameters(next.Link.MAVlist[next.SysId, next.CompId]);
    }
  }

  public ConnectionViewModel() {
    _initializing = true;
    _comPort.Progress += OnProgress;
    AppState.Connections.ActiveChanged += OnActiveConnectionChanged;
    ApplyPersistentLinkSettings();
    RefreshPorts();
    string savedPort = Settings.Instance.ComPort;
    if (!string.IsNullOrWhiteSpace(savedPort)) {
      if (!Ports.Contains(savedPort)) {
        Ports.Insert(1, savedPort);
      }
      SelectedPort = savedPort;
    }
    if (int.TryParse(Settings.Instance.BaudRate, out var savedBaud) && savedBaud > 0) {
      SelectedBaud = savedBaud;
    } else {
      SelectedBaud = Settings.Instance.GetInt32("baudrate", SelectedBaud);
    }
    SelectedBaud = SavedBaudForPort(SelectedPort, SelectedBaud);
    _autoConnect = Settings.Instance.GetBoolean("autoconnect", false);
    _initializing = false;
    // Capture before scheduling: Dispose may otherwise win the race and accessing Token on a
    // disposed source from the delayed task body would throw.
    var lifetimeToken = _lifetimeCts.Token;
    _lifetimeTask = Task.Run(() => HeartbeatLoop(lifetimeToken));
  }

  private Services.ProgressReporter? _connectDialog;
  private readonly SemaphoreSlim _connectGate = new(1, 1);

  private readonly Lock _readerSync = new();
  private readonly HashSet<string> _serviceBulletinProbes = new(StringComparer.Ordinal);
  private readonly HashSet<string> _serviceBulletinsShown = new(StringComparer.Ordinal);
  private CancellationTokenSource? _readerCts;
  private int _readerGeneration;
  private DateTime _connectedAtUtc = DateTime.MinValue;
  private DateTime _lastVersionPollUtc = DateTime.MinValue;
  private DateTime _nextReaderErrorLogUtc = DateTime.MinValue;
  private bool _lastArmed;
  private int _homeRefreshRunning;
  private readonly bool _initializing;
  private bool _updatingVehicleChoices;
  private string _vehicleChoiceSignature = "";
  private int _selectionSwitchInProgress;

  private int StartReader() {
    StopReader();

    foreach (var mav in _comPort.MAVlist) {
      mav.cs.rateattitude = CurrentState.rateattitudebackup;
      mav.cs.rateposition = CurrentState.ratepositionbackup;
      mav.cs.ratestatus = CurrentState.ratestatusbackup;
      mav.cs.ratesensors = CurrentState.ratesensorsbackup;
      mav.cs.raterc = CurrentState.ratercbackup;
    }
    RequestStreams();

    var cts = new CancellationTokenSource();
    // Capture the token now: a fast disconnect can cancel+dispose the CTS before the task
    // body ever runs, and cts.Token on a disposed source throws.
    var token = cts.Token;
    int generation;
    lock (_readerSync) {
      generation = ++_readerGeneration;
      _readerCts = cts;
    }
    _ = Task.Run(() => SerialReaderLoop(cts, token, generation));
    return generation;
  }

  private void RequestStreams() {
    try {
      foreach (var mav in _comPort.MAVlist) {
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTENDED_STATUS, mav.cs.ratestatus, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, mav.cs.rateposition, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA1, mav.cs.rateattitude, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA2, mav.cs.rateattitude, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA3, mav.cs.ratesensors, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.RAW_SENSORS, mav.cs.ratesensors, mav.sysid, mav.compid);
        _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.RC_CHANNELS, mav.cs.raterc, mav.sysid, mav.compid);
      }
    } catch {

    }
  }

  private void StopReader() {
    AppState.ParameterLoads.CancelCurrent();
    CancellationTokenSource? cts;
    lock (_readerSync) {
      _readerGeneration++;
      cts = _readerCts;
      _readerCts = null;
    }
    if (cts == null) {
      return;
    }
    try {
      cts.Cancel();
    } catch (ObjectDisposedException) {
    }
    cts.Dispose();
  }

  public void Shutdown() {
    StopReader();
    Services.Speech.Stop();
    _ = _transportRelease.Begin(_comPort);
    AppState.Connections.Primary.MarkClosed();
    CloseLogs();
    AppState.Connections.Dispose();
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref _disposeState, 1) != 0) {
      return;
    }

    _comPort.Progress -= OnProgress;
    AppState.Connections.ActiveChanged -= OnActiveConnectionChanged;
    _lifetimeCts.Cancel();
    Shutdown();
    try {
      _lifetimeTask.Wait(TimeSpan.FromSeconds(1));
    } catch {
    }
    _lifetimeCts.Dispose();
  }

  private async Task SerialReaderLoop(CancellationTokenSource self, CancellationToken ct,
      int generation) {
    int consecutiveErrors = 0;
    while (!ct.IsCancellationRequested) {

      if (!AppState.Connections.Primary.IsOpen) {
        HandleLinkLost(self, generation);
        break;
      }

      try {

        if (_comPort.giveComport == false) {
          var now = DateTime.UtcNow;
          var newestPacket = NewestPacketUtc();
          if (ConnectionHealth.IsSilent(
                  now, newestPacket, _connectedAtUtc, TimeSpan.FromSeconds(10))) {
            SetLinkQualityLost();
            if (ConnectionHealth.ShouldCloseSilentLink(
                    _comPort.BaseStream, _comPort.MAV.cs.armed,
                    now, newestPacket, _connectedAtUtc,
                    TimeSpan.FromSeconds(10))) {
              HandleLinkLost(self, generation);
              break;
            }
            // Keep an armed radio link open through a telemetry fade so it can recover, matching
            // upstream. Disarmed dead links are closed above to release joystick/output resources.
          }
        }

        if (_comPort.giveComport == false) {
          var start = DateTime.UtcNow;
          while (_comPort.giveComport == false && _comPort.BaseStream?.IsOpen == true &&
                 ConnectionHealth.ShouldPollReader(_comPort.BaseStream) &&
                 !ct.IsCancellationRequested &&
                 start.AddSeconds(1) > DateTime.UtcNow) {
            await _comPort.readPacketAsync().ConfigureAwait(false);
          }

          foreach (var mav in _comPort.MAVlist) {
            mav.cs.UpdateCurrentSettings(null, false, _comPort, mav);
          }
          RefreshHomeOnArmTransition(ct);
        }

        consecutiveErrors = 0;
        await Task.Delay(_comPort.giveComport ? 50 : 1, ct).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        if (++consecutiveErrors >= 5) {
          bool close = ConnectionHealth.ShouldCloseAfterReaderErrors(_comPort.BaseStream);
          if (close) {
            Console.Error.WriteLine(
                $"Primary MAVLink reader had five consecutive errors on " +
                $"{_comPort.BaseStream?.GetType().Name}; closing the connection: {ex}");
            HandleLinkLost(self, generation);
            break;
          }
          SetLinkQualityLost();
          DateTime errorUtc = DateTime.UtcNow;
          if (errorUtc >= _nextReaderErrorLogUtc) {
            _nextReaderErrorLogUtc = errorUtc.AddSeconds(5);
            Console.Error.WriteLine(
                $"Primary MAVLink reader errors on {_comPort.BaseStream?.GetType().Name}; "
                + $"keeping the persistent listener open: {ex}");
          }
          consecutiveErrors = 0;
        }
        try {
          await Task.Delay(50, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          break;
        }
      }
    }
  }

  private void SendHeartbeat() {
    try {
      var mav = _comPort.MAV;
      _comPort.sendPacket(
          new MAVLink.mavlink_heartbeat_t {
            type = (byte)MAVLink.MAV_TYPE.GCS,
            autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
            mavlink_version = 3,
          },
          mav.sysid, mav.compid);
    } catch {
    }
  }

  private async Task HeartbeatLoop(CancellationToken ct) {
    while (!ct.IsCancellationRequested) {
      try {
        if (AppState.Connections.Primary.IsOpen) {
          if (Settings.Instance.GetBoolean("CHK_GCSheartbeat", true)) {
            SendHeartbeat();
          }
          if (_connectedAtUtc != DateTime.MinValue && !_comPort.giveComport &&
              !_comPort.MAV.cs.armed &&
              DateTime.UtcNow > _connectedAtUtc.AddSeconds(60)) {
            _comPort.getParamPoll();
            _comPort.getParamPoll();
          }
          if (_connectedAtUtc != DateTime.MinValue && !_comPort.giveComport &&
              DateTime.UtcNow > _lastVersionPollUtc.AddSeconds(20)) {
            _lastVersionPollUtc = DateTime.UtcNow;
            foreach (var mav in _comPort.MAVlist) {
              if (mav.cs.capabilities == 0 && mav.cs.version < new Version(0, 1)) {
                _comPort.getVersion(mav.sysid, mav.compid, false);
              }
            }
          }
        }
        RefreshVehicleChoices();
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        return;
      } catch {
        try {
          await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          return;
        }
      }
    }
  }

  private void RefreshVehicleChoices(bool reloadActiveParameters = false) {
    Services.MavLinkConnection[] openConnections = [.. AppState.Connections.Snapshot()
        .Where(connection => connection.IsOpen)];
    MavSystemChoice[] choices = [.. openConnections
        .SelectMany(connection => connection.Link.MAVlist.ToArray()
            .Where(mav => mav.sysid != 0 &&
                mav.compid != (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_MISSIONPLANNER)
            .Select(mav => new MavSystemChoice(
                connection.Link, mav.sysid, mav.compid, connection.Endpoint,
                VehicleChoiceLabel(connection, mav))))
        .OrderBy(choice => choice.Endpoint, StringComparer.OrdinalIgnoreCase)
        .ThenBy(choice => choice.SysId)
        .ThenBy(choice => choice.CompId)];
    string signature = string.Join(";", choices.Select(choice =>
        $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(choice.Link)}:" +
        $"{choice.SysId}:{choice.CompId}:{choice.Label}"));
    if (!reloadActiveParameters &&
        string.Equals(signature, _vehicleChoiceSignature, StringComparison.Ordinal)) {
      return;
    }
    _vehicleChoiceSignature = signature;

    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
      MavSystemChoice? fallbackSelection = null;
      _updatingVehicleChoices = true;
      try {
        // A passive startup UDP listener is an available transport, not an established vehicle
        // session. Keep the ordinary CONNECT action available until the primary link is open or
        // at least one real MAVLink system has arrived on any secondary connection.
        IsConnected = AppState.Connections.Primary.IsOpen || choices.Length > 0;
        ConnectText = IsConnected ? "DISCONNECT" : "CONNECT";
        VehicleChoices.Clear();
        foreach (var choice in choices) {
          VehicleChoices.Add(choice);
        }
        HasVehicleChoices = choices.Length > 0;
        var selected = choices.FirstOrDefault(choice =>
            ReferenceEquals(choice.Link, AppState.comPort) &&
            choice.SysId == AppState.comPort.sysidcurrent &&
            choice.CompId == AppState.comPort.compidcurrent)
            ?? choices.FirstOrDefault();
        SelectedVehicle = selected;
        if (selected != null) {
          if (!ReferenceEquals(AppState.comPort, selected.Link) ||
              selected.Link.sysidcurrent != selected.SysId ||
              selected.Link.compidcurrent != selected.CompId) {
            AppState.Connections.SetActive(selected.Link);
            selected.Link.sysidcurrent = selected.SysId;
            selected.Link.compidcurrent = selected.CompId;
            fallbackSelection = selected;
          } else if (reloadActiveParameters) {
            fallbackSelection = selected;
          }
        }
      } finally {
        _updatingVehicleChoices = false;
      }
      if (fallbackSelection != null) {
        LoadSelectedVehicleParameters(fallbackSelection);
      }
    });
  }

  internal void RefreshManagedConnections(bool reloadActiveParameters = false) =>
      RefreshVehicleChoices(reloadActiveParameters);

  private void OnActiveConnectionChanged(
      Services.MavLinkConnection previous,
      Services.MavLinkConnection current) {
    if (Volatile.Read(ref _selectionSwitchInProgress) != 0) {
      return;
    }
    // This path covers automatic fallback after a modem disappears. AppState has already cleared
    // the new target's cached parameters; rebuild the selector and start a fresh read on the UI.
    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        RefreshVehicleChoices(reloadActiveParameters: true));
  }

  private static string VehicleChoiceLabel(
      Services.MavLinkConnection connection, MAVState mav) {
    string component;
    if (mav.CANNode) {
      component = string.IsNullOrWhiteSpace(mav.VersionString)
          ? "CAN node"
          : mav.VersionString;
    } else if (mav.compid == (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1) {
      component = mav.aptype.ToString();
    } else {
      component = ((MAVLink.MAV_COMPONENT)mav.compid).ToString();
      const string prefix = "MAV_COMP_ID_";
      if (component.StartsWith(prefix, StringComparison.Ordinal)) {
        component = component[prefix.Length..];
      }
      component = component.Replace('_', ' ');
    }
    return $"{connection.Endpoint} — {mav.sysid}:{mav.compid} {component}";
  }

  private void LoadSelectedVehicleParameters(MavSystemChoice choice) {
    MAVLinkInterface link = choice.Link;
    var mav = link.MAVlist[choice.SysId, choice.CompId];
    // A list from a previous selection may be correct, but displaying it while a new read is in
    // flight is unsafe. Always make the selected target visibly empty until this request completes.
    ResetSelectedVehicleParameters(mav);

    var operation = AppState.ParameterLoads.Start(
        choice.SysId, choice.CompId, _lifetimeCts.Token, OnProgress);
    Status = $"Loading parameters for {choice.Label}…";
    AppState.RaiseConnectionChanged();
    _ = ObserveSelectedVehicleParameterLoad(choice, mav, operation);
  }

  internal static void ResetSelectedVehicleParameters(MAVState mav) {
    mav.param.Clear();
    lock (mav.param_types) {
      mav.param_types.Clear();
    }
  }

  internal static void ResetAllVehicleParameters(MAVLinkInterface comPort) {
    foreach (var mav in comPort.MAVlist.ToArray()) {
      ResetSelectedVehicleParameters(mav);
    }
  }

  private async Task ObserveSelectedVehicleParameterLoad(
      MavSystemChoice choice,
      MAVState mav,
      Services.VehicleParameterLoadCoordinator.Operation operation) {
    Exception? error = null;
    try {
      await operation.Completion.ConfigureAwait(false);
    } catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) {
      return;
    } catch (Exception ex) {
      error = ex;
    }

    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
      if (!operation.IsLatest || !ReferenceEquals(AppState.comPort, choice.Link) ||
          choice.Link.BaseStream?.IsOpen != true ||
          choice.Link.sysidcurrent != choice.SysId || choice.Link.compidcurrent != choice.CompId) {
        return;
      }
      if (error != null) {
        // PARAM_VALUE processing can populate the shared list incrementally before the upstream
        // reader reports a timeout/error. Never expose that partial result as a valid list.
        ResetSelectedVehicleParameters(mav);
      }
      Status = error == null
          ? $"Selected {choice.Label}. {mav.param.Count} params."
          : $"Selected {choice.Label}; parameter load failed: {error.Message}";
      if (error == null) {
        StartCubeServiceBulletinChecks(choice.Link, mav);
      }
      AppState.RaiseConnectionChanged();
    });
  }

  private void StartCubeServiceBulletinChecks(MAVLinkInterface link, MAVState mav) {
    if (!Services.CubeServiceBulletin.IsAffectedCubeBlack(mav.SerialString)) {
      return;
    }
    string identity = string.IsNullOrWhiteSpace(mav.SerialString)
        ? $"{mav.sysid}:{mav.compid}"
        : mav.SerialString;
    if (_serviceBulletinsShown.Contains(identity)) {
      return;
    }

    double? Parameter(string name) =>
        mav.param.ContainsKey(name) ? mav.param[name].Value : null;
    if (Services.CubeServiceBulletin.RequiresParameterScan(mav.SerialString, Parameter)) {
      ShowCubeServiceBulletinOnce(identity, mav, "parameter scan");
      return;
    }
    if (!_serviceBulletinProbes.Add(identity)) {
      return;
    }

    _ = Task.Run(() => Services.CubeServiceBulletin.ProbeSpi(link, mav)).ContinueWith(task =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
          _serviceBulletinProbes.Remove(identity);
          if (task.Status != TaskStatus.RanToCompletion || !task.Result
              || !ReferenceEquals(AppState.comPort, link)
              || link.BaseStream?.IsOpen != true
              || link.sysidcurrent != mav.sysid || link.compidcurrent != mav.compid) {
            return;
          }
          ShowCubeServiceBulletinOnce(identity, mav, "SPI scan");
        }), TaskScheduler.Default);
  }

  private void ShowCubeServiceBulletinOnce(string identity, MAVState mav, string detectedVia) {
    if (!_serviceBulletinsShown.Add(identity)) {
      return;
    }
    _ = Services.CubeServiceBulletin.ShowAsync(
        Services.CubeServiceBulletin.Capture(mav), detectedVia);
  }

  private bool IsReaderSessionActive(int generation) {
    lock (_readerSync) {
      return _readerGeneration == generation && _readerCts != null &&
          _comPort.BaseStream?.IsOpen == true;
    }
  }

  private DateTime NewestPacketUtc() {
    DateTime newest = DateTime.MinValue;
    foreach (var mav in _comPort.MAVlist) {
      if (mav.lastvalidpacket > newest) {
        newest = mav.lastvalidpacket;
      }
    }
    return newest;
  }

  private void SetLinkQualityLost() {
    foreach (var mav in _comPort.MAVlist) {
      mav.cs.linkqualitygcs = 0;
    }
  }

  internal void PrepareForConnection() {
    AppState.ParameterLoads.CancelCurrent();
    ResetAllVehicleParameters(_comPort);
    ApplyPersistentLinkSettings();
    _lastArmed = false;
    _lastVersionPollUtc = DateTime.MinValue;
    foreach (var mav in _comPort.MAVlist) {
      mav.cs.ResetInternals();
    }
  }

  internal void AdoptOpenConnection(string endpoint) {
    if (_comPort.BaseStream?.IsOpen != true) {
      return;
    }
    OpenLogs();
    AppState.Connections.Primary.Endpoint = endpoint;
    AppState.Connections.Primary.MarkOpened();
    AppState.Connections.SetActive(_comPort);
    IsConnected = true;
    ConnectText = "DISCONNECT";
    Status = $"Connected to {endpoint}. {_comPort.MAV.param.Count} params.";
    _connectedAtUtc = DateTime.UtcNow;
    StartReader();
    RefreshVehicleChoices();
    AppState.RaiseConnectionChanged();
    RaiseConnected();
  }

  private void RaiseConnected() {
    try {
      Connected?.Invoke();
    } catch {
      // A UI convenience such as auto-loading a mission must never tear down a valid link.
    }
  }

  private void StartBackgroundParameterLoad(int generation) {
    byte sysid = _comPort.MAV.sysid;
    byte compid = _comPort.MAV.compid;
    var operation = AppState.ParameterLoads.Start(
        sysid, compid, _lifetimeCts.Token, OnProgress);
    _ = Task.Run(async () => {
      Exception? error = null;
      try {
        await operation.Completion.ConfigureAwait(false);
      } catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) {
        return;
      } catch (Exception ex) {
        error = ex;
      }

      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        lock (_readerSync) {
          if (_readerGeneration != generation || _readerCts == null ||
              _comPort.BaseStream?.IsOpen != true || !operation.IsLatest) {
            return;
          }
        }
        Status = error == null
            ? $"Connected. {_comPort.MAV.param.Count} params loaded in background."
            : "Connected, but background parameter load failed: " + error.Message;
        if (error == null) {
          StartCubeServiceBulletinChecks(_comPort, _comPort.MAV);
        }
        AppState.RaiseConnectionChanged();
      });
    });
  }

  private void StartPostConnectMetadataCheck(int generation) {
    string versionString = _comPort.MAV.VersionString;
    if (string.IsNullOrWhiteSpace(versionString)) {
      return;
    }

    _ = Task.Run(async () => {
      Version version;
      try {
        version = VersionDetection.GetVersion(versionString);
      } catch {
        return;
      }

      // Keep metadata refresh independent from the firmware manifest. A temporary failure of one
      // upstream endpoint must not suppress the other half of the post-connect behaviour.
      try {
        await ParameterMetaDataRepositoryAPMpdef.GetMetaDataVersioned(version).ConfigureAwait(false);
      } catch (Exception ex) {
        Console.Error.WriteLine($"Version-specific parameter metadata refresh failed: {ex.Message}");
      }

      Services.VehicleFirmwareUpdate? update = null;
      try {
        update = Services.VehicleFirmwarePolicy.FindNewerOfficialRelease(
            versionString, APFirmware.GetReleaseNewest(APFirmware.RELEASE_TYPES.OFFICIAL));
      } catch (Exception ex) {
        Console.Error.WriteLine($"Vehicle firmware update check failed: {ex.Message}");
      }

      if (update == null || !IsReaderSessionActive(generation)) {
        return;
      }

      Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
        if (!IsReaderSessionActive(generation)) {
          return;
        }
        try {
          await Services.Dialogs.MessageShowAgain(
              $"New stable firmware: {update.VehicleType} {update.Available}",
              $"The connected vehicle reports {update.Current}. Stable {update.VehicleType} " +
              $"{update.Available} is available. Release notes: " +
              "https://discuss.ardupilot.org/tags/stable-release",
              $"vehicle-firmware-{update.VehicleType}-{update.Available}");
        } catch (Exception ex) {
          Console.Error.WriteLine($"Unable to show the vehicle firmware notice: {ex.Message}");
        }
      });
    });
  }

  private void RefreshHomeOnArmTransition(CancellationToken ct) {
    bool armed = _comPort.MAV.cs.armed;
    bool becameArmed = armed && !_lastArmed;
    _lastArmed = armed;
    if (!becameArmed || _comPort.MAV.apname == MAVLink.MAV_AUTOPILOT.INVALID ||
        _comPort.MAV.aptype == MAVLink.MAV_TYPE.GIMBAL ||
        Interlocked.Exchange(ref _homeRefreshRunning, 1) != 0) {
      return;
    }

    _ = Task.Run(async () => {
      try {
        while (_comPort.giveComport && _comPort.BaseStream?.IsOpen == true) {
          await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (!ct.IsCancellationRequested && _comPort.BaseStream?.IsOpen == true) {
          _comPort.MAV.cs.HomeLocation = new PointLatLngAlt(
              _comPort.getWP(_comPort.MAV.sysid, _comPort.MAV.compid, 0));
        }
      } catch (OperationCanceledException) {
      } catch {
        // Home refresh is best-effort, matching upstream's arm-transition helper.
      } finally {
        Interlocked.Exchange(ref _homeRefreshRunning, 0);
      }
    });
  }

  private static void ApplyPersistentLinkSettings() {
    var settings = Settings.Instance;
    CurrentState.rateattitudebackup = Math.Max(0, settings.GetInt32("CMB_rateattitude", CurrentState.DefaultAttitudeRate));
    CurrentState.ratepositionbackup = Math.Max(0, settings.GetInt32("CMB_rateposition", 2));
    CurrentState.ratestatusbackup = Math.Max(0, settings.GetInt32("CMB_ratestatus", 2));
    CurrentState.ratercbackup = Math.Max(0, settings.GetInt32("CMB_raterc", 2));
    CurrentState.ratesensorsbackup = Math.Max(0, settings.GetInt32("CMB_ratesensors", 2));
    int sysid = settings.ContainsKey("gcsid")
        ? settings.GetInt32("gcsid", 255)
        : settings.GetInt32("GCS_sysid", 255);
    MAVLinkInterface.gcssysid = (byte)Math.Clamp(sysid, 1, 255);
  }

  private void HandleLinkLost(CancellationTokenSource self, int generation) {
    lock (_readerSync) {
      if (_readerCts != self || _readerGeneration != generation) {
        // StopReader or a newer reader already owns the shared MAVLink interface. In particular,
        // an old reader must never close a newly-opened connection.
        return;
      }

      _readerCts = null;
      _connectedAtUtc = DateTime.MinValue;
    }

    // Never hold the reader lock while a driver/socket is closing. Disconnect on the UI thread
    // must be able to invalidate this generation even if an unplugged device never returns from
    // Close. Logical state changes are published before the best-effort OS cleanup completes.
    _ = _transportRelease.Begin(_comPort);
    ResetAllVehicleParameters(_comPort);
    CloseLogs();
    AppState.Connections.NotifyClosed(AppState.Connections.Primary);
    Services.Speech.Stop();
    self.Dispose();

    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
      lock (_readerSync) {
        // A reconnect or an explicit disconnect may have happened while this notification was
        // queued on the UI thread.
        if (_readerGeneration != generation || _readerCts != null) {
          return;
        }
      }
      IsConnected = false;
      ConnectText = "CONNECT";
      Status = "Connection lost.";
      RefreshVehicleChoices();
      AppState.RaiseConnectionChanged();
    });
  }

  private void OnProgress(int percent, string status) =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        Progress = percent;
        if (!string.IsNullOrEmpty(status)) {
          Status = status;
        }
        _connectDialog?.Set(percent < 0 ? 0 : percent, status);
      });

  [RelayCommand]
  private void RefreshPorts() {
    int bleGeneration = Interlocked.Increment(ref _bleRefreshGeneration);
    var cur = SelectedPort;
    Ports.Clear();
    Ports.Add("AUTO");
    foreach (var p in DedupePorts(SerialPort.GetPortNames())) {
      Ports.Add(p);
    }
    foreach (var net in new[] { "TCP", "UDP", "UDPCl", "WS" }) {
      Ports.Add(net);
    }

    // Preserve a configured removable serial device even while it is unplugged. It can then be
    // selected automatically when the app is launched after the device appears again.
    if (!string.IsNullOrWhiteSpace(cur) && !Ports.Contains(cur)) {
      Ports.Insert(1, cur);
    }

    SelectedPort = Ports.Contains(cur ?? "") ? cur : Ports.FirstOrDefault(p => p != "AUTO");

    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) {
      const string scanning = "Scanning Bluetooth LE devices…";
      Status = scanning;
      CancellationTokenSource scanCancellation =
          CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
      CancellationTokenSource? previous;
      lock (_bleRefreshSync) {
        previous = _bleRefreshCancellation;
        _bleRefreshCancellation = scanCancellation;
      }
      try {
        previous?.Cancel();
      } catch (ObjectDisposedException) {
      }
      _ = RefreshBlePortsAsync(bleGeneration, scanning, scanCancellation);
    }
  }

  private readonly object _bleRefreshSync = new();
  private int _bleRefreshGeneration;
  private CancellationTokenSource? _bleRefreshCancellation;

  private async Task RefreshBlePortsAsync(
      int generation, string scanningStatus, CancellationTokenSource cancellation) {
    CancellationToken cancellationToken = cancellation.Token;
    try {
      IReadOnlyList<Services.BleDeviceInfo> devices =
          await Services.BleSerial.DiscoverAsync(
              TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
      await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
        if (generation != Volatile.Read(ref _bleRefreshGeneration) ||
            cancellationToken.IsCancellationRequested) {
          return;
        }
        foreach (Services.BleDeviceInfo device in devices) {
          if (!Ports.Contains(device.Endpoint)) {
            int networkStart = Ports.IndexOf("TCP");
            Ports.Insert(networkStart >= 0 ? networkStart : Ports.Count, device.Endpoint);
          }
        }
        if (Status == scanningStatus) {
          Status = devices.Count == 0
              ? "No Nordic UART BLE devices found."
              : $"Found {devices.Count} Nordic UART BLE device(s).";
        }
      });
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
        if (generation == Volatile.Read(ref _bleRefreshGeneration) && Status == scanningStatus) {
          Status = "Bluetooth LE scan unavailable: " + ex.Message;
        }
      });
    } finally {
      lock (_bleRefreshSync) {
        if (ReferenceEquals(_bleRefreshCancellation, cancellation)) {
          _bleRefreshCancellation = null;
        }
      }
      cancellation.Dispose();
    }
  }

  private void CancelBleRefresh() {
    CancellationTokenSource? cancellation;
    lock (_bleRefreshSync) {
      cancellation = _bleRefreshCancellation;
    }
    try {
      cancellation?.Cancel();
    } catch (ObjectDisposedException) {
    }
  }

  private static readonly string[] _internalPorts = ["Bluetooth-Incoming-Port", "debug-console"];

  private static IEnumerable<string> DedupePorts(string[] names) {
    var all = names.Distinct()
        .Where(n => !_internalPorts.Any(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    var cuDevices = new HashSet<string>(
        all.Where(n => n.Contains("/cu.")).Select(n => n.Replace("/cu.", "/tty.")));
    return all.Where(n => !cuDevices.Contains(n)).OrderBy(n => n);
  }

  internal static bool IsSerialEndpoint(string? port) =>
      !string.IsNullOrWhiteSpace(port) && port is not "AUTO" and not "TCP" and not "UDP"
          and not "UDPCl" and not "WS";

  internal static bool IsBleEndpoint(string? port) =>
      !string.IsNullOrWhiteSpace(port) &&
      port.StartsWith(Services.BleEndpoint.Prefix, StringComparison.OrdinalIgnoreCase);

  internal static string PortBaudKey(string port) => port.Replace(" ", "_") + "_BAUD";

  private static int SavedBaudForPort(string? port, int fallback) {
    if (!IsSerialEndpoint(port) || IsBleEndpoint(port)) {
      return fallback;
    }
    int saved = Settings.Instance.GetInt32(PortBaudKey(port!), fallback);
    return saved > 0 ? saved : fallback;
  }

  private void OpenLogs() {
    try {
      Directory.CreateDirectory(Settings.Instance.LogDir);
      var dt = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
      string tlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".tlog";
      string rlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".rlog";
      int a = 1;
      while (File.Exists(tlog) || File.Exists(rlog)) {
        dt = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + "-" + a++;
        tlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".tlog";
        rlog = Settings.Instance.LogDir + Path.DirectorySeparatorChar + dt + ".rlog";
      }
      _comPort.logfile =
          new BufferedStream(File.Open(tlog, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
      _comPort.rawlogfile =
          new BufferedStream(File.Open(rlog, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    } catch (Exception ex) {
      CloseLogs();
      Status = "Telemetry logging disabled: " + ex.Message;
    }
  }

  private void CloseLogs() {
    try {
      _comPort.logfile?.Close();
      _comPort.rawlogfile?.Close();
    } catch {

    }
    _comPort.logfile = null;
    _comPort.rawlogfile = null;
  }

  [RelayCommand]
  private async Task ToggleConnect() {
    MAVLinkInterface activeLink = AppState.comPort;
    Services.MavLinkConnection? activeConnection = AppState.Connections.Find(activeLink);
    if (activeConnection?.IsOpen == true) {
      double displayedSpeed = activeLink.MAV.cs.groundspeed * CurrentState.multiplierspeed;
      if (activeLink.MAV.cs.groundspeed > 4 && !await Services.Dialogs.Confirm(
              "Disconnect",
              $"The vehicle is still moving at {displayedSpeed:0.0} {CurrentState.SpeedUnit}. Disconnect anyway?")) {
        return;
      }
      if (activeConnection is { IsPrimary: false }) {
        AppState.ParameterLoads.CancelCurrent();
        await AppState.Connections.RemoveAsync(activeConnection);
        Status = $"Disconnected {activeConnection.Endpoint}.";
        RefreshVehicleChoices();
      } else {
        await DisconnectAsync("Disconnected.");
      }
      return;
    }

    await ConnectAsync(interactive: true);
  }

  internal async Task TryAutoConnectAsync() {
    if (!AutoConnect || AppState.Connections.Primary.IsOpen ||
        string.IsNullOrWhiteSpace(SelectedPort)) {
      return;
    }

    Status = $"Auto-connecting to {SelectedPort}…";
    await ConnectAsync(interactive: false);
  }

  internal async Task ImportConnectionListAsync(string path) {
    if (!await _connectGate.WaitAsync(0)) {
      await Services.Dialogs.Alert(
          "Connection List", "Another connection attempt is already running.");
      return;
    }

    var reporter = new Services.ProgressReporter("Opening Connection List");
    _connectDialog = reporter;
    AppState.ActiveConnectReporter = reporter;
    reporter.Set(0, "Reading connection list…");
    reporter.Show2();
    try {
      Services.ConnectionListOpenResult result = await Services.ConnectionListService.OpenFileAsync(
          path, AppState.Connections, reporter.Token,
          (percent, message) => reporter.Set(percent, message));
      RefreshVehicleChoices();
      Status = $"Connection List: opened {result.Opened.Count}/{result.Requested}.";

      var issues = result.ParseErrors
          .Select(error => $"Line {error.Line}: {error.Message}")
          .Concat(result.Failures.Select(failure =>
              $"{failure.Endpoint.DisplayName}: {failure.Message}"))
          .Take(12)
          .ToArray();
      string summary = $"Opened {result.Opened.Count} connection(s).";
      int issueCount = result.ParseErrors.Count + result.Failures.Count;
      if (issueCount > 0) {
        summary += $"\n\n{issueCount} entry/entries were not opened:\n" +
            string.Join("\n", issues);
        if (issueCount > issues.Length) {
          summary += $"\n…and {issueCount - issues.Length} more.";
        }
      }
      await Services.Dialogs.Alert("Connection List", summary);
    } catch (OperationCanceledException) when (reporter.CancelRequested) {
      Status = "Connection List opening cancelled.";
    } catch (Exception ex) {
      Status = "Connection List failed: " + ex.Message;
      await Services.Dialogs.Alert("Connection List", ex.Message);
    } finally {
      reporter.Close();
      if (ReferenceEquals(_connectDialog, reporter)) {
        _connectDialog = null;
      }
      if (ReferenceEquals(AppState.ActiveConnectReporter, reporter)) {
        AppState.ActiveConnectReporter = null;
      }
      _connectGate.Release();
    }
  }

  internal async Task<(bool Connected, string Error)> ConnectPreparedStreamAsync(
      ICommsSerial stream, string endpoint, bool getParams) {
    if (!await _connectGate.WaitAsync(0)) {
      return (false, "Another connection attempt is already running.");
    }

    try {
      if (AppState.Connections.Primary.IsOpen) {
        return (false, "Another vehicle connection is already active.");
      }
      if (!await WaitForPreviousTransportAsync()) {
        try {
          stream.Dispose();
        } catch {
        }
        return (false, PreviousTransportBusyMessage);
      }
      _comPort.BaseStream = stream;
      PrepareForConnection();
      await Task.Run(() =>
          _comPort.Open(getparams: getParams, skipconnectedcheck: true, showui: false));
      if (_comPort.BaseStream?.IsOpen != true) {
        return (false, $"Could not connect to {endpoint}.");
      }
      AdoptOpenConnection(endpoint);
      return (true, "");
    } catch (Exception ex) {
      _ = _transportRelease.Begin(_comPort);
      AppState.Connections.Primary.MarkClosed();
      CloseLogs();
      return (false, ex.Message);
    } finally {
      _connectGate.Release();
    }
  }

  internal Task DisconnectAsync(string status) {
    StopReader();
    Services.Speech.Stop();
    _connectedAtUtc = DateTime.MinValue;
    _ = _transportRelease.Begin(_comPort);
    ResetAllVehicleParameters(_comPort);
    CloseLogs();
    AppState.Connections.NotifyClosed(AppState.Connections.Primary);
    IsConnected = false;
    ConnectText = "CONNECT";
    Status = status;
    RefreshVehicleChoices();
    AppState.RaiseConnectionChanged();
    return Task.CompletedTask;
  }

  private async Task ConnectAsync(bool interactive) {
    if (!await _connectGate.WaitAsync(0)) {
      Status = "A connection attempt is already running.";
      return;
    }

    var sel = SelectedPort;
    if (string.IsNullOrEmpty(sel)) {
      if (interactive) {
        await Services.Dialogs.Alert("Connect", "No port selected.");
      } else {
        Status = "Auto-connect skipped: no saved port.";
      }
      _connectGate.Release();
      return;
    }
    if (IsBleEndpoint(sel)) {
      CancelBleRefresh();
    }

    try {
      if (!await WaitForPreviousTransportAsync()) {
        Status = PreviousTransportBusyMessage;
        return;
      }
      ICommsSerial? stream = await BuildStreamAsync(sel, interactive);
      if (stream == null) {
        if (sel != "AUTO" || interactive) {
          Status = "";
        }
        return;
      }

      string endpoint = sel;
      if (sel == "AUTO" && !string.IsNullOrWhiteSpace(stream.PortName)) {
        endpoint = stream.PortName;
        if (!Ports.Contains(endpoint)) {
          Ports.Insert(1, endpoint);
        }
        SelectedPort = endpoint;
        if (stream.BaudRate > 0) {
          SelectedBaud = stream.BaudRate;
        }
      }

      _comPort.BaseStream = stream;

      PrepareForConnection();

      if (stream is SerialPort serial &&
          Settings.Instance.GetBoolean("CHK_resetapmonconnect", false)) {
        try {
          serial.DtrEnable = false;
          serial.RtsEnable = false;
          serial.toggleDTR();
        } catch {
          // Some serial implementations cannot toggle before opening; normal Open still proceeds.
        }
      }

      OpenLogs();

      var dlg = new Services.ProgressReporter("Connecting Mavlink");
      bool keepTransportOpenOnCancel = ConnectionHealth.IsPersistentListener(stream);
      dlg.KeepConnectionOpenOnCancel = keepTransportOpenOnCancel;
      if (keepTransportOpenOnCancel) {
        dlg.SetCancellationText("Keep UDP Open");
      }
      _connectDialog = dlg;

      AppState.ActiveConnectReporter = dlg;
      dlg.Set(0, $"Connecting {endpoint}…");

      var initialization = new ConnectionInitializationState();
      using CancellationTokenRegistration cancelRegistration = dlg.Token.Register(() => {
        var stage = initialization.Stage;
        bool releaseTransport = ShouldReleaseTransportOnInitializationCancel(
            stage, keepTransportOpenOnCancel);
        if (releaseTransport) {
          _ = _transportRelease.Begin(_comPort);
          AppState.Connections.Primary.MarkClosed();
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
          dlg.Close();
          if (releaseTransport) {
            CloseLogs();
            IsConnected = false;
            ConnectText = "CONNECT";
            Status = "";
          } else if (stage == ConnectionInitializationStage.LoadingParameters) {
            Status = "Stopping parameter loading; the connection will remain active…";
          }
          AppState.RaiseConnectionChanged();
        });
      });
      dlg.Show2();

      Exception? openError = null;
      Exception? parameterError = null;
      bool backgroundParamLoad = false;
      bool parameterLoadSkipped = false;
      try {

        Task open = Task.Factory.StartNew(
            () => initialization.OpenTransport(
                () => _comPort.Open(
                    getparams: false, skipconnectedcheck: true, showui: true),
                () => _comPort.BaseStream.IsOpen),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        if (keepTransportOpenOnCancel) {
          // The upstream open loop observes the reporter cancellation and returns promptly while
          // retaining the bound UDP listener. Await the worker itself so the connection is not
          // published while that worker still owns MAVLink reads.
          await open;
        } else {
          await open.WaitAsync(dlg.Token);
        }
        if (_comPort.BaseStream.IsOpen && !dlg.CancelRequested &&
            _comPort.MAV.compid != (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_PERIPHERAL) {
          backgroundParamLoad = ShouldLoadParametersInBackground(
              stream, Settings.Instance.GetBoolean("Params_BG", false));
          if (!backgroundParamLoad) {
            dlg.SetCancellationText("Skip Parameters");
            // Closing the window is not a skip: the read goes on and the toolbar status/progress
            // under the Connect button keep reporting it. Only the button cancels.
            dlg.ContinueInBackgroundOnClose = true;
            var operation = AppState.ParameterLoads.Start(
                _comPort.MAV.sysid, _comPort.MAV.compid, dlg.Token, OnProgress);
            try {
              await operation.Completion;
            } catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) {
              parameterLoadSkipped = true;
            } catch (Exception ex) {
              parameterError = ex;
            }
          }
        }
      } catch (OperationCanceledException) when (
          dlg.CancelRequested && _comPort.BaseStream.IsOpen &&
          !ShouldReleaseTransportOnInitializationCancel(
              initialization.Stage, keepTransportOpenOnCancel)) {
        parameterLoadSkipped = true;
      } catch (Exception ex) {
        openError = ex;
      }
      if (dlg.CancelRequested && _comPort.BaseStream.IsOpen &&
          !ShouldReleaseTransportOnInitializationCancel(
              initialization.Stage, keepTransportOpenOnCancel)) {
        parameterLoadSkipped = true;
        backgroundParamLoad = false;
      }
      if (openError == null && _comPort.BaseStream.IsOpen) {
        initialization.MarkConnected();
      }
      _connectDialog = null;
      AppState.ActiveConnectReporter = null;

      var completedStage = initialization.Stage;
      if (dlg.CancelRequested &&
          ShouldReleaseTransportOnInitializationCancel(
              completedStage, keepTransportOpenOnCancel)) {
        dlg.Close();
        _ = _transportRelease.Begin(_comPort);
        AppState.Connections.Primary.MarkClosed();
        CloseLogs();
        IsConnected = false;
        ConnectText = "CONNECT";
        Status = "";
        AppState.RaiseConnectionChanged();
        return;
      }

      if (openError != null) {
        dlg.Close();
        _ = _transportRelease.Begin(_comPort);
        AppState.Connections.Primary.MarkClosed();
        CloseLogs();
        IsConnected = false;
        Status = interactive ? "" : "Auto-connect failed: " + openError.Message;
        if (interactive) {
          await Services.Dialogs.Alert("Connection error", openError.Message);
        }
        return;
      }

      IsConnected = _comPort.BaseStream.IsOpen;
      ConnectText = IsConnected ? "DISCONNECT" : "CONNECT";
      if (IsConnected) {
        AppState.Connections.Primary.Endpoint = endpoint;
        AppState.Connections.Primary.MarkOpened();
        AppState.Connections.SetActive(_comPort);
        Settings.Instance.ComPort = endpoint;
        Settings.Instance.BaudRate = SelectedBaud.ToString();
        if (IsSerialEndpoint(endpoint) && !IsBleEndpoint(endpoint)) {
          Settings.Instance[PortBaudKey(endpoint)] = SelectedBaud.ToString();
        }
        int receivedParameters = _comPort.MAV.param.TotalReceived;
        int reportedParameters = _comPort.MAV.param.TotalReported;
        string parameterCount = reportedParameters > 0
            ? $"{receivedParameters} of {reportedParameters} parameters"
            : $"{receivedParameters} parameters (total unknown)";
        Status = parameterLoadSkipped
            ? $"Connected with {parameterCount}. Loading was cancelled; use Retry Now when needed."
            : parameterError != null
                ? $"Connected with {parameterCount}, but loading failed: {parameterError.Message}"
                : backgroundParamLoad
                    ? "Connected. Loading parameters in background…"
                    : $"Connected. {_comPort.MAV.param.Count} params.";
        _connectedAtUtc = DateTime.UtcNow;
        int generation = StartReader();
        RefreshVehicleChoices();
        StartPostConnectMetadataCheck(generation);

        if (backgroundParamLoad && !dlg.CancelRequested) {
          StartBackgroundParameterLoad(generation);
        } else if (!parameterLoadSkipped && parameterError == null) {
          StartCubeServiceBulletinChecks(_comPort, _comPort.MAV);
        }
        RaiseConnected();

        dlg.Set(100, Status);
        dlg.SetCancellationText("Close");
        if (!dlg.CancelRequested && !dlg.Detached) {
          await Task.Delay(1200);
          // Closing a progress window normally means Cancel. This is the successful completion
          // path, so do not fire the callback registered on the dialog token.
          dlg.Complete();
        }
      } else {
        dlg.Close();
        _ = _transportRelease.Begin(_comPort);
        AppState.Connections.Primary.MarkClosed();
        CloseLogs();
        Status = interactive ? "" : $"Auto-connect failed on {endpoint}.";
        if (interactive) {
          await Services.Dialogs.Alert("Connection failed", $"Could not connect on {endpoint}.");
        }
      }
      AppState.RaiseConnectionChanged();
    } catch (Exception ex) {
      AppState.ActiveConnectReporter = null;
      _ = _transportRelease.Begin(_comPort);
      AppState.Connections.Primary.MarkClosed();
      CloseLogs();
      Status = interactive ? "" : "Auto-connect failed: " + ex.Message;
      IsConnected = false;
      ConnectText = "CONNECT";
      if (interactive) {
        await Services.Dialogs.Alert("Connection error", ex.Message);
      }
    } finally {
      _connectGate.Release();
    }
  }

  private const string PreviousTransportBusyMessage =
      "The previous device is still releasing its OS transport. Port selection remains available; retry shortly.";

  private Task<bool> WaitForPreviousTransportAsync() =>
      _transportRelease.WaitForCurrentAsync(_comPort, TimeSpan.FromSeconds(1));

  private async Task<ICommsSerial?> BuildStreamAsync(string sel, bool interactive) {
    switch (sel) {
      case "AUTO":
        return await ScanForStreamAsync(interactive);

      case "TCP": {
          var defaults = new[] { Setting("TCP_host", "127.0.0.1"), Setting("TCP_port", "5760") };
          var v = interactive
              ? await PromptAsync("TCP client", "Host / IP", defaults[0], "Remote port", defaults[1])
              : defaults;
          if (v == null) {
            return null;
          }

          Store("TCP_host", v[0]);
          Store("TCP_port", v[1]);
          return CreateConfiguredNetworkStream("TCP", v[0], v[1]);
        }

      case "UDPCl": {
          var defaults = new[] { Setting("UDP_host", "127.0.0.1"), Setting("UDP_port", "14550") };
          var v = interactive
              ? await PromptAsync("UDP client", "Host / IP", defaults[0], "Remote port", defaults[1])
              : defaults;
          if (v == null) {
            return null;
          }

          Store("UDP_host", v[0]);
          Store("UDP_port", v[1]);
          return CreateConfiguredNetworkStream("UDPCl", v[0], v[1]);
        }

      case "UDP": {
          var defaults = new[] { Setting("UDP_port", "14550"), "" };
          var v = interactive
              ? await PromptAsync("UDP listener", "Local port", defaults[0], null, "")
              : defaults;
          if (v == null) {
            return null;
          }

          Store("UDP_port", v[0]);
          return CreateConfiguredNetworkStream("UDP", v[0], "");
        }

      case "WS": {
          var defaults = new[] { Setting("WS_url", "ws://127.0.0.1:8080"), "" };
          var v = interactive
              ? await PromptAsync("WebSocket", "URL", defaults[0], null, "")
              : defaults;
          if (v == null) {
            return null;
          }

          Store("WS_url", v[0]);
          return CreateConfiguredNetworkStream("WS", v[0], "");
        }

      default:
        if (IsBleEndpoint(sel)) {
          if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() &&
              !OperatingSystem.IsWindows()) {
            throw new PlatformNotSupportedException(
                "Bluetooth LE serial is not supported on this platform.");
          }
          return new Services.BleSerial(sel) { BaudRate = SelectedBaud };
        }
        return new SerialPort {
          PortName = sel,
          BaudRate = SelectedBaud,
          espFix = Settings.Instance.GetBoolean("CHK_rtsresetesp32", false),
        };
    }
  }

  internal static ICommsSerial CreateConfiguredNetworkStream(
      string kind, string primary, string secondary) => kind switch {
        "TCP" => new PreconfiguredTcpSerial(primary, secondary),
        "UDPCl" => new PreconfiguredUdpClient(primary, secondary),
        "UDP" => new PreconfiguredUdpListener(primary),
        "WS" => new Services.PortableWebSocketSerial(primary),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown network transport."),
      };

  internal static bool ShouldLoadParametersInBackground(
      ICommsSerial stream, bool configured) {
    // An inbound UDP listener can receive many independent MAVLink systems. The first heartbeat
    // only proves that the shared socket is usable; it does not select a device whose parameter
    // service is required to answer. Waiting synchronously for that arbitrary endpoint would keep
    // the connection logically closed (and the button labelled CONNECT), preventing pages such as
    // NV Modem from discovering the other live broadcast endpoints.
    return configured || stream is UdpSerial;
  }

  internal static bool ShouldReleaseTransportOnInitializationCancel(
      ConnectionInitializationStage stage,
      bool keepTransportOpen = false) =>
      stage == ConnectionInitializationStage.OpeningTransport && !keepTransportOpen;

  private async Task<ICommsSerial?> ScanForStreamAsync(bool interactive) {
    var dlg = new Services.ProgressReporter("Scanning serial ports");
    _connectDialog = dlg;
    AppState.ActiveConnectReporter = dlg;
    dlg.Set(0, "Looking for MAVLink on serial ports…");
    dlg.Show2();

    ICommsSerial? stream;
    bool cancelled;
    var scanToken = dlg.Token;
    try {
      stream = await Task.Run(() => ScanForPort(scanToken));
    } finally {
      cancelled = dlg.CancelRequested;
      if (ReferenceEquals(_connectDialog, dlg)) {
        _connectDialog = null;
      }
      if (ReferenceEquals(AppState.ActiveConnectReporter, dlg)) {
        AppState.ActiveConnectReporter = null;
      }
      dlg.Close();
    }

    if (stream == null && !cancelled) {
      const string message = "No MAVLink serial port was found during the automatic scan.";
      if (interactive) {
        await Services.Dialogs.Alert("Auto scan", message);
      } else {
        Status = message;
      }
    }
    return stream;
  }

  private ICommsSerial? ScanForPort(CancellationToken cancellationToken) {
    if (!DedupePorts(SerialPort.GetPortNames()).Any()) {
      return null;
    }

    CommsSerialScan.Scan(false);
    var started = DateTime.UtcNow;
    var deadline = started.AddSeconds(50);
    while (Volatile.Read(ref CommsSerialScan.run) == 1 && DateTime.UtcNow < deadline) {
      if (cancellationToken.IsCancellationRequested) {
        Interlocked.Exchange(ref CommsSerialScan.run, 0);
        return null;
      }

      var found = CommsSerialScan.portinterface?.FirstOrDefault();
      if (CommsSerialScan.foundport && found != null) {
        Interlocked.Exchange(ref CommsSerialScan.run, 0);
        return found;
      }

      double elapsed = (DateTime.UtcNow - started).TotalSeconds;
      _connectDialog?.Set(Math.Min(99, elapsed / 50 * 100),
          $"Looking for MAVLink… {Math.Max(0, Volatile.Read(ref CommsSerialScan.running))} port(s) active");
      Thread.Sleep(200);
    }

    var result = CommsSerialScan.portinterface?.FirstOrDefault();
    Interlocked.Exchange(ref CommsSerialScan.run, 0);
    return result;
  }

  private static string Setting(string key, string fallback) {
    var v = AppState.CommsSettings.TryGetValue(key, out var s) ? s : "";
    if (string.IsNullOrEmpty(v)) {
      v = Settings.Instance["connection_" + key] ?? "";
    }
    return string.IsNullOrEmpty(v) ? fallback : v;
  }

  private static void Store(string key, string? value) {
    AppState.CommsSettings[key] = value ?? "";
    Settings.Instance["connection_" + key] = value ?? "";
  }

  private static async Task<string[]?> PromptAsync(
      string title, string l1, string v1, string? l2, string v2) {
    var owner = (Avalonia.Application.Current?.ApplicationLifetime
                 as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (owner == null) {
      return [v1, v2];
    }

    var r = await ConnectDialog.Show(owner, title, l1, v1, l2, v2);
    if (r == null) {
      return null;
    }

    return [r[0] ?? "", r[1] ?? ""];
  }
}

internal interface IPreconfiguredNetworkStream {
  bool SuppressesUpstreamInput { get; }
}

internal sealed class PreconfiguredTcpSerial : TcpSerial, IPreconfiguredNetworkStream {
  internal PreconfiguredTcpSerial(string host, string port) {
    Host = host;
    Port = port;
  }

  public bool SuppressesUpstreamInput => true;

  protected override inputboxreturn OnInputBoxShow(
      string title, string prompttext, ref string text) => inputboxreturn.OK;
}

internal sealed class PreconfiguredUdpClient : UdpSerialConnect, IPreconfiguredNetworkStream {
  private readonly string _host;
  private readonly string _port;

  internal PreconfiguredUdpClient(string host, string port) {
    _host = host;
    _port = port;
    Port = port;
  }

  public bool SuppressesUpstreamInput => true;

  protected override inputboxreturn OnInputBoxShow(
      string title, string prompttext, ref string text) => inputboxreturn.OK;

  protected override string OnSettings(string name, string value, bool set = false) {
    if (name.StartsWith("UDP_host", StringComparison.Ordinal)) {
      return _host;
    }
    if (name.StartsWith("UDP_port", StringComparison.Ordinal)) {
      return _port;
    }
    return base.OnSettings(name, value, set);
  }
}

internal sealed class PreconfiguredUdpListener : UdpSerial, IPreconfiguredNetworkStream {
  internal PreconfiguredUdpListener(string port) => Port = port;

  public bool SuppressesUpstreamInput => true;

  protected override inputboxreturn OnInputBoxShow(
      string title, string prompttext, ref string text) => inputboxreturn.OK;
}

internal static class ConnectionHealth {
  // A bound UDP socket is a discovery surface, not a session owned by one vehicle. It must
  // outlive disappearing broadcasters so returning modems are received without reopening it.
  internal static bool IsPersistentListener(ICommsSerial? stream) => stream is UdpSerial;

  internal static bool ShouldCloseSilentLink(ICommsSerial? stream, bool armed,
      DateTime nowUtc, DateTime newestPacketUtc, DateTime connectedAtUtc, TimeSpan timeout) =>
      !IsPersistentListener(stream) &&
      ShouldCloseSilentLink(armed, nowUtc, newestPacketUtc, connectedAtUtc, timeout);

  internal static bool ShouldCloseSilentLink(bool armed, DateTime nowUtc,
      DateTime newestPacketUtc, DateTime connectedAtUtc, TimeSpan timeout) =>
      !armed && IsSilent(nowUtc, newestPacketUtc, connectedAtUtc, timeout);

  internal static bool ShouldCloseAfterReaderErrors(ICommsSerial? stream) =>
      !IsPersistentListener(stream);

  // MAVLink can legally produce short datagrams, and UdpSerial exposes the size of the next
  // datagram rather than a stream-sized backlog on every platform. Requiring the legacy
  // stream-oriented >10-byte threshold can leave that datagram at the head of the socket forever.
  internal static bool ShouldPollReader(ICommsSerial? stream) => stream != null &&
      stream.BytesToRead > (IsPersistentListener(stream) ? 0 : 10);

  internal static bool IsSilent(DateTime nowUtc, DateTime newestPacketUtc,
      DateTime connectedAtUtc, TimeSpan timeout) {
    if (connectedAtUtc == DateTime.MinValue) {
      return false;
    }
    // MAVState can retain the timestamp from the previous endpoint. A new session gets its full
    // grace period, but is still closed if it never receives a first packet.
    DateTime baseline = newestPacketUtc > connectedAtUtc ? newestPacketUtc : connectedAtUtc;
    return nowUtc - baseline > timeout;
  }
}
