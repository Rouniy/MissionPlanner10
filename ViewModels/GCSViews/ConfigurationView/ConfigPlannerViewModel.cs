using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Utilities;
using MissionPlanner.Services;

namespace MissionPlanner.ViewModels.GCSViews.ConfigurationView;

// Remaining upstream-only controls and their platform impact are tracked in docs/PORT_STATUS.md.
public partial class ConfigPlannerViewModel : ViewModelBase, System.IDisposable {
  private const string _defaultMapIconDesc =
      "{alt}{altunit} {airspeed}{speedunit} id:{sysid} Sats:{satcount} HDOP:{gpshdop} Volts:{battery_voltage}";

  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private bool _loading;
  private bool _enablingFlightCommandShortcuts;

  public ObservableCollection<string> DistUnitsOptions { get; } = new() { "Meters", "Feet" };
  public ObservableCollection<string> SpeedUnitsOptions { get; } =
      new() { "meters_per_second", "fps", "kph", "mph", "knots" };
  public ObservableCollection<string> ThemeOptions { get; } =
      new(MissionPlanner.Services.ThemeService.Names);
  public ObservableCollection<string> LayoutOptions { get; } = new() { "Basic", "Advanced", "Custom" };
  public ObservableCollection<string> LanguageOptions { get; } =
      new(LocalizationService.DisplayNames);
  public ObservableCollection<string> SpeechOptions { get; } = new() { "Warning", "Critical", "All" };

  public ObservableCollection<string> SeverityOptions { get; } = new() {
    "Emergency", "Alert", "Critical", "Error", "Warning", "Notice", "Info", "Debug"
  };

  public ObservableCollection<string> MapCacheOptions { get; } =
      new() { "ServerOnly", "ServerAndCache", "CacheOnly" };

  public ObservableCollection<string> SecondaryDisplayStyleOptions { get; } =
      new() { "Normal", "Transparent", "Hidden" };

  // ponytail: upstream's CMB_osdcolor_SelectedIndexChanged body is fully commented out; this only persists "hudcolor" and applies nothing live.
  public ObservableCollection<string> OsdColorOptions { get; } = new() {
    "White", "Black", "Red", "Green", "Blue", "Yellow", "Orange", "Cyan",
    "Magenta", "Gray", "LightGray", "Lime", "Pink", "Purple"
  };

  [ObservableProperty]
  private string _distUnits = "Meters";

  [ObservableProperty]
  private string _altUnits = "Meters";

  [ObservableProperty]
  private string _speedUnits = "meters_per_second";

  [ObservableProperty]
  private string _theme = "Emerald";

  [ObservableProperty]
  private string _layout = "Advanced";

  [ObservableProperty]
  private bool _layoutSelectorVisible = true;

  [ObservableProperty]
  private string _language = "English (United States)";

  [ObservableProperty]
  private string _languageNote = "";

  [ObservableProperty]
  private string _speechLevel = "Warning";

