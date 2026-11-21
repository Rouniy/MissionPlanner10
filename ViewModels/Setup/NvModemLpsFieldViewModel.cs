using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Services;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;
using Value = MissionPlanner.Services.Nv5ConfigurationClient.Value;

namespace MissionPlanner.ViewModels.Setup;

/// <summary>A choice of a list in the LPS field window: its native value, how it reads, and why it
/// cannot be chosen, when it cannot.</summary>
public sealed record NvLpsChoice(int Value, string Label, string Reason = "", string Tip = "") {
  public bool IsEnabled => Reason.Length == 0;
  public string? TipText => Reason.Length != 0 ? Reason : Tip.Length != 0 ? Tip : null;
  public override string ToString() => Label;
}

/// <summary>A modem whose saved field the Field tab can show.</summary>
public sealed partial class NvLpsSource : ObservableObject {
  internal NvLpsSource(int index, string label) {
    Index = index;
    Label = label;
  }

  internal int Index { get; }
  public string Label { get; }

  /// <summary>Only a modem whose settings are read holds a field to show.</summary>
  [ObservableProperty]
  private bool _isEnabled;

  public override string ToString() => Label;
}

/// <summary>One surveyed beacon of the Field tab, its values as the operator reads them.</summary>
public sealed partial class NvLpsBeaconRow : ObservableObject {
  internal static readonly string[] Properties = [nameof(Id), nameof(Address), nameof(East),
      nameof(North), nameof(Up), nameof(SigmaEast), nameof(SigmaNorth), nameof(SigmaUp),
      nameof(BiasSigma), nameof(EastNorth), nameof(EastUp), nameof(NorthUp)];

  private readonly string[] _cells = Enumerable.Repeat("", Properties.Length).ToArray();
  private readonly Action<int, int, string> _edited;
  private readonly int _row;

  internal NvLpsBeaconRow(int row, Action<int, int, string> edited) {
    _row = row;
    _edited = edited;
    Name = $"B{row + 1}";
  }

  public string Name { get; }

  [ObservableProperty]
  private bool _isShown = true;

  public string Id { get => _cells[0]; set => Edit(0, value); }
  public string Address { get => _cells[1]; set => Edit(1, value); }
  public string East { get => _cells[2]; set => Edit(2, value); }
  public string North { get => _cells[3]; set => Edit(3, value); }
  public string Up { get => _cells[4]; set => Edit(4, value); }
  public string SigmaEast { get => _cells[5]; set => Edit(5, value); }
  public string SigmaNorth { get => _cells[6]; set => Edit(6, value); }
  public string SigmaUp { get => _cells[7]; set => Edit(7, value); }
  public string BiasSigma { get => _cells[8]; set => Edit(8, value); }
  public string EastNorth { get => _cells[9]; set => Edit(9, value); }
  public string EastUp { get => _cells[10]; set => Edit(10, value); }
  public string NorthUp { get => _cells[11]; set => Edit(11, value); }

  /// <summary>A cell as a column of the table shows it.</summary>
  internal string Cell(int column) => _cells[column];

  internal void Show(int column, string text) {
    if (_cells[column] != text) {
      _cells[column] = text;
      OnPropertyChanged(Properties[column]);
    }
  }

  private void Edit(int column, string value) {
    if (value != _cells[column]) {
      _edited(_row, column, value ?? "");
    }
    // The cell shows what the field holds now, whether the edit was taken or not.
    OnPropertyChanged(Properties[column]);
  }
}

/// <summary>A modem of the LPS field window: its role in the field, and its settings.</summary>
public sealed partial class NvLpsModem : ObservableObject {
  private readonly Action<NvLpsModem, string> _changed;

  internal NvLpsModem(int index, string name, string address, Nv5NativeSettingsEditor editor,
      Action<NvLpsModem, string> changed) {
    Index = index;
    Name = name;
    _address = address;
    Editor = editor;
    _changed = changed;
    _role = Roles[0];
    _radio = Radios[0];
    _beacon = Beacons[0];
  }

  internal int Index { get; }
  public string Name { get; }
  public Nv5NativeSettingsEditor Editor { get; }

  /// <summary>Where the modem is reached, as its tip.</summary>
  [ObservableProperty]
  private string _address;

  public ObservableCollection<NvLpsChoice> Roles { get; } =
      [new(0, "Off"), new(1, "Rover"), new(2, "Beacon")];

  public ObservableCollection<NvLpsChoice> Radios { get; } =
      [new(1, "Radio 1"), new(2, "Radio 2")];

  public ObservableCollection<NvLpsChoice> Beacons { get; } = [new(0, "—")];

  [ObservableProperty]
  private NvLpsChoice? _role;

  [ObservableProperty]
  private NvLpsChoice? _radio;

  [ObservableProperty]
  private NvLpsChoice? _beacon;

  /// <summary>What saving does to this modem, or how its save went.</summary>
  [ObservableProperty]
  private string _line = "Reading its settings…";

  [ObservableProperty]
  private bool _lineIsProblem;

  /// <summary>The role controls are shown once the modem's LPS settings are read.</summary>
  [ObservableProperty]
  private bool _loadedShown;

  [ObservableProperty]
  private bool _radioShown;

  [ObservableProperty]
  private bool _beaconShown;

  [ObservableProperty]
  private bool _editable;

  internal int RoleValue => Role?.Value ?? 0;

  // Rover-only settings as native text, kept while another modem is the rover.
  internal Dictionary<string, string> Rover { get; } = new(StringComparer.Ordinal);

  // Survey row this modem answers for (0: none), and the ID it held when read.
  internal int BeaconRow { get; set; }
  internal string BeaconReadId { get; set; } = "";
  internal bool Loaded { get; set; }
  internal bool Durable { get; set; }
  internal bool Reread { get; set; }

  /// <summary>The commit was sent: until the modem is read again, whether it saved is
  /// unknown.</summary>
  internal bool Committing { get; set; }

  /// <summary>What this save asked the modem to hold, to tell afterwards whether it does.</summary>
  internal Dictionary<string, string> Staged { get; set; } = new(StringComparer.Ordinal);

  internal string Outcome { get; set; } = "";
  internal byte[] ExpectedDigest { get; set; } = [];
  internal IReadOnlyDictionary<string, Value> Frozen { get; set; } =
      new Dictionary<string, Value>(StringComparer.Ordinal);

  partial void OnRoleChanged(NvLpsChoice? value) => _changed(this, nameof(Role));
  partial void OnRadioChanged(NvLpsChoice? value) => _changed(this, nameof(Radio));
  partial void OnBeaconChanged(NvLpsChoice? value) => _changed(this, nameof(Beacon));
}

/// <summary>
/// One surveyed LPS field and each modem's role in it. Every changed modem is checked, saved and
/// read back on its own, beacons before the rover; the group is not one atomic transaction, so a
/// modem saved earlier keeps its new settings if a later one fails.
/// </summary>
public sealed partial class NvModemLpsFieldViewModel : ObservableObject {
  internal const int MaximumModems = 16;
  internal const int FirstSigmaColumn = 5;
  internal const int FirstCovarianceColumn = 9;

  /// <summary>Survey row settings in the order the beacon table shows them.</summary>
  internal static readonly IReadOnlyList<(string Suffix, string Title, string Tip)> Columns = [
    ("ID", "ID", "Survey identity of the beacon. Each beacon modem answers for one ID."),
    ("ADDR", "Address", "Nonzero LR2021 ranging address of the beacon in hexadecimal, unique in "
        + "the field."),
    ("E_MM", "East (m)", "Surveyed position east of the field origin."),
    ("N_MM", "North (m)", "Surveyed position north of the field origin."),
    ("U_MM", "Up (m)", "Surveyed antenna height above the field origin."),
    ("CEE", "σE (m)", "Survey uncertainty east. The modem stores its square."),
    ("CNN", "σN (m)", "Survey uncertainty north. The modem stores its square."),
    ("CUU", "σU (m)", "Survey uncertainty up. The modem stores its square."),
    ("BVAR", "Bias σ (m)", "Uncertainty of this beacon's range bias. The modem stores its square."),
    ("CEN", "EN (m²)", "Survey covariance of east and north, in square metres."),
    ("CEU", "EU (m²)", "Survey covariance of east and up, in square metres."),
    ("CNU", "NU (m²)", "Survey covariance of north and up, in square metres."),
  ];

