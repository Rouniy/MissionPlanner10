using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Utilities;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.ViewModels;

public partial class FlightDataViewModel : ViewModelBase, IDisposable {
  private const string _autoPanSetting = "CHK_autopan";

  private MAVLinkInterface _comPort => AppState.comPort;
  private readonly DispatcherTimer _timer;
  // Attitude is copied to the HUD far more often than the rest of the panel: the HUD interpolates
  // between samples, so a 100 ms copy interval beating against a 10 Hz stream shows as jerks.
  private readonly DispatcherTimer _attitudeTimer;
  private readonly TlogPlayer _tlog = new();
  private readonly PythonScriptHost _python;
  private FlightPlannerViewModel? _scriptFlightPlanner;
  private readonly Dictionary<string, Action<string>> _customActions = new(StringComparer.Ordinal);
  private Views.JoystickSetupWindow? _joystickSetupWindow;

  public event Action? RequestFlightPlanner;

  [ObservableProperty]
  private double _roll;

  [ObservableProperty]
  private double _pitch;

  [ObservableProperty]
  private double _yaw;

  [ObservableProperty]
  private double _alt;

  [ObservableProperty]
  private double _groundSpeed;

  [ObservableProperty]
  private double _airSpeed;

  [ObservableProperty]
  private double _wpDist;

  [ObservableProperty]
  private int _wpNo;

  [ObservableProperty]
  private double _missionProgress;

  [ObservableProperty]
  private string _missionProgressText = "No mission loaded";

  private long _nextMissionProgressUpdate;

  [ObservableProperty]
  private double _verticalSpeed;

  [ObservableProperty]
  private double _distToHome;

  [ObservableProperty]
  private bool _showDistanceToHome = true;

  [ObservableProperty]
  private bool _hudOverlayEnabled = true;

  [ObservableProperty]
  private double _batteryVoltage;

  [ObservableProperty]
  private int _batteryRemaining;

  [ObservableProperty]
  private double _satCount;

  [ObservableProperty]
  private int _gpsFixType;

  [ObservableProperty]
  private double _gpsHdop;

  [ObservableProperty]
  private string _mode = "—";

  [ObservableProperty]
  private bool _armed;

  [ObservableProperty]
  private string _armText = "ARM";

  [ObservableProperty]
  private bool _isJoystickActive;

  [ObservableProperty]
  private string _messages = "";

  [ObservableProperty]
  private string _statusText = "";

  private int _lastMsgCount = -1;

  public ObservableCollection<ServoOut> Servos { get; } = [];

  public LogBrowseViewModel TelemetryLogs { get; } = new();
  public LogBrowseViewModel DataFlashLogs { get; } = new();
  public OpenDroneIdViewModel OpenDroneId { get; } = new();

  [ObservableProperty]
  private bool _prearmOk;

  [ObservableProperty]
  private string _prearmText = "—";

  [ObservableProperty]
  private double _ekfStatus;

  public ObservableCollection<string> Modes { get; } = [];

  private MissionPlanner.ArduPilot.Firmwares? _modeFirmware;
  private MissionPlanner.ArduPilot.Firmwares? _mountModeFirmware;
  private int _mountModeParameterCount = -1;
  private bool _mountModeMetadataAvailable;
  private int _waypointOptionMax = -1;

  [ObservableProperty]
  private string _selectedMode = "STABILIZE";

  [ObservableProperty]
  private bool _quickViewEditable = true;

  [ObservableProperty]
  private bool _preflightEditVisible = true;

  [ObservableProperty]
  private bool _anemometerVisible = true;

  public FlightDataViewModel() {
    if (Settings.Instance[_autoPanSetting] != null) {
      _autoPan = Settings.Instance.GetBoolean(_autoPanSetting);
    }
    _python = new PythonScriptHost(
        () => AppState.comPort,
        () => AppState.Connections.Snapshot().Select(connection => connection.Link).ToArray(),
        () => this,
        () => _scriptFlightPlanner);
    for (int i = 1; i <= 8; i++) {
      Servos.Add(new ServoOut(i));
    }

    for (int ch = 5; ch <= 16; ch++) {
      ServoRelayItems.Add(new ServoChannel(ch));
    }
    for (int r = 0; r < 16; r++) {
      ServoRelayItems.Add(new RelayChannel(r));
    }

    var auxFunctions = LoadAuxFunctionOptions();
    for (int index = 0; index < 7; index++) {
      AuxOptions.Add(new AuxFunctionRow(index, auxFunctions));
    }

    _quickViewCount = Math.Clamp(Settings.Instance.GetInt32("quickViewCount", 6), 1, 12);
    _quickColumns = Math.Clamp(Settings.Instance.GetInt32("quickViewColumns", 2), 1, 6);
    InitQuickItems();
    InitPreflightChecks();
    InitTuningFields();
    LoadHudSettings();
    LoadGimbalVideoSettings();
    IsJoystickActive = AppState.JoystickControl.Active is { enabled: true };

    SelectedMessage = MessageOptions.FirstOrDefault(m =>
        m.Id == (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE) ?? MessageOptions.FirstOrDefault();

    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _timer.Tick += (_, _) => Pump();
    _timer.Start();
    _attitudeTimer = new DispatcherTimer { Interval = AttitudeInterval(false) };
    _attitudeTimer.Tick += (_, _) => PumpAttitude();
    _attitudeTimer.Start();

    _tlog.Packet += OnTlogPacket;
    _tlog.Progress += OnTlogProgress;
    _python.Output += OnPythonOutput;
    MissionPlanner.Warnings.WarningEngine.QuickPanelColoring += OnQuickPanelColoring;
    Services.DisplayViewService.Changed += OnDisplayViewChanged;
    RefreshDisplayView();
  }

  internal void SetScriptFlightPlanner(FlightPlannerViewModel flightPlanner) =>
      _scriptFlightPlanner = flightPlanner;

  private void OnDisplayViewChanged(object? sender, EventArgs e) =>
      Dispatcher.UIThread.Post(RefreshDisplayView);

  private void RefreshDisplayView() {
    var profile = Services.DisplayViewService.Current;
    QuickViewEditable = !profile.lockQuickView;
    PreflightEditVisible = profile.displayPreFlightTabEdit;
    AnemometerVisible = profile.displayAnenometer;
  }

  private void OnQuickPanelColoring(string field, string color) =>
      Dispatcher.UIThread.Post(() => {
        foreach (var item in QuickItems.Where(item => item.Field == field)) {
          item.ApplyWarningColor(color);
        }
      });

  public void Dispose() {
    MissionPlanner.Warnings.WarningEngine.QuickPanelColoring -= OnQuickPanelColoring;
    Services.DisplayViewService.Changed -= OnDisplayViewChanged;
    _timer.Stop();
    _attitudeTimer.Stop();
    _python.Dispose();
    _tlog.Close();
    OpenDroneId.Dispose();
    CloseVideoWindow();
    _joystickSetupWindow?.Close();
    _joystickSetupWindow = null;
  }

  internal static TimeSpan AttitudeInterval(bool slowMachine) => TimeSpan.FromMilliseconds(slowMachine ? 50 : 20);

  /// <summary>Hands the latest attitude to the HUD; cheap enough to run at 50 Hz.</summary>
  private void PumpAttitude() {
    var cs = _comPort.MAV?.cs;
    if (cs == null) {
      return;
    }
    Roll = cs.roll;
    Pitch = cs.pitch;
    Yaw = cs.yaw;
  }

  private void Pump() {
    IsJoystickActive = AppState.JoystickControl.Active is { enabled: true };
    var cs = _comPort.MAV?.cs;
    if (cs == null) {
      return;
    }

    PumpAttitude();
    Alt = cs.alt;
    GroundSpeed = cs.groundspeed;
    AirSpeed = cs.airspeed;
    WpDist = cs.wp_dist;
    WpNo = (int)cs.wpno;
    if (Environment.TickCount64 >= _nextMissionProgressUpdate) {
      UpdateMissionProgress(cs);
      _nextMissionProgressUpdate = Environment.TickCount64 + 500;
    }
    VerticalSpeed = cs.verticalspeed;
    AudioVario.SetClimbRate(cs.climbrate);
    DistToHome = cs.DistToHome;
    var settings = Settings.Instance;
    ShowDistanceToHome = settings.GetBoolean("CHK_disttohomeflightdata", true);
    HudOverlayEnabled = settings.GetBoolean("CHK_hudshow", true);
    bool slowMachine = settings.GetBoolean("SlowMachine", false);
    var desiredInterval = slowMachine ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromMilliseconds(100);
    if (_timer.Interval != desiredInterval) {
      _timer.Interval = desiredInterval;
    }
    if (_attitudeTimer.Interval != AttitudeInterval(slowMachine)) {
      _attitudeTimer.Interval = AttitudeInterval(slowMachine);
    }
    XpdrMaintReq = cs.xpdr_maint_req;
    XpdrGpsUnavailable = cs.xpdr_gps_unavail;
    XpdrGpsNoFix = cs.xpdr_gps_no_fix;
    XpdrTxSystemFailure = cs.xpdr_adsb_tx_sys_fail;
    XpdrAirborne = cs.xpdr_airborne_status;
    XpdrNic = TransponderAccuracy.Nic(cs.xpdr_nic);
    XpdrNacp = TransponderAccuracy.Nacp(cs.xpdr_nacp);
    BatteryVoltage = cs.battery_voltage;
    BatteryRemaining = (int)Math.Round(EstimateBatteryPercent(
        cs.battery_voltage, cs.current, cs.battery_usedmah, ParamValue("BATT_CAPACITY"),
        ref _peakBattVoltage, cs.battery_remaining));
    SatCount = cs.satcount;
    GpsFixType = (int)cs.gpsstatus;
    GpsHdop = cs.gpshdop;
    Mode = cs.mode ?? "—";
    Armed = cs.armed;
    ArmText = cs.armed ? "DISARM" : "ARM";
    RefreshFlightOptions(cs);

    float[] outs =
    [
            cs.ch1out,
            cs.ch2out,
            cs.ch3out,
            cs.ch4out,
            cs.ch5out,
            cs.ch6out,
            cs.ch7out,
            cs.ch8out,
        ];
    for (int i = 0; i < 8; i++) {
      Servos[i].Value = (int)outs[i];
    }

    PrearmOk = cs.prearmstatus;
    PrearmText = PrearmOk ? "Ready to Arm" : "Not Ready to Arm";
    EkfStatus = cs.ekfstatus;
    BatCurrent = cs.current;
    NavBearing = cs.nav_bearing;

    WindDir = cs.wind_dir;
    WindVel = cs.wind_vel;
    Aoa = cs.AOA;
    Ssa = cs.SSA;
    XTrackError = cs.xtrack_error;
    TurnRate = cs.turnrate;
    BatteryVoltage2 = cs.battery_voltage2;
    BatteryRemaining2 = (int)Math.Round(
        Services.BatteryEstimator.EstimatePercent(cs.battery_voltage2, cs.battery_remaining2));
    BatCurrent2 = cs.current2;
    ThrottlePercent = cs.ch3percent;
    Failsafe = cs.failsafe;
    SafetyActive = cs.safetyactive;
    LinkQuality = cs.linkqualitygcs;
    TargetAlt = cs.targetalt;
    TargetSpeed = cs.targetairspeed;

    UpdateQuickItems(cs);
    SampleTuning(cs);

    if (cs.messages != null && cs.messages.Count != _lastMsgCount) {
      try {
        var snapshot = cs.messages.ToArray();
        _lastMsgCount = snapshot.Length;
        StatusText = string.Join("\n",
            snapshot.TakeLast(200).Select(m => $"{m.time:HH:mm:ss}  {m.message?.TrimEnd()}"));
      } catch (InvalidOperationException) {

      }
    }

    if (_hudUserFields.Count > 0) {
      var sb = new System.Text.StringBuilder();
      foreach (var p in _statusProps) {
        if (!_hudUserFields.Contains(p.Name)) {
          continue;
        }
        object? v;
        try {
          v = p.GetValue(cs);
        } catch {
          v = null;
        }
        string prefix = _hudUserPrefixes.TryGetValue(p.Name, out var savedPrefix)
            ? savedPrefix
            : p.Name + ": ";
        sb.AppendLine(v is float or double ? $"{prefix}{v:0.00}" : $"{prefix}{v}");
      }
      HudCustomText = sb.ToString().TrimEnd();
    } else if (HudCustomText.Length > 0) {
      HudCustomText = "";
    }

    RefreshStatus(cs);
    RefreshPreflight(cs);
  }

  private double _peakBattVoltage;

  private static double EstimateBatteryPercent(double voltage, double current, double usedMah,
      double capacityMah, ref double peakVoltage, double firmwarePercent) {
    if (voltage < 1.0) {
      peakVoltage = 0;
      return firmwarePercent;
    }

    if (voltage > peakVoltage) {
      peakVoltage = voltage;
    }

    if (current > 0) {
      var byCapacity = Services.BatteryEstimator.FromCapacity(usedMah, capacityMah);
      if (byCapacity.HasValue) {
        return byCapacity.Value;
      }
    }

    int cells = Services.BatteryEstimator.InferCells(peakVoltage);
    return Services.BatteryEstimator.EstimatePercent(voltage, cells, firmwarePercent);
  }

  private double ParamValue(string name) {
    try {
      var p = _comPort.MAV?.param;
      return p != null && p.ContainsKey(name) ? p[name].Value : 0;
    } catch {
      return 0;
    }
  }

  [ObservableProperty]
  private double _batCurrent;

  [ObservableProperty]
  private double _windDir;

  [ObservableProperty]
  private double _windVel;

  [ObservableProperty]
  private double _aoa;

  [ObservableProperty]
  private double _ssa;

  [ObservableProperty]
  private double _xTrackError;

  [ObservableProperty]
  private double _turnRate;

  [ObservableProperty]
  private double _batteryVoltage2;

  [ObservableProperty]
  private int _batteryRemaining2;

  [ObservableProperty]
  private double _batCurrent2;

  [ObservableProperty]
  private double _throttlePercent;

  [ObservableProperty]
  private bool _failsafe;

  [ObservableProperty]
  private bool _safetyActive;

  [ObservableProperty]
  private double _linkQuality;

  [ObservableProperty]
  private double _targetAlt;

  [ObservableProperty]
  private double _targetSpeed;

  public ObservableCollection<QuickItem> QuickItems { get; } = [];

  [ObservableProperty]
  private int _quickViewCount = 6;

  [ObservableProperty]
  private int _quickColumns = 2;

  private static readonly (string field, string color)[] _quickDefaults = [
    ("alt", "#D197F8"),
    ("groundspeed", "#FE842E"),
    ("current", "#FF605B"),
    ("airspeed", "#00FF53"),
    ("verticalspeed", "#FEFE56"),
    ("DistToHome", "#00FFFC"),
  ];

  private void InitQuickItems() {
    for (int i = 0; i < QuickViewCount; i++) {
      QuickItems.Add(CreateQuickItem(i));
    }
  }

  private QuickItem CreateQuickItem(int index) {
    var defaults = _quickDefaults[index % _quickDefaults.Length];
    var key = $"quickView{index + 1}";
    string field = Settings.Instance.ContainsKey(key) && !string.IsNullOrEmpty(Settings.Instance[key])
        ? Settings.Instance[key]
        : defaults.field;
    var item = new QuickItem(field, defaults.color) {
      Desc = DescFor(_comPort.MAV?.cs, field),
    };
    return item;
  }

  private void ResizeQuickItems() {
    while (QuickItems.Count > QuickViewCount) {
      QuickItems.RemoveAt(QuickItems.Count - 1);
    }
    while (QuickItems.Count < QuickViewCount) {
      QuickItems.Add(CreateQuickItem(QuickItems.Count));
    }
  }

  partial void OnQuickViewCountChanged(int value) {
    int clamped = Math.Clamp(value, 1, 12);
    if (value != clamped) {
      QuickViewCount = clamped;
      return;
    }
    Settings.Instance["quickViewCount"] = value.ToString(CultureInfo.InvariantCulture);
    ResizeQuickItems();
  }

  partial void OnQuickColumnsChanged(int value) {
    int clamped = Math.Clamp(value, 1, 6);
    if (value != clamped) {
      QuickColumns = clamped;
      return;
    }
    Settings.Instance["quickViewColumns"] = value.ToString(CultureInfo.InvariantCulture);
  }

  [RelayCommand]
  private async Task ConfigureQuickViewLayout() {
    var columnsText = await Services.Dialogs.InputBox(
        "QuickView Layout", "Number of columns (1..6)",
        QuickColumns.ToString(CultureInfo.InvariantCulture));
    if (columnsText == null) {
      return;
    }

    int currentRows = (int)Math.Ceiling(QuickViewCount / (double)QuickColumns);
    var rowsText = await Services.Dialogs.InputBox(
        "QuickView Layout", "Number of rows (total cells may not exceed 12)",
        currentRows.ToString(CultureInfo.InvariantCulture));
    if (rowsText == null) {
      return;
    }

    if (!TryParseQuickViewLayout(columnsText, rowsText, out int columns, out int count)) {
      await Services.Dialogs.Alert(
          "QuickView Layout", "Enter 1..6 columns and enough rows for a total of 1..12 cells.");
      return;
    }

    QuickColumns = columns;
    QuickViewCount = count;
  }

  internal static bool TryParseQuickViewLayout(
      string columnsText, string rowsText, out int columns, out int count) {
    columns = 0;
    count = 0;
    if (!int.TryParse(columnsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedColumns)
        || !int.TryParse(rowsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rows)
        || parsedColumns is < 1 or > 6 || rows < 1) {
      return false;
    }

    if (rows > 12 / parsedColumns) {
      return false;
    }

    columns = parsedColumns;
    count = parsedColumns * rows;
    return true;
  }

  private static string DescFor(MissionPlanner.CurrentState? cs, string field) {
    try {
      return cs?.GetNameandUnit(field) ?? field;
    } catch {
      return field;
    }
  }

  private void UpdateQuickItems(MissionPlanner.CurrentState cs) {
    foreach (var item in QuickItems) {
      var pi = _statusProps.FirstOrDefault(p => p.Name == item.Field);
      if (pi == null) {
        continue;
      }
      try {
        object? v = pi.GetValue(cs);
        item.Number = v switch {
          bool b => b ? 1 : 0,
          IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture),
          _ => item.Number,
        };
      } catch {

      }
    }
  }

  public void SetQuickField(QuickItem item, string field) {
    item.Field = field;
    item.Desc = DescFor(_comPort.MAV?.cs, field);
    // A WarningEngine colour belongs to the previous field. Keep neither its background nor its
    // contrasting text colour when the operator assigns a different CurrentState value.
    item.ApplyWarningColor("NoColor");
    int idx = QuickItems.IndexOf(item);
    if (idx >= 0) {
      Settings.Instance[$"quickView{idx + 1}"] = field;
    }
  }

  public System.Collections.Generic.List<(string name, string desc)> QuickFieldList() {
    var cs = _comPort.MAV?.cs;
    var list = new System.Collections.Generic.List<(string, string)>();
    foreach (var p in _statusProps) {
      var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
      if (!IsNumber(t) && t != typeof(bool)) {
        continue;
      }
      list.Add((p.Name, DescFor(cs, p.Name)));
    }
    list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
    return list;
  }

  public ObservableCollection<StatusItem> Statuses { get; } = [];