  [ObservableProperty]
  private string _severity = "Warning";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(MapCacheNote))]
  private string _mapCache = "ServerAndCache";

  public string MapCacheNote => MapTileSourceFactory.ParseAccessMode(MapCache) switch {
    MapTileAccessMode.ServerOnly =>
        "Tiles are downloaded from the selected provider and are not written to disk.",
    MapTileAccessMode.CacheOnly =>
        "Offline mode: only tiles already stored in the local cache are shown; no tile network requests are made.",
    _ =>
        "Downloaded tiles are stored in the local cache and reused when the network is unavailable.",
  };

  [ObservableProperty]
  private string _secondaryDisplayStyle = "Normal";

  [ObservableProperty]
  private string _osdColor = "White";

  [ObservableProperty]
  private string _logDir = "";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(SpeechSubOptionsVisible))]
  private bool _enableSpeech;

  public bool SpeechSubOptionsVisible => EnableSpeech;

  [ObservableProperty]
  private string _varioButtonText = "Start Vario";

  [ObservableProperty]
  private string _varioStatus = "vario stopped";

  [ObservableProperty]
  private string _speechStatus = MissionPlanner.Services.Speech.LastStatus;

  [ObservableProperty]
  private bool _speechArmedOnly;

  [ObservableProperty]
  private bool _speechWaypoint;

  [ObservableProperty]
  private bool _speechMode;

  [ObservableProperty]
  private bool _speechCustom;

  [ObservableProperty]
  private bool _speechBattery;

  [ObservableProperty]
  private bool _speechAltWarning;

  [ObservableProperty]
  private bool _speechArmDisarm;

  [ObservableProperty]
  private bool _speechLowSpeed;

  [ObservableProperty]
  private bool _enableHudOverlay = true;

  [ObservableProperty]
  private bool _enableFlightCommandShortcuts;

  [ObservableProperty]
  private bool _loadWaypointsOnConnect;

  [ObservableProperty]
  private bool _startupUdpListenersEnabled = StartupUdpListenerOptions.DefaultEnabled;

  [ObservableProperty]
  private int _startupUdpPrimaryPort = StartupUdpListenerOptions.DefaultPrimaryPort;

  [ObservableProperty]
  private int _startupUdpAlternatePort = StartupUdpListenerOptions.DefaultAlternatePort;

  public string StartupUdpListenerStatus => StartupUdpListenerService.Status;

  [ObservableProperty]
  private bool _displayInFlightData = true;

  [ObservableProperty]
  private bool _mapFollowPlane;

  [ObservableProperty]
  private bool _resetOnUsbConnect;

  [ObservableProperty]
  private bool _rtsResetEsp32;

  [ObservableProperty]
  private bool _displayCog = true;

  [ObservableProperty]
  private bool _displayHeading = true;

  [ObservableProperty]
  private bool _displayNavBearing = true;

  [ObservableProperty]
  private bool _displayRadius = true;

  [ObservableProperty]
  private bool _displayTarget = true;

  [ObservableProperty]
  private bool _displayTooltip;

  [ObservableProperty]
  private bool _betaUpdates;

  [ObservableProperty]
  private bool _passwordProtect;

  [ObservableProperty]
  private bool _showAirports = true;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(AdsbSettingsVisible))]
  private bool _enableAdsb;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(AdsbTcpPortVisible))]
  private string _adsbServer = ExternalAdsbOptions.DefaultServer;

  [ObservableProperty]
  private int _adsbPort = ExternalAdsbOptions.DefaultPort;

  [ObservableProperty]
  private string _adsbStatus = "external ADS-B disabled";

  public bool AdsbSettingsVisible => EnableAdsb;

  public bool AdsbTcpPortVisible => !System.Uri.TryCreate(
      ExternalAdsbOptions.NormalizeServer(AdsbServer), System.UriKind.Absolute, out var uri)
      || (uri.Scheme != System.Uri.UriSchemeHttp && uri.Scheme != System.Uri.UriSchemeHttps);

  [ObservableProperty]
  private bool _noRcReceiver;

  [ObservableProperty]
  private bool _showTfr;

  [ObservableProperty]
  private bool _autoParamCommit;

  [ObservableProperty]
  private bool _showNoFly;

  [ObservableProperty]
  private bool _showHongKongNoFly;

  [ObservableProperty]
  private bool _paramsBg;

  [ObservableProperty]
  private bool _slowMachine;

  [ObservableProperty]
  private bool _gdiPlus;

  [ObservableProperty]
  private bool _analyticsOptOut;

  [ObservableProperty]
  private int _telemAttitude = 4;

  [ObservableProperty]
  private int _telemPosition = 2;

  [ObservableProperty]
  private int _telemModeStatus = 2;

  [ObservableProperty]
  private int _telemRc = 2;

  [ObservableProperty]
  private int _telemSensor = 2;

  [ObservableProperty]
  private int _trackLength = 200;

  [ObservableProperty]
  private int _lineLength = 500;

  [ObservableProperty]
  private int _gcsId = 255;

  public ConfigPlannerViewModel() {
    Load();
    AudioVario.StateChanged += OnVarioStateChanged;
    DisplayViewService.Changed += OnDisplayViewChanged;
    MissionPlanner.Services.Speech.StatusChanged += HandleSpeechStatusChanged;
    RefreshVarioState();
  }

  private void Load() {
    _loading = true;
    var s = Settings.Instance;

    DistUnits = s["distunits"] ?? DistUnits;
    AltUnits = s["altunits"] ?? AltUnits;
    SpeedUnits = s["speedunits"] ?? SpeedUnits;
    Theme = MissionPlanner.Services.ThemeService.Current;
    Layout = DisplayViewService.Current.displayName.ToString();
    LayoutSelectorVisible = DisplayViewService.Current.displayPlannerLayout;
    Language = LocalizationService.DisplayForSetting(
        s["language"], System.Globalization.CultureInfo.CurrentUICulture);
    SpeechLevel = s["speechlevel"] ?? SpeechLevel;

    int sev = s.GetInt32("severity", 4);
    if (sev >= 0 && sev < SeverityOptions.Count) {
      Severity = SeverityOptions[sev];
    }

    MapCache = MapTileSourceFactory.NormalizeAccessMode(s["mapCache"]);
    SecondaryDisplayStyle = s.GetString("GMapMarkerBase_InactiveDisplayStyle", SecondaryDisplayStyle);
    OsdColor = s["hudcolor"] ?? OsdColor;
    LogDir = s.LogDir;

    EnableSpeech = s.GetBoolean("speechenable", EnableSpeech);
    SpeechArmedOnly = s.GetBoolean("speech_armed_only", SpeechArmedOnly);
    SpeechWaypoint = s.GetBoolean("speechwaypointenabled", SpeechWaypoint);
    SpeechMode = s.GetBoolean("speechmodeenabled", SpeechMode);
    SpeechCustom = s.GetBoolean("speechcustomenabled", SpeechCustom);
    SpeechBattery = s.GetBoolean("speechbatteryenabled", SpeechBattery);
    SpeechAltWarning = s.GetBoolean("speechaltenabled", SpeechAltWarning);
    SpeechArmDisarm = s.GetBoolean("speecharmenabled", SpeechArmDisarm);
    SpeechLowSpeed = s.GetBoolean("speechlowspeedenabled", SpeechLowSpeed);

    EnableHudOverlay = s.GetBoolean("CHK_hudshow", EnableHudOverlay);
    EnableFlightCommandShortcuts = s.GetBoolean(
        FlightCommandShortcuts.EnabledSettingKey, EnableFlightCommandShortcuts);
    LoadWaypointsOnConnect = s.GetBoolean("loadwpsonconnect", LoadWaypointsOnConnect);
    StartupUdpListenerOptions startupUdp = StartupUdpListenerOptions.Load(s);
    StartupUdpListenersEnabled = startupUdp.Enabled;
    StartupUdpPrimaryPort = startupUdp.PrimaryPort;
    StartupUdpAlternatePort = startupUdp.AlternatePort;
    DisplayInFlightData = s.GetBoolean("CHK_disttohomeflightdata", DisplayInFlightData);
    MapFollowPlane = s.GetBoolean("CHK_maprotation", MapFollowPlane);
    ResetOnUsbConnect = s.GetBoolean("CHK_resetapmonconnect", ResetOnUsbConnect);
    RtsResetEsp32 = s.GetBoolean("CHK_rtsresetesp32", RtsResetEsp32);

    DisplayCog = s.GetBoolean("GMapMarkerBase_DisplayCOG", DisplayCog);
    DisplayHeading = s.GetBoolean("GMapMarkerBase_DisplayHeading", DisplayHeading);
    DisplayNavBearing = s.GetBoolean("GMapMarkerBase_DisplayNavBearing", DisplayNavBearing);
    DisplayRadius = s.GetBoolean("GMapMarkerBase_DisplayRadius", DisplayRadius);
    DisplayTarget = s.GetBoolean("GMapMarkerBase_DisplayTarget", DisplayTarget);
    DisplayTooltip = s.GetString("mapicondesc", "") != "";

    BetaUpdates = s.GetBoolean("beta_updates", BetaUpdates);
    PasswordProtect = s.GetBoolean("password_protect", PasswordProtect);
    ShowAirports = s.GetBoolean("showairports", ShowAirports);
    AdsbServer = s.GetString("adsbserver", ExternalAdsbOptions.DefaultServer);
    AdsbPort = s.GetInt32("adsbport", ExternalAdsbOptions.DefaultPort);
    EnableAdsb = s.GetBoolean("enableadsb", EnableAdsb);
    NoRcReceiver = s.GetBoolean("norcreceiver", NoRcReceiver);
    ShowTfr = s.GetBoolean("showtfr", ShowTfr);
    AutoParamCommit = s.GetBoolean("autoParamCommit", AutoParamCommit);
    ShowNoFly = s.GetBoolean("ShowNoFly", ShowNoFly);
    ShowHongKongNoFly = s.GetBoolean("hknfzforceshow", ShowHongKongNoFly);
    ParamsBg = s.GetBoolean("Params_BG", ParamsBg);
    SlowMachine = s.GetBoolean("SlowMachine", SlowMachine);
    GdiPlus = s.GetBoolean("CHK_GDIPlus", GdiPlus);
    AnalyticsOptOut = s.GetBoolean("analyticsoptout", AnalyticsOptOut);

    TelemAttitude = s.GetInt32("CMB_rateattitude", TelemAttitude);
    TelemPosition = s.GetInt32("CMB_rateposition", TelemPosition);
    TelemModeStatus = s.GetInt32("CMB_ratestatus", TelemModeStatus);
    TelemRc = s.GetInt32("CMB_raterc", TelemRc);
    TelemSensor = s.GetInt32("CMB_ratesensors", TelemSensor);

    TrackLength = s.GetInt32("NUM_tracklength", TrackLength);
    LineLength = s.GetInt32("GMapMarkerBase_Length", LineLength);
    GcsId = s.ContainsKey("gcsid")
        ? s.GetInt32("gcsid", MAVLinkInterface.gcssysid)
        : s.GetInt32("GCS_sysid", MAVLinkInterface.gcssysid);

    _loading = false;
  }

  [RelayCommand]
  private async System.Threading.Tasks.Task ToggleVario() {
    if (AudioVario.IsRunning) {
      await System.Threading.Tasks.Task.Run(AudioVario.Stop);
    } else {
      await System.Threading.Tasks.Task.Run(AudioVario.Start);
    }
    RefreshVarioState();
  }

  private void OnVarioStateChanged(object? sender, System.EventArgs e) =>
      Avalonia.Threading.Dispatcher.UIThread.Post(RefreshVarioState);

  private void RefreshVarioState() {
    VarioButtonText = AudioVario.IsRunning ? "Stop Vario" : "Start Vario";
    VarioStatus = AudioVario.Status;
  }

  private void OnDisplayViewChanged(object? sender, System.EventArgs e) =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        _loading = true;
        Layout = DisplayViewService.Current.displayName.ToString();
        LayoutSelectorVisible = DisplayViewService.Current.displayPlannerLayout;
        _loading = false;
      });

  public void Dispose() {
    AudioVario.StateChanged -= OnVarioStateChanged;
    DisplayViewService.Changed -= OnDisplayViewChanged;
    MissionPlanner.Services.Speech.StatusChanged -= HandleSpeechStatusChanged;
  }

  private void HandleSpeechStatusChanged(string status) =>
      Avalonia.Threading.Dispatcher.UIThread.Post(() => SpeechStatus = status);

  [RelayCommand]
  private void TestSpeech() {
    if (!EnableSpeech) {
      SpeechStatus = "Enable Speech before running the test.";
      return;
    }
    MissionPlanner.Services.Speech.Speak("Проверка звука Mission Planner 10");
    SpeechStatus = MissionPlanner.Services.Speech.LastStatus;
  }

  [RelayCommand]
  private async System.Threading.Tasks.Task RerequestParams() {
    if (_comPort.BaseStream?.IsOpen != true) {
      return;
    }

    try {
      await AppState.ParameterLoads.LoadLatestAsync(
          _comPort.MAV.sysid, _comPort.MAV.compid);
      AppState.RaiseConnectionChanged();
    } catch {

    }
  }

  partial void OnDistUnitsChanged(string value) {
    if (_loading) return;
    Settings.Instance["distunits"] = value;
    AppState.ApplyUnits();
  }

  partial void OnAltUnitsChanged(string value) {
    if (_loading) return;
    Settings.Instance["altunits"] = value;
    AppState.ApplyUnits();
  }

  partial void OnSpeedUnitsChanged(string value) {
    if (_loading) return;
    Settings.Instance["speedunits"] = value;
    AppState.ApplyUnits();
  }

  partial void OnThemeChanged(string value) {
    if (_loading) return;
    MissionPlanner.Services.ThemeService.Apply(value);
  }

  partial void OnLayoutChanged(string value) {
    if (_loading) return;
    if (System.Enum.TryParse(value, ignoreCase: true, out DisplayNames name)) {
      DisplayViewService.SetPreset(name);
    }
  }

  partial void OnLanguageChanged(string value) {
    if (_loading) return;
    if (!LocalizationService.TryCultureForDisplay(value, out var culture)) return;
    Settings.Instance["language"] = culture.Name;
    LanguageNote = "Language change requires a restart to take effect.";
  }

  partial void OnSpeechLevelChanged(string value) {
    if (_loading) return;
    Settings.Instance["speechlevel"] = value;
  }

  partial void OnSeverityChanged(string value) {
    if (_loading) return;
    int idx = SeverityOptions.IndexOf(value);
    if (idx < 0) idx = 4;
    Settings.Instance["severity"] = idx.ToString();
  }

  partial void OnMapCacheChanged(string value) {
    if (_loading) return;
    MapTileSourceFactory.SetAccessMode(value);
  }

  partial void OnSecondaryDisplayStyleChanged(string value) {
    if (_loading) return;
    Settings.Instance["GMapMarkerBase_InactiveDisplayStyle"] = value;
  }

  partial void OnOsdColorChanged(string value) {
    if (_loading) return;
    Settings.Instance["hudcolor"] = value;
  }

  partial void OnLogDirChanged(string value) {
    if (_loading) return;
    if (!string.IsNullOrEmpty(value) && System.IO.Directory.Exists(value)) {
      Settings.Instance.LogDir = value;
    }
  }

  partial void OnEnableSpeechChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speechenable"] = value.ToString();
    MissionPlanner.Services.Speech.Enabled = value;
  }

  partial void OnSpeechArmedOnlyChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speech_armed_only"] = value.ToString();
  }

  private static async System.Threading.Tasks.Task PromptTemplate(
      string key, string title, string fallback) {
    var current = Settings.Instance[key] ?? fallback;
    var text = await Services.Dialogs.InputBox(title, "What do you want it to say?", current);
    if (!string.IsNullOrEmpty(text)) {
      Settings.Instance[key] = text;
    } else if (Settings.Instance[key] == null) {
      Settings.Instance[key] = fallback;
    }
  }

  private static async System.Threading.Tasks.Task PromptNumber(
      string key, string title, string prompt, string fallback) {
    var current = Settings.Instance[key] ?? fallback;
    var text = await Services.Dialogs.InputBox(title, prompt, current);
    if (!string.IsNullOrEmpty(text) &&
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)) {
      Settings.Instance[key] = text;
    } else if (Settings.Instance[key] == null) {
      Settings.Instance[key] = fallback;
    }
  }

  partial void OnSpeechWaypointChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speechwaypointenabled"] = value.ToString();
    if (value) {
      _ = PromptTemplate("speechwaypoint", "Waypoint", "Heading to Waypoint {wpn}");
    }
  }

  partial void OnSpeechModeChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speechmodeenabled"] = value.ToString();
    if (value) {
      _ = PromptTemplate("speechmode", "Mode", "Mode changed to {mode}");
    }
  }

  partial void OnSpeechCustomChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speechcustomenabled"] = value.ToString();
    if (value) {
      _ = PromptTemplate("speechcustom", "Custom",
          "Heading to Waypoint {wpn}, altitude is {alt}, Ground speed is {gsp} ");
    }
  }

  partial void OnSpeechBatteryChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speechbatteryenabled"] = value.ToString();
    if (value) {
      _ = ConfigureBatterySpeechAsync();
    }
  }

  private static async System.Threading.Tasks.Task ConfigureBatterySpeechAsync() {
    await PromptTemplate("speechbattery", "Battery",
        "WARNING, Battery at {batv} Volt, {batp} percent");
    await PromptNumber("speechbatteryvolt", "Battery Level",
        "What Voltage do you want to warn at?", "9.6");
    await PromptNumber("speechbatterypercent", "Battery Level",
        "What percentage do you want to warn at?", "20");
  }

  partial void OnSpeechAltWarningChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speechaltenabled"] = value.ToString();
    if (value) {
      _ = ConfigureAltSpeechAsync();
    }
  }

  private static async System.Threading.Tasks.Task ConfigureAltSpeechAsync() {
    await PromptTemplate("speechalt", "Altitude Warning", "WARNING, low altitude {alt}");
    // Stored in raw metres (matching upstream); the prompt shows and accepts display units.
    var storedMetres = Settings.Instance.GetFloat("speechaltheight", 2f / CurrentState.multiplieralt);
    var current = (storedMetres * CurrentState.multiplieralt)
        .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    var text = await Services.Dialogs.InputBox("Altitude Warning",
        $"What altitude do you want to warn at ({CurrentState.AltUnit})?", current);
    if (!string.IsNullOrEmpty(text) &&
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var alt)) {
      Settings.Instance["speechaltheight"] = (alt / CurrentState.multiplieralt)
          .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
  }

  partial void OnSpeechArmDisarmChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speecharmenabled"] = value.ToString();
    if (value) {
      _ = ConfigureArmSpeechAsync();
    }
  }

  private static async System.Threading.Tasks.Task ConfigureArmSpeechAsync() {
    await PromptTemplate("speecharm", "Arm", "Armed");
    await PromptTemplate("speechdisarm", "Disarmed", "Disarmed");
  }

  partial void OnSpeechLowSpeedChanged(bool value) {
    if (_loading) return;
    Settings.Instance["speechlowspeedenabled"] = value.ToString();
    if (value) {
      _ = ConfigureLowSpeedSpeechAsync();
    }
  }

  private static async System.Threading.Tasks.Task ConfigureLowSpeedSpeechAsync() {
    await PromptTemplate("speechlowgroundspeed", "Ground Speed", "Low Ground Speed {gsp}");
    await PromptNumber("speechlowgroundspeedtrigger", "Speed trigger",
        "What speed do you want to warn at (m/s)?", "0");
    await PromptTemplate("speechlowairspeed", "Air Speed", "Low Air Speed {asp}");
    await PromptNumber("speechlowairspeedtrigger", "Speed trigger",
        "What speed do you want to warn at (m/s)?", "0");
  }

  partial void OnEnableHudOverlayChanged(bool value) {
    if (_loading) return;
    Settings.Instance["CHK_hudshow"] = value.ToString();
  }

  partial void OnEnableFlightCommandShortcutsChanged(bool value) {
    if (_loading) return;
    if (!value) {
      Settings.Instance[FlightCommandShortcuts.EnabledSettingKey] = false.ToString();
      return;
    }

    // A checked box must never enable global vehicle commands before the reject-by-default
    // warning has been explicitly accepted.
    _loading = true;
    EnableFlightCommandShortcuts = false;
    _loading = false;
    if (!_enablingFlightCommandShortcuts) {
      _ = ConfirmEnableFlightCommandShortcutsAsync();
    }
  }

  private async System.Threading.Tasks.Task ConfirmEnableFlightCommandShortcutsAsync() {
    _enablingFlightCommandShortcuts = true;
    try {
      bool accepted = await Dialogs.ConfirmDangerous(
          "Enable Flight Command Shortcuts",
          "Enable the official Shortcuts-plugin commands globally?\n\n"
          + "Alt+A AUTO; Alt+G LOITER; Alt+U ALT HOLD; Alt+S STABILIZE; Alt+H RTL; "
          + "Alt+T TAKEOFF 2 m; Alt+L LAND; Alt+0 throttle channel 3 to 1000 us.\n\n"
          + "Every use still requires a separate reject-by-default confirmation and fresh "
          + "telemetry from the exact selected modem/vehicle. Takeoff, LAND and throttle "
          + "override are blocked while disarmed.",
          "Enable Shortcuts");
      _loading = true;
      try {
        EnableFlightCommandShortcuts = accepted;
        Settings.Instance[FlightCommandShortcuts.EnabledSettingKey] = accepted.ToString();
      } finally {
        _loading = false;
      }
    } finally {
      _enablingFlightCommandShortcuts = false;
    }
  }

  partial void OnLoadWaypointsOnConnectChanged(bool value) {
    if (_loading) return;
    Settings.Instance["loadwpsonconnect"] = value.ToString();
  }

  partial void OnStartupUdpListenersEnabledChanged(bool value) {
    if (_loading) return;
    Settings.Instance[StartupUdpListenerService.EnabledSettingKey] = value.ToString();
  }

  partial void OnStartupUdpPrimaryPortChanged(int value) {
    if (_loading) return;
    int normalized = StartupUdpListenerOptions.NormalizePort(
        value, StartupUdpListenerOptions.DefaultPrimaryPort);
    Settings.Instance[StartupUdpListenerService.PrimaryPortSettingKey] =
        normalized.ToString(System.Globalization.CultureInfo.InvariantCulture);
  }

  partial void OnStartupUdpAlternatePortChanged(int value) {
    if (_loading) return;
    int normalized = StartupUdpListenerOptions.NormalizePort(
        value, StartupUdpListenerOptions.DefaultAlternatePort);
    Settings.Instance[StartupUdpListenerService.AlternatePortSettingKey] =
        normalized.ToString(System.Globalization.CultureInfo.InvariantCulture);
  }

  partial void OnDisplayInFlightDataChanged(bool value) {
    if (_loading) return;
    Settings.Instance["CHK_disttohomeflightdata"] = value.ToString();
  }

  partial void OnMapFollowPlaneChanged(bool value) {
    if (_loading) return;
    Settings.Instance["CHK_maprotation"] = value.ToString();

    if (value) ShowNoFly = false;
  }

  partial void OnResetOnUsbConnectChanged(bool value) {
    if (_loading) return;
    Settings.Instance["CHK_resetapmonconnect"] = value.ToString();
  }

  partial void OnRtsResetEsp32Changed(bool value) {
    if (_loading) return;
    Settings.Instance["CHK_rtsresetesp32"] = value.ToString();
  }

  partial void OnDisplayCogChanged(bool value) {
    if (_loading) return;
    Settings.Instance["GMapMarkerBase_DisplayCOG"] = value.ToString();
  }

  partial void OnDisplayHeadingChanged(bool value) {
    if (_loading) return;
    Settings.Instance["GMapMarkerBase_DisplayHeading"] = value.ToString();
  }

  partial void OnDisplayNavBearingChanged(bool value) {
    if (_loading) return;
    Settings.Instance["GMapMarkerBase_DisplayNavBearing"] = value.ToString();
  }

  partial void OnDisplayRadiusChanged(bool value) {
    if (_loading) return;
    Settings.Instance["GMapMarkerBase_DisplayRadius"] = value.ToString();
  }

  partial void OnDisplayTargetChanged(bool value) {
    if (_loading) return;
    Settings.Instance["GMapMarkerBase_DisplayTarget"] = value.ToString();
  }

  partial void OnDisplayTooltipChanged(bool value) {
    if (_loading) return;
    Settings.Instance["mapicondesc"] = value ? _defaultMapIconDesc : "";
  }

  partial void OnBetaUpdatesChanged(bool value) {
    if (_loading) return;
    Settings.Instance["beta_updates"] = value.ToString();
  }

  partial void OnPasswordProtectChanged(bool value) {
    if (_loading) return;
    _ = ApplyPasswordProtectAsync(value);
  }

  private async System.Threading.Tasks.Task ApplyPasswordProtectAsync(bool value) {
    if (value) {
      var pw = await Services.Dialogs.PasswordInputBox("Password Protect",
          "Enter a new password for the Setup and Config screens");
      if (string.IsNullOrEmpty(pw)) {
        _loading = true;
        PasswordProtect = false;
        _loading = false;
        return;
      }
      Password.EnterPassword(pw);
    }
    Settings.Instance["password_protect"] = value.ToString();
  }

  partial void OnShowAirportsChanged(bool value) {
    if (_loading) return;
    Settings.Instance["showairports"] = value.ToString();
  }

  partial void OnEnableAdsbChanged(bool value) {
    if (_loading) return;
    Settings.Instance["enableadsb"] = value.ToString();
    _ = ApplyAdsbSettingsAsync();
  }

  partial void OnAdsbServerChanged(string value) {
    if (_loading) return;
    Settings.Instance["adsbserver"] = ExternalAdsbOptions.NormalizeServer(value);
    if (EnableAdsb) {
      _ = ApplyAdsbSettingsAsync();
    }
  }

  partial void OnAdsbPortChanged(int value) {
    if (_loading) return;
    Settings.Instance["adsbport"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    if (EnableAdsb) {
      _ = ApplyAdsbSettingsAsync();
    }
  }

  private async System.Threading.Tasks.Task ApplyAdsbSettingsAsync() {
    try {
      AdsbStatus = EnableAdsb ? "starting external ADS-B…" : "external ADS-B disabled";
      await AppState.Traffic.ConfigureExternalAsync(EnableAdsb, AdsbServer, AdsbPort);
      AdsbStatus = AppState.Traffic.ExternalStatus;
    } catch (System.Exception ex) {
      AdsbStatus = $"external ADS-B error: {ex.Message}";
    }
  }

  partial void OnNoRcReceiverChanged(bool value) {
    if (_loading) return;
    Settings.Instance["norcreceiver"] = value.ToString();
  }

  partial void OnShowTfrChanged(bool value) {
    if (_loading) return;
    Settings.Instance["showtfr"] = value.ToString();
  }

  partial void OnAutoParamCommitChanged(bool value) {
    if (_loading) return;
    Settings.Instance["autoParamCommit"] = value.ToString();
  }

  partial void OnShowNoFlyChanged(bool value) {
    if (_loading) return;
    Settings.Instance["ShowNoFly"] = value.ToString();
    Services.NoFlyOverlay.NotifyVisibilityChanged();

    if (value) MapFollowPlane = false;
  }

  partial void OnShowHongKongNoFlyChanged(bool value) {
    if (_loading) return;
    Settings.Instance["hknfzforceshow"] = value.ToString();
    Services.NoFlyOverlay.NotifyVisibilityChanged();
  }

  partial void OnParamsBgChanged(bool value) {
    if (_loading) return;
    Settings.Instance["Params_BG"] = value.ToString();
  }

  partial void OnSlowMachineChanged(bool value) {
    if (_loading) return;
    Settings.Instance["SlowMachine"] = value.ToString();
  }

  partial void OnGdiPlusChanged(bool value) {
    if (_loading) return;
    Settings.Instance["CHK_GDIPlus"] = value.ToString();
  }

  partial void OnAnalyticsOptOutChanged(bool value) {
    if (_loading) return;
    Settings.Instance["analyticsoptout"] = value.ToString();
  }

  partial void OnTelemAttitudeChanged(int value) {
    if (_loading) return;
    Settings.Instance["CMB_rateattitude"] = value.ToString();
    _comPort.MAV.cs.rateattitude = value;
    CurrentState.rateattitudebackup = value;
    if (_comPort.BaseStream?.IsOpen == true) {
      _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA1, value);
      _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA2, value);
    }
  }

  partial void OnTelemPositionChanged(int value) {
    if (_loading) return;
    Settings.Instance["CMB_rateposition"] = value.ToString();
    _comPort.MAV.cs.rateposition = value;
    CurrentState.ratepositionbackup = value;
    if (_comPort.BaseStream?.IsOpen == true) {
      _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, value);
    }
  }

  partial void OnTelemModeStatusChanged(int value) {
    if (_loading) return;
    Settings.Instance["CMB_ratestatus"] = value.ToString();
    _comPort.MAV.cs.ratestatus = value;
    CurrentState.ratestatusbackup = value;
    if (_comPort.BaseStream?.IsOpen == true) {
      _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTENDED_STATUS, value);
    }
  }

  partial void OnTelemRcChanged(int value) {
    if (_loading) return;
    Settings.Instance["CMB_raterc"] = value.ToString();
    _comPort.MAV.cs.raterc = value;
    CurrentState.ratercbackup = value;
    if (_comPort.BaseStream?.IsOpen == true) {
      _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.RC_CHANNELS, value);
    }
  }

  partial void OnTelemSensorChanged(int value) {
    if (_loading) return;
    Settings.Instance["CMB_ratesensors"] = value.ToString();
    _comPort.MAV.cs.ratesensors = value;
    CurrentState.ratesensorsbackup = value;
    if (_comPort.BaseStream?.IsOpen == true) {
      _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA3, value);
      _comPort.requestDatastream(MAVLink.MAV_DATA_STREAM.RAW_SENSORS, value);
    }
  }

  partial void OnTrackLengthChanged(int value) {
    if (_loading) return;
    Settings.Instance["NUM_tracklength"] = value.ToString();
  }

  partial void OnLineLengthChanged(int value) {
    if (_loading) return;
    Settings.Instance["GMapMarkerBase_Length"] = value.ToString();
  }

  partial void OnGcsIdChanged(int value) {
    if (_loading) return;
    MAVLinkInterface.gcssysid = (byte)value;
    Settings.Instance["gcsid"] = value.ToString();
  }
}