  private static readonly int[] Bandwidths = [125000, 250000, 500000, 1000000];
  private static readonly string[] RoverKeys = ["LPS_FC_SYS", "LPS_FC_COMP", "LPS_Z_MM",
      "LPS_TIE_VAR", "LPS_RNG_VAR"];
  private const string OnlyOneRover = "Only one modem can be the rover.";
  private const string ReadingTheModems = "Reading the modems…";

  private enum FieldState {
    Ready,
    Saving,
    Done,
    Failed,
  }

  /// <summary>What saving would do now: the modems that change, their values, and what blocks
  /// it.</summary>
  private sealed class Plan {
    internal List<int> Queue { get; } = [];
    internal Dictionary<int, SortedDictionary<string, string>> Candidates { get; } = [];
    internal List<(string Text, bool Problem)> Lines { get; } = [];
    internal List<string> Problems { get; } = [];
    internal uint Generation { get; set; }
    internal int Rover { get; set; } = -1;
    internal bool Participants { get; set; }

    /// <summary>A modem is still being read, so what it holds is not known yet.</summary>
    internal bool Waiting { get; set; }

    /// <summary>Modems whose line names a problem; any of them blocks the save.</summary>
    internal int NodeProblems { get; set; }
  }

  private readonly Func<Nv5NativeSettingsEditor> _createEditor;
  private readonly Func<object, bool> _routeOpen;
  private readonly SortedDictionary<string, string> _text = new(StringComparer.Ordinal);
  private FieldState _state = FieldState.Ready;
  private List<int> _queue = [];
  private List<int> _touched = [];
  private int _current = -1;
  private int _failed = -1;
  private int _fieldSource = -1;
  private int _roverShown = -1;
  private bool _waitReadback;
  private bool _fieldEdited;
  private bool _filling;

  // Set when Close waits for a save stopped before its commit to be cancelled on its modem.
  private bool _closePending;

  /// <param name="createEditor">Makes an editor that shares the page's snapshot store.</param>
  /// <param name="routeOpen">Whether a modem's route is still open; without it every route
  /// is.</param>
  internal NvModemLpsFieldViewModel(Func<Nv5NativeSettingsEditor> createEditor,
      Func<object, bool>? routeOpen = null) {
    _createEditor = createEditor;
    _routeOpen = routeOpen ?? (_ => true);
    foreach (string key in Nv5LpsField.Keys) {
      _text[key] = "0";
    }
    for (int row = 0; row < Nv5LpsField.MaximumBeacons; row++) {
      BeaconRows.Add(new NvLpsBeaconRow(row, TableEdited));
    }
    foreach (int hertz in Bandwidths) {
      string label = (hertz / 1000).ToString(CultureInfo.InvariantCulture) + " kHz";
      RangingBandwidths.Add(new NvLpsChoice(hertz, label));
      MeetingBandwidths.Add(new NvLpsChoice(hertz, label));
    }
    for (int factor = 5; factor <= 10; factor++) {
      string label = "SF" + factor.ToString(CultureInfo.InvariantCulture);
      RangingSpreadingFactors.Add(new NvLpsChoice(factor, label));
      MeetingSpreadingFactors.Add(new NvLpsChoice(factor, label));
    }
    ShowField();
  }

  /// <summary>A frame to send on the target's own link.</summary>
  internal event Action<Nv5ConfigurationFrame, Target>? SendFrame;

  /// <summary>A modem's commit was sent with these values; it restarts under the address they
  /// give it.</summary>
  internal event Action<Nv5NativeSettingsEditor, IReadOnlyDictionary<string, Value>>? CommitSent;

  /// <summary>A close that waited for unfinished saves to be cancelled can go ahead.</summary>
  internal event Action? CloseRequested;

  public string Explanation =>
      "Choose each modem's role, then check the surveyed field they share. Saving checks, saves "
      + "and restarts each changed modem on its own, beacons before the rover. A modem saved "
      + "earlier keeps its new settings if a later one fails.";

  public string TieHint =>
      "Saving the launch height does not tie the barometer. Tie it with +LPS_TIE in the modem's "
      + "configuration mode while the flight controller is disarmed and still.";

  public ObservableCollection<NvLpsModem> Modems { get; } = [];
  public ObservableCollection<NvLpsSource> FieldSources { get; } = [];
  public ObservableCollection<NvLpsBeaconRow> BeaconRows { get; } = [];

  /// <summary>The rows of the beacons in the survey, which the table shows.</summary>
  public ObservableCollection<NvLpsBeaconRow> ShownBeaconRows { get; } = [];
  public ObservableCollection<NvLpsChoice> BeaconCounts { get; } = [new(3, "3"), new(4, "4")];
  public ObservableCollection<NvLpsChoice> RangingBandwidths { get; } = [];
  public ObservableCollection<NvLpsChoice> RangingSpreadingFactors { get; } = [];
  public ObservableCollection<NvLpsChoice> MeetingBandwidths { get; } = [];
  public ObservableCollection<NvLpsChoice> MeetingSpreadingFactors { get; } = [];

  /// <summary>The LPS rows of the modem settings, one per modem.</summary>
  public ObservableCollection<NvDistanceRow> LpsRows { get; } = [];

  internal IReadOnlyList<Nv5NativeSettingsEditor> Editors =>
      Modems.Select(modem => modem.Editor).ToList();

  /// <summary>The survey as the form holds it, in exact native text.</summary>
  internal IReadOnlyDictionary<string, string> Field => _text;

  /// <summary>True while modems are being saved one after another.</summary>
  public bool Active => _state == FieldState.Saving;

  [ObservableProperty]
  private string _status = ReadingTheModems;

  [ObservableProperty]
  private string _applyText = "Nothing to save";

  [ObservableProperty]
  private string _applyTip = "";

  [ObservableProperty]
  private bool _canApply;

  [ObservableProperty]
  private string _problemsText = "";

  [ObservableProperty]
  private bool _problemsShown;

  [ObservableProperty]
  private bool _inputsEnabled = true;

  [ObservableProperty]
  private NvLpsSource? _selectedFieldSource;

  [ObservableProperty]
  private string _latitudeText = "";

  [ObservableProperty]
  private string _longitudeText = "";

  [ObservableProperty]
  private decimal? _ellipsoidHeight;

  [ObservableProperty]
  private decimal? _seaLevelHeight;

  [ObservableProperty]
  private string _datumText = "";

  [ObservableProperty]
  private NvLpsChoice? _selectedBeaconCount;

  [ObservableProperty]
  private string _generationText = "";

  [ObservableProperty]
  private bool _showCovariance;

  [ObservableProperty]
  private bool _radioSettingsShown;

  [ObservableProperty]
  private NvLpsChoice? _rangingBandwidth;

  [ObservableProperty]
  private NvLpsChoice? _rangingSpreadingFactor;

  [ObservableProperty]
  private bool _extendedRanging;

