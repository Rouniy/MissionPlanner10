using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Services;
using Phase = MissionPlanner.Services.Nv5ConfigurationClient.ClientPhase;
using Value = MissionPlanner.Services.Nv5ConfigurationClient.Value;

namespace MissionPlanner.ViewModels.Setup;

/// <summary>What one Distance row shows: whether ranging is on and which end of the link the
/// modem is, the reading, and what the latest burst consisted of.</summary>
/// <param name="Mode">Ranging on or off, and whether this modem measures or answers.</param>
/// <param name="Reading">The plain part of the reading.</param>
/// <param name="Alert">The failure part of the reading, shown after it in red.</param>
/// <param name="Burst">The counts of the latest burst, never a running total.</param>
/// <param name="Text">The whole row as plain text, for its tip.</param>
internal sealed record NvDistanceReadout(
    string Mode, string Reading, string Alert, string Burst, string Text) {
  internal static readonly NvDistanceReadout Empty = new("", "", "", "", "");
}

/// <summary>One row of a Distance group, laid out like Live radio status: a bold name, then the
/// ranging state, the reading and the latest burst.</summary>
public sealed partial class NvDistanceRow : ObservableObject {
  public NvDistanceRow(string name) {
    _name = name;
  }

  [ObservableProperty]
  private string _name;

  [ObservableProperty]
  private string _mode = "";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(AlertSeparator))]
  private string _reading = "";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(AlertSeparator))]
  private string _alert = "";

  [ObservableProperty]
  private string _burst = "";

  [ObservableProperty]
  private string _text = "";

  /// <summary>Separates the plain reading from the failure that follows it.</summary>
  public string AlertSeparator => Reading.Length != 0 && Alert.Length != 0 ? " · " : "";

  internal void Show(NvDistanceReadout readout) {
    Mode = readout.Mode;
    Reading = readout.Reading;
    Alert = readout.Alert;
    Burst = readout.Burst;
    Text = readout.Text;
  }
}

/// <summary>Ranging telemetry, ranging state and the ranging switch-off of one NV5 modem.</summary>
public sealed partial class Nv5NativeSettingsEditor {
  /// <summary>How long, and over how many boots, a ranging switch-off keeps trying before it
  /// gives up.</summary>
  private const uint QuickWriteBudgetMs = 120000;

  private const byte QuickWriteAttempts = 10;

  private readonly Nv5DistanceMessage[] _distance = new Nv5DistanceMessage[2];
  private readonly uint[] _distanceReceived = new uint[2];
  private readonly bool[] _haveDistance = new bool[2];

  // Readout of the latest telemetry; the rows put the ranging state in front of it.
  private readonly string[] _distanceReading = ["", ""];
  private readonly string[] _distanceAlert = ["", ""];
  private readonly string[] _distanceBurst = ["", ""];
  private readonly string[] _distanceText = [NoTelemetry, NoTelemetry];
  private readonly NvDistanceReadout[] _distanceRows = [NvDistanceReadout.Empty,
      NvDistanceReadout.Empty];

  // The only values a ranging switch-off writes.
  private readonly List<Value> _quickWrites = [];
  private bool _quickWrite;
  private uint _quickStarted;
  private byte _quickAttempts;

  private const string NoTelemetry = "No distance telemetry";

  /// <summary>The Distance group of the settings page: one row per radio.</summary>
  public ObservableCollection<NvDistanceRow> DistanceRows { get; } =
      [new NvDistanceRow("Radio 1"), new NvDistanceRow("Radio 2")];

  /// <summary>Ranging state and readout of a zero-based radio index, as the Distance group shows
  /// it.</summary>
  internal NvDistanceReadout DistanceRow(int channel) =>
      channel is 0 or 1 ? _distanceRows[channel] : NvDistanceReadout.Empty;

  /// <summary>Ranging on a zero-based radio index: 1 saved or running on, 0 saved off, -1
  /// unknown.</summary>
  internal int RangingState(int channel) {
    if (channel is < 0 or > 1) {
      return -1;
    }
    string name = $"CH{channel + 1}_RNG_EN";
    // Disabled telemetry describes runtime only; saved ranging may turn on at the next boot.
    if ((_values.ContainsKey(name) && ReadNumber(name) != 0)
        || (_haveDistance[channel] && _distance[channel].Status != Nv5DistanceStatuses.Disabled)) {
      return 1;
    }
    Nv5ConfigResultMessage info = _client.Result;
    if (!_values.ContainsKey(name) || !_readBack || info.CommittedKnown == 0
        || _valuesIdentity.CatalogId != info.CatalogId
        || _valuesIdentity.Revision != info.CommittedRevision
        || !Same(_valuesIdentity.Digest, info.CommittedDigest)) {
      return -1;
    }
    return 0;
  }

