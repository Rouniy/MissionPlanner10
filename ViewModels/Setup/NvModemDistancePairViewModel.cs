using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Services;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;
using Value = MissionPlanner.Services.Nv5ConfigurationClient.Value;

namespace MissionPlanner.ViewModels.Setup;

/// <summary>A modem offered as the other end of the link.</summary>
public sealed partial class NvPairPeerChoice : ObservableObject {
  internal NvPairPeerChoice(string uid, string label) {
    Uid = uid;
    _label = label;
  }

  internal string Uid { get; }

  [ObservableProperty]
  private string _label;

  public override string ToString() => Label;
}

/// <summary>A ranging period the form offers, in data packets.</summary>
public sealed record NvPairPeriodChoice(uint Packets, string Label) {
  public override string ToString() => Label;
}

/// <summary>One end of the link that may measure: named after the modem and its data role once
/// both modems are read.</summary>
public sealed partial class NvPairManagerChoice : ObservableObject {
  [ObservableProperty]
  private string _label = "";

  public override string ToString() => Label;
}

public enum NvPairComparisonKind {
  Same,
  Neutral,
  Differs,
  Derived,
  Problem,
}

/// <summary>A setting of the selected radio on both modems, side by side.</summary>
public sealed record NvPairComparisonRow(
    string Name, string First, string Second, string Note, string Tip, NvPairComparisonKind Kind) {
  /// <summary>Highlighted: a shared setting that differs, a derived fingerprint, or a
  /// problem.</summary>
  public bool Marked => Kind is NvPairComparisonKind.Differs or NvPairComparisonKind.Derived
      or NvPairComparisonKind.Problem;

  /// <summary>Why the row is shown as it is, or nothing for a plain equal setting.</summary>
  public string? TipText => Tip.Length == 0 ? null : Tip;
}

/// <summary>Live radio state of one channel, as the link status reports it.</summary>
internal readonly record struct NvPairLink(byte Role, uint FrequencyHz, uint BandwidthHz,
    byte Modulation);

/// <summary>
/// Configures ranging between two NV5 modems: verifies both candidate link contracts before
/// saving, saves the answering modem first, and switches ranging off modem by modem when a pair
/// save is not possible.
/// </summary>
/// <remarks>
/// Each modem saves atomically; the pair is not a distributed transaction. The owner calls
/// <see cref="Service"/> periodically, delivers the modems' replies to <see cref="Receive"/>,
/// sends every <see cref="SendFrame"/> frame, and tells the window about modems through
/// <see cref="Offer"/> and <see cref="Observe"/>.
/// </remarks>
public sealed partial class NvModemDistancePairViewModel : ObservableObject {
  private enum PairPhase {
    Ready,
    CollectHashes,
    VerifyProof,
    Saving,
    SwitchingOff,
    Done,
    Failed,
  }

  private sealed record Peer(Target Target, uint Uptime, string Label);

  // Limits of the modem's ranging schedule. The modem still validates every candidate; these
  // only keep the form from offering what it refuses.
  internal const uint MinimumPeriodPackets = 2;
  internal const uint MaximumPeriodPackets = 0x800000;
  internal const uint DefaultPeriodPackets = 1024;
  internal const int MaximumExchanges = 29;
  internal const int MaximumMarginMs = 40;
  internal const int MinimumPowerDbm = -19;
  internal const int MaximumPowerDbm = 22;
  private const int WindowFixedMs = 40;
  private const int WindowPerExchangeMs = 40;

  // Settings the modem's link fingerprint leaves out because the two ends of one link
  // legitimately differ, plus receiver-local radio settings.
  private static readonly string[] PerModemSettings = ["PWR_DBM", "OWNS_SCHED", "SX_LNA",
      "TCXO_MV", "CHIP", "TX_PHASE", "LINK_US", "OPEN_LOOP", "SCAN_DW", "DIRECT_FREQ", "RX_BOOST",
      "LNA_GAIN"];

  // What this window stages on a radio, and puts back when a check fails before any commit.
  private static readonly string[] StagedSuffixes = ["RNG_EN", "RNG_PER", "RNG_PHASE", "RNG_N",
      "RNG_SPAN", "RNG_PWR", "RNG_ADDR", "RNG_MGR", "RNG_PAD_MS", "PEER_HASH"];

  private static readonly IReadOnlyDictionary<string, Value> NoValues =
      new Dictionary<string, Value>(StringComparer.Ordinal);

  private static readonly IReadOnlyDictionary<string, string> NoTexts =
      new Dictionary<string, string>(StringComparer.Ordinal);

  private readonly Nv5NativeSettingsEditor[] _editors;
  private readonly string[] _names = new string[2];
  private readonly Dictionary<string, Peer> _peers = new(StringComparer.Ordinal);
  private readonly Target[] _opening;
  private readonly string _fixedUid;
  private readonly Func<object, bool> _routeOpen;
  private readonly uint[] _hash = new uint[2];
  private readonly bool[] _prepared = new bool[2];
  private readonly bool[] _complete = new bool[2];
  private readonly int[] _offOutcome = new int[2];
  private readonly string[] _offReason = ["", ""];

  // What each table showed for the settings a save stages, edits included: put back when the
  // save ends before any commit.
  private readonly IReadOnlyDictionary<string, string>[] _preSave = [NoTexts, NoTexts];
  private string _peerUid;
  private PairPhase _state = PairPhase.Ready;
  private string _comparisonShown = "";
  private int _first;
  private int _measuring = -1;
  private int _receiver = -1;
  private int _choosers;
  private bool _secondStarted;
  private bool _loaded;
  private bool _marginSupported;
  private bool _peerSwitch;
  private bool _announceRead;
  private bool _offAttempted;
  private bool _refreshing;
  private bool _quiet;

  /// <param name="first">The modem selected on the settings page: Modem 1.</param>
  /// <param name="second">The modem the window opens on as Modem 2.</param>
  /// <param name="createEditor">Makes an editor that shares the page's snapshot store.</param>
  /// <param name="routeOpen">Whether a modem's route is still open; without it every route
  /// is.</param>
  internal NvModemDistancePairViewModel(Target first, Target second,
      Func<Nv5NativeSettingsEditor> createEditor, Func<object, bool>? routeOpen = null) {
    _routeOpen = routeOpen ?? (_ => true);
    _opening = [first, second];
    for (int i = 0; i < 2; i++) {
      _names[i] = TabName(i, _opening[i]);
    }
    _fixedUid = UidKey(first.Uid);
    _peerUid = UidKey(second.Uid);
    _peers[_peerUid] = new Peer(second, 0, "");
    _fixedName = ModemText(first, "");
    Peers.Add(new NvPairPeerChoice(_peerUid, ModemText(second, "")));
    _selectedPeer = Peers[0];
    SetPeriod(DefaultPeriodPackets);
    _editors = [createEditor(), createEditor()];
    for (int i = 0; i < 2; i++) {
      int index = i;
      Nv5NativeSettingsEditor editor = _editors[i];
      editor.SendFrame += (frame, target) => SendFrame?.Invoke(frame, target);
      editor.CommitSent += values => CommitSent?.Invoke(editor, values);
      editor.Prepared += (valid, channel1, channel2) => OnPrepared(index, valid, channel1, channel2);
      editor.DurableSaved += () => OnDurableSaved(index);
      editor.ConfigurationFinished += success => OnConfigurationFinished(index, success);
    }
    _modem1TabName = _names[0];
    _modem2TabName = _names[1];
    UpdateHints();
  }