  private static readonly System.Reflection.PropertyInfo[] _statusProps =
      [.. typeof(MissionPlanner.CurrentState)
          .GetProperties()
          .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
          .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

  private void RefreshStatus(MissionPlanner.CurrentState cs) {
    if (Statuses.Count != _statusProps.Length) {
      Statuses.Clear();
      foreach (var p in _statusProps) {
        Statuses.Add(new StatusItem(p.Name));
      }
    }
    for (int i = 0; i < _statusProps.Length; i++) {
      object? v;
      try {
        v = _statusProps[i].GetValue(cs);
      } catch {
        v = null;
      }
      Statuses[i].Value = v is float or double ? $"{v:0.000}" : v?.ToString() ?? "";
    }
  }

  public ObservableCollection<CheckItem> PreflightChecks { get; } = [];

  private const string _manualChecksJsonKey = "preflight_manual_json";
  private readonly List<PreflightChecklistDefinition> _preflightDefinitions = [];

  private static readonly string[] _defaultManualChecks = [
    "Tail and wings secured?",
    "All servos respond to input?",
    "All servos respond to pitch and roll?",
    "Center of gravity at indicated point?",
    "Servo linkages are secure?",
    "Camera is on and ready to fly?",
  ];

  private void InitPreflightChecks() {
    ReplacePreflightDefinitions(PreflightChecklist.Load(
        legacyManualItems: LoadManualPreflightChecks()));
  }

  private static IReadOnlyList<string> LoadManualPreflightChecks() {
    if (Settings.Instance[_manualChecksJsonKey] is { Length: > 0 } json) {
      try {
        return (JsonSerializer.Deserialize<string[]>(json) ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
      } catch (JsonException) {
        // Fall through to the legacy semicolon-separated value.
      }
    }

    bool hasLegacyValue = Settings.Instance.ContainsKey("preflight_manual");
    var legacy = Settings.Instance.GetList("preflight_manual")
        .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
    return hasLegacyValue ? legacy : _defaultManualChecks;
  }

  private void RefreshPreflight(MissionPlanner.CurrentState cs) {
    if (PreflightChecks.Count != _preflightDefinitions.Count) {
      ReplacePreflightDefinitions(_preflightDefinitions);
    }

    for (int i = 0; i < _preflightDefinitions.Count; i++) {
      CheckItem view = PreflightChecks[i];
      var evaluation = PreflightChecklist.Evaluate(
          _preflightDefinitions[i], cs, LookupParameter, view.Ok);
      view.Set(evaluation.DisplayText, evaluation.IsSatisfied);
    }
  }

  [RelayCommand]
  private async Task EditPreflight() {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return;
    }

    var rows = new ObservableCollection<PreflightEditorRow>(
        _preflightDefinitions.Select(item => new PreflightEditorRow(item.Clone())));
    var table = new Avalonia.Controls.DataGrid {
      AutoGenerateColumns = false,
      ItemsSource = rows,
      MinHeight = 280,
    };
    table.Columns.Add(new Avalonia.Controls.DataGridTextColumn {
      Header = "Description",
      Width = new Avalonia.Controls.DataGridLength(1.4,
          Avalonia.Controls.DataGridLengthUnitType.Star),
      Binding = new Avalonia.Data.Binding(nameof(PreflightEditorRow.Description)),
    });
    table.Columns.Add(new Avalonia.Controls.DataGridTextColumn {
      Header = "Displayed text",
      Width = new Avalonia.Controls.DataGridLength(1,
          Avalonia.Controls.DataGridLengthUnitType.Star),
      Binding = new Avalonia.Data.Binding(nameof(PreflightEditorRow.Text)),
    });
    table.Columns.Add(new Avalonia.Controls.DataGridTextColumn {
      Header = "Condition",
      Width = new Avalonia.Controls.DataGridLength(1.5,
          Avalonia.Controls.DataGridLengthUnitType.Star),
      Binding = new Avalonia.Data.Binding(nameof(PreflightEditorRow.Condition)),
    });
    table.Columns.Add(new Avalonia.Controls.DataGridTextColumn {
      Header = "True color",
      Width = new Avalonia.Controls.DataGridLength(0.7,
          Avalonia.Controls.DataGridLengthUnitType.Star),
      Binding = new Avalonia.Data.Binding(nameof(PreflightEditorRow.TrueColor)),
    });
    table.Columns.Add(new Avalonia.Controls.DataGridTextColumn {
      Header = "False color",
      Width = new Avalonia.Controls.DataGridLength(0.7,
          Avalonia.Controls.DataGridLengthUnitType.Star),
      Binding = new Avalonia.Data.Binding(nameof(PreflightEditorRow.FalseColor)),
    });

    var add = new Avalonia.Controls.Button { Content = "Add" };
    var delete = new Avalonia.Controls.Button { Content = "Delete" };
    var defaults = new Avalonia.Controls.Button { Content = "Restore defaults" };
    var save = new Avalonia.Controls.Button { Content = "Save" };
    var cancel = new Avalonia.Controls.Button { Content = "Cancel" };
    var actions = new Avalonia.Controls.StackPanel {
      Orientation = Avalonia.Layout.Orientation.Horizontal,
      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
      Spacing = 8,
      Children = { add, delete, defaults, save, cancel },
    };
    var dlg = new Avalonia.Controls.Window {
      Title = "Pre-flight Checklist Editor",
      Width = 980,
      Height = 560,
      MinWidth = 760,
      MinHeight = 420,
      WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
      Content = new Avalonia.Controls.DockPanel {
        Margin = new Avalonia.Thickness(10),
        Children = {
          new Avalonia.Controls.TextBlock {
            Text = "Conditions: satcount >= 5, PARAM:ARMING_CHECK != 0, manual, "
                + "or manual(mode). Combine checks with &&. Text supports {value}, "
                + "{trigger}, and {name}.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
            [Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Top,
          },
          new Avalonia.Controls.Border {
            Child = actions,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            [Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Bottom,
          },
          table,
        },
      },
    };

    add.Click += (_, _) => {
      var row = new PreflightEditorRow(new PreflightChecklistDefinition());
      rows.Add(row);
      table.SelectedItem = row;
      table.ScrollIntoView(row, null);
    };
    delete.Click += (_, _) => {
      if (table.SelectedItem is PreflightEditorRow row) {
        rows.Remove(row);
      }
    };
    defaults.Click += async (_, _) => {
      if (!await Services.Dialogs.Confirm(
              "Restore Checklist", "Replace every checklist item with the upstream defaults?")) {
        return;
      }
      rows.Clear();
      foreach (var item in PreflightChecklist.LoadDefaults()) {
        rows.Add(new PreflightEditorRow(item));
      }
    };
    save.Click += async (_, _) => {
      var definitions = new List<PreflightChecklistDefinition>();
      for (int i = 0; i < rows.Count; i++) {
        PreflightEditorRow row = rows[i];
        if (string.IsNullOrWhiteSpace(row.Description)) {
          await Services.Dialogs.Alert("Checklist", $"Row {i + 1} needs a description.");
          return;
        }
        if (!Avalonia.Media.Color.TryParse(row.TrueColor, out _)
            || !Avalonia.Media.Color.TryParse(row.FalseColor, out _)) {
          await Services.Dialogs.Alert(
              "Checklist", $"Row {i + 1} contains an invalid color.");
          return;
        }
        if (!PreflightChecklist.TryParseExpression(
                row.Condition, row.Description, row.Text,
                row.TrueColor, row.FalseColor, out var definition, out string error)) {
          await Services.Dialogs.Alert("Checklist", $"Row {i + 1}: {error}.");
          return;
        }
        definitions.Add(definition);
      }
      try {
        PreflightChecklist.Save(definitions);
        dlg.Close(definitions);
      } catch (Exception ex) {
        await Services.Dialogs.Alert("Checklist", $"Unable to save checklist: {ex.Message}");
      }
    };
    cancel.Click += (_, _) => dlg.Close((List<PreflightChecklistDefinition>?)null);

    var result = await dlg.ShowDialog<List<PreflightChecklistDefinition>?>(top);
    if (result == null) {
      return;
    }
    Settings.Instance.Remove(_manualChecksJsonKey);
    Settings.Instance.Remove("preflight_manual");
    ReplacePreflightDefinitions(result);
  }

  private double? LookupParameter(string name) =>
      _comPort.MAV.param.ContainsKey(name) ? _comPort.MAV.param[name].Value : null;

  private void ReplacePreflightDefinitions(
      IEnumerable<PreflightChecklistDefinition> definitions) {
    var manualStates = PreflightChecks
        .Where(item => item.Manual)
        .GroupBy(item => item.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Ok, StringComparer.Ordinal);
    var replacements = definitions.Select(item => item.Clone()).ToArray();

    _preflightDefinitions.Clear();
    _preflightDefinitions.AddRange(replacements);
    PreflightChecks.Clear();
    foreach (var definition in replacements) {
      bool manual = definition.ConditionType == PreflightCondition.NONE;
      var item = new CheckItem(
          definition.Description, manual, definition.TrueColor, definition.FalseColor);
      if (manualStates.TryGetValue(definition.Description, out bool state)) {
        item.Ok = state;
      }
      PreflightChecks.Add(item);
    }
  }

  public ObservableCollection<object> ServoRelayItems { get; } = [];

  public ObservableCollection<AuxFunctionRow> AuxOptions { get; } = [];

  private static IReadOnlyList<ParamOption> LoadAuxFunctionOptions() {
    try {
      var firmware = AppState.comPort.MAV.cs.firmware.ToString();
      return ParameterMetaDataRepository.GetParameterOptionsInt("RC6_OPTION", firmware)
          .Select(option => new ParamOption(option.Key, $"{option.Key}: {option.Value}"))
          .ToArray();
    } catch {
      return [];
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task SetAuxFunction(string? spec) {
    if (!TryParseAuxRequest(spec, out int index, out int level)) {
      return;
    }
    var row = AuxOptions.FirstOrDefault(item => item.Index == index);
    if (row?.SelectedFunction == null) {
      row?.Status = "Select a function.";
      return;
    }
    if (!Connected) {
      row.Status = "Not connected.";
      return;
    }

    int function = row.SelectedFunction.Value;
    try {
      bool accepted = await Task.Run(() => _comPort.doCommand(
          _comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.DO_AUX_FUNCTION,
          function, level, 0, 0, 0, 0, 0));
      row.Status = accepted ? AuxLevelName(level) : "Command rejected.";
      Log($"Aux {index + 1} function {function} -> {AuxLevelName(level)}");
    } catch (Exception ex) {
      row.Status = ex.Message;
    }
  }

  internal static bool TryParseAuxRequest(string? spec, out int index, out int level) {
    index = level = -1;
    if (spec == null) {
      return false;
    }
    var parts = spec.Split(':');
    return parts.Length == 2 && int.TryParse(parts[0], out index)
        && int.TryParse(parts[1], out level) && index is >= 0 and < 7 && level is >= 0 and <= 2;
  }

  internal static string AuxLevelName(int level) => level switch {
    (int)MAVLink.MAV_CMD_DO_AUX_FUNCTION_SWITCH_LEVEL.LOW => "Low",
    (int)MAVLink.MAV_CMD_DO_AUX_FUNCTION_SWITCH_LEVEL.MIDDLE => "Middle",
    (int)MAVLink.MAV_CMD_DO_AUX_FUNCTION_SWITCH_LEVEL.HIGH => "High",
    _ => "Unknown",
  };

  [RelayCommand]
  [Obsolete]
  private async Task SetServoChannel(string spec) {
    if (!Connected || spec == null) {
      return;
    }
    var parts = spec.Split(':');
    if (parts.Length != 2 || !int.TryParse(parts[0], out int ch)) {
      return;
    }
    var chan = ServoRelayItems.OfType<ServoChannel>().FirstOrDefault(s => s.Number == ch);
    int pwm = parts[1] switch {
      "low" => chan?.Min ?? 1100,
      "high" => chan?.Max ?? 1900,
      "toggle" => (chan?.Toggled ?? false) ? (chan!.Min) : (chan!.Max),
      _ => (int)(((chan?.Min ?? 1100) + (chan?.Max ?? 1900)) / 2),
    };
    if (parts[1] == "toggle" && chan != null) {
      chan.Toggled = !chan.Toggled;
    }
    await Task.Run(() =>
        _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.DO_SET_SERVO,
            ch, pwm, 0, 0, 0, 0, 0));
    Log($"Servo {ch} -> {pwm}");
  }

  [RelayCommand]
  private async Task RenameServoChannel(ServoChannel? channel) {
    if (channel == null) {
      return;
    }
    string? label = await Services.Dialogs.InputBox(
        "Servo labels", $"Servo {channel.Number} description", channel.Label);
    if (label == null) {
      return;
    }
    string? low = await Services.Dialogs.InputBox(
        "Servo labels", "Low button label", channel.LowLabel);
    if (low == null) {
      return;
    }
    string? high = await Services.Dialogs.InputBox(
        "Servo labels", "High button label", channel.HighLabel);
    if (high == null) {
      return;
    }
    channel.Label = string.IsNullOrWhiteSpace(label) ? $"Servo {channel.Number}" : label.Trim();
    channel.LowLabel = string.IsNullOrWhiteSpace(low) ? "Low" : low.Trim();
    channel.HighLabel = string.IsNullOrWhiteSpace(high) ? "High" : high.Trim();
  }

  [RelayCommand]
  private async Task RenameRelayChannel(RelayChannel? channel) {
    if (channel == null) {
      return;
    }
    string? label = await Services.Dialogs.InputBox(
        "Relay labels", $"Relay {channel.Index + 1} description", channel.Label);
    if (label == null) {
      return;
    }
    string? low = await Services.Dialogs.InputBox(
        "Relay labels", "Low button label", channel.LowLabel);
    if (low == null) {
      return;
    }
    string? high = await Services.Dialogs.InputBox(
        "Relay labels", "High button label", channel.HighLabel);
    if (high == null) {
      return;
    }
    channel.Label = string.IsNullOrWhiteSpace(label) ? $"Relay {channel.Index + 1}" : label.Trim();
    channel.LowLabel = string.IsNullOrWhiteSpace(low) ? "Low" : low.Trim();
    channel.HighLabel = string.IsNullOrWhiteSpace(high) ? "High" : high.Trim();
  }

  [ObservableProperty]
  private string _scriptStatus = "No Script Running";

  [ObservableProperty]
  private string _selectedScript = "None";

  [ObservableProperty]
  private bool _scriptSelected;

  [ObservableProperty]
  private string _scriptEditorText = "";

  [ObservableProperty]
  private string _scriptOutput = "";

  private string? _selectedScriptPath;

  private void OnPythonOutput(string text) =>
      Dispatcher.UIThread.Post(() => ScriptOutput += text);

  [RelayCommand]
  private async Task SelectScript() {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(
        new Avalonia.Platform.Storage.FilePickerOpenOptions {
          Title = "Select Python script",
          AllowMultiple = false,
          FileTypeFilter = new[] {
            new Avalonia.Platform.Storage.FilePickerFileType("Python script") { Patterns = new[] { "*.py" } },
          },
        });
    if (files.Count > 0) {
      _selectedScriptPath = files[0].TryGetLocalPath();
      SelectedScript = files[0].Name;
      ScriptSelected = true;
      ScriptStatus = "Selected " + files[0].Name;
    }
  }

  [RelayCommand]
  private async Task RunScript() {
    if (_python.IsRunning) {
      ScriptStatus = "Script already running";
      return;
    }
    ScriptOutput = "";
    ScriptStatus = "Running";
    var code = ScriptEditorText;
    if (!string.IsNullOrWhiteSpace(code)) {
      await _python.RunAsync(code);
    } else if (_selectedScriptPath != null) {
      await _python.RunFileAsync(_selectedScriptPath);
    } else {
      ScriptStatus = "No script selected";
      return;
    }
    ScriptStatus = _python.IsRunning ? "Running" : "Finished (or aborted)";
  }

  [RelayCommand]
  private void AbortScript() {
    _python.Abort();
    ScriptStatus = "Aborting…";
  }

  [RelayCommand]
  private void EditScript() {
    if (_selectedScriptPath != null && System.IO.File.Exists(_selectedScriptPath)) {
      ScriptEditorText = System.IO.File.ReadAllText(_selectedScriptPath);
      ScriptStatus = "Editing " + SelectedScript;
    } else {
      ScriptStatus = "No script file to edit";
    }
  }

  private static async Task<string?> PickFileAsync(string title, string ext, string desc) {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return null;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(
        new Avalonia.Platform.Storage.FilePickerOpenOptions {
          Title = title,
          AllowMultiple = false,
          FileTypeFilter = new[] {
            new Avalonia.Platform.Storage.FilePickerFileType(desc) { Patterns = new[] { ext } },
          },
        });
    return files.Count > 0 ? files[0].TryGetLocalPath() : null;
  }

  private static async Task<string?> PickSaveAsync(string title, string ext) {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return null;
    }
    var file = await top.StorageProvider.SaveFilePickerAsync(
        new Avalonia.Platform.Storage.FilePickerSaveOptions {
          Title = title,
          DefaultExtension = ext,
        });
    return file?.TryGetLocalPath();
  }

  private string? _tlogPath;

  [ObservableProperty]
  private double _tlogProgress;

  [ObservableProperty]
  private string _tlogPositionText = "0.00 %";

  [ObservableProperty]
  private string _playbackSpeedText = "x 1.0";

  [ObservableProperty]
  private bool _tlogPlaying;

  [ObservableProperty]
  private int _selectedActionTabIndex;

  public event Action<int>? ActionTabShortcutRequested;

  public bool HasTlog => _tlog.IsOpen;

  private bool _seekFromUi = true;

  [RelayCommand]
  private async Task LoadTlog() {
    var path = await PickFileAsync("Open telemetry log", "*.tlog", "Telemetry log");
    if (path == null) {
      return;
    }
    try {
      await Task.Run(() => _tlog.Open(path));
      _tlogPath = path;
      LogStatus = $"Loaded {System.IO.Path.GetFileName(path)} ({_tlog.Duration:hh\\:mm\\:ss}).";
    } catch (Exception ex) {
      LogStatus = "Open failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private void PlayPauseTlog() {
    if (!_tlog.IsOpen) {
      LogStatus = "Load a telemetry log first.";
      return;
    }
    if (_tlog.IsPlaying) {
      _tlog.Pause();
      TlogPlaying = false;
    } else {
      _tlog.Play();
      TlogPlaying = true;
    }
  }

  [RelayCommand]
  private void SetTlogSpeed(string factor) {
    if (double.TryParse(factor, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var s)) {
      _tlog.Speed = s;
      PlaybackSpeedText = $"x {_tlog.Speed:0.0##}";
    }
  }

  public void SelectActionTab(int index) {
    if (index is >= 0 and <= 9) {
      if (ActionTabShortcutRequested == null) {
        SelectedActionTabIndex = index;
      } else {
        ActionTabShortcutRequested.Invoke(index);
      }
    }
  }

  public void AdjustTlogSpeed(int direction) {
    if (direction == 0) {
      return;
    }
    _tlog.Speed = NextTlogSpeed(_tlog.Speed, direction);
    PlaybackSpeedText = $"x {_tlog.Speed:0.0##}";
  }

  internal static double NextTlogSpeed(double current, int direction) {
    if (direction < 0) {
      return Math.Clamp(current > 1 ? current - 1 : current / 2, 0.1, 10);
    }
    if (direction > 0) {
      return Math.Clamp(current > 1 ? current + 1 : current * 2, 0.1, 10);
    }
    return Math.Clamp(current, 0.1, 10);
  }

  partial void OnTlogProgressChanged(double value) {
    if (_seekFromUi && _tlog.IsOpen) {
      _tlog.Seek(value / 100.0);
    }
  }

  private void OnTlogProgress(double fraction) {
    Dispatcher.UIThread.Post(() => {
      _seekFromUi = false;
      TlogProgress = fraction * 100.0;
      _seekFromUi = true;
      TlogPositionText = $"{fraction * 100.0:0.00} %";
      if (fraction >= 1.0) {
        TlogPlaying = false;
      }
    });
  }

  private void OnTlogPacket(MAVLink.MAVLinkMessage msg) {
    var cs = _comPort.MAV?.cs;
    if (cs == null) {
      return;
    }
    switch (msg.msgid) {
      case (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT
          when msg.data is MAVLink.mavlink_global_position_int_t gpi:
        if (gpi.lat != 0 || gpi.lon != 0) {
          cs.lat = gpi.lat / 1e7;
          cs.lng = gpi.lon / 1e7;
        }
        cs.alt = gpi.alt / 1000.0f;
        break;
      case (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE
          when msg.data is MAVLink.mavlink_attitude_t att:
        cs.roll = att.roll * 180f / (float)Math.PI;
        cs.pitch = att.pitch * 180f / (float)Math.PI;
        cs.yaw = att.yaw * 180f / (float)Math.PI;
        break;
      case (uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD
          when msg.data is MAVLink.mavlink_vfr_hud_t hud:
        cs.groundspeed = hud.groundspeed;
        cs.airspeed = hud.airspeed;
        break;
    }
  }

  [RelayCommand]
  private async Task TlogToKml() {
    if (_tlogPath == null) {
      LogStatus = "Load a telemetry log first.";
      return;
    }
    var outPath = await PickSaveAsync("Save KML", "kml");
    if (outPath == null) {
      return;
    }
    try {
      await Task.Run(() => TlogPlayer.ExportKml(_tlogPath, outPath));
      LogStatus = "Wrote " + System.IO.Path.GetFileName(outPath);
    } catch (Exception ex) {
      LogStatus = "Export failed: " + ex.Message;
    }
  }

  private string? _binPath;

  [RelayCommand]
  private async Task ReviewLog() {
    var path = await PickFileAsync("Open dataflash log", "*.bin", "DataFlash log");
    if (path == null) {
      return;
    }
    _binPath = path;
    try {
      await Views.LogBrowseWindow.OpenWith(path);
      LogStatus = $"Opened {System.IO.Path.GetFileName(path)} in Log Browser.";
    } catch (Exception ex) {
      LogStatus = "Open failed: " + ex.Message;
    }
  }

  private async Task<string?> EnsureBinAsync() {
    if (_binPath != null) {
      return _binPath;
    }
    _binPath = await PickFileAsync("Open dataflash log", "*.bin", "DataFlash log");
    return _binPath;
  }

  [RelayCommand]
  private async Task CreateKmlGpx() {
    var bin = await EnsureBinAsync();
    if (bin == null) {
      return;
    }
    try {
      await Task.Run(() => {
        var baseName = System.IO.Path.ChangeExtension(bin, null);
        DataFlashLog.ExportKml(bin, baseName + ".kml");
        DataFlashLog.ExportGpx(bin, baseName + ".gpx");
      });
      LogStatus = "Wrote KML + GPX next to the log.";
    } catch (Exception ex) {
      LogStatus = "Export failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private async Task ConvertBinToLog() {
    var bin = await EnsureBinAsync();
    if (bin == null) {
      return;
    }
    var outPath = await PickSaveAsync("Save text log", "log");
    if (outPath == null) {
      return;
    }
    try {
      await Task.Run(() => DataFlashLog.ConvertBinToLog(bin, outPath));
      LogStatus = "Wrote " + System.IO.Path.GetFileName(outPath);
    } catch (Exception ex) {
      LogStatus = "Convert failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private async Task CreateMatlab() {
    var bin = await EnsureBinAsync();
    if (bin == null) {
      return;
    }
    try {
      await Task.Run(() => DataFlashLog.ExportMatlab(bin, s => Dispatcher.UIThread.Post(() => LogStatus = s)));
      LogStatus = "Matlab export complete.";
    } catch (Exception ex) {
      LogStatus = "Matlab export failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private async Task AutoAnalysis() {
    var bin = await EnsureBinAsync();
    if (bin == null) {
      return;
    }
    LogStatus = "Analyzing…";
    try {
      var summary = await Task.Run(() =>
          Services.LogAnalyzer.Format(Services.LogAnalyzer.Analyze(bin)));
      LogStatus = summary;
    } catch (Exception ex) {
      LogStatus = "Analysis failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private async Task GeoReferenceImages() {

    LogStatus = "Geo Reference: matching images to GPS by EXIF timestamp...";
    await Views.GeoRefWindow.OpenWith();
  }

  [RelayCommand]
  private async Task OrganizeLogs() {
    var root = Settings.Instance.LogDir;
    var candidates = await Task.Run(() => LogOrganizer.FindCandidates(root));
    if (candidates.Length == 0) {
      LogStatus = $"No tlog/rlog/bin/log files found in {root}.";
      return;
    }

    if (!await Dialogs.Confirm(
            "Organize Logs",
            $"Move {candidates.Length} log file(s) into vehicle/system folders under:\n{root}\n\n" +
            "Associated files with the same base name will be moved too.")) {
      return;
    }

    LogStatus = $"Organizing {candidates.Length} log file(s)…";
    try {
      var processed = await Task.Run(() => LogOrganizer.Organize(root));
      LogStatus = $"Organized {processed} tlog/rlog/bin/log file(s) under {root}.";
    } catch (Exception ex) {
      LogStatus = "Log organization failed: " + ex.Message;
    }
  }

  [ObservableProperty]
  private double _tilt;

  [ObservableProperty]
  private double _pan;

  [ObservableProperty]
  private double _roll2;

  private long _lastMountSend;
  private int _mountNudgeVersion;
  private bool _suppressMountUpdates;

  [Obsolete]
  partial void OnTiltChanged(double value) => NudgeMount();

  [Obsolete]
  partial void OnPanChanged(double value) => NudgeMount();

  [Obsolete]
  partial void OnRoll2Changed(double value) => NudgeMount();

  [Obsolete]
  private void NudgeMount() {
    if (!Connected || _suppressMountUpdates) {
      return;
    }
    int version = Interlocked.Increment(ref _mountNudgeVersion);
    long now = Environment.TickCount64;
    int delay = MountTrailingDelay(now, Interlocked.Read(ref _lastMountSend));
    if (delay > 0) {
      _ = SendTrailingMount(delay, version);
      return;
    }
    Interlocked.Exchange(ref _lastMountSend, now);
    _ = PointMount();
  }

  internal static int MountTrailingDelay(long now, long lastSend) =>
      (int)Math.Clamp(100 - (now - lastSend), 0, 100);

  [Obsolete]
  private async Task SendTrailingMount(int delayMilliseconds, int version) {
    await Task.Delay(delayMilliseconds);
    if (version != Volatile.Read(ref _mountNudgeVersion)
        || !Connected || _suppressMountUpdates) {
      return;
    }
    Interlocked.Exchange(ref _lastMountSend, Environment.TickCount64);
    await PointMount();
  }

  [RelayCommand]
  [Obsolete]
  private async Task PointMount() {
    if (!Connected) {
      return;
    }
    double t = Tilt, p = Pan, r = Roll2;
    var angles = MountControlCentidegrees(t, r, p);
    try {
      await Task.Run(() => _comPort.setMountControl(
          Sysid, Compid, angles.Pitch, angles.Roll, angles.Yaw, false));
      Log($"Mount tilt {t} pan {p} roll {r}");
    } catch (Exception ex) {
      Log("Mount command failed: " + ex.Message);
    }
  }

  internal static (double Pitch, double Roll, double Yaw) MountControlCentidegrees(
      double tilt, double roll, double pan) => (tilt * 100, roll * 100, pan * 100);

  [RelayCommand]
  [Obsolete]
  private async Task ResetPosition() {
    if (!Connected) {
      return;
    }

    _suppressMountUpdates = true;
    try {
      Tilt = 0;
      Pan = 0;
      Roll2 = 0;
      Interlocked.Increment(ref _mountNudgeVersion);
      Interlocked.Exchange(ref _lastMountSend, Environment.TickCount64);
    } finally {
      _suppressMountUpdates = false;
    }

    try {
      await Task.Run(() => {
        _comPort.setMountConfigure(
            MAVLink.MAV_MOUNT_MODE.MAVLINK_TARGETING, false, false, false);
        _comPort.setMountControl(Sysid, Compid, 0, 0, 0, false);
      });
      Log("Mount reset to MAVLink targeting at neutral");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Reset Mount", "Command failed: " + ex.Message);
    }
  }

  [ObservableProperty]
  private string _logStatus = "";

  [RelayCommand]
  private void DownloadDataflashLog() {
    Views.LogDownloadWindow.OpenWindow();
    LogStatus = "Opened the MAVLink DataFlash log downloader.";
  }

  private bool Connected => CanSendMessage(_comPort);

  internal static readonly TimeSpan VehicleActionResponseTimeout = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan VehicleStatePollInterval = TimeSpan.FromMilliseconds(100);

  internal static string VehicleActionFailureMessage(Exception exception) => exception switch {
    TimeoutException => "Timed out waiting for a response from the vehicle.",
    ObjectDisposedException => "The vehicle connection was closed while the command was running.",
    _ => "Command failed: " + exception.Message,
  };

  private async Task ReportVehicleActionFailure(string action, Exception exception) {
    string message = VehicleActionFailureMessage(exception);
    Log($"{action} failed: {exception.Message}");
    await Services.Dialogs.Alert(action, message);
  }

  [RelayCommand]
  [Obsolete]
  private async Task ToggleArm() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    bool target = !_comPort.MAV.cs.armed;
    string action = target ? "Arm" : "Disarm";
    if (!target && !await Services.Dialogs.Confirm(
            "Disarm", "Disarm the vehicle now? In-flight disarming can cause a crash.")) {
      return;
    }

    var statusMessages = new System.Collections.Concurrent.ConcurrentQueue<string>();
    MAVLinkInterface link = _comPort;
    byte targetSystem = link.MAV.sysid;
    byte targetComponent = link.MAV.compid;
    int subscription = -1;
    try {
      subscription = link.SubscribeToPacketType(
          MAVLink.MAVLINK_MSG_ID.STATUSTEXT,
          message => {
            statusMessages.Enqueue(System.Text.Encoding.ASCII.GetString(
                message.ToStructure<MAVLink.mavlink_statustext_t>().text).TrimEnd('\0'));
            return true;
          },
          targetSystem,
          targetComponent);
      bool accepted;
      try {
        accepted = await Task.Run(() => link.doARM(
            targetSystem, targetComponent, target, false, VehicleActionResponseTimeout));
      } finally {
        if (subscription >= 0) {
          link.UnSubscribeToPacketType(subscription);
          subscription = -1;
        }
      }

      if (accepted) {
        Messages += $"{action}: ok\n";
        return;
      }

      string reason = string.Join(Environment.NewLine,
          statusMessages.Where(text => !string.IsNullOrWhiteSpace(text)));
      string details = reason.Length == 0 ? "The vehicle rejected the command." : reason;
      bool force = await Services.Dialogs.Confirm(
          $"Force {action}",
          $"{action} failed.\n\n{details}\n\nForce {action} bypasses safety checks and can "
          + "cause a crash or serious injury. Continue?");
      if (!force) {
        Messages += $"{action}: rejected\n{details}\n";
        return;
      }

      bool forced = await Task.Run(() => link.doARM(
          targetSystem, targetComponent, target, true, VehicleActionResponseTimeout));
      Messages += $"Force {action}: {(forced ? "ok" : "rejected")}\n";
    } catch (Exception ex) {
      await ReportVehicleActionFailure("Arm / Disarm", ex);
    } finally {
      if (subscription >= 0) {
        try {
          link.UnSubscribeToPacketType(subscription);
        } catch (Exception ex) {
          Log("Unable to remove arm-status subscription: " + ex.Message);
        }
      }
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task SetMode() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    MAVLinkInterface link = _comPort;
    MAVState mav = link.MAV;
    MissionPlanner.CurrentState currentState = mav.cs;
    if (RequiresModeFailsafeConfirmation(currentState.failsafe)
        && !await Services.Dialogs.Confirm(
            "Failsafe",
            "The vehicle is currently in failsafe. Changing mode can override its automatic "
            + "recovery action. Change mode anyway?")) {
      Messages += "Mode change cancelled while the vehicle is in failsafe.\n";
      return;
    }
    string modeName = SelectedMode;
    string? readiness = AutoTuneReadinessMessage(
        currentState.firmware,
        modeName,
        currentState.mode,
        currentState.armed,
        currentState.landed_state);
    if (readiness != null) {
      Messages += readiness + "\n";
      await Services.Dialogs.Alert("Set Mode", readiness);
      return;
    }
    var request = new MAVLink.mavlink_set_mode_t();
    if (!link.translateMode(mav.sysid, mav.compid, modeName, ref request)) {
      Messages += $"Mode {modeName} is not available for {currentState.firmware}.\n";
      return;
    }
    var statusMessages = new System.Collections.Concurrent.ConcurrentQueue<string>();
    int subscription = -1;
    try {
      subscription = link.SubscribeToPacketType(
          MAVLink.MAVLINK_MSG_ID.STATUSTEXT,
          message => {
            statusMessages.Enqueue(System.Text.Encoding.ASCII.GetString(
                message.ToStructure<MAVLink.mavlink_statustext_t>().text).TrimEnd('\0'));
            return true;
          },
          mav.sysid,
          mav.compid);
      await Task.Run(() => link.setMode(mav.sysid, mav.compid, request));
      var feedback = await WaitForModeChangeFeedback(
          currentState, modeName, statusMessages, TimeSpan.FromSeconds(2));
      if (feedback.Failure is { } failure) {
        string explanation = ModeChangeFailureExplanation(
            currentState.firmware, modeName, failure);
        Messages += explanation + "\n";
        await Services.Dialogs.Alert("Set Mode", explanation);
      } else if (feedback.Accepted) {
        Messages += $"Mode changed to {modeName}\n";
      } else {
        Messages += $"Requested mode {modeName}; no confirmation was received.\n";
      }
    } catch (Exception ex) {
      await ReportVehicleActionFailure("Set Mode", ex);
    } finally {
      if (subscription >= 0) {
        try {
          link.UnSubscribeToPacketType(subscription);
        } catch (Exception ex) {
          Log("Unable to remove mode-status subscription: " + ex.Message);
        }
      }
    }
  }

  internal static bool IsCopterAutoTuneSourceMode(string? mode) =>
      NormalizeModeName(mode) is "STABILIZE" or "ALTHOLD" or "POSHOLD" or "LOITER";

  internal static string? AutoTuneReadinessMessage(
      MissionPlanner.ArduPilot.Firmwares firmware,
      string? requestedMode,
      string? currentMode,
      bool armed,
      byte landedState) {
    if (firmware != MissionPlanner.ArduPilot.Firmwares.ArduCopter2
        || NormalizeModeName(requestedMode) != "AUTOTUNE") {
      return null;
    }
    if (NormalizeModeName(currentMode) == "AUTOTUNE") {
      return null;
    }

    bool sourceAllowed = IsCopterAutoTuneSourceMode(currentMode);
    bool onGround = landedState == (byte)MAVLink.MAV_LANDED_STATE.ON_GROUND;
    if (sourceAllowed && armed && !onGround) {
      return null;
    }

    return "ArduCopter can enter AutoTune only while armed and airborne, with non-zero throttle, "
        + "from Stabilize, AltHold, PosHold, or Loiter. "
        + $"Current state: mode {currentMode ?? "unknown"}, "
        + $"{(armed ? "armed" : "disarmed")}, "
        + $"{(onGround ? "on ground" : "landed state " + landedState)}.";
  }

  internal static bool IsModeChangeFailureStatus(string? status) =>
      !string.IsNullOrWhiteSpace(status)
      && status.Contains("mode change", StringComparison.OrdinalIgnoreCase)
      && status.Contains("failed", StringComparison.OrdinalIgnoreCase);

  internal static string ModeChangeFailureExplanation(
      MissionPlanner.ArduPilot.Firmwares firmware,
      string? requestedMode,
      string vehicleStatus) {
    if (firmware == MissionPlanner.ArduPilot.Firmwares.ArduCopter2
        && NormalizeModeName(requestedMode) == "AUTOTUNE"
        && vehicleStatus.Contains("init failed", StringComparison.OrdinalIgnoreCase)) {
      return vehicleStatus
          + "\n\nArduCopter AutoTune must be entered while armed and airborne, with non-zero "
          + "throttle, from Stabilize, AltHold, PosHold, or Loiter. Circle is not an allowed "
          + "source mode.";
    }
    return vehicleStatus;
  }

  private static string NormalizeModeName(string? mode) => new(
      (mode ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

  private static async Task<(bool Accepted, string? Failure)> WaitForModeChangeFeedback(
      MissionPlanner.CurrentState currentState,
      string requestedMode,
      System.Collections.Concurrent.ConcurrentQueue<string> statusMessages,
      TimeSpan timeout) {
    long deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
    do {
      if (NormalizeModeName(currentState.mode) == NormalizeModeName(requestedMode)) {
        return (true, null);
      }
      string? failure = statusMessages.FirstOrDefault(IsModeChangeFailureStatus);
      if (failure != null) {
        return (false, failure);
      }
      await Task.Delay(50);
    } while (Environment.TickCount64 < deadline);

    return (false, statusMessages.FirstOrDefault(IsModeChangeFailureStatus));
  }

  private void RefreshFlightOptions(MissionPlanner.CurrentState cs) {
    RefreshMountModes(cs.firmware);

    if (_modeFirmware != cs.firmware || Modes.Count == 0) {
      string previous = SelectedMode;
      string[] modes = ModesForFirmware(cs.firmware);
      Modes.Clear();
      foreach (string mode in modes) {
        Modes.Add(mode);
      }
      _modeFirmware = cs.firmware;
      SelectedMode = modes.FirstOrDefault(mode =>
              string.Equals(mode, cs.mode, StringComparison.OrdinalIgnoreCase))
          ?? modes.FirstOrDefault(mode =>
              string.Equals(mode, previous, StringComparison.OrdinalIgnoreCase))
          ?? modes.FirstOrDefault()
          ?? previous;
    }

    int maximum = _comPort.MAV.wps.Count;
    foreach (string parameter in new[] { "CMD_TOTAL", "WP_TOTAL", "MIS_TOTAL" }) {
      if (_comPort.MAV.param.ContainsKey(parameter)) {
        maximum = Math.Max(maximum,
            (int)Math.Round(_comPort.MAV.param[parameter].Value));
      }
    }
    maximum = Math.Clamp(maximum, 0, ushort.MaxValue);
    if (maximum == _waypointOptionMax) {
      return;
    }

    int previousWaypoint = SelectedWaypoint?.Index ?? 0;
    WpNumbers.Clear();
    WpNumbers.Add(new WaypointOption(0, "0 (Home)"));
    for (int index = 1; index <= maximum; index++) {
      WpNumbers.Add(new WaypointOption(index, index.ToString(CultureInfo.InvariantCulture)));
    }
    _waypointOptionMax = maximum;
    SelectedWaypoint = WpNumbers.FirstOrDefault(item => item.Index == previousWaypoint)
        ?? WpNumbers[0];
  }

  internal static string[] ModesForFirmware(MissionPlanner.ArduPilot.Firmwares firmware) {
    try {
      return [.. MissionPlanner.ArduPilot.Common.getModesList(firmware)
          .Select(mode => mode.Value)
          .Where(mode => !string.IsNullOrWhiteSpace(mode))
          .Distinct(StringComparer.OrdinalIgnoreCase)];
    } catch {
      return [];
    }
  }

  private void RefreshMountModes(MissionPlanner.ArduPilot.Firmwares firmware) {
    int parameterCount = _comPort.MAV.param.Count;
    if (_mountModeFirmware == firmware && MountModes.Count > 0
        && (_mountModeMetadataAvailable || parameterCount == _mountModeParameterCount)) {
      return;
    }

    int previousValue = SelectedMountMode?.Value ?? 0;
    var metadata = LoadMountModeMetadata(firmware.ToString());
    var options = BuildMountModeOptions(metadata);
    MountModes.Clear();
    foreach (var option in options) {
      MountModes.Add(option);
    }
    _mountModeFirmware = firmware;
    _mountModeParameterCount = parameterCount;
    _mountModeMetadataAvailable = metadata.Count > 0;
    SelectedMountMode = MountModes.FirstOrDefault(option => option.Value == previousValue)
        ?? MountModes.FirstOrDefault();
  }

  private static IReadOnlyList<KeyValuePair<int, string>> LoadMountModeMetadata(string firmware) {
    foreach (string name in new[] { "MNT1_DEFLT_MODE", "MNT_DEFLT_MODE", "MNT_MODE" }) {
      try {
        var options = ParameterMetaDataRepository.GetParameterOptionsInt(name, firmware);
        if (options is { Count: > 0 }) {
          return options;
        }
      } catch {
        // Fall back to the MAV_MOUNT_MODE values below when metadata is not available yet.
      }
    }
    return [];
  }

  internal static IReadOnlyList<ParamOption> BuildMountModeOptions(
      IEnumerable<KeyValuePair<int, string>> metadata) {
    var options = metadata
        .GroupBy(option => option.Key)
        .Select(group => group.First())
        .OrderBy(option => option.Key)
        .Select(option => new ParamOption(option.Key, option.Value))
        .ToArray();
    return options.Length > 0
        ? options
        : [
          new ParamOption(0, "Retract"),
          new ParamOption(1, "Neutral"),
          new ParamOption(2, "MAVLink Targeting"),
          new ParamOption(3, "RC Targeting"),
          new ParamOption(4, "GPS Point"),
        ];
  }

  internal static bool RequiresModeFailsafeConfirmation(bool failsafe) => failsafe;

  [RelayCommand]
  [Obsolete]
  private Task QuickAuto() {
    SelectedMode = "AUTO";
    return SetMode();
  }

  [RelayCommand]
  [Obsolete]
  private Task QuickLoiter() {
    SelectedMode = "LOITER";
    return SetMode();
  }

  [RelayCommand]
  [Obsolete]
  private Task QuickRtl() {
    SelectedMode = "RTL";
    return SetMode();
  }

  public IReadOnlyList<MavlinkMessageOption> MessageOptions { get; } =
      BuildMessageIntervalOptions();

  internal static IReadOnlyList<MavlinkMessageOption> BuildMessageIntervalOptions() =>
      Enum.GetValues<MAVLink.MAVLINK_MSG_ID>()
          // This legacy off-spec camera announcement is not a requestable MAVLink message. It is
          // retained in the decoder for old logs/devices, but must not appear beside the standard
          // VIDEO_STREAM_INFORMATION entry in the SET_MESSAGE_INTERVAL UI.
          .Where(value => !string.Equals(
              value.ToString(), "zVIDEO_STREAM_INFORMATION", StringComparison.Ordinal))
          .GroupBy(value => Convert.ToUInt32(value, CultureInfo.InvariantCulture))
          .Select(group => new MavlinkMessageOption(group.Key, group.First().ToString()))
          .OrderBy(option => option.Id)
          .ToArray();

  [ObservableProperty]
  private MavlinkMessageOption? _selectedMessage;

  [ObservableProperty]
  private double _messageRateHz = 1;

  [ObservableProperty]
  private string _messageIntervalStatus = "";

  public static float MessageIntervalMicroseconds(double rateHz) {
    if (!double.IsFinite(rateHz) || rateHz <= 0) {
      throw new ArgumentOutOfRangeException(nameof(rateHz), "Message rate must be greater than zero.");
    }
    return (float)Math.Round(1_000_000d / Math.Clamp(rateHz, 0.01, 1000d));
  }

  [RelayCommand]
  private async Task ApplyMessageInterval() {
    try {
      await SetMessageInterval(
          MessageIntervalMicroseconds(MessageRateHz), $"{MessageRateHz:0.##} Hz");
    } catch (ArgumentOutOfRangeException ex) {
      MessageIntervalStatus = ex.Message;
    }
  }

  [RelayCommand]
  private Task StopMessageInterval() => SetMessageInterval(-1, "stopped");

  [RelayCommand]
  private Task ResetMessageInterval() => SetMessageInterval(0, "firmware default");

  private async Task SetMessageInterval(float intervalMicroseconds, string description) {
    if (!Connected || SelectedMessage == null) {
      MessageIntervalStatus = "Connect and select a MAVLink message first.";
      return;
    }

    var message = SelectedMessage;
    try {
#pragma warning disable CS0612 // Upstream MAVLinkInterface exposes only this command API today.
      bool accepted = await Task.Run(() =>
          _comPort.doCommand(
              _comPort.MAV.sysid,
              _comPort.MAV.compid,
              MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
              message.Id,
              intervalMicroseconds,
              0, 0, 0, 0, 0));
#pragma warning restore CS0612
      MessageIntervalStatus = accepted
          ? $"{message.Name}: {description}"
          : $"{message.Name}: request rejected";
      Log("Message interval " + MessageIntervalStatus);
    } catch (Exception ex) {
      MessageIntervalStatus = $"{message.Name}: {ex.Message}";
    }
  }

  public ObservableCollection<string> Actions { get; } =
      [
            "Loiter_Unlim",
            "Return_To_Launch",
            "Preflight_Calibration",
            "Mission_Start",
            "Preflight_Reboot_Shutdown",
            "Trigger_Camera",
            "System_Time",
            "Battery_Reset",
            "ADSB_Out_Ident",
            "Scripting_cmd_stop_and_restart",
            "Scripting_cmd_stop",
            "HighLatency_Enable",
            "HighLatency_Disable",
            "Toggle_Safety_Switch",
            "Do_Parachute",
            "Engine_Start",
            "Engine_Stop",
            "Terminate_Flight",
            "Format_SD_Card",
      ];

  /// <summary>
  /// Registers an extension-provided action in the Flight Data action selector.
  /// </summary>
  public void RegisterCustomAction(
      string action, Action<string> handler, string? after = null, string? before = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(action);
    ArgumentNullException.ThrowIfNull(handler);
    if (Actions.Contains(action) || _customActions.ContainsKey(action)) {
      throw new InvalidOperationException($"Action {action} already exists.");
    }

    int index = -1;
    if (after != null) {
      int afterIndex = Actions.IndexOf(after);
      if (afterIndex >= 0) {
        index = afterIndex + 1;
      }
    }
    if (before != null) {
      int beforeIndex = Actions.IndexOf(before);
      if (beforeIndex >= 0) {
        index = beforeIndex;
      }
    }

    _customActions.Add(action, handler);
    if (index >= 0) {
      Actions.Insert(index, action);
    } else {
      Actions.Add(action);
    }
  }

  public bool UnregisterCustomAction(string action) {
    if (!_customActions.Remove(action)) {
      return false;
    }
    return Actions.Remove(action);
  }

  [ObservableProperty]
  private string _selectedAction = "Return_To_Launch";

  public ObservableCollection<WaypointOption> WpNumbers { get; } = [];

  [ObservableProperty]
  private WaypointOption? _selectedWaypoint;

  [ObservableProperty]
  private double _homeAlt;

  [ObservableProperty]
  private double _changeSpeedValue;

  [ObservableProperty]
  private double _changeAltValue = 100;

  [ObservableProperty]
  private double _loiterRadValue;

  [RelayCommand]
  [Obsolete]
  private async Task ChangeSpeed() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    double v = ChangeSpeedValue;
    try {
      bool accepted = await Task.Run(() =>
          _comPort.doCommand(Sysid, Compid, MAVLink.MAV_CMD.DO_CHANGE_SPEED,
              0, (float)v, 0, 0, 0, 0, 0));
      if (!accepted) {
        await Services.Dialogs.Alert("Change Speed", "The vehicle rejected the command.");
        return;
      }
      Log($"Change speed {v}");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Change Speed", "Command failed: " + ex.Message);
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task ChangeAlt() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    int newalt = (int)ChangeAltValue;
    try {
      await Task.Run(() =>
          _comPort.setNewAlt(Sysid, Compid,
              (float)DisplayToVehicleAltitude(
                  newalt, MissionPlanner.CurrentState.multiplieralt)));
      Log($"Change alt {newalt}");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Change Altitude", "Command failed: " + ex.Message);
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task SetLoiterRad() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    if (!_comPort.MAV.param.ContainsKey("LOITER_RAD")
        && !_comPort.MAV.param.ContainsKey("WP_LOITER_RAD")) {
      await Services.Dialogs.Alert(
          "Set Loiter Radius", "This vehicle does not expose a loiter-radius parameter.");
      return;
    }
    int newrad = (int)LoiterRadValue;
    try {
      bool accepted = await Task.Run(() =>
          _comPort.setParam(["LOITER_RAD", "WP_LOITER_RAD"],
              newrad / MissionPlanner.CurrentState.multiplierdist));
      if (!accepted) {
        await Services.Dialogs.Alert("Set Loiter Radius", "The vehicle rejected the value.");
        return;
      }
      Log($"Set loiter rad {newrad}");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Set Loiter Radius", "Command failed: " + ex.Message);
    }
  }

  public ObservableCollection<ParamOption> MountModes { get; } = [];

  [ObservableProperty]
  private ParamOption? _selectedMountMode;

  [RelayCommand]
  [Obsolete]
  private async Task SetMount() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    if (SelectedMountMode == null) {
      Messages += "No mount mode is available.\n";
      return;
    }

    int mode = SelectedMountMode.Value;
    try {
      bool accepted = await Task.Run(() => _comPort.MAV.param.ContainsKey("MNT_MODE")
          ? _comPort.setParam(_comPort.MAV.sysid, _comPort.MAV.compid, "MNT_MODE", mode)
          : _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid,
              MAVLink.MAV_CMD.DO_MOUNT_CONTROL, 0, 0, 0, 0, 0, 0, mode));
      if (!accepted) {
        await Services.Dialogs.Alert("Set Mount", "The vehicle did not accept the mount mode.");
        return;
      }
      Log($"Set mount mode {SelectedMountMode.Text} ({mode})");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Set Mount", "No response from the vehicle: " + ex.Message);
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task RestartMission() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    try {
      await Task.Run(() => {
        _comPort.setWPCurrent(_comPort.MAV.sysid, _comPort.MAV.compid, 0);
        _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid,
            MAVLink.MAV_CMD.MISSION_START, 0, 0, 0, 0, 0, 0, 0);
      });
      Log("Restart mission");
    } catch (Exception ex) {
      await ReportVehicleActionFailure("Restart Mission", ex);
    }
  }

  [RelayCommand]
  [Obsolete]
  private async Task ResumeMission() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    if (!await Services.Dialogs.MessageShowAgain("Resume Mission",
        "Warning: this will reprogram your mission, arm and issue a takeoff command (copter).",
        "resumemission")) {
      return;
    }
    int last = _comPort.MAV.cs.lastautowp == -1 ? 1 : _comPort.MAV.cs.lastautowp;
    var s = await Services.Dialogs.InputBox("Resume at", "Resume mission at waypoint #", last.ToString());
    if (!int.TryParse(s, out int lastwpno) || lastwpno < 0 || lastwpno > ushort.MaxValue) {
      await Services.Dialogs.Alert(
          "Resume Mission", $"Waypoint must be between 0 and {ushort.MaxValue}.");
      return;
    }
    MAVLinkInterface link = _comPort;
    MAVState mav = link.MAV;
    var cs = mav.cs;
    byte targetSystem = mav.sysid;
    byte targetComponent = mav.compid;
    Log("Resuming mission…");
    try {
      bool completed = await Task.Run(() => {
        var cmds = new System.Collections.Generic.List<MissionPlanner.Utilities.Locationwp>();
        ushort wpcount = link.getWPCount(targetSystem, targetComponent);
        if (wpcount == 0 || lastwpno >= wpcount) {
          throw new InvalidOperationException(
              $"Waypoint {lastwpno} is not present in the vehicle mission ({wpcount} items).");
        }
        var lastwpdata = link.getWP(targetSystem, targetComponent, (ushort)lastwpno,
            MAVLink.MAV_MISSION_TYPE.MISSION, VehicleActionResponseTimeout);
        for (ushort a = 0; a < wpcount; a++) {
          var wp = a == lastwpno
              ? lastwpdata
              : link.getWP(targetSystem, targetComponent, a,
                  MAVLink.MAV_MISSION_TYPE.MISSION, VehicleActionResponseTimeout);
          if (a < lastwpno && a != 0) {
            if (wp.id != (ushort)MAVLink.MAV_CMD.TAKEOFF
                && wp.id < (ushort)MAVLink.MAV_CMD.LAST) {
              continue;
            }
            if (wp.id > (ushort)MAVLink.MAV_CMD.DO_LAST) {
              continue;
            }
          }
          cmds.Add(wp);
        }
        ushort wpno = 0;
        link.setWPTotalAsync(targetSystem, targetComponent, (ushort)cmds.Count)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        foreach (var loc in cmds) {
          if (link.setWP(targetSystem, targetComponent, loc, wpno, (MAVLink.MAV_FRAME)loc.frame)
              != MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED) {
            return false;
          }
          wpno++;
        }
        link.setWPACK(targetSystem, targetComponent);
        link.setWPCurrent(
            targetSystem, targetComponent, 1, VehicleActionResponseTimeout);

        if (cs.firmware == MissionPlanner.ArduPilot.Firmwares.ArduCopter2) {
          if (!SendAndWaitForVehicleState(
              () => {
                link.setMode(targetSystem, targetComponent, "GUIDED");
                return true;
              },
              () => cs.mode.Equals("Guided", StringComparison.OrdinalIgnoreCase),
              VehicleActionResponseTimeout)) {
            return false;
          }
          if (!SendAndWaitForVehicleState(
              () => link.doARM(targetSystem, targetComponent, true, false,
                  VehicleActionResponseTimeout),
              () => cs.armed,
              VehicleActionResponseTimeout)) {
            return false;
          }
          if (!SendAndWaitForVehicleState(
              () => link.doCommand(targetSystem, targetComponent,
                  MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, lastwpdata.alt),
              () => cs.alt >= lastwpdata.alt - 2,
              VehicleActionResponseTimeout)) {
            return false;
          }
        }
        link.setMode(targetSystem, targetComponent, "AUTO");
        return true;
      });
      if (!completed) {
        const string message = "The vehicle did not complete the resume sequence.";
        Log("Resume mission failed: " + message);
        await Services.Dialogs.Alert("Resume Mission", message);
        return;
      }
      Log("Resume mission");
    } catch (Exception ex) {
      await ReportVehicleActionFailure("Resume Mission", ex);
    }
  }

  internal static bool SendAndWaitForVehicleState(
      Func<bool> sendCommand, Func<bool> stateReached, TimeSpan timeout) {
    ArgumentNullException.ThrowIfNull(sendCommand);
    ArgumentNullException.ThrowIfNull(stateReached);
    if (timeout <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    if (!sendCommand()) {
      return false;
    }
    while (!stateReached()) {
      TimeSpan remaining = timeout - elapsed.Elapsed;
      if (remaining <= TimeSpan.Zero) {
        return false;
      }
      System.Threading.Thread.Sleep(
          remaining < VehicleStatePollInterval ? remaining : VehicleStatePollInterval);
    }
    return true;
  }

  [RelayCommand]
  private void RawSensorView() {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    var win = new Views.RawSensorWindow();
    if (top != null) {
      win.Show(top);
    } else {
      win.Show();
    }
  }

  [RelayCommand]
  private void Joystick() {
    if (_joystickSetupWindow is { } existing) {
      existing.Activate();
      return;
    }

    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    var window = new Views.JoystickSetupWindow();
    _joystickSetupWindow = window;
    window.Closed += (_, _) => {
      if (ReferenceEquals(_joystickSetupWindow, window)) {
        _joystickSetupWindow = null;
      }
    };
    if (top != null) {
      window.Show(top);
    } else {
      window.Show();
    }
    Log("Joystick setup opened.");
  }

  [RelayCommand]
  private void DisableJoystick() {
    if (AppState.JoystickControl.Active is { } joystick) {
      AppState.JoystickControl.Stop(joystick, "Joystick disabled from Flight Data.");
      IsJoystickActive = false;
      Log("Joystick disabled and RC overrides released.");
    } else {
      IsJoystickActive = false;
      Log("Joystick is not enabled.");
    }
  }

  internal Views.JoystickSetupWindow? ActiveJoystickSetupWindow => _joystickSetupWindow;

  internal static bool CanSendMessage(MAVLinkInterface? link) {
    try {
      return link?.BaseStream?.IsOpen == true;
    } catch (ObjectDisposedException) {
      return false;
    }
  }

  [RelayCommand]
  private async Task ShowMessage() {
    if (!CanSendMessage(_comPort)) {
      Log("Message not sent: no active vehicle connection.");
      return;
    }
    var txt = await Services.Dialogs.InputBox("Enter Message", "Enter Message to be logged");
    if (string.IsNullOrEmpty(txt)) {
      return;
    }
    try {
      _comPort.send_text(5, txt);
    } catch {
      await Services.Dialogs.Alert("Error", "No response from vehicle.");
    }
  }

  [ObservableProperty]
  private bool _autoPan = true;

  partial void OnAutoPanChanged(bool value) =>
      Settings.Instance[_autoPanSetting] = value.ToString();

  public event System.Action? TrackClearRequested;

  [RelayCommand]
  private void ClearTrack() {
    TrackClearRequested?.Invoke();
    Log("Track cleared.");
  }

  [ObservableProperty]
  private double _cursorLat;

  [ObservableProperty]
  private double _cursorLng;

  public const double TuningWindowSeconds = 30.0;

  [ObservableProperty]
  private bool _tuning;

  private readonly System.Collections.Generic.HashSet<string> _tuningFields = [];
  private long _tuningStart = Environment.TickCount64;

  public event Action<double, System.Collections.Generic.IReadOnlyDictionary<string, double>>? TuningSampled;

  public event Action? TuningFieldsChanged;

  private void InitTuningFields() {
    foreach (var f in new[] { "roll", "pitch", "nav_roll", "nav_pitch" }) {
      if (_statusProps.Any(p => p.Name == f)) {
        _tuningFields.Add(f);
      }
    }
  }

  partial void OnTuningChanged(bool value) {
    if (value) {
      _tuningStart = Environment.TickCount64;
    }
  }

  public System.Collections.Generic.List<(string name, string desc)> TuningFieldList() => QuickFieldList();

  public bool IsTuningField(string name) => _tuningFields.Contains(name);

  public void SetTuningFields(System.Collections.Generic.IEnumerable<string> names) {
    _tuningFields.Clear();
    foreach (var n in names) {
      _tuningFields.Add(n);
    }
    _tuningStart = Environment.TickCount64;
    TuningFieldsChanged?.Invoke();
  }

  private void SampleTuning(MissionPlanner.CurrentState cs) {
    if (!Tuning || _tuningFields.Count == 0 || TuningSampled == null) {
      return;
    }
    double t = (Environment.TickCount64 - _tuningStart) / 1000.0;
    var sample = new System.Collections.Generic.Dictionary<string, double>();
    foreach (var name in _tuningFields) {
      var pi = _statusProps.FirstOrDefault(p => p.Name == name);
      if (pi == null) {
        continue;
      }
      try {
        if (pi.GetValue(cs) is IConvertible c) {
          sample[name] = Convert.ToDouble(c, CultureInfo.InvariantCulture);
        }
      } catch {

      }
    }
    if (sample.Count > 0) {
      TuningSampled.Invoke(t, sample);
    }
  }

  private byte Sysid => _comPort.MAV.sysid;
  private byte Compid => _comPort.MAV.compid;

  internal static byte GuidedFrameByte(string frame) => frame switch {
    "Absolute" => (byte)MAVLink.MAV_FRAME.GLOBAL,
    "Terrain" => (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT,
    _ => (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
  };

  internal static string GuidedFrameName(byte frame) => (MAVLink.MAV_FRAME)frame switch {
    MAVLink.MAV_FRAME.GLOBAL => "Absolute",
    MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT => "Terrain",
    _ => "Relative",
  };

  private static double AltitudeMultiplier(double multiplier) =>
      double.IsFinite(multiplier) && multiplier > 0 ? multiplier : 1;

  internal static double GuidedAltitudeDefault(
      MissionPlanner.ArduPilot.Firmwares firmware, double multiplier) =>
      (firmware == MissionPlanner.ArduPilot.Firmwares.ArduCopter2 ? 10 : 100)
      * AltitudeMultiplier(multiplier);

  internal static double DisplayToVehicleAltitude(double altitude, double multiplier) =>
      altitude / AltitudeMultiplier(multiplier);

  internal static byte ResolveGuidedFrame(bool hasGuidedTarget, byte guidedFrame,
      string? savedFrame) {
    if (hasGuidedTarget) {
      return IsGuidedFrame(guidedFrame)
          ? guidedFrame
          : (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT;
    }
    return byte.TryParse(savedFrame, NumberStyles.Integer, CultureInfo.InvariantCulture,
        out byte parsed) && IsGuidedFrame(parsed)
        ? parsed
        : (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT;
  }

  private static bool IsGuidedFrame(byte frame) => (MAVLink.MAV_FRAME)frame is
      MAVLink.MAV_FRAME.GLOBAL
      or MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT
      or MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT;

  private static bool TryParseAltitude(string? text, out double altitude) =>
      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out altitude)
      || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out altitude);

  private static bool HasGuidedTarget(MAVLink.mavlink_mission_item_int_t guided) =>
      guided.x != 0 || guided.y != 0 || guided.z != 0;

  [Obsolete]
  public async Task<bool> SetGuidedAltitude() {
    double multiplier = AltitudeMultiplier(MissionPlanner.CurrentState.multiplieralt);
    double defaultAltitude = GuidedAltitudeDefault(_comPort.MAV.cs.firmware, multiplier);
    if (TryParseAltitude(Settings.Instance["guided_alt"], out double savedAltitude)) {
      defaultAltitude = savedAltitude;
    }

    byte savedFrame = ResolveGuidedFrame(false, 0, Settings.Instance["guided_alt_frame"]);
    var result = await Services.Dialogs.AltInputBox(
        "Enter Guided Mode Alt", defaultAltitude, GuidedFrameName(savedFrame));
    if (result == null) {
      return false;
    }
    if (!double.IsFinite(result.Value.Alt)) {
      await Services.Dialogs.Alert("Guided altitude", "Bad altitude value.");
      return false;
    }

    byte frame = GuidedFrameByte(result.Value.Frame);
    double vehicleAltitude = DisplayToVehicleAltitude(result.Value.Alt, multiplier);
    Settings.Instance["guided_alt"] =
        result.Value.Alt.ToString(CultureInfo.InvariantCulture);
    Settings.Instance["guided_alt_frame"] = frame.ToString(CultureInfo.InvariantCulture);
    _comPort.MAV.GuidedMode.z = (float)vehicleAltitude;
    _comPort.MAV.GuidedMode.frame = frame;

    if (string.Equals(_comPort.MAV.cs.mode, "Guided", StringComparison.OrdinalIgnoreCase)) {
      double lat = _comPort.MAV.GuidedMode.x / 1e7;
      double lng = _comPort.MAV.GuidedMode.y / 1e7;
      if (ValidCoordinates(lat, lng)) {
        try {
          await GuidedGotoVehicle(lat, lng, vehicleAltitude, frame);
        } catch (Exception ex) {
          await Services.Dialogs.Alert(
              "Guided altitude", "Could not update the active target: " + ex.Message);
          return false;
        }
      }
    }
    Log($"Guided altitude {result.Value.Alt:0.##} ({result.Value.Frame})");
    return true;
  }

  [Obsolete]
  private async Task GuidedGotoVehicle(double lat, double lng, double altitude, byte frame) {
    var wp = new MissionPlanner.Utilities.Locationwp {
      id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
      lat = lat,
      lng = lng,
      alt = (float)altitude,
      frame = frame,
    };
    await Task.Run(() => _comPort.setGuidedModeWP(wp));
  }

  [Obsolete]
  private async Task GuidedGoto(double lat, double lng, double alt, string frame) {
    double vehicleAltitude = DisplayToVehicleAltitude(
        alt, MissionPlanner.CurrentState.multiplieralt);
    await GuidedGotoVehicle(lat, lng, vehicleAltitude, GuidedFrameByte(frame));
    Log($"Fly to {lat:0.000000},{lng:0.000000} @ {alt:0.##} ({frame})");
  }

  [Obsolete]
  public async Task FlyToHere(double lat, double lng) {
    if (!Connected) {
      return;
    }
    if (!ValidCoordinates(lat, lng)) {
      await Services.Dialogs.Alert("Fly To Here", "Bad coordinates.");
      return;
    }
    if (_comPort.MAV.GuidedMode.z == 0 && !await SetGuidedAltitude()) {
      return;
    }
    try {
      await GuidedGotoVehicle(lat, lng, _comPort.MAV.GuidedMode.z,
          _comPort.MAV.GuidedMode.frame);
      double displayAltitude = _comPort.MAV.GuidedMode.z
          * AltitudeMultiplier(MissionPlanner.CurrentState.multiplieralt);
      Log($"Fly to {lat:0.000000},{lng:0.000000} @ {displayAltitude:0.##} "
          + $"({GuidedFrameName(_comPort.MAV.GuidedMode.frame)})");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Fly To Here", "Command failed: " + ex.Message);
    }
  }

  [Obsolete]
  public async Task FlyToCoords() {
    if (!Connected) {
      return;
    }
    var text = await Services.Dialogs.InputBox(
        "Fly To Coords", "Enter lat;lng;alt or lat;lng");
    if (!TryParseCoordinates(text, out double lat, out double lng, out double? parsedAlt)) {
      await Services.Dialogs.Alert("Fly To Coords", "Invalid coordinates.");
      return;
    }

    bool hasGuidedTarget = HasGuidedTarget(_comPort.MAV.GuidedMode);
    byte frame = ResolveGuidedFrame(hasGuidedTarget, _comPort.MAV.GuidedMode.frame,
        Settings.Instance["guided_alt_frame"]);
    try {
      if (parsedAlt.HasValue) {
        await GuidedGoto(lat, lng, parsedAlt.Value, GuidedFrameName(frame));
      } else {
        if (_comPort.MAV.GuidedMode.z == 0 && !await SetGuidedAltitude()) {
          return;
        }
        frame = _comPort.MAV.GuidedMode.frame;
        await GuidedGotoVehicle(lat, lng, _comPort.MAV.GuidedMode.z, frame);
        Log($"Fly to {lat:0.000000},{lng:0.000000} using the current guided altitude");
      }
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Fly To Coords", "Command failed: " + ex.Message);
    }
  }

  [Obsolete]
  public async Task PointCameraHere(double lat, double lng) {
    if (!Connected) {
      return;
    }
    if (!ValidCoordinates(lat, lng)) {
      await Services.Dialogs.Alert("Point Camera Here", "Bad coordinates.");
      return;
    }
    var s = await Services.Dialogs.InputBox("Point Camera Here", "Enter Target Alt (relative to home)", "0");
    if (!TryParseAltitude(s, out double alt)) {
      return;
    }
    try {
      await PointCameraAt(lat, lng,
          DisplayToVehicleAltitude(alt, MissionPlanner.CurrentState.multiplieralt),
          MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT);
      Log($"Camera ROI -> {lat:0.000000},{lng:0.000000}");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Point Camera Here", "Command failed: " + ex.Message);
    }
  }

  [Obsolete]
  public async Task PointCameraCoords() {
    if (!Connected) {
      return;
    }
    var text = await Services.Dialogs.InputBox(
        "Point Camera Coords", "Enter lat;lng;alt(abs), or lat;lng");
    if (!TryParseCoordinates(text, out double lat, out double lng, out double? altitude)) {
      return;
    }
    double alt = altitude.HasValue
        ? DisplayToVehicleAltitude(
            altitude.Value, MissionPlanner.CurrentState.multiplieralt)
        : MissionPlanner.Utilities.srtm.getAltitude(lat, lng).alt;
    try {
      await PointCameraAt(lat, lng, alt, MAVLink.MAV_FRAME.GLOBAL);
      Log($"Camera ROI -> {lat:0.000000},{lng:0.000000} @ {alt:0.0} m AMSL");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Point Camera Coords", "Command failed: " + ex.Message);
    }
  }

  [Obsolete]
  private Task PointCameraAt(double lat, double lng, double alt, MAVLink.MAV_FRAME frame) =>
      Task.Run(() => _comPort.doCommandInt(Sysid, Compid, MAVLink.MAV_CMD.DO_SET_ROI,
          0, 0, 0, 0, (int)(lat * 1e7), (int)(lng * 1e7), (float)alt, frame: frame));

  public async Task AddPoiHere(double lat, double lng) {
    if (!ValidCoordinates(lat, lng)) {
      return;
    }
    var name = await Services.Dialogs.InputBox("Add POI", "Name", "POI");
    if (name == null) {
      return;
    }
    double altitude = MissionPlanner.Utilities.srtm.getAltitude(lat, lng).alt;
    Services.PoiStore.Add(lat, lng, altitude, name);
    Log($"POI {name} added at {lat:0.000000},{lng:0.000000}");
  }

  public async Task AddPoiCoords() {
    var text = await Services.Dialogs.InputBox(
        "POI at Coords", "Enter lat;lng;alt, or lat;lng");
    if (!TryParseCoordinates(text, out double lat, out double lng, out double? altitude)) {
      return;
    }
    var name = await Services.Dialogs.InputBox("POI at Coords", "Name", "POI");
    if (name == null) {
      return;
    }
    double resolvedAltitude = altitude ?? MissionPlanner.Utilities.srtm.getAltitude(lat, lng).alt;
    Services.PoiStore.Add(lat, lng, resolvedAltitude, name);
    Log($"POI {name} added at {lat:0.000000},{lng:0.000000}");
  }

  public async Task DeleteNearestPoi(double lat, double lng) {
    var (Point, Distance) = Services.PoiStore.All
        .Select(point => (Point: point, Distance: DistanceMeters(lat, lng, point.Lat, point.Lng)))
        .OrderBy(candidate => candidate.Distance)
        .FirstOrDefault();
    if (Point == null || Distance > 1000) {
      await Services.Dialogs.Alert("Delete POI", "No POI was found within 1 km of this location.");
      return;
    }
    if (!await Services.Dialogs.Confirm(
        "Delete POI", $"Delete {Point.Name} ({Distance:0} m away)?")) {
      return;
    }
    Services.PoiStore.Remove(Point);
    Log($"POI {Point.Name} deleted");
  }

  public async Task ClearPois() {
    if (Services.PoiStore.All.Count == 0
        || !await Services.Dialogs.Confirm("Clear POIs", "Delete every saved POI?")) {
      return;
    }
    Services.PoiStore.Clear();
    Log("All POIs cleared");
  }

  public async Task LoadPois() {
    var path = await PickFileAsync("Load POIs", "*.txt", "POI file");
    if (path == null) {
      return;
    }
    try {
      int imported = Services.PoiStore.Import(path);
      Log($"Imported {imported} POI(s) from {System.IO.Path.GetFileName(path)}");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Load POIs", ex.Message);
    }
  }

  public async Task SavePois() {
    var path = await PickSaveAsync("Save POIs", "txt");
    if (path == null) {
      return;
    }
    try {
      Services.PoiStore.Save(path);
      Log($"Saved {Services.PoiStore.All.Count} POI(s) to {System.IO.Path.GetFileName(path)}");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Save POIs", ex.Message);
    }
  }

  public Task OpenFlightPlanner() {
    RequestFlightPlanner?.Invoke();
    return Task.CompletedTask;
  }

  internal static bool TryParseCoordinates(
      string? text, out double lat, out double lng, out double? altitude) {
    lat = lng = 0;
    altitude = null;
    var fields = text?.Split(';', StringSplitOptions.TrimEntries);
    if (fields is not { Length: 2 or 3 }
        || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
        || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lng)
        || !ValidCoordinates(lat, lng)) {
      return false;
    }
    if (fields.Length == 3) {
      if (!double.TryParse(
          fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedAltitude)) {
        return false;
      }
      altitude = parsedAltitude;
    }
    return true;
  }

  private static bool ValidCoordinates(double lat, double lng) =>
      double.IsFinite(lat) && double.IsFinite(lng)
      && lat is >= -90 and <= 90 && lng is >= -180 and <= 180
      && (lat != 0 || lng != 0);

  private static double DistanceMeters(double lat1, double lng1, double lat2, double lng2) {
    const double radius = 6378137;
    double dLat = (lat2 - lat1) * Math.PI / 180;
    double dLng = (lng2 - lng1) * Math.PI / 180;
    double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
        + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
        * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
    return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
  }

  private void UpdateMissionProgress(MissionPlanner.CurrentState cs) {
    var home = cs.HomeLocation;
    if (home == null || !ValidCoordinates(home.Lat, home.Lng)) {
      home = cs.PlannedHomeLocation;
    }
    var info = CalculateMissionProgress(
        home?.Lat ?? 0, home?.Lng ?? 0,
        _comPort.MAV.wps.OrderBy(item => item.Key).ToArray(),
        (int)cs.wpno, cs.wp_dist);
    MissionProgress = info.TotalDistance > 0
        ? Math.Clamp(info.TravelledDistance / info.TotalDistance * 100, 0, 100)
        : 0;
    MissionProgressText = info.ItemCount == 0
        ? "No mission loaded"
        : $"Mission WP {cs.wpno}/{info.ItemCount}  •  "
          + $"{info.TravelledDistance:0} / {info.TotalDistance:0} m  •  "
          + $"next {cs.wp_dist:0} m";
  }

  internal static MissionProgressInfo CalculateMissionProgress(
      double homeLat, double homeLng,
      IReadOnlyList<KeyValuePair<int, MAVLink.mavlink_mission_item_int_t>> items,
      int currentWaypoint, double distanceToCurrentWaypoint) {
    var points = items
        .Where(item => item.Key != 0
            && Controls.MapView.TryGlobalPosition(item.Value, out _, out _))
        .Select(item => {
          Controls.MapView.TryGlobalPosition(item.Value, out double lat, out double lng);
          return (Seq: item.Key, Lat: lat, Lng: lng);
        })
        .ToArray();
    if (points.Length == 0) {
      return new MissionProgressInfo(0, 0, 0);
    }

    bool havePrevious = ValidCoordinates(homeLat, homeLng);
    double previousLat = homeLat;
    double previousLng = homeLng;
    double total = 0;
    double travelled = 0;
    foreach (var (Seq, Lat, Lng) in points) {
      if (havePrevious) {
        double leg = DistanceMeters(previousLat, previousLng, Lat, Lng);
        total += leg;
        if (Seq <= currentWaypoint) {
          travelled += leg;
        }
      }
      previousLat = Lat;
      previousLng = Lng;
      havePrevious = true;
    }
    if (travelled > 0 && double.IsFinite(distanceToCurrentWaypoint)) {
      travelled -= Math.Max(0, distanceToCurrentWaypoint);
    }
    return new MissionProgressInfo(points.Length, total, Math.Clamp(travelled, 0, total));
  }

  [Obsolete]
  public async Task TriggerCameraNow() {
    if (!Connected) {
      await Services.Dialogs.Alert("Trigger Camera", "Not connected.");
      return;
    }
    await SendCameraTrigger("Trigger Camera");
  }

  public async Task SetHomeHere(double lat, double lng) {
    if (!Connected) {
      await Services.Dialogs.Alert("Set Home", "Not connected.");
      return;
    }
    if (!ValidCoordinates(lat, lng)) {
      await Services.Dialogs.Alert("Set Home", "Bad coordinates.");
      return;
    }

    var terrain = MissionPlanner.Utilities.srtm.getAltitude(lat, lng);
    if (!IsTerrainAltitudeUsable(terrain.currenttype, terrain.alt, allowOcean: true)) {
      await Services.Dialogs.Alert("Set Home", "No SRTM data is available for this area.");
      return;
    }
    if (!await Services.Dialogs.Confirm(
        "Set Home",
        "This will reset the onboard home position and affects RTL. Continue?")) {
      return;
    }

    try {
      bool accepted = await Task.Run(() =>
          _comPort.doCommandInt(Sysid, Compid, MAVLink.MAV_CMD.DO_SET_HOME,
              0, 0, 0, 0, (int)(lat * 1e7), (int)(lng * 1e7), (float)terrain.alt,
              frame: MAVLink.MAV_FRAME.GLOBAL));
      if (!accepted) {
        await Services.Dialogs.Alert("Set Home", "The vehicle rejected the new home position.");
        return;
      }
      Log($"Home set -> {lat:0.000000},{lng:0.000000} @ {terrain.alt:0.0} m AMSL");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Set Home", "Command failed: " + ex.Message);
      return;
    }

    try {
      await _comPort.getHomePositionAsync(Sysid, Compid);
    } catch (Exception ex) {
      Log("Home command was accepted, but readback failed: " + ex.Message);
    }
  }

  internal static bool IsTerrainAltitudeUsable(
      MissionPlanner.Utilities.srtm.tiletype type, double altitude, bool allowOcean) =>
      double.IsFinite(altitude)
      && (type == MissionPlanner.Utilities.srtm.tiletype.valid
          || allowOcean && type == MissionPlanner.Utilities.srtm.tiletype.ocean);

  [Obsolete]
  public async Task SetEkfOriginHere(double lat, double lng) {
    if (!Connected) {
      await Services.Dialogs.Alert("Set EKF Origin", "Not connected.");
      return;
    }
    if (!ValidCoordinates(lat, lng)) {
      await Services.Dialogs.Alert("Set EKF Origin", "Bad coordinates.");
      return;
    }
    var terrain = MissionPlanner.Utilities.srtm.getAltitude(lat, lng);
    if (!IsTerrainAltitudeUsable(terrain.currenttype, terrain.alt, allowOcean: false)) {
      await Services.Dialogs.Alert(
          "Set EKF Origin", "No SRTM data is available for this area.");
      return;
    }
    var go = new MAVLink.mavlink_set_gps_global_origin_t {
      latitude = (int)(lat * 1e7),
      longitude = (int)(lng * 1e7),
      altitude = (int)(terrain.alt * 1000),
      target_system = Sysid,
    };
    try {
      await Task.Run(() => _comPort.sendPacket(go, Sysid, Compid));
      Log($"EKF origin set -> {lat:0.000000},{lng:0.000000} @ {terrain.alt:0.0} m AMSL");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Set EKF Origin", "Command failed: " + ex.Message);
    }
  }

  [Obsolete]
  public async Task TakeOffHere() {
    if (!Connected) {
      await Services.Dialogs.Alert("Takeoff", "Not connected.");
      return;
    }
    string savedAltitude = Settings.Instance["takeoff_alt"] ?? "5";
    var text = await Services.Dialogs.InputBox(
        "Takeoff", "Enter Takeoff Alt (m)", savedAltitude);
    if (!TryParseAltitude(text, out double parsedAltitude)
        || !double.IsFinite(parsedAltitude) || parsedAltitude <= 0
        || parsedAltitude > float.MaxValue) {
      if (text != null) {
        await Services.Dialogs.Alert("Takeoff", "Bad altitude value.");
      }
      return;
    }
    float altitude = (float)parsedAltitude;
    Settings.Instance["takeoff_alt"] = altitude.ToString(CultureInfo.InvariantCulture);
    try {
      await Task.Run(Settings.Instance.Save);
      bool accepted = await Task.Run(() => {
        _comPort.setMode("GUIDED");
        return _comPort.doCommand(
            Sysid, Compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, altitude);
      });
      if (!accepted) {
        await Services.Dialogs.Alert("Takeoff", "The vehicle rejected the command.");
        return;
      }
      Log($"Takeoff to {altitude} m");
    } catch (Exception ex) {
      await Services.Dialogs.Alert("Takeoff", "Command failed: " + ex.Message);
    }
  }

  public async Task JumpToTag() {
    if (!Connected) {
      return;
    }
    var s = await Services.Dialogs.InputBox("Jump To Tag", "Tag Id (0-65535)");
    if (!ushort.TryParse(s, out var tag)) {
      return;
    }
    await Task.Run(() => _comPort.doCommand(Sysid, Compid, MAVLink.MAV_CMD.DO_JUMP_TAG,
        tag, 0, 0, 0, 0, 0, 0));
    Log($"Jump to tag {tag}");
  }

  [RelayCommand]
  [Obsolete]
  private async Task DoAction() {
    var a = SelectedAction;
    if (_customActions.TryGetValue(a, out var customAction)) {
      try {
        customAction(a);
        Log($"Custom action {a} completed");
      } catch (Exception ex) {
        Log($"Custom action {a} failed: {ex.Message}");
      }
      return;
    }

    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    if (ActionRequiresConfirmation(a)
        && !await Services.Dialogs.Confirm("Confirm Action", ActionConfirmationText(a))) {
      Log($"Action {a} cancelled");
      return;
    }
    try {
      bool accepted = await Task.Run(() => RunAction(a));
      if (!accepted) {
        Log($"Action {a} rejected by the vehicle");
        await Services.Dialogs.Alert(
            "Vehicle Action", $"The vehicle rejected the {a} command.");
        return;
      }
      Log($"Action {a} sent");
    } catch (Exception ex) {
      Log($"Action {a} failed: {ex.Message}");
    }
  }

  internal static bool ActionRequiresConfirmation(string action) => action switch {
    "Trigger_Camera" or "System_Time" or "Scripting_cmd_stop_and_restart"
        or "Scripting_cmd_stop" => false,
    _ => true,
  };

  internal static string ActionConfirmationText(string action) => action switch {
    "Terminate_Flight" =>
        "Terminate the flight now? This command can stop the vehicle in flight and cannot be undone.",
    "Format_SD_Card" =>
        "Format the vehicle SD card now? All logs and other data on that card will be permanently erased.",
    "Do_Parachute" =>
        "Release the parachute now and stop the motors? This action cannot be undone.",
    _ => $"Send the vehicle action {action}?",
  };

  internal static MAVLink.PARACHUTE_ACTION ParachuteCommandAction =>
      MAVLink.PARACHUTE_ACTION.PARACHUTE_RELEASE;

  [Obsolete]
  private bool RunAction(string a) {
    byte s = _comPort.MAV.sysid;
    byte c = _comPort.MAV.compid;
    switch (a) {
      case "Loiter_Unlim":
        return _comPort.doCommand(
            s, c, MAVLink.MAV_CMD.LOITER_UNLIM, 0, 0, 0, 0, 0, 0, 0);
      case "Return_To_Launch":
        return _comPort.doCommand(
            s, c, MAVLink.MAV_CMD.RETURN_TO_LAUNCH, 0, 0, 0, 0, 0, 0, 0);
      case "Preflight_Calibration":
        float gyro = _comPort.MAV.cs.firmware
                     == MissionPlanner.ArduPilot.Firmwares.ArduCopter2 ? 1 : 0;
        return _comPort.doCommand(s, c, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION,
            gyro, 0, 1, 0, 0, 0, 0);
      case "Mission_Start":
        return _comPort.doCommand(
            s, c, MAVLink.MAV_CMD.MISSION_START, 0, 0, 0, 0, 0, 0, 0);
      case "Preflight_Reboot_Shutdown":
        return _comPort.doReboot();
      case "Trigger_Camera":
        _comPort.setDigicamControl(true);
        return true;
      case "System_Time":
        _comPort.sendPacket(new MAVLink.mavlink_system_time_t {
          time_unix_usec = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000),
          time_boot_ms = 0,
        }, s, c);
        return true;
      case "Battery_Reset":
        return _comPort.doCommand(
            s, c, MAVLink.MAV_CMD.BATTERY_RESET, 255, 100, 0, 0, 0, 0, 0);
      case "ADSB_Out_Ident":
        return _comPort.doCommand(
            s, c, MAVLink.MAV_CMD.DO_ADSB_OUT_IDENT, 0, 0, 0, 0, 0, 0, 0);
      case "Scripting_cmd_stop_and_restart":
        return _comPort.doCommandInt(s, c, MAVLink.MAV_CMD.SCRIPTING,
            (int)MAVLink.SCRIPTING_CMD.STOP_AND_RESTART, 0, 0, 0, 0, 0, 0);
      case "Scripting_cmd_stop":
        return _comPort.doCommandInt(s, c, MAVLink.MAV_CMD.SCRIPTING,
            (int)MAVLink.SCRIPTING_CMD.STOP, 0, 0, 0, 0, 0, 0);
      case "HighLatency_Enable":
        return _comPort.doHighLatency(true);
      case "HighLatency_Disable":
        return _comPort.doHighLatency(false);
      case "Toggle_Safety_Switch":
        if (s == 0) {
          throw new InvalidOperationException("Cannot toggle safety for system id 0.");
        }
        uint customMode = _comPort.MAV.cs.sensors_enabled.motor_control
                          && _comPort.MAV.cs.sensors_enabled.seen ? 1u : 0u;
        _comPort.setMode(new MAVLink.mavlink_set_mode_t {
          custom_mode = customMode,
          target_system = s,
        }, MAVLink.MAV_MODE_FLAG.SAFETY_ARMED);
        return true;
      case "Do_Parachute":
        return _comPort.doCommand(s, c, MAVLink.MAV_CMD.DO_PARACHUTE,
            (float)ParachuteCommandAction, 0, 0, 0, 0, 0, 0);
      case "Engine_Start":
        return _comPort.doEngineControl(s, c, true);
      case "Engine_Stop":
        return _comPort.doEngineControl(s, c, false);
      case "Terminate_Flight":
        return _comPort.doCommand(
            s, c, MAVLink.MAV_CMD.DO_FLIGHTTERMINATION, 1, 0, 0, 0, 0, 0, 0);
      case "Format_SD_Card":
        return _comPort.doCommandInt(
            s, c, MAVLink.MAV_CMD.STORAGE_FORMAT, 1, 1, 0, 0, 0, 0, 0);
      default:
        throw new InvalidOperationException($"Unknown action {a}.");
    }
  }

  [RelayCommand]
  private async Task SetWp() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    ushort idx = (ushort)(SelectedWaypoint?.Index ?? 0);
    try {
      await Task.Run(() =>
          _comPort.setWPCurrent(_comPort.MAV.sysid, _comPort.MAV.compid, idx));
      Log($"Set current WP {idx}");
    } catch (Exception ex) {
      await ReportVehicleActionFailure("Set WP", ex);
    }
  }

  [RelayCommand]
  private void SetHome() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    var cs = _comPort.MAV.cs;
    cs.altoffsethome = cs.altoffsethome != 0 ? 0 : -(float)(cs.HomeAlt / CurrentState.multiplieralt);
    Log(cs.altoffsethome != 0 ? "Home-alt offset on" : "Home-alt offset off");
  }

  [RelayCommand]
  [Obsolete]
  private async Task AbortLand() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    try {
      await Task.Run(() =>
          _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid,
              MAVLink.MAV_CMD.DO_GO_AROUND, 0, 0, 0, 0, 0, 0, 0));
      Log("Abort landing (go-around) sent");
    } catch (Exception ex) {
      await ReportVehicleActionFailure("Abort Landing", ex);
    }
  }

  [ObservableProperty]
  private int _servoNumber = 1;

  [ObservableProperty]
  private int _servoPwm = 1500;

  private readonly bool[] _relayState = new bool[16];

  [RelayCommand]
  [Obsolete]
  private async Task SetServo() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    int n = ServoNumber;
    int pwm = ServoPwm;
    await Task.Run(() =>
        _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.DO_SET_SERVO,
            n, pwm, 0, 0, 0, 0, 0));
    Log($"Servo {n} -> {pwm} us");
  }

  [RelayCommand]
  [Obsolete]
  private async Task SetRelay(string spec) {
    if (!Connected || spec == null) {
      return;
    }
    var parts = spec.Split(':');
    if (parts.Length != 2 || !int.TryParse(parts[0], out int idx) || idx >= _relayState.Length) {
      return;
    }
    bool on = parts[1] switch {
      "low" => false,
      "high" => true,
      _ => !_relayState[idx],
    };
    _relayState[idx] = on;
    await Task.Run(() =>
        _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.DO_SET_RELAY,
            idx, on ? 1 : 0, 0, 0, 0, 0, 0));
    Log($"Relay {idx} {(on ? "ON" : "OFF")}");
  }

  [ObservableProperty]
  private double _gimbalPitch;

  [ObservableProperty]
  private double _gimbalYaw;

  [RelayCommand]
  [Obsolete]
  private async Task PointGimbal() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    double pitch = GimbalPitch;
    double yaw = GimbalYaw;
    await Task.Run(() =>
        _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.DO_MOUNT_CONTROL,
            (float)pitch, 0, (float)yaw, 0, 0, 0, 2));
    Log($"Gimbal pitch {pitch} yaw {yaw}");
  }

  [RelayCommand]
  [Obsolete]
  private async Task TriggerCamera() {
    if (!Connected) {
      Messages += "Not connected.\n";
      return;
    }
    await SendCameraTrigger("Trigger Camera");
  }

  [Obsolete]
  private async Task SendCameraTrigger(string title) {
    try {
      await Task.Run(() => _comPort.setDigicamControl(true));
      Log("Camera trigger sent");
    } catch (Exception ex) {
      await Services.Dialogs.Alert(title, "Command failed: " + ex.Message);
    }
  }

  private void Log(string m) => Messages += m + "\n";

  [ObservableProperty]
  private bool _hudShowIcons = true;

  [ObservableProperty]
  private bool _hudRussian;

  [ObservableProperty]
  private int _hudBatteryCells;

  [ObservableProperty]
  private bool _hudGroundBrown;

  [ObservableProperty]
  private bool _hudSixteenByNine;

  [ObservableProperty]
  private int _hudColumn;

  [ObservableProperty]
  private int _mapColumn = 2;

  [ObservableProperty]
  private double _navBearing;

  [ObservableProperty]
  private string _hudCustomText = "";

  [ObservableProperty]
  private bool _hudHeading = true;

  [ObservableProperty]
  private bool _hudSpeed = true;

  [ObservableProperty]
  private bool _hudAlt = true;

  [ObservableProperty]
  private bool _hudConnection = true;

  [ObservableProperty]
  private bool _hudXTrack = true;

  [ObservableProperty]
  private bool _hudRollPitch = true;

  [ObservableProperty]
  private bool _hudGps = true;

  [ObservableProperty]
  private bool _hudBattery = true;

  [ObservableProperty]
  private bool _hudBattery2 = true;

  [ObservableProperty]
  private bool _hudEkf = true;

  [ObservableProperty]
  private bool _hudVibe = true;

  [ObservableProperty]
  private bool _hudPrearm = true;

  [ObservableProperty]
  private bool _hudAoa = true;

  private readonly System.Collections.Generic.HashSet<string> _hudUserFields = [];
  private readonly Dictionary<string, string> _hudUserPrefixes = new(StringComparer.Ordinal);
  private bool _loadingHudSettings;

  private void LoadHudSettings() {
    _loadingHudSettings = true;
    try {
      HudShowIcons = SavedHudBool("HUD_showicons", true);
      HudRussian = SavedHudBool("russian_hud", false);
      HudGroundBrown = SavedHudBool("groundColorToolStripMenuItem", false);
      HudBatteryCells = SavedHudBool("HUD_showbatterycell", false)
          ? Math.Clamp(Settings.Instance.GetInt32("HUD_batterycellcount", 4), 1, 32)
          : 0;
      if (SavedHudBool("HudSwap", false)) {
        HudColumn = 2;
        MapColumn = 0;
      }

      HudHeading = SavedHudBool("HUD_item_heading", true);
      HudSpeed = SavedHudBool("HUD_item_speed", true);
      HudAlt = SavedHudBool("HUD_item_alt", true);
      HudConnection = SavedHudBool("HUD_item_connection", true);
      HudXTrack = SavedHudBool("HUD_item_xtrack", true);
      HudRollPitch = SavedHudBool("HUD_item_rollpitch", true);
      HudGps = SavedHudBool("HUD_item_gps", true);
      HudBattery = SavedHudBool("HUD_item_battery", true);
      HudBattery2 = SavedHudBool("HUD_item_battery2", true);
      HudEkf = SavedHudBool("HUD_item_ekf", true);
      HudVibe = SavedHudBool("HUD_item_vibe", true);
      HudPrearm = SavedHudBool("HUD_item_prearm", true);
      HudAoa = SavedHudBool("HUD_item_aoa", true);

      foreach (var property in _statusProps.Where(p => IsNumber(p.PropertyType))) {
        string key = "hud1_useritem_" + property.Name;
        if (!Settings.Instance.ContainsKey(key)) {
          continue;
        }
        _hudUserFields.Add(property.Name);
        _hudUserPrefixes[property.Name] = Settings.Instance[key] ?? property.Name + ": ";
      }
    } finally {
      _loadingHudSettings = false;
    }
  }

  private static bool SavedHudBool(string key, bool fallback) =>
      Settings.Instance.ContainsKey(key) ? Settings.Instance.GetBoolean(key) : fallback;

  private void SaveHudBool(string key, bool value) {
    if (!_loadingHudSettings) {
      Settings.Instance[key] = value.ToString();
    }
  }

  partial void OnHudShowIconsChanged(bool value) => SaveHudBool("HUD_showicons", value);
  partial void OnHudRussianChanged(bool value) => SaveHudBool("russian_hud", value);
  partial void OnHudGroundBrownChanged(bool value) =>
      SaveHudBool("groundColorToolStripMenuItem", value);
  partial void OnHudBatteryCellsChanged(int value) {
    if (_loadingHudSettings) {
      return;
    }
    Settings.Instance["HUD_showbatterycell"] = (value > 0).ToString();
    if (value > 0) {
      Settings.Instance["HUD_batterycellcount"] = value.ToString(CultureInfo.InvariantCulture);
    }
  }
  partial void OnHudHeadingChanged(bool value) => SaveHudBool("HUD_item_heading", value);
  partial void OnHudSpeedChanged(bool value) => SaveHudBool("HUD_item_speed", value);
  partial void OnHudAltChanged(bool value) => SaveHudBool("HUD_item_alt", value);
  partial void OnHudConnectionChanged(bool value) => SaveHudBool("HUD_item_connection", value);
  partial void OnHudXTrackChanged(bool value) => SaveHudBool("HUD_item_xtrack", value);
  partial void OnHudRollPitchChanged(bool value) => SaveHudBool("HUD_item_rollpitch", value);
  partial void OnHudGpsChanged(bool value) => SaveHudBool("HUD_item_gps", value);
  partial void OnHudBatteryChanged(bool value) => SaveHudBool("HUD_item_battery", value);
  partial void OnHudBattery2Changed(bool value) => SaveHudBool("HUD_item_battery2", value);
  partial void OnHudEkfChanged(bool value) => SaveHudBool("HUD_item_ekf", value);
  partial void OnHudVibeChanged(bool value) => SaveHudBool("HUD_item_vibe", value);
  partial void OnHudPrearmChanged(bool value) => SaveHudBool("HUD_item_prearm", value);
  partial void OnHudAoaChanged(bool value) => SaveHudBool("HUD_item_aoa", value);

  [RelayCommand]
  private void ToggleHudIcons() => HudShowIcons = !HudShowIcons;

  [RelayCommand]
  private void ToggleRussianHud() => HudRussian = !HudRussian;

  [RelayCommand]
  private void ToggleHudAspect() => HudSixteenByNine = !HudSixteenByNine;

  [RelayCommand]
  private void SwapHudMap() {
    (HudColumn, MapColumn) = (MapColumn, HudColumn);
    Settings.Instance["HudSwap"] = (HudColumn == 2).ToString().ToLowerInvariant();
  }

  private MissionPlanner.Controls.VideoControl? _video;
  private Views.VideoPopupWindow? _videoWindow;
  internal event Action<Views.GimbalVideoPresentation>? VideoPresentationRequested;
  private byte? _selectedCameraSystemId;
  private byte? _selectedCameraComponentId;
  private byte _selectedCameraStreamId;
  private readonly HashSet<Key> _gimbalVideoHeldKeys = [];
  private readonly HashSet<Key> _gimbalVideoPressedKeys = [];
  private GimbalVideoMotion _lastGimbalVideoMotion;
  private GimbalManagerTarget? _rateTarget;
  private MissionPlanner.ArduPilot.Mavlink.CameraProtocol? _zoomTarget;
  private bool _cameraRecordingRequested;

  private sealed record GimbalManagerTarget(
      MAVLinkInterface Link,
      byte SystemId,
      byte ComponentId,
      byte DeviceId,
      MissionPlanner.ArduPilot.Mavlink.GimbalManagerProtocol Manager);

  [ObservableProperty]
  private string _cameraTarget = "Auto-detect";

  [ObservableProperty]
  private double _cameraZoom;

  [ObservableProperty]
  private bool _gimbalYawLocked;

  [ObservableProperty]
  private int _gimbalDeviceId;

  [ObservableProperty]
  private double _gimbalSlewSlow = 1;

  [ObservableProperty]
  private double _gimbalSlewNormal = 5;

  [ObservableProperty]
  private double _gimbalSlewFast = 25;

  [ObservableProperty]
  private double _gimbalZoomSpeed = 1;

  [ObservableProperty]
  private double _gimbalCameraHorizontalFov = 40;

  [ObservableProperty]
  private double _gimbalCameraVerticalFov = 30;

  [ObservableProperty]
  private bool _gimbalUseReportedFov = true;

  [ObservableProperty]
  private string _gimbalVideoStatus =
      "Click the video for control. W/A/S/D slew, Q/E zoom, Alt+drag tracks.";

  private void LoadGimbalVideoSettings() {
    var settings = Settings.Instance;
    GimbalYawLocked = settings.ContainsKey("gimbal_video_yaw_lock")
        && settings.GetBoolean("gimbal_video_yaw_lock");
    GimbalDeviceId = Math.Clamp(settings.GetInt32("gimbal_video_device_id", 0), 0, 6);
    GimbalSlewSlow = ClampSetting(settings.GetDouble("gimbal_video_slew_slow", 1), 0.1, 360, 1);
    GimbalSlewNormal = ClampSetting(settings.GetDouble("gimbal_video_slew_normal", 5), 0.1, 360, 5);
    GimbalSlewFast = ClampSetting(settings.GetDouble("gimbal_video_slew_fast", 25), 0.1, 360, 25);
    GimbalZoomSpeed = ClampSetting(settings.GetDouble("gimbal_video_zoom_speed", 1), 0.01, 1, 1);
    GimbalCameraHorizontalFov = ClampSetting(settings.GetDouble("camera_fovh", 40), 0.01, 180, 40);
    GimbalCameraVerticalFov = ClampSetting(settings.GetDouble("camera_fovv", 30), 0.01, 180, 30);
    GimbalUseReportedFov = !settings.ContainsKey("gimbal_video_reported_fov")
        || settings.GetBoolean("gimbal_video_reported_fov");
  }

  private static double ClampSetting(double value, double min, double max, double fallback) =>
      double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

  private static void SaveGimbalSetting(string key, object value) =>
      Settings.Instance[key] = Convert.ToString(value, CultureInfo.InvariantCulture);

  partial void OnGimbalYawLockedChanged(bool value) =>
      SaveGimbalSetting("gimbal_video_yaw_lock", value);
  partial void OnGimbalDeviceIdChanged(int value) =>
      SaveGimbalSetting("gimbal_video_device_id", Math.Clamp(value, 0, 6));
  partial void OnGimbalSlewSlowChanged(double value) =>
      SaveGimbalSetting("gimbal_video_slew_slow", value);
  partial void OnGimbalSlewNormalChanged(double value) =>
      SaveGimbalSetting("gimbal_video_slew_normal", value);
  partial void OnGimbalSlewFastChanged(double value) =>
      SaveGimbalSetting("gimbal_video_slew_fast", value);
  partial void OnGimbalZoomSpeedChanged(double value) =>
      SaveGimbalSetting("gimbal_video_zoom_speed", value);
  partial void OnGimbalCameraHorizontalFovChanged(double value) =>
      SaveGimbalSetting("camera_fovh", value);
  partial void OnGimbalCameraVerticalFovChanged(double value) =>
      SaveGimbalSetting("camera_fovv", value);
  partial void OnGimbalUseReportedFovChanged(bool value) =>
      SaveGimbalSetting("gimbal_video_reported_fov", value);

  internal Views.VideoPopupWindow EnsureVideoWindow(bool showWindow = true) {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (_videoWindow == null) {
      _video = new MissionPlanner.Controls.VideoControl();
      _videoWindow = new Views.VideoPopupWindow(_video, this);
      Views.VideoPopupWindow openedWindow = _videoWindow;
      MissionPlanner.Controls.VideoControl openedVideo = _video;
      _videoWindow.Closed += (_, _) => {
        ReleaseGimbalVideoInput();
        openedVideo.Dispose();
        if (ReferenceEquals(_video, openedVideo)) {
          _video = null;
        }
        if (ReferenceEquals(_videoWindow, openedWindow)) {
          _videoWindow = null;
        }
      };
    }
    if (showWindow) {
      ShowVideoWindow(_videoWindow, top);
    }
    return _videoWindow;
  }

  private static void ShowVideoWindow(
      Views.VideoPopupWindow window,
      Avalonia.Controls.Window? owner) {
    if (window.IsVisible) {
      window.Activate();
    } else if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  internal Views.VideoPopupWindow RequestVideoPresentation(
      Views.GimbalVideoPresentation presentation) {
    Views.VideoPopupWindow window = EnsureVideoWindow(showWindow: false);
    Action<Views.GimbalVideoPresentation>? presenter = VideoPresentationRequested;
    if (presenter != null) {
      presenter(presentation);
    } else {
      var top = (Avalonia.Application.Current?.ApplicationLifetime
                 as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
      ShowVideoWindow(window, top);
    }
    return window;
  }

  internal void CloseVideoWindow() {
    Views.VideoPopupWindow? window = _videoWindow;
    MissionPlanner.Controls.VideoControl? video = _video;
    if (window != null) {
      try {
        window.Close();
      } catch {
        // Continue releasing libVLC even if the native window backend is already unavailable.
      }
    }
    if (ReferenceEquals(_video, video)) {
      video?.Dispose();
      _video = null;
    }
    if (ReferenceEquals(_videoWindow, window)) {
      _videoWindow = null;
    }
    ReleaseGimbalVideoInput();
  }

  private static async Task<string?> PromptTextAsync(string title, string label, string initial) {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return null;
    }
    var box = new Avalonia.Controls.TextBox { Text = initial, MinWidth = 360 };
    var ok = new Avalonia.Controls.Button {
      Content = "OK",
      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
    };
    var dlg = new Avalonia.Controls.Window {
      Title = title,
      Width = 420,
      Height = 150,
      WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
      Content = new Avalonia.Controls.StackPanel {
        Margin = new Avalonia.Thickness(10),
        Spacing = 8,
        Children = {
          new Avalonia.Controls.TextBlock { Text = label },
          box,
          ok,
        },
      },
    };
    ok.Click += (_, _) => dlg.Close(box.Text);
    return await dlg.ShowDialog<string?>(top);
  }

  private static async Task<(MavlinkVideoStreamOption Option, string Source)?>
      PromptVideoStreamAsync(
      IReadOnlyList<MavlinkVideoStreamOption> options) {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null || options.Count == 0) {
      return null;
    }

    var source = new Avalonia.Controls.TextBox { MinWidth = 520 };
    var details = new Avalonia.Controls.TextBlock {
      TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };
    var combo = new Avalonia.Controls.ComboBox {
      ItemsSource = options,
      SelectedIndex = 0,
      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
    };
    void Select(MavlinkVideoStreamOption? option) {
      if (option == null) {
        return;
      }
      source.Text = option.Source;
      details.Text = option.Details;
    }
    combo.SelectionChanged += (_, _) => Select(combo.SelectedItem as MavlinkVideoStreamOption);
    Select(options[0]);

    var ok = new Avalonia.Controls.Button {
      Content = "Play",
      MinWidth = 80,
      IsDefault = true,
    };
    var cancel = new Avalonia.Controls.Button {
      Content = "Cancel",
      MinWidth = 80,
      IsCancel = true,
    };
    var buttons = new Avalonia.Controls.StackPanel {
      Orientation = Avalonia.Layout.Orientation.Horizontal,
      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
      Spacing = 8,
      Children = { ok, cancel },
    };
    var dlg = new Avalonia.Controls.Window {
      Title = "MAVLink Camera Streams",
      Width = 600,
      SizeToContent = Avalonia.Controls.SizeToContent.Height,
      CanResize = false,
      WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
      Content = new Avalonia.Controls.StackPanel {
        Margin = new Avalonia.Thickness(14),
        Spacing = 8,
        Children = {
          new Avalonia.Controls.TextBlock { Text = "Announced stream" },
          combo,
          details,
          new Avalonia.Controls.TextBlock { Text = "libVLC source (editable)" },
          source,
          buttons,
        },
      },
    };
    ok.Click += (_, _) => dlg.Close(source.Text);
    cancel.Click += (_, _) => dlg.Close((string?)null);
    string? result = await dlg.ShowDialog<string?>(top);
    if (string.IsNullOrWhiteSpace(result)) {
      return null;
    }
    return (combo.SelectedItem as MavlinkVideoStreamOption ?? options[0], result);
  }

  internal static string DefaultVideoSource(string preset) => preset switch {
    "mjpeg" => "http://192.168.0.1:8080/?action=stream",
    "gstreamer" => "rtsp://192.168.144.25:8554/main.264",
    "herelink" => "rtsp://192.168.43.1:8554/fpv_stream",
    "camera" when OperatingSystem.IsLinux() => "v4l2:///dev/video0",
    "camera" when OperatingSystem.IsMacOS() => "avcapture://0",
    "camera" when OperatingSystem.IsWindows() => "dshow://",
    _ => "",
  };

  private static string LoadVideoSource(string preset) {
    string? saved = preset switch {
      "mjpeg" => Settings.Instance["mjpeg_url"],
      "gstreamer" => Settings.Instance["gstreamer_url"],
      "herelink" => Settings.Instance["herelink_url"],
      "camera" => Settings.Instance["video_device_mrl"],
      "mavlink" => Settings.Instance["video_mavlink_url"],
      _ => Settings.Instance["video_custom_url"],
    };
    if (!string.IsNullOrWhiteSpace(saved)) {
      return saved;
    }
    if (preset == "herelink"
        && Settings.Instance["herelinkip"] is { Length: > 0 } herelinkIp) {
      return Uri.TryCreate(herelinkIp, UriKind.Absolute, out _)
          ? herelinkIp
          : $"rtsp://{herelinkIp}:8554/fpv_stream";
    }
    return DefaultVideoSource(preset);
  }

  private static void SaveVideoSource(string preset, string source) {
    switch (preset) {
      case "mjpeg":
        Settings.Instance["mjpeg_url"] = source;
        break;
      case "gstreamer":
        Settings.Instance["gstreamer_url"] = source;
        break;
      case "herelink":
        Settings.Instance["herelink_url"] = source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host)) {
          Settings.Instance["herelinkip"] = uri.Host;
        }
        break;
      case "camera":
        Settings.Instance["video_device_mrl"] = source;
        break;
      case "mavlink":
        Settings.Instance["video_mavlink_url"] = source;
        break;
      default:
        Settings.Instance["video_custom_url"] = source;
        break;
    }
    Settings.Instance.Save();
  }

  private void PlayVideoSource(string source) {
    var win = RequestVideoPresentation(Views.GimbalVideoPresentation.PopOut);
    if (!win.Video.IsAvailable) {
      Log(win.Video.Status);
      win.UpdateStatus();
      return;
    }
    win.Video.Play(source);
    win.UpdateStatus();
    Log(win.Video.Status);
  }

  [RelayCommand]
  private async Task SetVideoSource(string preset) {
    string initial = LoadVideoSource(preset);
    var mrl = await PromptTextAsync("Video source", "Stream URL / MRL:", initial);
    if (string.IsNullOrWhiteSpace(mrl)) {
      return;
    }
    SaveVideoSource(preset, mrl);
    PlayVideoSource(mrl);
  }

  [RelayCommand]
  private async Task SelectMavlinkVideoStream() {
    var announced = MissionPlanner.ArduPilot.Mavlink.CameraProtocol.VideoStreams
        .Where(pair => !Connected || pair.Key.Item1 == Sysid);
    var options = MavlinkVideoStreams.Snapshot(announced);
    if (options.Count == 0) {
      string remembered = LoadVideoSource("mavlink");
      if (!string.IsNullOrWhiteSpace(remembered)
          && await Services.Dialogs.Confirm(
              "MAVLink Camera Streams",
              "No current stream announcements were received. Play the last saved "
              + "MAVLink camera source instead?")) {
        PlayVideoSource(remembered);
        return;
      }
      await Services.Dialogs.Alert(
          "MAVLink Camera Streams",
          "No supported VIDEO_STREAM_INFORMATION announcements have been received. "
          + "Connect the camera and use Developer Tools → Probe MAVLink Camera, then try again.");
      return;
    }
    var selected = await PromptVideoStreamAsync(options);
    if (selected == null) {
      return;
    }
    _selectedCameraSystemId = selected.Value.Option.SystemId;
    _selectedCameraComponentId = selected.Value.Option.ComponentId;
    _selectedCameraStreamId = selected.Value.Option.StreamId;
    CameraTarget = $"{selected.Value.Option.Name} "
        + $"({_selectedCameraSystemId}:{_selectedCameraComponentId})";
    SaveVideoSource("mavlink", selected.Value.Source);
    PlayVideoSource(selected.Value.Source);
  }

  private MissionPlanner.ArduPilot.Mavlink.CameraProtocol? ResolveCameraProtocol(
      out byte streamId) {
    streamId = 0;
    var cameras = _comPort.MAVlist
        .Where(state => state.sysid == Sysid && state.Camera != null)
        .ToArray();
    if (_selectedCameraSystemId.HasValue && _selectedCameraComponentId.HasValue) {
      var selected = cameras.FirstOrDefault(state =>
          state.sysid == _selectedCameraSystemId.Value
          && state.compid == _selectedCameraComponentId.Value);
      if (selected?.Camera != null) {
        streamId = _selectedCameraStreamId;
        return selected.Camera;
      }
    }

    var fallback = cameras
        .OrderBy(state => state.compid is >= (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_CAMERA
                                      and <= (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_CAMERA6
            ? 0 : 1)
        .ThenBy(state => state.compid)
        .FirstOrDefault();
    if (fallback?.Camera != null) {
      CameraTarget = $"MAVLink camera {fallback.sysid}:{fallback.compid}";
    }
    return fallback?.Camera;
  }

  private GimbalManagerTarget? ResolveGimbalManagerTarget() {
    if (!Connected) {
      return null;
    }
    var managers = _comPort.MAVlist
        .Where(state => state.sysid == Sysid && state.GimbalManager != null)
        .OrderBy(state => state.compid == Compid ? 0 : 1)
        .ThenBy(state => state.compid)
        .ToArray();
    var state = managers.FirstOrDefault();
    return state?.GimbalManager == null
        ? null
        : new GimbalManagerTarget(
            _comPort,
            state.sysid,
            state.compid,
            (byte)Math.Clamp(GimbalDeviceId, 0, 6),
            state.GimbalManager);
  }

  private MissionPlanner.ArduPilot.Mavlink.GimbalManagerProtocol?
      ResolveGimbalManagerProtocol() => ResolveGimbalManagerTarget()?.Manager;

  private void ConfigureCameraGeometry(
      MissionPlanner.ArduPilot.Mavlink.CameraProtocol camera) {
    camera.HFOV = (float)Math.Clamp(GimbalCameraHorizontalFov, 0.01, 180);
    camera.VFOV = (float)Math.Clamp(GimbalCameraVerticalFov, 0.01, 180);
    camera.UseFOVStatus = GimbalUseReportedFov
        && GimbalVideoInteraction.HasUsableReportedFov(
            camera.CameraFOVStatus.hfov,
            camera.CameraFOVStatus.vfov);
  }

  internal GimbalTrackingOverlay CurrentGimbalTrackingOverlay() {
    var camera = ResolveCameraProtocol(out _);
    if (camera == null) {
      return GimbalTrackingOverlay.None;
    }
    ConfigureCameraGeometry(camera);
    return GimbalVideoInteraction.TrackingOverlay(camera.CameraTrackingImageStatus);
  }

  internal void HandleGimbalVideoKeyState(
      Key key,
      KeyModifiers modifiers,
      bool pressed) {
    if (GimbalVideoInteraction.IsModifierKey(key)) {
      ApplyGimbalVideoMotion(modifiers);
      return;
    }
    if (GimbalVideoInteraction.IsMotionKey(key)) {
      if (pressed) {
        _gimbalVideoHeldKeys.Add(key);
      } else {
        _gimbalVideoHeldKeys.Remove(key);
      }
      ApplyGimbalVideoMotion(modifiers);
      return;
    }

    if (!pressed || !_gimbalVideoPressedKeys.Add(key)) {
      if (!pressed) {
        _gimbalVideoPressedKeys.Remove(key);
      }
      return;
    }

    switch (GimbalVideoInteraction.Hotkey(key, modifiers)) {
      case GimbalVideoHotkeyAction.TakePicture:
        _ = TakeMavlinkPhoto();
        break;
      case GimbalVideoHotkeyAction.ToggleRecording:
        _ = ToggleMavlinkCameraRecording();
        break;
      case GimbalVideoHotkeyAction.ToggleYawLock:
        _ = ToggleGimbalYawLock();
        break;
      case GimbalVideoHotkeyAction.Neutral:
        _ = GimbalNeutral();
        break;
      case GimbalVideoHotkeyAction.Home:
        _ = GimbalHome();
        break;
    }
  }

  internal void ReleaseGimbalVideoInput() {
    _gimbalVideoHeldKeys.Clear();
    _gimbalVideoPressedKeys.Clear();
    ApplyGimbalVideoMotion(KeyModifiers.None);
  }

  private void ApplyGimbalVideoMotion(KeyModifiers modifiers) {
    GimbalVideoMotion next = GimbalVideoInteraction.Motion(
        _gimbalVideoHeldKeys,
        modifiers,
        GimbalSlewSlow,
        GimbalSlewNormal,
        GimbalSlewFast,
        GimbalZoomSpeed);

    if (next.PitchRate != _lastGimbalVideoMotion.PitchRate
        || next.YawRate != _lastGimbalVideoMotion.YawRate) {
      var target = next.PitchRate == 0 && next.YawRate == 0
          ? _rateTarget
          : ResolveGimbalManagerTarget();
      if (_rateTarget != null && target != _rateTarget) {
        try {
          SendGimbalRates(_rateTarget, 0, 0);
        } catch {
          // Continue switching to the newly-selected vehicle/gimbal.
        }
      }
      _rateTarget = target;
      if (target != null) {
        try {
          SendGimbalRates(target, next.PitchRate, next.YawRate);
          GimbalVideoStatus = $"Slew pitch {next.PitchRate:0.#}°/s, "
              + $"yaw {next.YawRate:0.#}°/s ({(GimbalYawLocked ? "lock" : "follow")})";
        } catch (Exception ex) {
          GimbalVideoStatus = "Gimbal slew failed: " + ex.Message;
        }
      } else if (next.PitchRate != 0 || next.YawRate != 0) {
        GimbalVideoStatus = "No MAVLink gimbal manager detected for the active vehicle.";
      }
      if (next.PitchRate == 0 && next.YawRate == 0) {
        _rateTarget = null;
      }
    }

    if (next.ZoomRate != _lastGimbalVideoMotion.ZoomRate) {
      var camera = next.ZoomRate == 0 ? _zoomTarget : ResolveCameraProtocol(out _);
      if (_zoomTarget != null && camera != _zoomTarget) {
        _ = ObserveGimbalTask(
            _zoomTarget.SetZoomAsync(0, MAVLink.CAMERA_ZOOM_TYPE.ZOOM_TYPE_CONTINUOUS),
            "Stop camera zoom");
      }
      _zoomTarget = camera;
      if (camera != null) {
        _ = ObserveGimbalTask(
            camera.SetZoomAsync(
                next.ZoomRate,
                MAVLink.CAMERA_ZOOM_TYPE.ZOOM_TYPE_CONTINUOUS),
            next.ZoomRate == 0 ? "Stop camera zoom" : "Camera zoom");
      } else if (next.ZoomRate != 0) {
        GimbalVideoStatus = "No MAVLink camera detected for the active vehicle.";
      }
      if (next.ZoomRate == 0) {
        _zoomTarget = null;
      }
    }
    _lastGimbalVideoMotion = next;
  }

  private void SendGimbalRates(
      GimbalManagerTarget target,
      float pitchRate,
      float yawRate) {
    var packet = GimbalVideoInteraction.RatePacket(
        target.SystemId,
        target.ComponentId,
        target.DeviceId,
        pitchRate,
        yawRate,
        GimbalYawLocked);
    target.Link.sendPacket(packet, target.SystemId, target.ComponentId);
  }

  private async Task ObserveGimbalTask(Task task, string title) {
    try {
      await task;
    } catch (Exception ex) {
      GimbalVideoStatus = title + " failed: " + ex.Message;
    }
  }

  private async Task RunGimbalManagerCommand(
      string title,
      Func<MissionPlanner.ArduPilot.Mavlink.GimbalManagerProtocol, byte, Task<bool>> operation,
      Action? accepted = null) {
    var manager = ResolveGimbalManagerProtocol();
    if (manager == null) {
      GimbalVideoStatus = "No MAVLink gimbal manager detected for the active vehicle.";
      return;
    }
    try {
      bool success = await operation(
          manager,
          (byte)Math.Clamp(GimbalDeviceId, 0, 6));
      if (success) {
        accepted?.Invoke();
        GimbalVideoStatus = title + " command accepted.";
      } else {
        GimbalVideoStatus = title + " is not supported by the selected gimbal.";
      }
    } catch (Exception ex) {
      GimbalVideoStatus = title + " failed: " + ex.Message;
    }
  }

  [RelayCommand]
  private Task ToggleGimbalYawLock() {
    bool requested = !GimbalYawLocked;
    return RunGimbalManagerCommand(
        requested ? "Yaw lock" : "Yaw follow",
        (manager, id) => manager.SetRatesCommandAsync(
            _lastGimbalVideoMotion.PitchRate,
            _lastGimbalVideoMotion.YawRate,
            requested,
            id),
        () => GimbalYawLocked = requested);
  }

  [RelayCommand]
  private Task GimbalRetract() => RunGimbalManagerCommand(
      "Gimbal retract", (manager, id) => manager.RetractAsync(id));

  [RelayCommand]
  private Task GimbalNeutral() => RunGimbalManagerCommand(
      "Gimbal neutral", (manager, id) => manager.NeutralAsync(id));

  [RelayCommand]
  private Task GimbalPointDown() => RunGimbalManagerCommand(
      "Gimbal point down",
      (manager, id) => manager.SetAnglesCommandAsync(-90, 0, false, id));

  [RelayCommand]
  private Task GimbalHome() {
    var home = _comPort.MAV.cs.HomeLocation;
    return RunGimbalManagerCommand(
        "Gimbal point home",
        (manager, id) => manager.SetROILocationAsync(
            home.Lat, home.Lng, home.Alt, id, MAVLink.MAV_FRAME.GLOBAL));
  }

  internal async Task HandleGimbalVideoPointerCommand(
      GimbalVideoPointerCommand command) {
    var camera = ResolveCameraProtocol(out _);
    if (camera == null) {
      GimbalVideoStatus = "No MAVLink camera detected for the active vehicle.";
      return;
    }
    ConfigureCameraGeometry(camera);
    try {
      bool accepted;
      switch (command.Action) {
        case GimbalVideoPointerAction.TrackPoint:
          camera.SubscribeTracking(5);
          accepted = await camera.SetTrackingPointAsync(
              (float)command.End.X, (float)command.End.Y);
          break;
        case GimbalVideoPointerAction.TrackRectangle:
          camera.SubscribeTracking(5);
          accepted = await camera.SetTrackingRectangleAsync(
              (float)command.Start.X,
              (float)command.Start.Y,
              (float)command.End.X,
              (float)command.End.Y);
          break;
        case GimbalVideoPointerAction.PointOfInterest: {
            var location = camera.CalculateImagePointLocation(command.End.X, command.End.Y);
            if (location == null) {
              GimbalVideoStatus = "Camera FOV data cannot resolve that image point.";
              return;
            }
            var manager = ResolveGimbalManagerProtocol();
            if (manager == null) {
              GimbalVideoStatus = "No MAVLink gimbal manager detected for the active vehicle.";
              return;
            }
            accepted = await manager.SetROILocationAsync(
                location.Lat,
                location.Lng,
                location.Alt,
                (byte)Math.Clamp(GimbalDeviceId, 0, 6),
                MAVLink.MAV_FRAME.GLOBAL);
            break;
          }
        default: {
            var manager = ResolveGimbalManagerProtocol();
            var attitude = manager?.GetAttitude((byte)Math.Clamp(GimbalDeviceId, 0, 6));
            if (manager == null || attitude == null) {
              GimbalVideoStatus = "The selected gimbal has not reported its attitude yet.";
              return;
            }
            var rotation = camera.CalculateImagePointRotation(command.End.X, command.End.Y);
            accepted = await manager.SetAttitudeAsync(
                attitude * rotation,
                GimbalYawLocked,
                (byte)Math.Clamp(GimbalDeviceId, 0, 6));
            break;
          }
      }
      GimbalVideoStatus = accepted
          ? command.Action + " command accepted."
          : command.Action + " is not supported by the selected payload.";
    } catch (Exception ex) {
      GimbalVideoStatus = command.Action + " failed: " + ex.Message;
    }
  }

  private async Task RunMavlinkCameraCommand(
      string title,
      Func<MissionPlanner.ArduPilot.Mavlink.CameraProtocol, byte, Task> operation,
      Action? sent = null) {
    if (!Connected) {
      await Services.Dialogs.Alert(title, "Not connected.");
      return;
    }
    var camera = ResolveCameraProtocol(out byte streamId);
    if (camera == null) {
      await Services.Dialogs.Alert(
          title,
          "No MAVLink camera component has been detected. Use Developer Tools → "
          + "Probe MAVLink Camera and wait for CAMERA_INFORMATION.");
      return;
    }
    try {
      await operation(camera, streamId);
      sent?.Invoke();
      Log(title + " command sent to " + CameraTarget);
      GimbalVideoStatus = title + " command sent to " + CameraTarget + ".";
    } catch (Exception ex) {
      GimbalVideoStatus = title + " failed: " + ex.Message;
      await Services.Dialogs.Alert(title, "Command failed: " + ex.Message);
    }
  }

  [RelayCommand]
  private Task TakeMavlinkPhoto() => RunMavlinkCameraCommand(
      "Take Camera Photo", (camera, _) => camera.TakeSinglePictureAsync());

  [RelayCommand]
  private Task StartMavlinkCameraRecording() => RunMavlinkCameraCommand(
      "Start Camera Recording",
      (camera, streamId) => camera.StartRecordingAsync(streamId),
      () => _cameraRecordingRequested = true);

  [RelayCommand]
  private Task StopMavlinkCameraRecording() => RunMavlinkCameraCommand(
      "Stop Camera Recording",
      (camera, streamId) => camera.StopRecordingAsync(streamId),
      () => _cameraRecordingRequested = false);

  private Task ToggleMavlinkCameraRecording() => _cameraRecordingRequested
      ? StopMavlinkCameraRecording()
      : StartMavlinkCameraRecording();

  [RelayCommand]
  private Task SetMavlinkCameraZoom() {
    float zoom = (float)Math.Clamp(CameraZoom, 0, 100);
    return RunMavlinkCameraCommand(
        "Set Camera Zoom",
        (camera, _) => camera.SetZoomAsync(
            zoom, MAVLink.CAMERA_ZOOM_TYPE.ZOOM_TYPE_RANGE));
  }

  [RelayCommand]
  private async Task SnapshotVideo() {
    if (_videoWindow == null) {
      await Services.Dialogs.Alert("Video Snapshot", "Start a video stream first.");
      return;
    }
    var outPath = await PickSaveAsync("Save video snapshot", "png");
    if (outPath == null) {
      return;
    }
    _videoWindow.Video.Snapshot(outPath);
    _videoWindow.UpdateStatus();
    Log(_videoWindow.Video.Status);
  }

  [RelayCommand]
  private async Task RecordVideo() {
    var win = RequestVideoPresentation(Views.GimbalVideoPresentation.PopOut);
    var outPath = await PickSaveAsync("Record video to", "ts");
    if (outPath == null) {
      return;
    }
    win.Video.TryRecord(outPath);
    win.UpdateStatus();
    Log(win.Video.Status);
  }

  [RelayCommand]
  private void StopVideo() {
    if (_videoWindow == null) {
      return;
    }
    _videoWindow.Video.Stop();
    _videoWindow.UpdateStatus();
    Log(_videoWindow.Video.Status);
  }

  [RelayCommand]
  private async Task HudUserItems() {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return;
    }
    var panel = new Avalonia.Controls.StackPanel { Spacing = 3 };
    var selectedFields = new HashSet<string>(_hudUserFields, StringComparer.Ordinal);
    var prefixes = new Dictionary<string, string>(_hudUserPrefixes, StringComparer.Ordinal);
    foreach (var p in _statusProps) {
      if (!IsNumber(p.PropertyType)) {
        continue;
      }
      var cb = new Avalonia.Controls.CheckBox {
        Content = p.Name,
        IsChecked = _hudUserFields.Contains(p.Name),
        Width = 180,
        FontSize = 11,
      };
      var name = p.Name;
      var prefix = new Avalonia.Controls.TextBox {
        Text = prefixes.TryGetValue(name, out var savedPrefix) ? savedPrefix : name + ": ",
        Width = 260,
        FontSize = 11,
        IsEnabled = cb.IsChecked == true,
      };
      cb.IsCheckedChanged += (_, _) => {
        if (cb.IsChecked == true) {
          selectedFields.Add(name);
        } else {
          selectedFields.Remove(name);
        }
        prefix.IsEnabled = cb.IsChecked == true;
      };
      prefix.TextChanged += (_, _) => prefixes[name] = prefix.Text ?? name + ": ";
      panel.Children.Add(new Avalonia.Controls.StackPanel {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 8,
        Children = { cb, prefix },
      });
    }
    var dlg = new Avalonia.Controls.Window {
      Title = "HUD User Items",
      Width = 900,
      Height = 600,
      WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
      Content = new Avalonia.Controls.ScrollViewer {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        Content = panel,
      },
    };
    await dlg.ShowDialog(top);

    _hudUserFields.Clear();
    _hudUserPrefixes.Clear();
    foreach (var property in _statusProps.Where(p => IsNumber(p.PropertyType))) {
      string key = "hud1_useritem_" + property.Name;
      if (!selectedFields.Contains(property.Name)) {
        Settings.Instance.Remove(key);
        continue;
      }
      string prefix = prefixes.TryGetValue(property.Name, out var savedPrefix)
          ? savedPrefix
          : property.Name + ": ";
      _hudUserFields.Add(property.Name);
      _hudUserPrefixes[property.Name] = prefix;
      Settings.Instance[key] = prefix;
    }
    Settings.Instance.Save();
  }

  private static bool IsNumber(Type t) {
    t = Nullable.GetUnderlyingType(t) ?? t;
    return t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(uint)
        || t == typeof(short) || t == typeof(ushort) || t == typeof(long) || t == typeof(ulong)
        || t == typeof(byte) || t == typeof(sbyte) || t == typeof(decimal);
  }

  [RelayCommand]
  private async Task SetBatteryCells() {
    var top = (Avalonia.Application.Current?.ApplicationLifetime
               as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    if (top == null) {
      return;
    }
    var box = new Avalonia.Controls.NumericUpDown {
      Minimum = 0,
      Maximum = 14,
      Value = HudBatteryCells,
      Width = 120,
    };
    var ok = new Avalonia.Controls.Button {
      Content = "OK",
      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
    };
    var dlg = new Avalonia.Controls.Window {
      Title = "Battery Cell Count",
      Width = 220,
      Height = 140,
      WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
      Content = new Avalonia.Controls.StackPanel {
        Margin = new Avalonia.Thickness(10),
        Spacing = 8,
        Children = {
          new Avalonia.Controls.TextBlock { Text = "Cells (0 = auto):" },
          box,
          ok,
        },
      },
    };
    ok.Click += (_, _) => dlg.Close(true);
    var res = await dlg.ShowDialog<bool>(top);
    if (res) {
      HudBatteryCells = (int)(box.Value ?? 0);
    }
  }

  [ObservableProperty]
  private int _squawk = 1200;

  [ObservableProperty]
  private string _flightId = "";

  [ObservableProperty]
  private bool _modeA;

  [ObservableProperty]
  private bool _modeC;

  [ObservableProperty]
  private bool _modeS;

  [ObservableProperty]
  private bool _modeEs;

  [ObservableProperty]
  private string _transponderStatus = "Not connected";

  [ObservableProperty]
  private bool _xpdrMaintReq;

  [ObservableProperty]
  private bool _xpdrGpsUnavailable;

  [ObservableProperty]
  private bool _xpdrGpsNoFix;

  [ObservableProperty]
  private bool _xpdrTxSystemFailure;

  [ObservableProperty]
  private bool _xpdrAirborne;

  [ObservableProperty]
  private string _xpdrNic = "UNKNOWN";

  [ObservableProperty]
  private string _xpdrNacp = "UNKNOWN";

  private void SendTransponder(byte extraState) {
    if (!Connected) {
      TransponderStatus = "Not connected";
      return;
    }
    byte state = (byte)(extraState
        | (ModeA ? 16 : 0)
        | (ModeC ? 32 : 0)
        | (ModeS ? 64 : 0)
        | (ModeEs ? 128 : 0));
    var id = System.Text.Encoding.ASCII.GetBytes((FlightId ?? "").PadRight(8)[..8]);
    _comPort.uAvionixADSBControl(int.MaxValue, (ushort)Squawk, state, 0, id, 0);
  }

  [RelayCommand]
  private async Task ConnectTransponder() {
    if (!Connected) {
      TransponderStatus = "Not connected";
      return;
    }
    TransponderStatus = "Connecting…";
    await Task.Run(() =>
        _comPort.doCommand(_comPort.MAV.sysid, _comPort.MAV.compid, MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
            (float)MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_OUT_STATUS, 1000000.0f, 0, 0, 0, 0, 0));
    TransponderStatus = "Requested transponder status stream";
  }

  [RelayCommand]
  private void TransponderStandby() {
    ModeA = ModeC = ModeS = ModeEs = false;
    SendTransponder(0);
    TransponderStatus = "STBY";
  }

  [RelayCommand]
  private void TransponderOn() {
    ModeA = true;
    ModeC = false;
    ModeS = true;
    ModeEs = true;
    SendTransponder(0);
    TransponderStatus = "ON";
  }

  [RelayCommand]
  private void TransponderAlt() {
    ModeA = ModeC = ModeS = ModeEs = true;
    SendTransponder(0);
    TransponderStatus = "ALT";
  }

  [RelayCommand]
  private void TransponderIdent() {
    SendTransponder(8);
    TransponderStatus = "IDENT";
  }

  partial void OnSquawkChanged(int value) => SendTransponder(0);

}

internal static class TransponderAccuracy {
  private static readonly string[] _nic = [
    "UNKNOWN", "<20.0 NM", "<8.0 NM", "<4.0 NM", "<2.0 NM", "<1.0 NM",
    "<0.3 NM", "<0.2 NM", "<0.1 NM", "<75 m", "<25 m", "<7.5 m",
  ];

  private static readonly string[] _nacp = [
    "UNKNOWN", "<10.0 NM", "<4.0 NM", "<2.0 NM", "<1.0 NM", "<0.5 NM",
    "<0.3 NM", "<0.1 NM", "<0.05 NM", "<30 m", "<10 m", "<3 m",
  ];

  internal static string Nic(byte value) => value < _nic.Length ? _nic[value] : "UNKNOWN";

  internal static string Nacp(byte value) => value < _nacp.Length ? _nacp[value] : "UNKNOWN";
}

public partial class QuickItem(string field, string color) : ObservableObject {
  [ObservableProperty]
  private string _field = field;

  [ObservableProperty]
  private string _desc = "";

  [ObservableProperty]
  private double _number;

  public string Color { get; } = color;

  [ObservableProperty]
  private Avalonia.Media.IBrush _brush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));

  [ObservableProperty]
  private Avalonia.Media.IBrush _backgroundBrush = Avalonia.Media.Brushes.Transparent;

  [ObservableProperty]
  private Avalonia.Media.IBrush _labelBrush = Avalonia.Media.Brushes.White;

  internal void ApplyWarningColor(string color) {
    var configured = Avalonia.Media.Color.Parse(Color);
    var (Background, Foreground) = QuickWarningStyle.Resolve(color, configured);
    if (Background == null) {
      BackgroundBrush = Avalonia.Media.Brushes.Transparent;
      Brush = new Avalonia.Media.SolidColorBrush(Foreground);
      LabelBrush = Avalonia.Media.Brushes.White;
      return;
    }

    BackgroundBrush = new Avalonia.Media.SolidColorBrush(Background.Value);
    Brush = new Avalonia.Media.SolidColorBrush(Foreground);
    LabelBrush = new Avalonia.Media.SolidColorBrush(Foreground);
  }
}

internal static class QuickWarningStyle {
  internal static (Avalonia.Media.Color? Background, Avalonia.Media.Color Foreground) Resolve(
      string warningColor, Avalonia.Media.Color configuredForeground) {
    if (string.Equals(warningColor, "NoColor", StringComparison.OrdinalIgnoreCase) ||
        !Avalonia.Media.Color.TryParse(warningColor, out var parsed)) {
      return (null, configuredForeground);
    }

    int brightness = (parsed.R + parsed.G + parsed.B) / 3;
    return (parsed, brightness > 128 ? Avalonia.Media.Colors.Black : Avalonia.Media.Colors.White);
  }
}

public partial class AuxFunctionRow : ObservableObject {
  public AuxFunctionRow(int index, IReadOnlyList<ParamOption> functions) {
    Index = index;
    Label = $"Aux {index + 1}";
    Functions = functions;
    int saved = Settings.Instance.ContainsKey($"Aux{index}_desc")
        && int.TryParse(Settings.Instance[$"Aux{index}_desc"], out int parsed) ? parsed : 0;
    _selectedFunction = functions.FirstOrDefault(option => option.Value == saved)
        ?? functions.FirstOrDefault();
  }