  /// <summary>
  /// Saves CHn_RNG_EN = 0 for a zero-based radio index, and clears CHn_PEER_HASH, whose proof
  /// belonged to ranging on, and nothing else, without reading the device first. A restart before
  /// the commit begins it again on the next boot.
  /// </summary>
  internal bool SwitchRangingOff(int channel) {
    if (channel is < 0 or > 1 || _target is not { Verified: true } || _commitSent
        || (Busy && !Reading()) || HasPendingChanges()) {
      return false;
    }
    if (Reading()) {
      EndReadSession();
      KeepForResume();
      ReleaseReader();
    }
    // Ranging off changes the link fingerprint, so the peer proof made for ranging on no longer
    // matches it and the modem refuses the candidate. It is cleared, as the modem's own AT command
    // does when it switches ranging off.
    string prefix = $"CH{channel + 1}_";
    _quickWrites.Clear();
    foreach (string suffix in (string[])["RNG_EN", "PEER_HASH"]) {
      _quickWrites.Add(new Value(prefix + suffix, new byte[4], 0, 0, Nv5ConfigValueTypes.UInt32,
          Nv5ConfigValueFlags.Supported));
    }
    _quickWrite = true;
    _quickStarted = Now();
    _quickAttempts = 0;
    _retries = 0;
    return StartQuickWrite();
  }

  private bool StartQuickWrite() {
    if (_quickAttempts >= QuickWriteAttempts || unchecked(Now() - _quickStarted) > QuickWriteBudgetMs) {
      Fail("Ranging was not switched off: the modem restarted every time before the save could "
          + "finish.");
      return false;
    }
    _quickAttempts++;
    _client = NewClient();
    _started = _commitSent = false;
    _autoCommit = true;
    _commitRequested = _durableEmitted = false;
    _retryPending = _waitingForReader = _cancelPending = false;
    _writes.Clear();
    _writes.AddRange(_quickWrites);
    if (!_client.Discover(_target!, Now())) {
      Fail("Cannot start configuration discovery.");
      return false;
    }
    SetStep(Step.Info);
    SetStatus(_quickAttempts == 1
        ? "Switching ranging off without reading the modem first…"
        : "The modem restarted before the save finished; switching ranging off again…");
    Service();
    return true;
  }

  /// <summary>Begins the switch-off's transaction once the device has answered discovery.</summary>
  private void AdvanceQuickWriteInfo() {
    if (_client.Phase == Phase.RestartRequired) {
      // A save from before waits for its restart, and nothing new may begin until then.
      const string Waiting = "The modem holds saved settings that wait for a restart.";
      if (!RetryLater(Waiting, false)) {
        Fail(Waiting);
      }
      return;
    }
    // No read: the transaction is bound to the committed revision discovery reported.
    if (_client.Begin(FreshNonce(), _client.Result.CommittedRevision, Now())) {
      SetStep(Step.Begin);
    }
  }

  private bool ReceiveDistance(MAVLink.MAVLinkMessage message, object route) {
    if (_target == null || !Equals(route, _target.Route) || message.sysid != _target.System
        || message.compid != _target.Component) {
      return false;
    }
    Nv5DistanceMessage distance = message.ToStructure<Nv5DistanceMessage>();
    if (distance.Channel is >= 1 and <= 2) {
      int channel = distance.Channel - 1;
      _distance[channel] = distance;
      _distanceReceived[channel] = Now();
      _haveDistance[channel] = true;
      UpdateDistance();
    }
    return true;
  }

  /// <summary>A readout of the previous device must not stand under the new one.</summary>
  private void ResetDistance() {
    if (_target != null) {
      _lps.Observe(_target);
    }
    for (int channel = 0; channel < 2; channel++) {
      _haveDistance[channel] = false;
      _distanceReading[channel] = _distanceAlert[channel] = _distanceBurst[channel] = "";
      _distanceText[channel] = NoTelemetry;
    }
    UpdateDistanceRows();
  }

  private void UpdateDistance() {
    for (int channel = 0; channel < 2; channel++) {
      if (_haveDistance[channel]) {
        DescribeDistance(channel);
      }
    }
    UpdateDistanceRows();
  }