  /// <summary>A frame to send on the target's own link.</summary>
  internal event Action<Nv5ConfigurationFrame, Target>? SendFrame;

  /// <summary>A modem's commit was sent with these values; it restarts under the address they
  /// give it.</summary>
  internal event Action<Nv5NativeSettingsEditor, IReadOnlyDictionary<string, Value>>? CommitSent;

  public Nv5NativeSettingsEditor Modem1Editor => _editors[0];
  public Nv5NativeSettingsEditor Modem2Editor => _editors[1];
  internal IReadOnlyList<Nv5NativeSettingsEditor> Editors => _editors;

  public string Explanation =>
      "Both modems must use matching link settings and opposite TX/RX roles. Both candidates are "
      + "checked before saving; the responder saves first. Each modem saves atomically, and both "
      + "must confirm restart/readback for pair setup to succeed.";

  public ObservableCollection<NvPairPeerChoice> Peers { get; } = [];
  public IReadOnlyList<string> Channels { get; } = ["Radio 1", "Radio 2"];
  public ObservableCollection<NvPairManagerChoice> ManagerChoices { get; } = [new(), new()];
  public ObservableCollection<NvPairPeriodChoice> Periods { get; } = [];
  public ObservableCollection<NvPairComparisonRow> ComparisonRows { get; } = [];

  /// <summary>One row per modem for the selected radio.</summary>
  public ObservableCollection<NvDistanceRow> DistanceRows { get; } =
      [new NvDistanceRow("Modem 1"), new NvDistanceRow("Modem 2")];

  [ObservableProperty]
  private string _fixedName;

  [ObservableProperty]
  private NvPairPeerChoice? _selectedPeer;

  [ObservableProperty]
  private int _selectedChannelIndex;

  [ObservableProperty]
  private int _selectedManagerIndex;

  [ObservableProperty]
  private bool _rangingEnabled;

  [ObservableProperty]
  private NvPairPeriodChoice? _selectedPeriod;

  [ObservableProperty]
  private decimal _exchanges = 1;

  [ObservableProperty]
  private decimal _marginMs;

  [ObservableProperty]
  private decimal _spanKhz;

  [ObservableProperty]
  private decimal _powerDbm;

  [ObservableProperty]
  private string _addressText = "";

  [ObservableProperty]
  private bool _advancedShown;

  [ObservableProperty]
  private decimal _phasePackets;

  [ObservableProperty]
  private bool _differencesOnly = true;

  [ObservableProperty]
  private string _modem1TabName;

  [ObservableProperty]
  private string _modem2TabName;

  [ObservableProperty]
  private string _rolesText = "";

  [ObservableProperty]
  private bool _rolesProblem;

  [ObservableProperty]
  private string _switchHint = "";

  [ObservableProperty]
  private bool _switchHintVisible;

  [ObservableProperty]
  private string _pauseText = "";

  [ObservableProperty]
  private bool _spanHintVisible = true;

  [ObservableProperty]
  private string _phaseHint = "";

  [ObservableProperty]
  private bool _phaseProblem;

  [ObservableProperty]
  private string _lpsHint = "";

  [ObservableProperty]
  private bool _lpsHintVisible;

  [ObservableProperty]
  private string _peerDifferences = "";

  [ObservableProperty]
  private bool _peerDifferencesVisible;

  [ObservableProperty]
  private string _comparisonSummary = "Reading both modems…";

  [ObservableProperty]
  private string _status = "Reading both verified devices…";

  [ObservableProperty]
  private string _applyText = "Verify and save both modems";

  [ObservableProperty]
  private bool _canApply;

  [ObservableProperty]
  private bool _formEnabled = true;

  [ObservableProperty]
  private bool _peerEnabled = true;

  [ObservableProperty]
  private bool _managerEnabled;

  [ObservableProperty]
  private bool _marginEnabled;

  [ObservableProperty]
  private bool _spanEnabled;

  [ObservableProperty]
  private string _distanceTitle = "Distance on Radio 1";

  /// <summary>True while a pair save or a switch-off is under way.</summary>
  public bool Active => _state is PairPhase.CollectHashes or PairPhase.VerifyProof
      or PairPhase.Saving or PairPhase.SwitchingOff;

  private string ChannelPrefix => $"CH{SelectedChannelIndex + 1}_";
  private string ChannelName => Channels[Math.Clamp(SelectedChannelIndex, 0, 1)];

  /// <summary>Starts reading both modems; call once the frames are wired.</summary>
  internal void Open() {
    for (int i = 0; i < 2; i++) {
      _editors[i].ObserveTarget(_opening[i], 0);
    }
    Refresh();
  }

  internal void Service() {
    foreach (Nv5NativeSettingsEditor editor in _editors) {
      editor.Service();
    }
    Refresh();
  }

  internal void Receive(MAVLink.MAVLinkMessage message, object route) {
    foreach (Nv5NativeSettingsEditor editor in _editors) {
      editor.Receive(message, route);
    }
  }

  /// <summary>A modem of the page: a label for Modem 1, or a candidate for Modem 2.</summary>
  internal void Offer(Target target, uint uptime, string label) {
    if (!target.Verified || target.Uid is not { Length: 18 }) {
      return;
    }
    string text = ModemText(target, label);
    string uid = UidKey(target.Uid);
    if (uid == _fixedUid) {
      if (Follows(_editors[0].Target ?? _opening[0], target)) {
        FixedName = text;
      }
      return;
    }
    if (_peers.TryGetValue(uid, out Peer? known) && !Follows(known.Target, target)) {
      return;
    }
    _peers[uid] = new Peer(target, uptime, label);
    NvPairPeerChoice? listed = Peers.FirstOrDefault(choice => choice.Uid == uid);
    if (listed == null) {
      // Listed by system and component, wherever the modem the window opened on sits.
      int position = 0;
      for (; position < Peers.Count; position++) {
        Target other = _peers[Peers[position].Uid].Target;
        if (other.System > target.System
            || (other.System == target.System && other.Component > target.Component)) {
          break;
        }
      }
      Peers.Insert(position, new NvPairPeerChoice(uid, text));
    } else {
      listed.Label = text;
    }
    if (uid == _peerUid && !_peerSwitch && _names[1] != TabName(1, target)) {
      _names[1] = TabName(1, target);
      Modem2TabName = _names[1];
    }
  }

  /// <summary>The current route and boot of a modem, which a later switch to it starts
  /// from.</summary>
  internal void Observe(Target target, uint uptime) {
    string uid = UidKey(target.Uid);
    if (_peers.TryGetValue(uid, out Peer? known) && Follows(known.Target, target)) {
      _peers[uid] = known with { Target = target, Uptime = uptime };
    }
    foreach (Nv5NativeSettingsEditor editor in _editors) {
      if (editor.Target is { } current && UidKey(current.Uid) == uid && Follows(current, target)) {
        editor.ObserveTarget(target, uptime);
      }
    }
  }