  public int Index { get; }
  public string Label { get; }
  public IReadOnlyList<ParamOption> Functions { get; }

  [ObservableProperty]
  private ParamOption? _selectedFunction;

  [ObservableProperty]
  private string _status = "";

  partial void OnSelectedFunctionChanged(ParamOption? value) {
    if (value != null) {
      Settings.Instance[$"Aux{Index}_desc"] = value.Value.ToString(CultureInfo.InvariantCulture);
    }
  }
}

public sealed record MavlinkMessageOption(uint Id, string Name) {
  public override string ToString() => $"{Id}: {Name}";
}

internal readonly record struct MissionProgressInfo(
    int ItemCount, double TotalDistance, double TravelledDistance);

public partial class RelayChannel : ObservableObject {
  public RelayChannel(int index) {
    Index = index;
    _label = SavedText($"Relay{index}_desc", $"Relay {index + 1}");
    _lowLabel = SavedText($"Relay{index}_lowdesc", "Low");
    _highLabel = SavedText($"Relay{index}_highdesc", "High");
  }

  public int Index { get; }

  [ObservableProperty]
  private string _label;

  [ObservableProperty]
  private string _lowLabel;

  [ObservableProperty]
  private string _highLabel;

  partial void OnLabelChanged(string value) => Settings.Instance[$"Relay{Index}_desc"] = value;
  partial void OnLowLabelChanged(string value) => Settings.Instance[$"Relay{Index}_lowdesc"] = value;
  partial void OnHighLabelChanged(string value) => Settings.Instance[$"Relay{Index}_highdesc"] = value;