  private void DescribeDistance(int channel) {
    Nv5DistanceMessage d = _distance[channel];
    uint elapsed = unchecked(Now() - _distanceReceived[channel]);
    ulong age = d.AgeMs == uint.MaxValue ? ulong.MaxValue : d.AgeMs + (ulong)elapsed;
    // Whole seconds, and silence while telemetry keeps arriving: a running millisecond counter
    // draws the eye for nothing.
    static string Seconds(ulong milliseconds) =>
        ((milliseconds + 500) / 1000).ToString(CultureInfo.InvariantCulture) + " s";
    bool overdue = elapsed > Math.Max(2000UL, d.CycleUs / 500 + 1000);
    // The modem clears its counters when a ranging window opens, so they describe the latest
    // burst only: each of its exchanges ended valid, timed out, or late for its slot.
    int exchanges = d.ValidCount + d.TimeoutCount + d.LateCount;
    string burst = "";
    if (exchanges > 0) {
      burst = $"last burst {d.ValidCount} of {exchanges} valid";
      if (d.TimeoutCount != 0) {
        burst += $", {d.TimeoutCount} timed out";
      }
      if (d.LateCount != 0) {
        burst += $", {d.LateCount} late";
      }
    }
    // Which end this modem is, where its settings tell: see UpdateDistanceRows.
    string prefix = $"CH{channel + 1}_";
    bool roleKnown = _values.ContainsKey(prefix + "ROLE") && ReadNumber(prefix + "ROLE") <= 1;
    bool answers = roleKnown && ReadNumber(prefix + "ROLE") != ReadNumber(prefix + "RNG_MGR");
    var plain = new List<string>();
    var reading = new List<string>();
    var alert = new List<string>();
    void Add(string part) {
      plain.Add(part);
      reading.Add(part);
    }
    void Failure(string part) {
      plain.Add(part);
      alert.Add(part);
    }
    string counted = "";
    if (d.Status is Nv5DistanceStatuses.Valid or Nv5DistanceStatuses.Stale) {
      bool stale = d.Status == Nv5DistanceStatuses.Stale || overdue;
      bool calibrated = Calibrated(d);
      Add(Metres(calibrated ? d.CorrectedMetres : d.DistanceCm / 100.0));
      if (calibrated) {
        Add(d.ReferenceScope == Nv5RangingReferenceScopes.WholeAssembly
            ? "calibrated whole assembly"
            : "calibrated at RF connectors");
        Add("raw " + Metres(d.DistanceCm / 100.0));
      }
      Add(stale ? "STALE" : "VALID");
      if (age != ulong.MaxValue) {
        Add("age " + Seconds(age));
      }
      if (!calibrated) {
        // Calibration is optional, so a raw range is an ordinary reading. A record that is
        // present but does not apply says why; having no record at all is left to the tip.
        Add("raw");
        byte why = d.CalibrationStatus == Nv5RangingCalibrationStatuses.Applied
            ? Nv5RangingCalibrationStatuses.InvalidResult
            : d.CalibrationStatus;
        if (why == Nv5RangingCalibrationStatuses.NoRecord) {
          plain.Add(CalibrationText(why));
        } else if (why != Nv5RangingCalibrationStatuses.NotEvaluated) {
          Add(CalibrationText(why));
        }
      }
      counted = burst;
    } else {
      switch (d.Status) {
        case Nv5DistanceStatuses.Disabled:
          Add("Disabled");
          break;
        case Nv5DistanceStatuses.Waiting:
          // The modem that answers never has a distance; it reports this state for good.
          if (answers) {
            if (exchanges > 0) {
              counted = $"last burst {d.ValidCount} of {exchanges} answered";
            } else {
              Add("no request answered yet");
            }
          } else {
            Add(roleKnown ? "waiting for the first result" : "Waiting");
            counted = burst;
          }
          break;
        case Nv5DistanceStatuses.Timeout:
          Failure("Timed out");
          counted = burst;
          break;
        case Nv5DistanceStatuses.Late:
          Failure("Late");
          counted = burst;
          break;
        case Nv5DistanceStatuses.HardwareFault:
          Failure("Radio fault");
          counted = burst;
          break;
        case Nv5DistanceStatuses.Suppressed:
          Failure("Suppressed while the transmitter is off");
          break;
        default:
          Failure("Unavailable");
          break;
      }
      if (overdue) {
        Failure("no telemetry for " + Seconds(elapsed));
      }
    }
    _distanceReading[channel] = string.Join(" · ", reading);
    _distanceAlert[channel] = string.Join(" · ", alert);
    _distanceBurst[channel] = counted;
    if (counted.Length != 0) {
      plain.Add(counted);
    }
    _distanceText[channel] = string.Join(" · ", plain);
  }