  /// <summary>
  /// Whether a passport on <paramref name="next"/>'s route reaches a modem held on
  /// <paramref name="current"/>'s: always on the same route, and from another one only once the
  /// held route has closed. A second open link to the same modem would otherwise take it over,
  /// restarting its read or failing its save.
  /// </summary>
  private bool Follows(Target current, Target next) =>
      Equals(current.Route, next.Route) || !_routeOpen(current.Route);

  /// <summary>Closes unless a save is under way; drops what was staged and frees both
  /// modems' read sessions.</summary>
  internal bool TryClose() {
    if (Active) {
      Status = "Wait for both save outcomes before closing this window.";
      return false;
    }
    foreach (Nv5NativeSettingsEditor editor in _editors) {
      editor.Discard();
      // A modem serves one read at a time; one this window leaves half done is freed now.
      editor.StopReading();
    }
    return true;
  }

  /// <summary>Saves the form to both modems, or only switches ranging off, modem by
  /// modem.</summary>
  internal void Apply() {
    if (SwitchOffOnly(out _)) {
      SwitchOff();
      return;
    }
    if (Active || !_editors[0].CanBeginSave || !_editors[1].CanBeginSave) {
      return;
    }
    _offAttempted = false;
    Array.Fill(_preSave, NoTexts);
    if (!AddressValue(out uint address)) {
      Refuse("Enter a nonzero ranging address of up to eight hexadecimal digits.");
      return;
    }
    uint period = SelectedPeriod?.Packets ?? 0;
    if (period < MinimumPeriodPackets || (period & (period - 1)) != 0) {
      Refuse("Choose a period that is a power of two.");
      return;
    }
    if ((uint)PhasePackets >= period) {
      AdvancedShown = true;
      Refuse("The phase must be below the period.");
      return;
    }
    string prefix = ChannelPrefix;
    foreach (Nv5NativeSettingsEditor editor in _editors) {
      if (!Supported(editor.ReadValues, prefix + "RNG_PAD_MS")) {
        Refuse("Update both modems to configure the ranging window margin.");
        return;
      }
    }
    string managerName = prefix + "RNG_MGR";
    int choosers = _editors.Count(editor => editor.ReadValues.ContainsKey(managerName));
    if (choosers == 1) {
      Refuse("The two modems run different firmware. Update both before setting up ranging.");
      return;
    }
    // Without the setting the receiver measures, which is what choice 0 means.
    uint side = choosers == 2 ? (uint)SelectedManagerIndex : 0;
    for (int i = 0; i < 2 && choosers == 2 && RangingEnabled; i++) {
      IReadOnlyDictionary<string, Value> held = _editors[i].ReadValues;
      if (Raw(held, prefix + "RNG_EN") != 0 && Raw(held, managerName) != side) {
        Refuse("Ranging is on with the other end measuring. Switch it off and save, then choose "
            + "the measuring modem and switch it on again.");
        return;
      }
    }
    uint firstRole = Raw(_editors[0].CandidateValues(), prefix + "ROLE");
    uint secondRole = Raw(_editors[1].CandidateValues(), prefix + "ROLE");
    if (firstRole == secondRole || firstRole > 1 || secondRole > 1) {
      Fail("Choose a working TX/RX link. These radios do not have opposite roles.");
      return;
    }
    var fields = new List<(string Suffix, string Text)> {
      ("EN", RangingEnabled ? "1" : "0"),
      ("PER", Text(period)),
      ("PHASE", Text((uint)PhasePackets)),
      ("N", Text((uint)Exchanges)),
      ("PAD_MS", Text((uint)MarginMs)),
      ("SPAN", Text((uint)Math.Round(SpanKhz * 1000m, MidpointRounding.AwayFromZero))),
      ("PWR", ((int)PowerDbm).ToString(CultureInfo.InvariantCulture)),
      ("ADDR", Text(address)),
    };
    if (choosers == 2) {
      fields.Add(("MGR", Text(side)));
    }
    for (int i = 0; i < 2; i++) {
      _preSave[i] = _editors[i].TableTexts(StagedSuffixes.Select(suffix => prefix + suffix));
    }
    foreach (Nv5NativeSettingsEditor editor in _editors) {
      foreach ((string suffix, string text) in fields) {
        if (!editor.SetValue(prefix + "RNG_" + suffix, text)) {
          Fail("The selected radio does not support these distance settings.");
          return;
        }
      }
    }
    // The end that answers saves first. That is the transmitter unless it is the one measuring.
    int transmitter = firstRole == 1 ? 0 : 1;
    _first = side == 1 ? 1 - transmitter : transmitter;
    _secondStarted = false;
    Array.Clear(_prepared);
    Array.Clear(_complete);
    SetState(PairPhase.CollectHashes);
    Status = "Checking both candidate link contracts…";
    // Both begin, or neither goes on: a check that one modem cannot start ends the other's.
    for (int i = 0; i < 2 && _state == PairPhase.CollectHashes; i++) {
      if (!_editors[i].Prepare() && _state == PairPhase.CollectHashes) {
        Fail($"{_names[i]} could not start its check, so neither modem was changed. "
            + _editors[i].StatusText);
      }
    }
  }

  /// <summary>The candidate whose live radio roles and carrier look like the other end of a
  /// link, or 0.</summary>
  internal static int LikelyPeer(IReadOnlyDictionary<int, NvPairLink> own,
      IReadOnlyList<IReadOnlyDictionary<int, NvPairLink>> candidates) {
    for (int i = 0; i < candidates.Count; i++) {
      foreach ((int channel, NvPairLink mine) in own.OrderBy(pair => pair.Key)) {
        if (!candidates[i].TryGetValue(channel, out NvPairLink theirs)) {
          continue;
        }
        bool opposite = mine.Role <= 1 && theirs.Role <= 1 && mine.Role != theirs.Role;
        // A hopping radio reports the carrier of the moment, so only its span is comparable.
        bool sameBand = mine.BandwidthHz == theirs.BandwidthHz
            && (mine.BandwidthHz != 0 || mine.FrequencyHz == theirs.FrequencyHz);
        if (opposite && sameBand && mine.Modulation == theirs.Modulation) {
          return i;
        }
      }
    }
    return 0;
  }

  /// <summary>
  /// The data pause a burst takes, in milliseconds: the modem's fixed airtimes, CPU and handoff
  /// cells and full-window relative drift ahead of the exchanges and the margin. These are
  /// reservation budgets, not measured maxima.
  /// </summary>
  internal static int DistancePauseMs(int exchanges, int marginMs) {
    ulong tail = (ulong)(WindowFixedMs + WindowPerExchangeMs * exchanges + marginMs) * 1000;
    ulong prefix = 0;
    for (int pass = 0; pass < 8; pass++) {
      ulong skew = 1000 + ((prefix + tail) * 100 + 999999) / 1000000;
      ulong requestEnd = 20000 + 27968 + 2000 + 100 + 1000 + skew;
      ulong responseArm = requestEnd + 20000 + 10000 + 15000 + 1000 + 1000 + skew;
      ulong responseEnd = responseArm + 85568 + 2000 + 100 + 1000 + skew;
      ulong required = (responseEnd + 40000 + 1000 + 1000 + skew + 999) / 1000 * 1000;
      if (required == prefix) {
        return (int)((prefix + tail) / 1000);
      }
      prefix = required;
    }
    return 0;
  }