  private static string SavedText(string key, string fallback) =>
      string.IsNullOrWhiteSpace(Settings.Instance[key]) ? fallback : Settings.Instance[key]!;
}

public partial class ServoOut(int number) : ObservableObject {
  public int Number { get; } = number;

  [ObservableProperty]
  private int _value;
}

public partial class StatusItem(string name) : ObservableObject {
  public string Name { get; } = name;

  [ObservableProperty]
  private string _value = "";
}

public partial class CheckItem : ObservableObject {
  public CheckItem(
      string name, bool manual = false, string trueColor = "Green", string falseColor = "Red") {
    Name = name;
    Manual = manual;
    TrueBrush = ParseBrush(trueColor, Avalonia.Media.Colors.Green);
    FalseBrush = ParseBrush(falseColor, Avalonia.Media.Colors.Red);
  }

  [ObservableProperty]
  private string _name;

  public bool Manual { get; }

  [ObservableProperty]
  private string _value = "";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(Foreground))]
  private bool _ok;

  public Avalonia.Media.IBrush TrueBrush { get; }
  public Avalonia.Media.IBrush FalseBrush { get; }
  public Avalonia.Media.IBrush Foreground => Ok ? TrueBrush : FalseBrush;

  public void Set(string value, bool ok) {
    Value = value;
    Ok = ok;
  }

  private static Avalonia.Media.IBrush ParseBrush(
      string value, Avalonia.Media.Color fallback) =>
      new Avalonia.Media.SolidColorBrush(
          Avalonia.Media.Color.TryParse(value, out var parsed) ? parsed : fallback);
}