  private void UpdateDistanceRows() {
    for (int channel = 0; channel < 2; channel++) {
      string prefix = $"CH{channel + 1}_";
      bool known = _values.TryGetValue(prefix + "RNG_EN", out Value? enabled);
      // Live telemetry is newer than the settings that were read; it decides when present.
      bool telemetry = _haveDistance[channel];
      bool on = telemetry
          ? _distance[channel].Status != Nv5DistanceStatuses.Disabled
          : known && ReadNumber(prefix + "RNG_EN") != 0;
      string mode;
      if (!telemetry && !known) {
        mode = _values.Count == 0 ? "Not read yet" : "No ranging on this radio";
      } else if (!telemetry && (enabled!.Flags & Nv5ConfigValueFlags.Supported) == 0) {
        mode = "No ranging on this radio";
      } else if (!on) {
        mode = "Ranging off";
      } else {
        mode = "Ranging on";
        // The measuring end is a shared setting: 0 names the receiver, 1 the transmitter.
        // Firmware without it measures on the receiver, which is what a missing value gives.
        if (_values.ContainsKey(prefix + "ROLE") && ReadNumber(prefix + "ROLE") <= 1) {
          mode += ReadNumber(prefix + "ROLE") == ReadNumber(prefix + "RNG_MGR")
              ? " · measures"
              : " · answers";
        }
      }
      string text = on && _distanceText[channel].Length != 0
          ? $"{mode} · {_distanceText[channel]}"
          : mode;
      _distanceRows[channel] = new NvDistanceReadout(mode,
          on ? (telemetry ? _distanceReading[channel] : NoTelemetry) : "",
          on && telemetry ? _distanceAlert[channel] : "",
          on && telemetry ? _distanceBurst[channel] : "", text);
      // A radio ranges either with its pair or for LPS, never both: its row says which. The pair
      // window keeps showing the radio's own ranging state.
      if (LpsRadio() == channel + 1) {
        NvDistanceReadout lps = LpsRow(radioInRow: true);
        DistanceRows[channel].Show(on ? lps with { Mode = lps.Mode + " · ranging on" } : lps);
      } else {
        DistanceRows[channel].Show(_distanceRows[channel]);
      }
    }
    DistanceTitle = LpsSupported ? "Distance and LPS" : "Distance";
  }

  /// <summary>A numeric setting as last read, or zero when the device has none of that
  /// name.</summary>
  private uint ReadNumber(string name) {
    if (!_values.TryGetValue(name, out Value? value)) {
      return 0;
    }
    uint number = 0;
    for (int i = 0; i < Math.Min(4, value.Bytes.Length); i++) {
      number |= (uint)value.Bytes[i] << (8 * i);
    }
    return number;
  }

  /// <summary>Whether a reading carries a correction the modem applied, with every field that
  /// proves it.</summary>
  private static bool Calibrated(Nv5DistanceMessage d) =>
      d.ValidCount != 0 && (d.Flags & Nv5DistanceFlags.Restored) != 0
      && d.Calibration == Nv5DistanceCalibrations.Calibrated
      && d.CalibrationStatus == Nv5RangingCalibrationStatuses.Applied && d.CalibrationEpoch != 0
      && d.BankRevision != 0 && d.ReferenceScope <= Nv5RangingReferenceScopes.WholeAssembly
      && d.CalibrationId is { } id && id.Any(value => value != 0)
      && float.IsFinite(d.CorrectedMetres) && d.CorrectedMetres > 0
      && float.IsFinite(d.IndependentVariance) && d.IndependentVariance >= 0
      && float.IsFinite(d.PersistentVariance) && d.PersistentVariance >= 0;

  private static string Metres(double metres) =>
      metres.ToString("F2", CultureInfo.InvariantCulture) + " m";

  /// <summary>Typed applicability from the public ranging diagnostics.</summary>
  private static string CalibrationText(byte status) => status switch {
    Nv5RangingCalibrationStatuses.NotEvaluated => "not evaluated",
    Nv5RangingCalibrationStatuses.Applied => "applied",
    Nv5RangingCalibrationStatuses.NoRecord => "no calibration record",
    Nv5RangingCalibrationStatuses.IdentityUnknown => "unknown radio identity",
    Nv5RangingCalibrationStatuses.PeerMismatch => "different peer",
    Nv5RangingCalibrationStatuses.ProfileMismatch => "different radio profile",
    Nv5RangingCalibrationStatuses.AssemblyMismatch => "different antenna assembly",
    Nv5RangingCalibrationStatuses.WrongReferenceScope => "wrong reference plane",
    Nv5RangingCalibrationStatuses.Unchecked => "calibration not checked",
    Nv5RangingCalibrationStatuses.OutsideDomain => "outside calibrated conditions",
    Nv5RangingCalibrationStatuses.Incomplete => "incomplete ranging burst",
    Nv5RangingCalibrationStatuses.InvalidModel => "invalid calibration model",
    Nv5RangingCalibrationStatuses.InvalidResult => "invalid calibration result",
    Nv5RangingCalibrationStatuses.UnsupportedUncertainty => "unsupported uncertainty",
    _ => $"unknown calibration status {status}",
  };
}