  partial void OnSelectedPeerChanged(NvPairPeerChoice? value) {
    if (value == null) {
      return;
    }
    // Applied by Refresh as soon as the second editor is idle.
    _peerSwitch = value.Uid != _peerUid;
    Refresh();
  }

  partial void OnSelectedChannelIndexChanged(int value) {
    _loaded = false;
    Refresh();
  }

  partial void OnSelectedManagerIndexChanged(int value) => Refresh();

  partial void OnRangingEnabledChanged(bool value) => Refresh();

  partial void OnDifferencesOnlyChanged(bool value) => Refresh();

  partial void OnSelectedPeriodChanged(NvPairPeriodChoice? value) => UpdateHints();

  partial void OnExchangesChanged(decimal value) => UpdateHints();

  partial void OnMarginMsChanged(decimal value) => UpdateHints();

  partial void OnPhasePacketsChanged(decimal value) => UpdateHints();

  private bool SwitchOffOnly(out string blocked) {
    blocked = "";
    // Off needs no check against the peer, so it does not wait for both complete reads, which a
    // modem that keeps restarting may never finish. Such a modem may also restart in the middle
    // of a pair save; switched off on its own, it is simply tried again on its next boot.
    bool steady = !_editors[0].RestartedRecently && !_editors[1].RestartedRecently;
    string marginName = ChannelPrefix + "RNG_PAD_MS";
    bool marginSupported = _editors.All(editor => Supported(editor.ReadValues, marginName));
    if (Active || _peerSwitch || _state == PairPhase.Done || RangingEnabled
        || (_editors[0].CanBeginSave && _editors[1].CanBeginSave && steady && marginSupported)) {
      return false;
    }
    int channel = SelectedChannelIndex;
    int[] states = [_editors[0].RangingState(channel), _editors[1].RangingState(channel)];
    if (states[0] != 1 && states[1] != 1) {
      return false;
    }
    for (int i = 0; i < 2; i++) {
      if (states[i] == 0) {
        continue;
      }
      if (_editors[i].HasPendingChanges()) {
        blocked = $"{_names[i]} has unsaved edits in its tab, which switching ranging off would "
            + "not save. Drop them with Read settings first.";
        return false;
      }
      if (_editors[i].Target is not { Verified: true }) {
        return false;
      }
    }
    return true;
  }

  private void SetPeriod(uint packets) {
    bool quiet = _quiet;
    _quiet = true;
    Periods.Clear();
    for (uint value = MinimumPeriodPackets; value <= MaximumPeriodPackets; value *= 2) {
      Periods.Add(new NvPairPeriodChoice(value, Text(value)));
    }
    if (packets < MinimumPeriodPackets) {
      packets = DefaultPeriodPackets;
    }
    NvPairPeriodChoice? choice = Periods.FirstOrDefault(period => period.Packets == packets);
    if (choice == null) {
      // Show what the modem holds rather than silently replacing it.
      choice = new NvPairPeriodChoice(packets, $"{Text(packets)} (not a power of two)");
      Periods.Add(choice);
    }
    SelectedPeriod = choice;
    _quiet = quiet;
  }