public sealed record WaypointOption(int Index, string Label) {
  public override string ToString() => Label;
}

public partial class ServoChannel : ObservableObject {
  public ServoChannel(int number) {
    Number = number;
    _label = SavedText($"Servo{number}_desc", $"Servo {number}");
    _lowLabel = SavedText($"Servo{number}_lowdesc", "Low");
    _highLabel = SavedText($"Servo{number}_highdesc", "High");
    _min = SavedInt($"Servo{number}_low", 1100);
    _max = SavedInt($"Servo{number}_high", 1900);
  }

  public int Number { get; }

  [ObservableProperty]
  private string _label;

  [ObservableProperty]
  private string _lowLabel;

  [ObservableProperty]
  private string _highLabel;

  [ObservableProperty]
  private int _min = 1100;

  [ObservableProperty]
  private int _max = 1900;

  public bool Toggled { get; set; }

  partial void OnLabelChanged(string value) => Settings.Instance[$"Servo{Number}_desc"] = value;
  partial void OnLowLabelChanged(string value) => Settings.Instance[$"Servo{Number}_lowdesc"] = value;
  partial void OnHighLabelChanged(string value) => Settings.Instance[$"Servo{Number}_highdesc"] = value;
  partial void OnMinChanged(int value) => Settings.Instance[$"Servo{Number}_low"] =
      value.ToString(CultureInfo.InvariantCulture);
  partial void OnMaxChanged(int value) => Settings.Instance[$"Servo{Number}_high"] =
      value.ToString(CultureInfo.InvariantCulture);

  private static string SavedText(string key, string fallback) =>
      string.IsNullOrWhiteSpace(Settings.Instance[key]) ? fallback : Settings.Instance[key]!;

  private static int SavedInt(string key, int fallback) =>
      int.TryParse(Settings.Instance[key], NumberStyles.Integer, CultureInfo.InvariantCulture,
          out int value) ? value : fallback;
}