  [ObservableProperty]
  private decimal? _responseSymbols;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(MeetingCarrierEnabled))]
  private bool _meetOnDataCarrier = true;

  [ObservableProperty]
  private decimal? _meetingMhz = 868m;

  [ObservableProperty]
  private NvLpsChoice? _meetingBandwidth;

  [ObservableProperty]
  private NvLpsChoice? _meetingSpreadingFactor;

  [ObservableProperty]
  private string _roverName = "Rover settings apply once a modem is the rover.";

  [ObservableProperty]
  private bool _roverEnabled;

  [ObservableProperty]
  private bool _roverAdvancedShown;

  [ObservableProperty]
  private decimal? _flightControllerSystem = 1;

  [ObservableProperty]
  private decimal? _flightControllerComponent = 1;

  [ObservableProperty]
  private decimal? _launchHeight = 0;

  [ObservableProperty]
  private decimal? _launchSigma = 0;

  [ObservableProperty]
  private decimal? _rangeSigma = 0;

  [ObservableProperty]
  private string _burstText = "";

  public bool MeetingCarrierEnabled => InputsEnabled && !MeetOnDataCarrier;

  partial void OnInputsEnabledChanged(bool value) => OnPropertyChanged(nameof(MeetingCarrierEnabled));

  /// <summary>Adds a discovered modem, or follows one already listed; the first one offered is
  /// Modem 1.</summary>
  internal void Offer(Target target, uint uptime, string address) {
    if (!target.Verified || target.Uid is not { Length: 18 }
        || target.Product != Nv5ConfigurationClient.ProductKind.Modem) {
      return;
    }
    foreach (NvLpsModem known in Modems) {
      if (known.Editor.Target is { } current && current.Uid.AsSpan().SequenceEqual(target.Uid)) {
        // A second open link to the same modem would otherwise take it over, restarting its read
        // or failing its save: the modem moves only once its own route has closed.
        if (Equals(current.Route, target.Route) || !_routeOpen(current.Route)) {
          known.Editor.ObserveTarget(target, uptime);
          if (address.Length != 0) {
            known.Address = address;
          }
        }
        return;
      }
    }
    if (_state == FieldState.Saving || Modems.Count >= MaximumModems) {
      return;
    }
    int index = Modems.Count;
    string name = string.Create(CultureInfo.InvariantCulture,
        $"Modem {index + 1} · {target.System}:{target.Component}");
    Nv5NativeSettingsEditor editor = _createEditor();
    // The window shows each modem's LPS row, so each is asked whether its reports are current.
    editor.LpsChallenges = true;
    var modem = new NvLpsModem(index, name, address, editor, OnModemChanged);
    editor.SendFrame += (frame, sent) => SendFrame?.Invoke(frame, sent);
    editor.CommitSent += values => {
      if (_state == FieldState.Saving && _current == index) {
        modem.Committing = true;
      }
      CommitSent?.Invoke(editor, values);
    };
    editor.Prepared += (valid, _, _) => OnPrepared(index, valid);
    editor.DurableSaved += () => OnDurableSaved(index);
    editor.ConfigurationFinished += success => OnConfigurationFinished(index, success);
    Modems.Add(modem);
    FieldSources.Add(new NvLpsSource(index, name));
    LpsRows.Add(new NvDistanceRow($"Modem {index + 1}"));
    editor.ObserveTarget(target, uptime);
    Refresh();
  }

  internal void Receive(MAVLink.MAVLinkMessage message, object route) {
    foreach (NvLpsModem modem in Modems.ToList()) {
      modem.Editor.Receive(message, route);
    }
  }

  internal void Service() {
    foreach (NvLpsModem modem in Modems.ToList()) {
      modem.Editor.Service();
    }
    Refresh();
  }

  /// <summary>
  /// Closes unless a save is under way or a committed save has no outcome yet; frees each modem's
  /// read session. A save stopped before its commit is cancelled on its modem first, since a
  /// closed window sends nothing more and the modem would stay busy until its writer lease ran
  /// out; <see cref="CloseRequested"/> follows once it is.
  /// </summary>
  internal bool TryClose() {
    if (Active || Modems.Any(modem => modem.Editor.AwaitingReconnect)) {
      Status = "Wait until the modem being saved has finished.";
      return false;
    }
    var ending = new List<string>();
    foreach (NvLpsModem modem in Modems) {
      modem.Editor.Cancel("Not saved: the LPS field window is closing.");
      if (modem.Editor.Saving) {
        ending.Add(modem.Name);
      }
    }
    _closePending = ending.Count != 0;
    if (_closePending) {
      Status = ending.Count == 1
          ? $"Closing once {ending[0]} has cancelled its unfinished save…"
          : $"Closing once {string.Join(", ", ending)} have cancelled their unfinished saves…";
      return false;
    }
    foreach (NvLpsModem modem in Modems) {
      modem.Editor.Discard();
      // A modem serves one read at a time; one this window leaves half done is freed now.
      modem.Editor.StopReading();
    }
    return true;
  }

  /// <summary>Shows a survey in the form, as Import does; false with a reason when a setting is
  /// missing.</summary>
  internal bool SetField(IReadOnlyDictionary<string, string> values, out string error) {
    foreach (string key in Nv5LpsField.Keys) {
      if (!values.ContainsKey(key)) {
        error = $"This modem's firmware does not hold a complete LPS field ({key} is missing).";
        return false;
      }
    }
    // Shown and compared as the modem reads it back: an imported 0x3E8 is 1000 here.
    SortedDictionary<string, string> canonical = Nv5LpsField.Canonical(values);
    foreach (string key in Nv5LpsField.Keys) {
      _text[key] = canonical[key];
    }
    ShowField();
    error = "";
    return true;
  }

  /// <summary>Shows the field the modem chosen in the source list holds.</summary>
  internal void ShowSourceField() {
    if (SelectedFieldSource is not { } source || source.Index >= Modems.Count
        || !Modems[source.Index].Loaded || !InputsEnabled) {
      return;
    }
    NvLpsModem modem = Modems[source.Index];
    if (!SetField(Nv5LpsField.FromValues(modem.Editor.ReadValues), out string error)) {
      Status = error;
      return;
    }
    _fieldSource = source.Index;
    _fieldEdited = true;
    Status = $"Showing the field saved on {modem.Name}.";
    Refresh();
  }

  /// <summary>Writes the field shown as a field document.</summary>
  internal void ExportField(string path) {
    string error = Nv5LpsField.Validate(_text);
    if (error.Length != 0) {
      Status = $"Not exported. Field: {error}";
      return;
    }
    string temporary = path + ".part";
    try {
      File.WriteAllBytes(temporary, Nv5LpsField.ToJson(Nv5LpsField.Normalized(_text)));
      File.Move(temporary, path, overwrite: true);
      Status = "Field exported.";
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      try {
        File.Delete(temporary);
      } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) {
        // The partial document is left where it is; the status names the failure.
      }
      Status = "Could not write the field document.";
    }
  }

  /// <summary>Shows the field of a field document; nothing changes on the modems until they are
  /// saved.</summary>
  internal void ImportField(string path) {
    if (!InputsEnabled) {
      return;
    }
    byte[] bytes;
    try {
      var file = new FileInfo(path);
      if (!file.Exists || file.Length > 65536) {
        Status = "Cannot read the field document.";
        return;
      }
      bytes = File.ReadAllBytes(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      Status = "Cannot read the field document.";
      return;
    }
    if (!Nv5LpsField.FromJson(bytes, out SortedDictionary<string, string> values,
            out string error)) {
      Status = error;
      return;
    }
    SetField(values, out _);
    _fieldEdited = true;
    Status = "Field imported. Check the roles, then verify and save.";
    Refresh();
  }

  /// <summary>Saves every changed modem, one after another, beacons before the rover.</summary>
  internal void Apply() {
    if (_state != FieldState.Ready) {
      return;
    }
    // The button shows the last refresh; the choices may have changed since.
    Plan plan = MakePlan();
    if (plan.Problems.Count != 0 || plan.NodeProblems > 0 || plan.Waiting
        || plan.Queue.Count == 0) {
      Refresh();
      Status = plan.Problems.Count != 0 ? string.Join(" ", plan.Problems)
          : plan.NodeProblems > 0 ? "Not saved: resolve the problems named under the modems first."
          : plan.Waiting ? "Not saved: wait until every modem is read."
          : "Nothing to save.";
      return;
    }
    foreach (int index in plan.Queue) {
      Nv5NativeSettingsEditor editor = Modems[index].Editor;
      if (!editor.Ready || !editor.ValuesReadBack || editor.HasPendingChanges()) {
        Status = $"{Modems[index].Name} is being read; save once it is.";
        return;
      }
    }
    foreach (int index in plan.Queue) {
      NvLpsModem modem = Modems[index];
      SortedDictionary<string, string> values = plan.Candidates[index];
      foreach ((string key, string text) in values) {
        if (!modem.Editor.SetValue(key, text)) {
          foreach (int staged in plan.Queue) {
            Modems[staged].Editor.Discard();
          }
          Status = $"{modem.Name} does not accept {key} = {text}. Nothing was saved.";
          return;
        }
      }
      modem.Frozen = modem.Editor.CandidateValues();
      modem.Staged = new Dictionary<string, string>(values, StringComparer.Ordinal);
      modem.Durable = modem.Reread = modem.Committing = false;
      SetOutcome(index, "Waiting for its turn.");
    }
    // Beacons first, so that the rover's first bursts find them configured.
    _queue = plan.Queue.OrderBy(index => Modems[index].RoleValue == Nv5LpsRoles.Rover ? 1 : 0)
        .ToList();
    _touched = [.. _queue];
    if (plan.Generation != 0) {
      _text["LPS_FIELD_GEN"] = plan.Generation.ToString(CultureInfo.InvariantCulture);
      ShowField();
    }
    SetState(FieldState.Saving);
    _failed = -1;
    InputsEnabled = false;
    Status = _queue.Count == 1 ? "Saving 1 modem…"
        : $"Saving {_queue.Count} modems one after another…";
    Next();
  }

  private void Next() {
    if (_queue.Count == 0) {
      SetState(FieldState.Done);
      _current = -1;
      Status = "Saved. Every changed modem restarted and read back what was saved. LPS uses raw "
          + "ranges unless a calibration applies; calibration is optional.";
      return;
    }
    _current = _queue[0];
    _queue.RemoveAt(0);
    NvLpsModem modem = Modems[_current];
    Nv5NativeSettingsEditor editor = modem.Editor;
    IReadOnlyDictionary<string, Value> current = editor.CandidateValues();
    bool unchanged = editor.Ready && editor.ValuesReadBack && current.Count == modem.Frozen.Count
        && modem.Frozen.All(pair => current.TryGetValue(pair.Key, out Value? now)
            && now.Type == pair.Value.Type && now.Bytes.AsSpan().SequenceEqual(pair.Value.Bytes));
    if (!unchanged) {
      Fail(_current, "its settings changed while it waited; they are read again.");
      return;
    }
    SetOutcome(_current, "Checking the new settings…");
    Status = $"Saving {modem.Name}…";
    if (!editor.Prepare()) {
      Fail(_current, $"the save could not start. {editor.StatusText}");
    }
  }

  private void Fail(int index, string reason) {
    NvLpsModem modem = Modems[index];
    // A request in flight is answered before the abort goes out.
    modem.Editor.Cancel("Not saved: " + reason);
    modem.Editor.Discard();
    // A commit that was sent may have been saved even without its receipt.
    SetOutcome(index, modem.Durable
        ? $"Saved, but its restart or read-back was not confirmed: {reason}"
        : modem.Committing
            ? $"Outcome unknown: the save was sent but not confirmed. {reason}"
            : $"Not saved: {reason}");
    foreach (int pending in _queue) {
      SetOutcome(pending, "Not attempted.");
      Modems[pending].Editor.Discard();
    }
    _queue.Clear();
    _current = -1;
    _waitReadback = false;
    _failed = index;
    modem.Reread = false;
    SetState(FieldState.Failed);
    Status = $"Saving stopped at {modem.Name}: {reason} Modems saved before it keep their new "
        + $"settings. {modem.Name} is read again; then the field can be saved again.";
  }

  private void OnPrepared(int index, bool valid) {
    if (_state != FieldState.Saving || _current != index) {
      return;
    }
    Nv5NativeSettingsEditor editor = Modems[index].Editor;
    if (!valid) {
      Fail(index, $"the modem refused the new settings. {editor.StatusText}");
      return;
    }
    SetOutcome(index, "Saving…");
    if (!editor.CommitPrepared()) {
      Fail(index, $"the save could not start. {editor.StatusText}");
    }
  }

  private void OnDurableSaved(int index) {
    if (_state != FieldState.Saving || _current != index) {
      return;
    }
    Modems[index].Durable = true;
    SetOutcome(index, "Saved; waiting for its restart…");
  }

  private void OnConfigurationFinished(int index, bool success) {
    if (_state != FieldState.Saving || _current != index) {
      return;
    }
    NvLpsModem modem = Modems[index];
    if (!success) {
      Fail(index, modem.Editor.StatusText);
      return;
    }
    _waitReadback = true;
    modem.ExpectedDigest = modem.Editor.ActiveDigest;
    SetOutcome(index, "Restarted; reading its settings back…");
  }

  /// <summary>Sets a modem's line to how its save goes, at once.</summary>
  private void SetOutcome(int index, string text) {
    NvLpsModem modem = Modems[index];
    modem.Outcome = text;
    modem.Line = text;
    modem.LineIsProblem = false;
  }

  private void SetState(FieldState state) {
    _state = state;
    OnPropertyChanged(nameof(Active));
    if (state == FieldState.Saving) {
      // A save begun while Close waits keeps the window open for its outcome.
      _closePending = false;
    }
  }

  private void OnModemChanged(NvLpsModem modem, string property) {
    if (_filling) {
      return;
    }
    if (property == nameof(NvLpsModem.Beacon)) {
      modem.BeaconRow = modem.Beacon?.Value ?? 0;
      modem.BeaconReadId = "";
    }
    Refresh();
  }

  private void Refresh() {
    if (_closePending && !Modems.Any(modem => modem.Editor.Saving)) {
      _closePending = false;
      CloseRequested?.Invoke();
      return;
    }
    for (int i = 0; i < Modems.Count; i++) {
      Nv5NativeSettingsEditor editor = Modems[i].Editor;
      if (!Modems[i].Loaded && editor.Ready && editor.ValuesReadBack && editor.LpsSupported) {
        LoadModem(i);
      }
    }
    AutoLoadField();
    if (_state == FieldState.Saving && _waitReadback && _current >= 0) {
      NvLpsModem modem = Modems[_current];
      if (modem.Editor.Ready && modem.Editor.ValuesReadBack) {
        _waitReadback = false;
        if (modem.ExpectedDigest.Length != 32
            || !modem.Editor.ReadBackDigest.AsSpan().SequenceEqual(modem.ExpectedDigest)) {
          Fail(_current, "what it reads back is not what was saved; another save replaced it.");
        } else {
          SetOutcome(_current, "Saved, restarted and read back.");
          Next();
        }
      }
    }
    if (_state == FieldState.Failed && _failed >= 0) {
      // Whether the failed modem saved is uncertain until it is read again.
      NvLpsModem modem = Modems[_failed];
      if (!modem.Reread && !modem.Editor.Busy) {
        modem.Reread = true;
        modem.Editor.RereadDevice();
      }
    }
    if (_state is FieldState.Done or FieldState.Failed) {
      Settle();
    }
    Plan plan = MakePlan();
    bool ready = _state == FieldState.Ready;
    // The rover fields follow the rover: they are filled again only when another modem becomes it.
    if (plan.Rover != _roverShown) {
      _roverShown = plan.Rover;
      ShowRover(plan.Rover);
    }
    bool nodeProblem = false;
    _filling = true;
    for (int i = 0; i < Modems.Count; i++) {
      NvLpsModem modem = Modems[i];
      bool outcome = !ready && _touched.Contains(i) && modem.Outcome.Length != 0;
      (string text, bool problem) = i < plan.Lines.Count ? plan.Lines[i] : ("", false);
      nodeProblem |= problem;
      modem.Line = outcome ? modem.Outcome : text;
      modem.LineIsProblem = !outcome && problem;
      // A control that does not apply to the role is left out rather than greyed.
      modem.Editable = ready && modem.Loaded;
      modem.LoadedShown = modem.Loaded;
      modem.RadioShown = modem.Loaded && modem.RoleValue != Nv5LpsRoles.Off;
      modem.BeaconShown = modem.Loaded && modem.RoleValue == Nv5LpsRoles.Beacon;
      int shown = modem.RoleValue == Nv5LpsRoles.Beacon ? modem.BeaconRow : 0;
      NvLpsChoice? beacon = modem.Beacons.FirstOrDefault(choice => choice.Value == shown);
      if (beacon != null && !Equals(modem.Beacon, beacon)) {
        modem.Beacon = beacon;
      }
    }
    _filling = false;
    bool reading = plan.Queue.Any(index => !Modems[index].Editor.Ready
        || !Modems[index].Editor.ValuesReadBack || Modems[index].Editor.HasPendingChanges());
    string reason = _state == FieldState.Saving ? "Saving…"
        : !ready ? "The modems that were saved are being read again."
        : plan.Problems.Count != 0 ? string.Join(" ", plan.Problems)
        : nodeProblem || plan.NodeProblems > 0 ? "Resolve the problems named under the modems first."
        : plan.Waiting || reading ? "Wait until every modem is read."
        : plan.Queue.Count == 0 ? "Every modem already holds these settings."
        : "";
    CanApply = reason.Length == 0;
    if (ready) {
      ApplyText = plan.Queue.Count == 0 ? "Nothing to save"
          : plan.Queue.Count == 1 ? "Verify and save 1 modem"
          : $"Verify and save {plan.Queue.Count} modems";
    }
    ApplyTip = reason.Length == 0
        ? "Checks each changed modem, then saves and restarts them one at a time, beacons before "
            + "the rover."
        : reason;
    ProblemsText = string.Join("\n", plan.Problems);
    ProblemsShown = ready && plan.Problems.Count != 0;
    // Rover settings apply only with exactly one rover; otherwise the line says why they wait.
    bool rover = plan.Rover >= 0;
    RoverName = rover ? $"Rover settings for {Modems[plan.Rover].Name}"
        : plan.Participants && plan.Problems.Contains(OnlyOneRover)
            ? "Rover settings wait until only one modem is the rover."
            : "Rover settings apply once a modem is the rover.";
    RoverEnabled = rover && ready;
    BurstText = rover ? BurstOf(Modems[plan.Rover]) : "";
    uint shownGeneration = (uint)Math.Clamp(Nv5LpsField.Number(_text, "LPS_FIELD_GEN"), 0,
        uint.MaxValue);
    GenerationText = !plan.Participants || plan.Generation == 0
        ? $"Field generation {shownGeneration}."
        : plan.Generation == shownGeneration
            ? $"Field generation {shownGeneration}. Saving keeps it."
            : $"Field generation {shownGeneration} here. Saving sets {plan.Generation}, so that "
                + "every modem tells this field from an older one.";
    for (int i = 0; i < Modems.Count; i++) {
      FieldSources[i].IsEnabled = Modems[i].Loaded;
      LpsRows[i].Show(Modems[i].Editor.LpsRow());
    }
    if (ready && Status == ReadingTheModems && Modems.Count != 0
        && Modems.All(modem => modem.Editor.Ready && modem.Editor.ValuesReadBack)) {
      Status = "Every modem is read. Choose the roles, check the field, then verify and save.";
    }
  }

  /// <summary>Ends a finished or stopped save once every touched modem is read back; after a
  /// failure, says whether the failed modem's save took effect.</summary>
  private void Settle() {
    bool settled = !(_state == FieldState.Failed && _failed >= 0 && !Modems[_failed].Reread);
    foreach (int index in _touched) {
      Nv5NativeSettingsEditor editor = Modems[index].Editor;
      settled &= editor.Ready && editor.ValuesReadBack && !editor.HasPendingChanges();
    }
    if (!settled) {
      return;
    }
    if (_state == FieldState.Failed && _failed >= 0) {
      NvLpsModem modem = Modems[_failed];
      if (modem.Durable || modem.Committing) {
        IReadOnlyDictionary<string, Value> held = modem.Editor.ReadValues;
        bool took = modem.Staged.Count != 0 && modem.Staged.All(pair =>
            held.TryGetValue(pair.Key, out Value? value)
            && Nv5NativeSettingsEditor.Display(value) == pair.Value);
        Status = took
            ? $"{modem.Name} was read again and holds the new settings: its save took effect. "
                + "The others can be saved now."
            : $"{modem.Name} was read again and does not hold the new settings: its save did not "
                + "take effect. Saving can be tried again.";
      }
    }
    SetState(FieldState.Ready);
    _failed = -1;
    InputsEnabled = true;
  }

  private Plan MakePlan() {
    var plan = new Plan();
    int rovers = 0;
    var bases = new List<IReadOnlyDictionary<string, string>>();
    // Settings count only once read back in full: a modem that reconnects has none for a while.
    bool Current(int index) {
      Nv5NativeSettingsEditor editor = Modems[index].Editor;
      return editor.Ready && editor.ValuesReadBack && !editor.HasPendingChanges();
    }
    for (int i = 0; i < Modems.Count; i++) {
      if (!Modems[i].Loaded || Modems[i].RoleValue == Nv5LpsRoles.Off) {
        continue;
      }
      plan.Participants = true;
      if (Current(i)) {
        bases.Add(Nv5LpsField.FromValues(Modems[i].Editor.ReadValues));
      }
      if (Modems[i].RoleValue == Nv5LpsRoles.Rover && rovers++ == 0) {
        plan.Rover = i;
      }
    }
    if (rovers > 1) {
      plan.Problems.Add(OnlyOneRover);
      plan.Rover = -1;
    }
    // Rows past the count are saved as the modem stores them, so the next read matches.
    SortedDictionary<string, string> common = Nv5LpsField.Normalized(_text);
    if (plan.Participants) {
      string error = Nv5LpsField.Validate(_text);
      if (error.Length != 0) {
        plan.Problems.Add($"Field: {error}");
      } else {
        plan.Generation = Nv5LpsField.Generation(_text, bases);
        if (plan.Generation == 0) {
          plan.Problems.Add("Every field generation is taken; switch LPS off on a modem first.");
        }
        common["LPS_FIELD_GEN"] = plan.Generation.ToString(CultureInfo.InvariantCulture);
      }
    }
    long count = Nv5LpsField.Number(_text, "LPS_BCOUNT");
    var taken = new Dictionary<int, int>();
    for (int i = 0; i < Modems.Count; i++) {
      NvLpsModem modem = Modems[i];
      Nv5NativeSettingsEditor editor = modem.Editor;
      if (!modem.Loaded) {
        bool readBack = editor.Ready && editor.ValuesReadBack;
        // A modem not read yet may already belong to the field: saving waits for it too.
        bool unreadable = editor.Failed && editor.ReadValues.Count == 0;
        if (unreadable) {
          plan.NodeProblems++;
        } else if (!(readBack && !editor.LpsSupported)) {
          plan.Waiting = true;
        }
        plan.Lines.Add((unreadable
            ? "Could not be read. Saving waits until it is: Read settings in its tab tries again. "
                + editor.StatusText
            : readBack && !editor.LpsSupported
                ? "Its firmware has no LPS settings; it is left as it is."
                : "Reading its settings…", unreadable));
        continue;
      }
      if (!Current(i)) {
        // Its role in the field is known, but not what it holds now: saving waits for it.
        if (editor.Failed) {
          plan.NodeProblems++;
          plan.Lines.Add(("Could not be read again. Saving waits until it is: Read settings in "
              + "its tab tries again. " + editor.StatusText, true));
        } else {
          plan.Waiting = true;
          plan.Lines.Add(("Reading its settings again…", false));
        }
        continue;
      }
      IReadOnlyDictionary<string, Value> read = editor.ReadValues;
      string Held(string key) =>
          read.TryGetValue(key, out Value? value) ? Nv5NativeSettingsEditor.Display(value) : "";
      int role = modem.RoleValue;
      int radio = modem.Radio?.Value ?? 1;
      var candidate = new SortedDictionary<string, string>(StringComparer.Ordinal);
      string problem = "";
      if (role == Nv5LpsRoles.Off) {
        candidate["LPS_ROLE"] = "0";
      } else {
        foreach ((string key, string text) in common) {
          candidate[key] = text;
        }
        candidate["LPS_ROLE"] = role.ToString(CultureInfo.InvariantCulture);
        candidate["LPS_RADIO"] = radio.ToString(CultureInfo.InvariantCulture);
        string beaconId = Held("LPS_BEACON");
        if (role == Nv5LpsRoles.Beacon) {
          if (modem.BeaconRow < 1 || modem.BeaconRow > count) {
            problem = "Choose the surveyed beacon it answers for.";
          } else if (taken.TryGetValue(modem.BeaconRow, out int other)) {
            problem = $"B{modem.BeaconRow} is {Modems[other].Name} already.";
          } else {
            taken[modem.BeaconRow] = i;
            beaconId = _text[Nv5LpsField.RowKey(modem.BeaconRow, "ID")];
          }
        }
        candidate["LPS_BEACON"] = beaconId;
        if (role == Nv5LpsRoles.Rover) {
          foreach ((string key, string text) in modem.Rover) {
            candidate[key] = text;
          }
        }
        if (problem.Length == 0 && modem.Radio is { IsEnabled: false } unavailable) {
          problem = unavailable.Reason;
        }
      }
      // Only what this modem holds is written; a setting its firmware lacks stays out.
      foreach (string key in candidate.Keys.Where(key => !read.ContainsKey(key)).ToList()) {
        candidate.Remove(key);
      }
      bool changed = candidate.Any(pair => pair.Value != Held(pair.Key));
      bool wasOn = Held("LPS_ROLE") != "0";
      bool shared = Held($"CH{radio}_RSV_EN") == "1";
      string radioUse = shared ? $"Radio {radio} keeps its data link; LPS uses its reserved gaps."
          : changed ? $"LPS takes Radio {radio} over: its data link stops."
          : $"LPS has Radio {radio} to itself: its data link is paused.";
      string line = problem.Length != 0 ? problem
          : role == Nv5LpsRoles.Off
              ? wasOn ? "Saving switches LPS off. The modem restarts." : "LPS off; nothing to change."
          : !changed
              ? (role == Nv5LpsRoles.Rover ? $"Rover on Radio {radio}; nothing to change."
                  : $"Beacon B{modem.BeaconRow} on Radio {radio}; nothing to change.") + " "
                  + radioUse
              : (role == Nv5LpsRoles.Rover
                  ? $"Saving makes it the rover on Radio {radio} and restarts it."
                  : $"Saving makes it beacon B{modem.BeaconRow} on Radio {radio} and restarts it.")
                  + " " + radioUse;
      plan.Lines.Add((line, problem.Length != 0));
      if (problem.Length != 0) {
        plan.NodeProblems++;
      } else if (changed) {
        plan.Queue.Add(i);
        plan.Candidates[i] = candidate;
      }
    }
    return plan;
  }

  /// <summary>The burst settings the pair window sets for the rover's radio, which LPS bursts
  /// use too.</summary>
  private static string BurstOf(NvLpsModem rover) {
    IReadOnlyDictionary<string, Value> values = rover.Editor.ReadValues;
    int radio = rover.Radio?.Value ?? 1;
    string prefix = $"CH{radio}_RNG_";
    if (!values.ContainsKey(prefix + "N")) {
      return "";
    }
    string Setting(string suffix) => values.TryGetValue(prefix + suffix, out Value? value)
        ? Nv5NativeSettingsEditor.Display(value) : "";
    long exchanges = Nv5NativeSettingsEditor.TryParseInteger(Setting("N"), out long n) ? n : 0;
    if (exchanges <= 1) {
      return $"Bursts on Radio {radio} use its distance settings: 1 exchange at "
          + $"{Setting("PWR")} dBm. Configure distance pair… changes them.";
    }
    double span = double.TryParse(Setting("SPAN"), NumberStyles.Float, CultureInfo.InvariantCulture,
        out double hertz) ? hertz / 1000 : 0;
    return string.Create(CultureInfo.InvariantCulture,
        $"Bursts on Radio {radio} use its distance settings: {exchanges} exchanges over {span:0.###} kHz at {Setting("PWR")} dBm. Configure distance pair… changes them.");
  }

  private void LoadModem(int index) {
    NvLpsModem modem = Modems[index];
    IReadOnlyDictionary<string, Value> values = modem.Editor.ReadValues;
    string Text(string key) =>
        values.TryGetValue(key, out Value? value) ? Nv5NativeSettingsEditor.Display(value) : "";
    _filling = true;
    int role = int.TryParse(Text("LPS_ROLE"), NumberStyles.None, CultureInfo.InvariantCulture,
        out int number) ? number : 0;
    modem.Role = Select(modem.Roles, role, $"Role {Text("LPS_ROLE")}");
    for (int radio = 1; radio <= 2; radio++) {
      string prefix = $"CH{radio}_";
      bool fitted = !values.ContainsKey(prefix + "PRESENT") || Text(prefix + "PRESENT") != "0";
      // Radio chip 0 is the LR2021, the only radio with the ranging LPS needs.
      bool lr2021 = !values.ContainsKey(prefix + "CHIP") || Text(prefix + "CHIP") == "0";
      string reason = !fitted ? $"Radio {radio} is not fitted on this modem."
          : !lr2021 ? $"Radio {radio} is not an LR2021, which LPS needs."
          : "";
      modem.Radios[radio - 1] = new NvLpsChoice(radio, $"Radio {radio}", reason);
    }
    int chosen = int.TryParse(Text("LPS_RADIO"), NumberStyles.None, CultureInfo.InvariantCulture,
        out int radioNumber) ? radioNumber : 1;
    modem.Radio = Select(modem.Radios, chosen, $"Radio {Text("LPS_RADIO")}");
    modem.BeaconReadId = Text("LPS_BEACON");
    modem.BeaconRow = 0;
    modem.Rover.Clear();
    foreach (string key in RoverKeys) {
      if (values.ContainsKey(key)) {
        modem.Rover[key] = Text(key);
      }
    }
    modem.Loaded = true;
    _filling = false;
    UpdateBeaconChoices();
  }

  private void AutoLoadField() {
    // Until the field is edited, show the one the first modem holds, preferring a configured one.
    if (_fieldEdited) {
      return;
    }
    int source = -1;
    for (int i = 0; i < Modems.Count && source < 0; i++) {
      if (Modems[i].Loaded && Modems[i].Editor.ReadValues.TryGetValue("LPS_FIELD_GEN",
              out Value? generation)
          && Nv5NativeSettingsEditor.Display(generation) != "0") {
        source = i;
      }
    }
    for (int i = 0; i < Modems.Count && source < 0 && _fieldSource < 0; i++) {
      if (Modems[i].Loaded) {
        source = i;
      }
    }
    if (source < 0 || source == _fieldSource) {
      return;
    }
    if (SetField(Nv5LpsField.FromValues(Modems[source].Editor.ReadValues), out _)) {
      _fieldSource = source;
      _filling = true;
      SelectedFieldSource = FieldSources[source];
      _filling = false;
      _fieldEdited = false;
    }
  }

  private void ShowRover(int rover) {
    if (rover < 0) {
      return;
    }
    Dictionary<string, string> values = Modems[rover].Rover;
    string Get(string key, string fallback) => values.TryGetValue(key, out string? text)
        ? text : fallback;
    _filling = true;
    FlightControllerSystem = Integer(Get("LPS_FC_SYS", "1"));
    FlightControllerComponent = Integer(Get("LPS_FC_COMP", "1"));
    LaunchHeight = Integer(Get("LPS_Z_MM", "0")) / 1000m;
    LaunchSigma = SigmaValue(Get("LPS_TIE_VAR", "0"));
    RangeSigma = SigmaValue(Get("LPS_RNG_VAR", "0"));
    _filling = false;
  }

  private void StoreRover(string key, string value) {
    if (_filling) {
      return;
    }
    Plan plan = MakePlan();
    if (plan.Rover >= 0 && Modems[plan.Rover].Rover.ContainsKey(key)) {
      Modems[plan.Rover].Rover[key] = value;
    }
    Refresh();
  }

  private void ShowField() {
    _filling = true;
    // Parsed as validation parses it, so the form never shows another value than is saved.
    long Number(string key) => Nv5LpsField.Number(_text, key);
    LatitudeText = FixedText(Number("LPS_LAT_E7"), 7);
    LongitudeText = FixedText(Number("LPS_LON_E7"), 7);
    EllipsoidHeight = Number("LPS_HAE_MM") / 1000m;
    SeaLevelHeight = Number("LPS_MSL_MM") / 1000m;
    ulong datum = (ulong)(uint)Number("LPS_DATUM_LO") | (ulong)(uint)Number("LPS_DATUM_HI") << 32;
    DatumText = "0x" + datum.ToString("X16", CultureInfo.InvariantCulture);
    int count = (int)Math.Clamp(Number("LPS_BCOUNT"), int.MinValue, int.MaxValue);
    SelectedBeaconCount = Select(BeaconCounts, count,
        $"{count.ToString(CultureInfo.InvariantCulture)} (not 3 or 4)");
    for (int row = 0; row < Nv5LpsField.MaximumBeacons; row++) {
      NvLpsBeaconRow beacon = BeaconRows[row];
      for (int column = 0; column < Columns.Count; column++) {
        string native = _text[Nv5LpsField.RowKey(row + 1, Columns[column].Suffix)];
        string text = native;
        if (column == 1) {
          text = "0x" + ((uint)Nv5LpsField.Number(_text, Nv5LpsField.RowKey(row + 1, "ADDR")))
              .ToString("X8", CultureInfo.InvariantCulture);
        } else if (column is >= 2 and <= 4) {
          text = FixedText(Nv5LpsField.Number(_text, Nv5LpsField.RowKey(row + 1,
              Columns[column].Suffix)), 3);
        } else if (column is >= FirstSigmaColumn and < FirstCovarianceColumn) {
          text = SigmaText(native);
        }
        beacon.Show(column, text);
      }
      beacon.IsShown = row < Math.Max(3, Math.Min(4, count));
    }
    List<NvLpsBeaconRow> shown = BeaconRows.Where(row => row.IsShown).ToList();
    if (!ShownBeaconRows.SequenceEqual(shown)) {
      ShownBeaconRows.Clear();
      foreach (NvLpsBeaconRow row in shown) {
        ShownBeaconRows.Add(row);
      }
    }
    RangingBandwidth = Select(RangingBandwidths, (int)Number("LPS_RNG_BW_HZ"),
        $"{Number("LPS_RNG_BW_HZ")} Hz");
    RangingSpreadingFactor = Select(RangingSpreadingFactors, (int)Number("LPS_RNG_SF"),
        $"SF{Number("LPS_RNG_SF")}");
    MeetingBandwidth = Select(MeetingBandwidths, (int)Number("LPS_RV_BW_HZ"),
        $"{Number("LPS_RV_BW_HZ")} Hz");
    MeetingSpreadingFactor = Select(MeetingSpreadingFactors, (int)Number("LPS_RV_SF"),
        $"SF{Number("LPS_RV_SF")}");
    ExtendedRanging = Number("LPS_RNG_EXT") != 0;
    ResponseSymbols = Number("LPS_RSP_SYM");
    long carrier = Number("LPS_RV_HZ");
    MeetOnDataCarrier = carrier == 0;
    if (carrier != 0) {
      MeetingMhz = carrier / 1000000m;
    }
    _filling = false;
    UpdateBeaconChoices();
  }

  private void StoreField(string key, string value) {
    if (_filling || _text[key] == value) {
      return;
    }
    _text[key] = value;
    _fieldEdited = true;
    if (key == "LPS_BCOUNT" || key.EndsWith("_ID", StringComparison.Ordinal)) {
      UpdateBeaconChoices();
    }
  }

  private void TableEdited(int row, int column, string text) {
    if (_filling || !InputsEnabled || row is < 0 or >= Nv5LpsField.MaximumBeacons
        || column < 0 || column >= Columns.Count) {
      return;
    }
    string key = Nv5LpsField.RowKey(row + 1, Columns[column].Suffix);
    string native = "";
    string problem = "";
    if (column is 0 or 1) {
      if (ParseUnsigned(text, hex: column == 1, out ulong value) && value <= uint.MaxValue) {
        native = value.ToString(CultureInfo.InvariantCulture);
      } else {
        problem = column == 0 ? "an ID from 0 to 4294967295" : "up to 8 hexadecimal digits";
      }
    } else if (column <= 4) {
      if (ParseFixed(text, 3, out long millimetres) && millimetres is >= int.MinValue
          and <= int.MaxValue) {
        native = millimetres.ToString(CultureInfo.InvariantCulture);
      } else {
        problem = "metres with up to 3 decimals";
      }
    } else if (column < FirstCovarianceColumn) {
      if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float,
              CultureInfo.InvariantCulture, out double sigma)
          || !SigmaVariance(sigma, out native)) {
        problem = "a standard deviation in metres, 0 or more";
      }
    } else if (float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float,
                   CultureInfo.InvariantCulture, out float covariance)
               && float.IsFinite(covariance)) {
      native = (covariance == 0 ? 0f : covariance).ToString("G9", CultureInfo.InvariantCulture);
    } else {
      problem = "square metres";
    }
    if (problem.Length != 0) {
      Status = $"B{row + 1} {Columns[column].Title}: enter {problem}.";
    } else {
      StoreField(key, native);
    }
    ShowField();
    Refresh();
  }

  private void UpdateBeaconChoices() {
    int count = (int)Math.Clamp(Nv5LpsField.Number(_text, "LPS_BCOUNT"), 0,
        Nv5LpsField.MaximumBeacons);
    _filling = true;
    foreach (NvLpsModem modem in Modems) {
      if (modem.BeaconRow == 0 && modem.BeaconReadId.Length != 0) {
        for (int row = 1; row <= count; row++) {
          if (_text[Nv5LpsField.RowKey(row, "ID")] == modem.BeaconReadId) {
            modem.BeaconRow = row;
          }
        }
      }
      List<NvLpsChoice> choices = [new(0, "—")];
      for (int row = 1; row <= count; row++) {
        choices.Add(new NvLpsChoice(row, $"B{row}",
            Tip: $"ID {_text[Nv5LpsField.RowKey(row, "ID")]}"));
      }
      if (!modem.Beacons.SequenceEqual(choices)) {
        modem.Beacons.Clear();
        foreach (NvLpsChoice choice in choices) {
          modem.Beacons.Add(choice);
        }
      }
      bool beacon = modem.RoleValue == Nv5LpsRoles.Beacon;
      int shown = beacon && modem.BeaconRow <= count ? modem.BeaconRow : 0;
      modem.Beacon = modem.Beacons.First(choice => choice.Value == shown);
    }
    _filling = false;
  }

  partial void OnSelectedFieldSourceChanged(NvLpsSource? value) {
    // Choosing a modem only names the source; Show takes its field.
  }

  partial void OnLatitudeTextChanged(string value) => Coordinate(value, "LPS_LAT_E7",
      "Enter the origin latitude in degrees, with up to 7 decimals.");

  partial void OnLongitudeTextChanged(string value) => Coordinate(value, "LPS_LON_E7",
      "Enter the origin longitude in degrees, with up to 7 decimals.");

  partial void OnEllipsoidHeightChanged(decimal? value) {
    if (!_filling && value is { } metres) {
      StoreField("LPS_HAE_MM", Millimetres(metres));
      Refresh();
    }
  }

  partial void OnSeaLevelHeightChanged(decimal? value) {
    if (!_filling && value is { } metres) {
      StoreField("LPS_MSL_MM", Millimetres(metres));
      Refresh();
    }
  }

  partial void OnDatumTextChanged(string value) {
    if (_filling) {
      return;
    }
    if (ParseUnsigned(value, hex: true, out ulong datum)) {
      StoreField("LPS_DATUM_LO", (datum & 0xffffffff).ToString(CultureInfo.InvariantCulture));
      StoreField("LPS_DATUM_HI", (datum >> 32).ToString(CultureInfo.InvariantCulture));
    } else {
      Status = "Enter the datum ID as up to 16 hexadecimal digits.";
    }
    ShowField();
    Refresh();
  }

  partial void OnSelectedBeaconCountChanged(NvLpsChoice? value) {
    if (_filling || value == null) {
      return;
    }
    StoreField("LPS_BCOUNT", value.Value.ToString(CultureInfo.InvariantCulture));
    ShowField();
    Refresh();
  }

  partial void OnRangingBandwidthChanged(NvLpsChoice? value) => Choice("LPS_RNG_BW_HZ", value);
  partial void OnRangingSpreadingFactorChanged(NvLpsChoice? value) => Choice("LPS_RNG_SF", value);
  partial void OnMeetingBandwidthChanged(NvLpsChoice? value) => Choice("LPS_RV_BW_HZ", value);
  partial void OnMeetingSpreadingFactorChanged(NvLpsChoice? value) => Choice("LPS_RV_SF", value);

  partial void OnExtendedRangingChanged(bool value) {
    if (!_filling) {
      StoreField("LPS_RNG_EXT", value ? "1" : "0");
      Refresh();
    }
  }

  partial void OnResponseSymbolsChanged(decimal? value) {
    if (!_filling && value is { } symbols) {
      StoreField("LPS_RSP_SYM", decimal.ToInt64(decimal.Round(symbols))
          .ToString(CultureInfo.InvariantCulture));
      Refresh();
    }
  }

  partial void OnMeetOnDataCarrierChanged(bool value) {
    if (_filling) {
      return;
    }
    StoreField("LPS_RV_HZ", value ? "0" : Hertz(MeetingMhz ?? 0));
    ShowField();
    Refresh();
  }

  partial void OnMeetingMhzChanged(decimal? value) {
    if (!_filling && !MeetOnDataCarrier && value is { } megahertz) {
      StoreField("LPS_RV_HZ", Hertz(megahertz));
      Refresh();
    }
  }

  partial void OnFlightControllerSystemChanged(decimal? value) {
    if (value is { } id) {
      StoreRover("LPS_FC_SYS", decimal.ToInt64(id).ToString(CultureInfo.InvariantCulture));
    }
  }

  partial void OnFlightControllerComponentChanged(decimal? value) {
    if (value is { } id) {
      StoreRover("LPS_FC_COMP", decimal.ToInt64(id).ToString(CultureInfo.InvariantCulture));
    }
  }

  partial void OnLaunchHeightChanged(decimal? value) {
    if (value is { } metres) {
      StoreRover("LPS_Z_MM", Millimetres(metres));
    }
  }

  partial void OnLaunchSigmaChanged(decimal? value) {
    if (value is { } sigma && SigmaVariance((double)sigma, out string variance)) {
      StoreRover("LPS_TIE_VAR", variance);
    }
  }

  partial void OnRangeSigmaChanged(decimal? value) {
    if (value is { } sigma && SigmaVariance((double)sigma, out string variance)) {
      StoreRover("LPS_RNG_VAR", variance);
    }
  }

  private void Coordinate(string text, string key, string problem) {
    if (_filling) {
      return;
    }
    if (ParseFixed(text, 7, out long value) && value is >= int.MinValue and <= int.MaxValue) {
      StoreField(key, value.ToString(CultureInfo.InvariantCulture));
    } else {
      Status = problem;
    }
    ShowField();
    Refresh();
  }

  private void Choice(string key, NvLpsChoice? value) {
    if (!_filling && value != null) {
      StoreField(key, value.Value.ToString(CultureInfo.InvariantCulture));
      Refresh();
    }
  }

  /// <summary>Selects the choice of a value, adding one for a value outside the list.</summary>
  private static NvLpsChoice Select(ObservableCollection<NvLpsChoice> choices, int value,
      string unlisted) {
    NvLpsChoice? choice = choices.FirstOrDefault(item => item.Value == value);
    if (choice == null) {
      choice = new NvLpsChoice(value, unlisted);
      choices.Add(choice);
    }
    return choice;
  }

  /// <summary>A native integer as fixed-point text, such as millimetres as metres; no
  /// rounding.</summary>
  internal static string FixedText(long value, int decimals) {
    bool negative = value < 0;
    ulong magnitude = negative ? (ulong)(-(value + 1)) + 1 : (ulong)value;
    ulong scale = 1;
    for (int i = 0; i < decimals; i++) {
      scale *= 10;
    }
    string text = (magnitude / scale).ToString(CultureInfo.InvariantCulture);
    if (decimals > 0) {
      text += "." + (magnitude % scale).ToString(CultureInfo.InvariantCulture)
          .PadLeft(decimals, '0');
    }
    return negative ? "-" + text : text;
  }

  /// <summary>Fixed-point text into an integer of that many decimals, exactly; more decimals are
  /// refused.</summary>
  internal static bool ParseFixed(string input, int decimals, out long value) {
    value = 0;
    string text = input.Trim().Replace('−', '-');
    Match match = FixedFormat().Match(text);
    if (!match.Success || (match.Groups[2].Length == 0 && match.Groups[3].Length == 0)
        || match.Groups[3].Length > decimals) {
      return false;
    }
    string digits = match.Groups[2].Value + match.Groups[3].Value.PadRight(decimals, '0');
    if (digits.Length > 18) {
      return false;
    }
    long magnitude = digits.Length == 0 ? 0
        : long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    value = match.Groups[1].Value == "-" ? -magnitude : magnitude;
    return true;
  }

  /// <summary>Decimal, or hexadecimal with 0x; hexadecimal without it when asked.</summary>
  internal static bool ParseUnsigned(string input, bool hex, out ulong value) {
    string text = input.Trim();
    bool hexadecimal = hex;
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
      text = text[2..];
      hexadecimal = true;
    }
    value = 0;
    return text.Length != 0 && ulong.TryParse(text,
        hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
        CultureInfo.InvariantCulture, out value);
  }

  /// <summary>The standard deviation of a stored variance, for display.</summary>
  private static string SigmaText(string variance) =>
      double.TryParse(variance, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
      && v >= 0 && double.IsFinite(v)
          ? Math.Sqrt(v).ToString("G4", CultureInfo.InvariantCulture)
          : variance;

  private static decimal SigmaValue(string variance) =>
      double.TryParse(variance, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
      && v >= 0 && double.IsFinite(v) ? (decimal)Math.Round(Math.Sqrt(v), 4) : 0;

  /// <summary>A typed standard deviation as the Float32 variance text the modem stores.</summary>
  private static bool SigmaVariance(double sigma, out string variance) {
    variance = "";
    if (!double.IsFinite(sigma) || sigma is < 0 or > 10000) {
      return false;
    }
    variance = ((float)(sigma * sigma)).ToString("G9", CultureInfo.InvariantCulture);
    return true;
  }

  private static string Millimetres(decimal metres) =>
      decimal.ToInt64(decimal.Round(metres * 1000, MidpointRounding.AwayFromZero))
          .ToString(CultureInfo.InvariantCulture);

  private static string Hertz(decimal megahertz) =>
      decimal.ToInt64(decimal.Round(megahertz * 1000000, MidpointRounding.AwayFromZero))
          .ToString(CultureInfo.InvariantCulture);

  private static decimal Integer(string text) =>
      Nv5NativeSettingsEditor.TryParseInteger(text.Trim(), out long number) ? number : 0;

  [GeneratedRegex(@"^([+-]?)(\d*)(?:[.,](\d*))?$")]
  private static partial Regex FixedFormat();
}