  private void UpdateHints() {
    if (_quiet) {
      return;
    }
    int exchanges = (int)Exchanges;
    int pauseMs = DistancePauseMs(exchanges, (int)MarginMs);
    PauseText = !_marginSupported
        ? "Update both modems to configure the ranging window margin."
        : "Data pause on this radio per burst: about " + (pauseMs < 1000
            ? $"{Text((uint)pauseMs)} ms"
            : (pauseMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + " s") + ".";
    // The field's tip explains the span; the line appears only to say why it is greyed.
    SpanHintVisible = exchanges == 1;
    SpanEnabled = !Active && exchanges > 1;
    // A phase the new period no longer covers stays as entered and is flagged, never rewritten.
    uint period = SelectedPeriod?.Packets ?? 0;
    bool phaseFits = (uint)PhasePackets < period;
    PhaseHint = phaseFits ? "Below the period; 0 always fits."
        : $"Must be below the period of {Text(period)}.";
    PhaseProblem = !phaseFits;
    if (!phaseFits) {
      AdvancedShown = true;
    }
  }

  private void LoadForm(IReadOnlyDictionary<string, Value> values) {
    string prefix = ChannelPrefix + "RNG_";
    bool quiet = _quiet;
    _quiet = true;
    RangingEnabled = Raw(values, prefix + "EN") != 0;
    SetPeriod(Raw(values, prefix + "PER"));
    _quiet = true;
    MarginMs = Math.Clamp(Raw(values, prefix + "PAD_MS"), 0, MaximumMarginMs);
    Exchanges = Math.Clamp(Raw(values, prefix + "N"), 1, MaximumExchanges);
    PhasePackets = Math.Clamp(Raw(values, prefix + "PHASE"), 0, MaximumPeriodPackets - 1);
    SpanKhz = Raw(values, prefix + "SPAN") / 1000m;
    PowerDbm = Math.Clamp(unchecked((int)Raw(values, prefix + "PWR")), MinimumPowerDbm,
        MaximumPowerDbm);
    AddressText = AddressString(Raw(values, prefix + "ADDR"));
    SelectedManagerIndex = Raw(values, prefix + "MGR") == 1 ? 1 : 0;
    _quiet = quiet;
    UpdateHints();
    // A stored offset is never left out of sight.
    if (PhasePackets != 0) {
      AdvancedShown = true;
    }
  }

  private void Refresh() {
    if (_refreshing || _quiet) {
      return;
    }
    _refreshing = true;
    try {
      RefreshNow();
    } finally {
      _refreshing = false;
    }
  }

  private void RefreshNow() {
    // A read in progress is left to finish; cutting it off would fail the editor instead. One
    // that only waits to be tried again has nothing to lose, so the switch need not wait for it.
    if (_peerSwitch && !Active && (!_editors[1].Busy || _editors[1].Retrying)) {
      SelectPeer();
    }
    // After a save each editor reads its modem again. Once both have, the pair is open for the
    // next change; until then the merged values still carry the old link hashes.
    if (_state == PairPhase.Done && _editors[0].Ready && _editors[1].Ready
        && _editors[0].ValuesReadBack && _editors[1].ValuesReadBack) {
      SetState(PairPhase.Ready);
    }
    // A modem the device refused as out of date must be read again first, or a save would begin
    // on the other modem alone.
    bool ready = !Active && !_peerSwitch && _editors[0].CanBeginSave
        && _editors[1].CanBeginSave;
    bool offOnly = SwitchOffOnly(out string offBlocked);
    CanApply = (ready || offOnly) && _state != PairPhase.Done;
    FormEnabled = !Active;
    PeerEnabled = !Active && _state != PairPhase.Done;
    SpanEnabled = !Active && Exchanges > 1;
    // Until the second editor has moved to the chosen modem, its values belong to the old one.
    IReadOnlyDictionary<string, Value>[] values = [_editors[0].CandidateValues(),
        _peerSwitch ? NoValues : _editors[1].CandidateValues()];
    IReadOnlyDictionary<string, Value>[] read = [_editors[0].ReadValues,
        _peerSwitch ? NoValues : _editors[1].ReadValues];
    string prefix = ChannelPrefix;
    bool editedHere = false;
    bool editedElsewhere = false;
    for (int i = 0; i < 2; i++) {
      foreach ((string name, Value value) in values[i]) {
        if (!read[i].TryGetValue(name, out Value? held) || !value.Bytes.SequenceEqual(held.Bytes)) {
          if (name.StartsWith(prefix, StringComparison.Ordinal)) {
            editedHere = true;
          } else {
            editedElsewhere = true;
          }
        }
      }
    }
    // The data roles are each modem's own. Which end measures is one shared setting; firmware
    // without it always measures on the receiver, which is also what a missing value reads as.
    string managerName = prefix + "RNG_MGR";
    bool bothRead = values[0].Count != 0 && values[1].Count != 0;
    string marginName = prefix + "RNG_PAD_MS";
    _marginSupported = bothRead && Supported(values[0], marginName)
        && Supported(values[1], marginName);
    MarginEnabled = !Active && _marginSupported;
    UpdateHints();
    uint[] roles = [Raw(values[0], prefix + "ROLE"), Raw(values[1], prefix + "ROLE")];
    bool opposite = bothRead && roles[0] <= 1 && roles[1] <= 1 && roles[0] != roles[1];
    _receiver = opposite ? (roles[0] == 0 ? 0 : 1) : -1;
    _choosers = bothRead
        ? (values[0].ContainsKey(managerName) ? 1 : 0) + (values[1].ContainsKey(managerName) ? 1 : 0)
        : 0;
    _measuring = !bothRead ? -1 : opposite ? -1 : -2;
    if (opposite) {
      // A receiver measures under choice 0 and a transmitter under choice 1.
      bool[] measures = [roles[0] == Raw(values[0], managerName),
          roles[1] == Raw(values[1], managerName)];
      if (measures[0] != measures[1]) {
        _measuring = measures[0] ? 0 : 1;
      }
    }
    ManagerChoices[0].Label = _receiver < 0 ? "The data receiver"
        : $"Modem {_receiver + 1} · receiver";
    ManagerChoices[1].Label = _receiver < 0 ? "The data transmitter"
        : $"Modem {2 - _receiver} · transmitter";
    if (bothRead && _choosers != 2 && SelectedManagerIndex != 0) {
      SelectedManagerIndex = 0;
    }
    ManagerEnabled = !Active && _choosers == 2;
    uint side = (uint)Math.Max(0, SelectedManagerIndex);
    // The firmware asks for ranging to be off on both modems before the measuring end changes.
    bool swapsWhileOn = false;
    for (int i = 0; i < 2 && _choosers == 2 && RangingEnabled; i++) {
      swapsWhileOn |= Raw(read[i], prefix + "RNG_EN") != 0 && Raw(read[i], managerName) != side;
    }
    RolesProblem = bothRead && (!opposite || _choosers == 1 || swapsWhileOn);
    RolesText = !bothRead ? "Roles follow once both modems are read."
        : !opposite
            ? $"{ChannelName}: the modems do not have opposite data roles, so neither can measure."
        : _choosers == 1
            ? "Only one of the modems lets the measuring end be chosen: they run different "
                + "firmware."
        : _choosers == 0 ? "This firmware always measures on the modem that receives data."
        : swapsWhileOn
            ? "Ranging is on with the other end measuring. Switch it off and save, then choose "
                + "the measuring modem and switch it on again."
        : $"Modem {(side == 0 ? 2 - _receiver : _receiver + 1)} answers the requests.";
    // What the modems do now, and what the next save will switch.
    bool[] on = [Raw(read[0], prefix + "RNG_EN") != 0, Raw(read[1], prefix + "RNG_EN") != 0];
    bool ticked = RangingEnabled;
    // Why switching off goes modem by modem, or why switching on has to wait.
    var unread = new List<string>();
    var restarting = new List<string>();
    var stale = new List<string>();
    for (int i = 0; i < 2; i++) {
      if (!_editors[i].Ready) {
        unread.Add(_names[i]);
      } else if (!_editors[i].CanBeginSave && !(i == 1 && _peerSwitch)) {
        stale.Add(_names[i]);
      }
      if (_editors[i].RestartedRecently) {
        restarting.Add(_names[i]);
      }
    }
    string aside = "";
    if (offBlocked.Length != 0) {
      aside = offBlocked;
    } else if (offOnly && unread.Count != 0) {
      aside = (unread.Count == 2 ? $"{unread[0]} and {unread[1]} are" : $"{unread[0]} is")
          + " not read. Ranging can still be switched off without reading: each modem saves it "
          + "off on its own, without the pair check, and keeps its other settings.";
    } else if (offOnly && restarting.Count != 0) {
      aside = string.Join(" and ", restarting) + " restarted within the last minute. Saving "
          + "switches ranging off on each modem on its own, without the pair check, so that a "
          + "restart cannot cut a pair save short; each keeps its other settings.";
    } else if (offOnly) {
      aside = "Ranging can be switched off without timing-margin support: each modem saves it "
          + "off on its own and keeps its other settings. Update both modems before changing the "
          + "pair settings.";
    } else if (stale.Count != 0 && !Active && _state != PairPhase.Done) {
      aside = stale.Count == 2
          ? $"{stale[0]} and {stale[1]} changed after they were read. Use Read settings in their "
              + "tabs before saving."
          : $"{stale[0]} changed after it was read. Use Read settings in its tab before saving.";
    } else if (!bothRead && ticked && !Active && _state != PairPhase.Done) {
      aside = "Ranging can be switched on once both modems are read.";
    }
    SwitchHintVisible = bothRead || _state == PairPhase.Done || aside.Length != 0;
    SwitchHint = _state == PairPhase.Done
        ? (_editors[0].Busy || _editors[1].Busy
            ? "Saved. Both modems are being read again; then the pair can be changed again."
            : "Saved. Read the modem that was not read again (Read settings in its tab) before "
                + "the next change.")
        : aside.Length != 0 || !bothRead ? aside
        : on[0] != on[1]
            ? $"Ranging is on at Modem {(on[0] ? 1 : 2)} only. Saving sets both modems the same way."
        : on[0]
            ? (ticked ? "Ranging is on. To switch it off on both modems, untick this and save."
                : "Saving switches ranging off on both modems.")
        : ticked ? "Saving switches ranging on at both modems."
        : "Ranging is off. To switch it on, tick this and save.";
    ApplyText = offOnly || (bothRead && !ticked && (on[0] || on[1]))
        ? "Switch ranging off on both modems"
        : bothRead && ticked && !on[0] && !on[1]
            ? "Verify and switch ranging on at both modems"
            : "Verify and save both modems";
    UpdateDistance();
    UpdateComparison(values[0], values[1], editedHere, editedElsewhere);
    // What the modems hold, not what is staged: the note must outlast a failed attempt.
    UpdatePeerDifferences(read[0], read[1]);
    // LPS on this radio takes its burst from the same exchanges, span and power.
    var lpsModems = new List<string>();
    for (int i = 0; i < 2; i++) {
      if (Raw(read[i], "LPS_ROLE") != 0
          && Raw(read[i], "LPS_RADIO") == (uint)(SelectedChannelIndex + 1)) {
        lpsModems.Add($"Modem {i + 1}");
      }
    }
    LpsHintVisible = lpsModems.Count != 0;
    if (lpsModems.Count != 0) {
      LpsHint = (lpsModems.Count == 2
          ? $"Both modems use {ChannelName} for LPS, with these exchanges, span and power."
          : $"{lpsModems[0]} uses {ChannelName} for LPS, with these exchanges, span and power.")
          + " Pair ranging cannot run on it meanwhile.";
    }
    // Every modem that could not be read is named, with its own reason. The outcome of a
    // switch-off stays in view instead.
    var unreadable = new List<string>();
    for (int i = 0; i < 2; i++) {
      if (!Active && !_peerSwitch && !_offAttempted && read[i].Count == 0 && _editors[i].Failed) {
        unreadable.Add($"{_names[i]} could not be read. {_editors[i].StatusText}");
      }
    }
    if (unreadable.Count != 0) {
      Status = string.Join(" ", unreadable);
    }
    if (!ready) {
      return;
    }
    if (!_loaded) {
      LoadForm(values[0]);
      _loaded = _announceRead = true;
      // The lines above were made for the form as it was; make them again for what it holds now.
      RefreshNow();
      return;
    }
    if (_announceRead) {
      _announceRead = false;
      Status = "Both modems read. Set the common distance settings, then verify and save.";
    }
  }

  private void SelectPeer() {
    _peerSwitch = false;
    if (SelectedPeer is not { } choice || !_peers.TryGetValue(choice.Uid, out Peer? peer)) {
      return;
    }
    _peerUid = choice.Uid;
    _names[1] = TabName(1, peer.Target);
    Modem2TabName = _names[1];
    // An earlier failure concerned another pair.
    if (_state == PairPhase.Failed) {
      SetState(PairPhase.Ready);
    }
    _offAttempted = false;
    _announceRead = true;
    Status = $"Reading {_names[1]}…";
    _editors[1].ObserveTarget(peer.Target, peer.Uptime);
  }

  private void UpdateDistance() {
    DistanceTitle = $"Distance on {ChannelName}";
    for (int i = 0; i < 2; i++) {
      DistanceRows[i].Show(_editors[i].DistanceRow(SelectedChannelIndex));
    }
  }

  private void UpdatePeerDifferences(IReadOnlyDictionary<string, Value> first,
      IReadOnlyDictionary<string, Value> second) {
    var differences = new List<string>();
    if (first.Count != 0 && second.Count != 0) {
      string prefix = ChannelPrefix + "RNG_";
      bool Differs(string suffix) => Raw(first, prefix + suffix) != Raw(second, prefix + suffix);
      uint PeerValue(string suffix) => Raw(second, prefix + suffix);
      if (Differs("EN")) {
        differences.Add(PeerValue("EN") != 0 ? "ranging on" : "ranging off");
      }
      if (Differs("PER")) {
        differences.Add($"period {Text(PeerValue("PER"))}");
      }
      if (Differs("PHASE")) {
        differences.Add($"phase {Text(PeerValue("PHASE"))}");
      }
      if (Differs("N")) {
        differences.Add($"exchanges {Text(PeerValue("N"))}");
      }
      if (Differs("SPAN")) {
        differences.Add("hop span "
            + (PeerValue("SPAN") / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + " kHz");
      }
      if (Differs("PWR")) {
        differences.Add($"power {unchecked((int)PeerValue("PWR"))} dBm");
      }
      if (Differs("ADDR")) {
        differences.Add($"address {AddressString(PeerValue("ADDR"))}");
      }
      if (Differs("MGR")) {
        differences.Add(PeerValue("MGR") == 1 ? "measured by the transmitter"
            : "measured by the receiver");
      }
    }
    PeerDifferencesVisible = differences.Count != 0;
    if (differences.Count != 0) {
      PeerDifferences = $"{_names[1]} now has: {string.Join(", ", differences)}. Saving writes "
          + "this form to both modems.";
    }
  }

  private void UpdateComparison(IReadOnlyDictionary<string, Value> first,
      IReadOnlyDictionary<string, Value> second, bool editedHere, bool editedElsewhere) {
    var rows = new List<NvPairComparisonRow>();
    string summary = "Reading both modems…";
    string radio = ChannelName;
    if (first.Count == 0 || second.Count == 0) {
      int unread = first.Count == 0 ? 0 : 1;
      var progress = new List<string>();
      for (int i = 0; i < 2 && !_peerSwitch; i++) {
        if (_editors[i].ReadProgress.Length != 0) {
          progress.Add($"{_names[i]}: {_editors[i].ReadProgress}");
        }
      }
      // A read that is waiting to be repeated says why, in the words of its own tab.
      int waiting = -1;
      foreach (int i in (int[])[unread, 1 - unread]) {
        if (waiting < 0 && !_peerSwitch && _editors[i].Retrying) {
          waiting = i;
        }
      }
      if (_state == PairPhase.SwitchingOff) {
        summary = $"{radio}: switching ranging off on each modem…";
      } else if (!_peerSwitch && _editors[unread].Failed) {
        summary = $"{_names[unread]} could not be read. Use Read settings in its tab.";
      } else if (waiting >= 0) {
        summary = $"{_names[waiting]}: {_editors[waiting].StatusText}";
      } else if (progress.Count != 0) {
        summary = $"Reading both modems… {string.Join(", ", progress)}";
      }
    } else {
      string prefix = ChannelPrefix;
      string roleName = prefix + "ROLE";
      string linkHashName = prefix + "LINK_HASH";
      var names = first.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
          .Concat(second.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
              && !first.ContainsKey(name)))
          .OrderBy(name => name, StringComparer.Ordinal)
          .ToList();
      // The roles decide which modem answers and which measures; keep them on top.
      if (names.Remove(roleName)) {
        names.Insert(0, roleName);
      }
      string Shown(IReadOnlyDictionary<string, Value> values, string name) {
        if (!values.TryGetValue(name, out Value? found)) {
          return "—";
        }
        if (!Supported(values, name)) {
          return "n/a";
        }
        if ((found.Flags & Nv5ConfigValueFlags.Secret) != 0) {
          return "hidden";
        }
        if (name == prefix + "RNG_ADDR") {
          return AddressString(Raw(values, name));
        }
        if (name == prefix + "RNG_MGR") {
          uint measuring = Raw(values, name);
          return $"{Text(measuring)} · "
              + (measuring == 0 ? "RX measures" : measuring == 1 ? "TX measures" : "unknown");
        }
        if (name == roleName) {
          uint role = Raw(values, name);
          return $"{Text(role)} · "
              + (role == 0 ? "RX" : role == 1 ? "TX" : role == 2 ? "TX/RX" : "unknown");
        }
        return Nv5NativeSettingsEditor.Display(found);
      }
      bool Same(string name) {
        if (!first.TryGetValue(name, out Value? a) || !second.TryGetValue(name, out Value? b)) {
          return false;
        }
        if (!Supported(first, name) || !Supported(second, name)) {
          return Supported(first, name) == Supported(second, name);
        }
        return a.Type == b.Type && a.Bytes.SequenceEqual(b.Bytes);
      }
      int shared = 0;
      bool badRoles = false;
      foreach (string name in names) {
        string suffix = name[prefix.Length..];
        bool secret = (((first.TryGetValue(name, out Value? a) ? a.Flags : 0)
            | (second.TryGetValue(name, out Value? b) ? b.Flags : 0))
            & Nv5ConfigValueFlags.Secret) != 0;
        NvPairComparisonKind kind = NvPairComparisonKind.Same;
        string note = "";
        string tip = "";
        if (name == roleName) {
          uint firstRole = Raw(first, name);
          uint secondRole = Raw(second, name);
          badRoles = firstRole == secondRole || firstRole > 1 || secondRole > 1
              || !Supported(first, name) || !Supported(second, name);
          kind = badRoles ? NvPairComparisonKind.Problem : NvPairComparisonKind.Neutral;
          note = badRoles ? "Not opposite"
              : _measuring >= 0 ? $"Modem {_measuring + 1} measures"
              : "Opposite roles";
          tip = "Ranging runs on a link with opposite data roles. Pair setup chooses which end "
              + "measures the distance; the other one answers.";
        } else if (Same(name)) {
          if (secret) {
            note = "Same";
            tip = "The values are hidden; they are identical on both modems.";
          }
        } else if (suffix.StartsWith("RNG_", StringComparison.Ordinal) || suffix == "PEER_HASH") {
          kind = NvPairComparisonKind.Neutral;
          note = "Set by pair setup";
          tip = "Saving writes this value to both modems.";
        } else if (PerModemSettings.Contains(suffix)) {
          kind = NvPairComparisonKind.Neutral;
          note = "May differ";
          tip = "The two ends of one link may use different values; the link fingerprint ignores "
              + "this setting.";
        } else if (suffix is "LINK_HASH" or "KEY_HASH") {
          kind = NvPairComparisonKind.Derived;
          note = suffix == "LINK_HASH" ? "Link fingerprint" : "Key fingerprint";
          tip = "Each modem derives this value from its own settings; it differs while one of them "
              + "differs.";
        } else {
          kind = NvPairComparisonKind.Differs;
          note = secret ? "Differs (hidden)" : "Differs";
          tip = "Both ends of one link normally need the same value.";
          shared++;
        }
        rows.Add(new NvPairComparisonRow(name, Shown(first, name), Shown(second, name), note, tip,
            kind));
      }
      // The fingerprint covers the ranging schedule only while ranging is enabled, and never its
      // power or the peer proof. Only then can differing fingerprints be blamed on other rows.
      uint Ranging(IReadOnlyDictionary<string, Value> values, string suffix) =>
          Raw(values, prefix + "RNG_" + suffix);
      bool[] enabled = [Ranging(first, "EN") != 0, Ranging(second, "EN") != 0];
      bool rangingDiffers = enabled[0] != enabled[1];
      if (enabled[0] && enabled[1]) {
        foreach (string suffix in (string[])["MGR", "PER", "PHASE", "ADDR", "N", "SPAN", "PAD_MS"]) {
          rangingDiffers |= Ranging(first, suffix) != Ranging(second, suffix);
        }
      }
      bool fingerprints = Supported(first, linkHashName) && Supported(second, linkHashName);
      if (_state == PairPhase.SwitchingOff) {
        summary = $"{radio}: switching ranging off on each modem…";
      } else if (Active) {
        summary = $"{radio}: verifying and saving both modems…";
      } else if (badRoles) {
        summary = $"{radio}: the two radios do not have opposite roles. Ranging needs one "
            + "transmitter and one receiver.";
      } else if (editedHere) {
        summary = $"{radio}: unsaved edits are included below, so the modems confirm the match "
            + $"only when you verify and save. Differing shared settings: {shared}.";
      } else if (!_editors[0].ValuesReadBack || !_editors[1].ValuesReadBack) {
        summary = _editors[0].Busy || _editors[1].Busy
            ? $"{radio}: reading both modems again…"
            : $"{radio}: a modem was not read again after the save. Use Read settings in its tab "
                + "before comparing.";
      } else if (!fingerprints) {
        summary = $"{radio}: differing shared settings: {shared}.";
      } else if (Same(linkHashName)) {
        summary = shared == 0
            ? $"{radio}: the link settings match on both modems."
            : $"{radio}: the link fingerprints match. Highlighted settings that differ but are "
                + $"unused in the current mode: {shared}.";
      } else if (rangingDiffers) {
        summary = shared == 0
            ? $"{radio}: only the distance settings differ. Saving writes the same values to both "
                + "modems."
            : $"{radio}: the distance settings differ, which saving aligns. Highlighted shared "
                + $"settings differ as well ({shared}); the modems check them when you verify and "
                + "save.";
      } else if (shared != 0) {
        summary = $"{radio}: the link fingerprints differ. Match the highlighted settings before "
            + $"enabling ranging. Differing shared settings: {shared}.";
      } else {
        summary = $"{radio}: the link fingerprints differ although no shared setting differs. The "
            + "modems may run different firmware versions.";
      }
      if (editedElsewhere && !Active) {
        summary += " Other unsaved edits in the modem tabs are saved with the pair.";
      }
    }
    List<NvPairComparisonRow> shownRows = DifferencesOnly
        ? rows.Where(row => row.Kind != NvPairComparisonKind.Same).ToList()
        : rows;
    string shown = summary + "\n" + string.Join("\n", shownRows);
    if (shown == _comparisonShown) {
      return;
    }
    _comparisonShown = shown;
    ComparisonSummary = summary;
    ComparisonRows.Clear();
    foreach (NvPairComparisonRow row in shownRows) {
      ComparisonRows.Add(row);
    }
  }

  private void SwitchOff() {
    int channel = SelectedChannelIndex;
    _offAttempted = true;
    SetState(PairPhase.SwitchingOff);
    // Known off stays untouched: a save would only restart that modem for nothing.
    var names = new List<string>();
    for (int i = 0; i < 2; i++) {
      _offReason[i] = "";
      _offOutcome[i] = _editors[i].RangingState(channel) == 0 ? 1 : 0;
      if (_offOutcome[i] == 0) {
        names.Add(_names[i]);
      }
    }
    Status = $"Switching ranging off on {string.Join(" and ", names)}, each modem on its own. A "
        + "modem that restarts before its save is tried again when it is back…";
    for (int i = 0; i < 2; i++) {
      if (_offOutcome[i] == 0 && !_editors[i].SwitchRangingOff(channel) && _offOutcome[i] == 0) {
        _offOutcome[i] = -1;
        _offReason[i] = _editors[i].StatusText;
      }
    }
    FinishSwitchOff();
    Refresh();
  }

  private void FinishSwitchOff() {
    if (_state != PairPhase.SwitchingOff || _offOutcome[0] == 0 || _offOutcome[1] == 0) {
      return;
    }
    if (_offOutcome[0] > 0 && _offOutcome[1] > 0) {
      SetState(PairPhase.Done);
      Status = "Ranging is off on both modems, and the pair proof made for it is cleared. Their "
          + "other distance settings were left as they were.";
      return;
    }
    SetState(PairPhase.Failed);
    Status = string.Join(" ", Enumerable.Range(0, 2).Select(i => _offOutcome[i] > 0
        ? $"{_names[i]}: ranging is off."
        : $"{_names[i]}: ranging was not switched off. {_offReason[i]}"));
  }

  private void OnPrepared(int index, bool valid, uint channel1, uint channel2) {
    if (_state is not (PairPhase.CollectHashes or PairPhase.VerifyProof)) {
      return;
    }
    _prepared[index] = true;
    _hash[index] = SelectedChannelIndex == 0 ? channel1 : channel2;
    if (_state == PairPhase.VerifyProof && !valid) {
      Fail("A complete candidate was rejected after peer verification. Match the radio profiles "
          + $"and check the distance values. {_names[index]}: {_editors[index].StatusText}");
      return;
    }
    if (!_prepared[0] || !_prepared[1]) {
      return;
    }
    if (_hash[0] == 0 || _hash[0] != _hash[1]) {
      Fail("Candidate link hashes differ. Match carrier, PHY, framing and distance settings on the "
          + "two radios before enabling ranging.");
      return;
    }
    if (_state == PairPhase.CollectHashes) {
      SetState(PairPhase.VerifyProof);
      Array.Clear(_prepared);
      string name = ChannelPrefix + "PEER_HASH";
      for (int i = 0; i < 2; i++) {
        var proof = new Dictionary<string, string>(StringComparer.Ordinal) {
          [name] = Text(_hash[1 - i]),
        };
        if (!_editors[i].ContinuePreparation(proof)) {
          Fail("Cannot stage the verified peer contract.");
          return;
        }
      }
    } else {
      SetState(PairPhase.Saving);
      Status = "Peer contracts match. Saving the responder, then the manager…";
      if (!_editors[_first].CommitPrepared()) {
        Fail($"Cannot commit the prepared responder. {_editors[_first].StatusText}");
      }
    }
  }

  private void OnDurableSaved(int index) {
    if (_state == PairPhase.Saving && index == _first && !_secondStarted) {
      _secondStarted = true;
      if (!_editors[1 - _first].CommitPrepared()) {
        Fail("Responder saved, but the manager could not start its commit. The pair may be "
            + "partially configured; read both devices before retrying.");
      }
    }
  }

  private void OnConfigurationFinished(int index, bool success) {
    if (_state == PairPhase.SwitchingOff) {
      if (_offOutcome[index] == 0) {
        _offOutcome[index] = success ? 1 : -1;
        if (!success) {
          _offReason[index] = _editors[index].StatusText;
        }
        FinishSwitchOff();
      }
      return;
    }
    if (_state is PairPhase.CollectHashes or PairPhase.VerifyProof) {
      // Nothing is committed while the candidates are checked, so nothing has changed yet.
      if (!success) {
        Fail($"{_names[index]} could not check its candidate, so neither modem was changed. "
            + _editors[index].StatusText);
      }
      return;
    }
    if (!Active || _complete[index]) {
      return;
    }
    if (!success) {
      Fail($"Modem {index + 1} did not confirm completion. The pair may be partially configured. "
          + _editors[index].StatusText);
      return;
    }
    _complete[index] = true;
    if (index == _first && !_secondStarted) {
      _secondStarted = true;
      if (!_editors[1 - _first].CommitPrepared()) {
        Fail("The responder completed, but the manager could not start its commit. Read both "
            + "devices before retrying.");
        return;
      }
    }
    if (_complete[0] && _complete[1]) {
      SetState(PairPhase.Done);
      Status = RangingEnabled
          ? "Both modems saved, restarted and verified. The range is shown on the measuring "
              + "modem; it is raw unless a calibration applies."
          : "Both modems saved, restarted and verified. Ranging is off on both.";
    }
  }

  /// <summary>
  /// Ends a pair save that cannot go on. A transaction not yet committed is aborted, and each table
  /// whose modem was not committed gets back what it showed before the save, edits made in its tab
  /// included.
  /// </summary>
  private void Fail(string reason) {
    SetState(PairPhase.Failed);
    Status = reason;
    foreach (Nv5NativeSettingsEditor editor in _editors) {
      editor.Cancel("Not saved: pair setup stopped before saving.");
    }
    PutBack();
  }

  private void PutBack() {
    for (int i = 0; i < 2; i++) {
      _editors[i].RestoreTableTexts(_preSave[i]);
      _preSave[i] = NoTexts;
    }
  }

  /// <summary>Reports an input problem without touching either device or the staged
  /// edits.</summary>
  private void Refuse(string reason) => Status = reason;

  private void SetState(PairPhase state) {
    bool wasActive = Active;
    _state = state;
    if (Active) {
      CanApply = false;
      FormEnabled = PeerEnabled = ManagerEnabled = MarginEnabled = SpanEnabled = false;
    }
    if (wasActive != Active) {
      OnPropertyChanged(nameof(Active));
    }
  }

  private bool AddressValue(out uint address) {
    address = 0;
    string digits = AddressText.Trim();
    if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
      digits = digits[2..];
    }
    if (digits.Length is 0 or > 8
        || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
            out uint value)
        || value == 0) {
      return false;
    }
    address = value;
    return true;
  }

  private static string TabName(int index, Target target) =>
      $"Modem {index + 1} · {target.System}:{target.Component}";

  private static string ModemText(Target target, string label) {
    string identity = $"{target.System}:{target.Component}";
    return label.Length == 0 ? identity : $"{identity} · {label}";
  }

  private static string AddressString(uint address) =>
      "0x" + address.ToString("X8", CultureInfo.InvariantCulture);

  private static string UidKey(byte[] uid) => Convert.ToHexString(uid);

  private static string Text(uint number) => number.ToString(CultureInfo.InvariantCulture);

  private static bool Supported(IReadOnlyDictionary<string, Value> values, string name) =>
      values.TryGetValue(name, out Value? value)
      && (value.Flags & Nv5ConfigValueFlags.Supported) != 0;

  private static uint Raw(IReadOnlyDictionary<string, Value> values, string name) {
    if (!values.TryGetValue(name, out Value? value)) {
      return 0;
    }
    uint number = 0;
    for (int i = 0; i < Math.Min(4, value.Bytes.Length); i++) {
      number |= (uint)value.Bytes[i] << (8 * i);
    }
    return number;
  }
}
