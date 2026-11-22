using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Services;
using Value = MissionPlanner.Services.Nv5ConfigurationClient.Value;

namespace MissionPlanner.ViewModels.Setup;

/// <summary>LPS reports, the LPS row and the LPS settings of one NV5 modem.</summary>
public sealed partial class Nv5NativeSettingsEditor {
  private static readonly string[] Attempts = ["waiting", "complete", "no acknowledgement",
      "no confirmation", "timed out", "late", "suppressed", "cancelled", "radio fault"];

  private static readonly string[] NoPositionReasons = ["off", "radio fault", "uncalibrated",
      "waiting for the flight controller", "waiting for the launch tie",
      "flight controller data stale", "waiting for output continuity", "starting",
      "ranging too slow", "ranges stale", "poor beacon geometry", "too uncertain", "on probation",
      "expired"];

  /// <summary>Survey rows of a field, LPS_B1_ to LPS_B4_.</summary>
  private const int LpsRows = 4;

  private readonly Nv5LpsTelemetry _lps = new();

  // Microseconds for the LPS reports, extended from the millisecond clock so that it never wraps
  // or runs backwards.
  private ulong _lpsClockMs;
  private uint _lpsClockLast;
  private bool _lpsClockStarted;

  /// <summary>The title of the Distance group: it names LPS once the modem has LPS
  /// settings.</summary>
  [ObservableProperty]
  private string _distanceTitle = "Distance";

  /// <summary>Whether to ask the modem to confirm its LPS reports are current. Off until a view
  /// needs them.</summary>
  internal bool LpsChallenges { get; set; }

  /// <summary>Live LPS reports of this modem, bound to its verified route.</summary>
  internal Nv5LpsTelemetry Lps => _lps;

  /// <summary>True once the modem has reported its LPS settings.</summary>
  internal bool LpsSupported =>
      _values.TryGetValue("LPS_ROLE", out Value? role)
      && (role.Flags & Nv5ConfigValueFlags.Supported) != 0;

  /// <summary>The radio LPS uses now, 1 or 2, or 0 while LPS is off or unknown. What the modem
  /// reports outranks what was read from it.</summary>
  internal int LpsRadio() {
    if (!LpsSupported) {
      return 0;
    }
    if (_lps.Received) {
      return _lps.Status.Role != Nv5LpsRoles.Off ? _lps.Status.Channel : 0;
    }
    return ReadNumber("LPS_ROLE") != 0 ? (int)ReadNumber("LPS_RADIO") : 0;
  }

  /// <summary>The digest of the configuration the modem reported active, as a save ended.</summary>
  internal byte[] ActiveDigest => Copy(_client.Result.ActiveDigest);

  /// <summary>The digest of the settings read back, or none while they are not a confirmed
  /// readback.</summary>
  internal byte[] ReadBackDigest => _readBack ? Copy(_valuesIdentity.Digest) : [];

  /// <summary>
  /// The LPS role and readout of this modem, as an LPS row shows it. In a radio's own row the
  /// radio is the row's name, so the mode names only the role.
  /// </summary>
  internal NvDistanceReadout LpsRow(bool radioInRow = false) {
    if (!LpsSupported) {
      string none = _values.Count == 0 ? "Not read yet" : "No LPS in this firmware";
      return new NvDistanceReadout(none, "", "", "", none);
    }
    ulong at = NowUs();
    bool reported = _lps.Received;
    Nv5LpsStatusMessage status = _lps.Status;
    uint role = reported ? status.Role : ReadNumber("LPS_ROLE");
    uint radio = reported ? status.Channel : ReadNumber("LPS_RADIO");
    string beaconRow = "";
    if (role == Nv5LpsRoles.Beacon) {
      uint id = ReadNumber("LPS_BEACON");
      uint count = ReadNumber("LPS_BCOUNT");
      for (uint row = 1; row <= count && row <= LpsRows; row++) {
        if (ReadNumber($"LPS_B{row}_ID") == id) {
          beaconRow = $"B{row}";
        }
      }
    }
    string mode = radioInRow
        ? role switch {
          Nv5LpsRoles.Rover => "LPS rover",
          Nv5LpsRoles.Beacon => beaconRow.Length == 0 ? "LPS beacon" : $"LPS beacon {beaconRow}",
          _ => "LPS off",
        }
        : role switch {
          Nv5LpsRoles.Rover => $"Rover · Radio {radio}",
          Nv5LpsRoles.Beacon => beaconRow.Length == 0
              ? $"Beacon · Radio {radio}"
              : $"Beacon {beaconRow} · Radio {radio}",
          _ => "LPS off",
        };
    if (role == Nv5LpsRoles.Off && !reported) {
      return new NvDistanceReadout(mode, "", "", "", mode);
    }
    var plain = new List<string>();
    var reading = new List<string>();
    var alert = new List<string>();
    var burst = new List<string>();
    var tip = new List<string> { mode };
    void Add(string part, bool failure = false) {
      plain.Add(part);
      (failure ? alert : reading).Add(part);
    }
    // Whole seconds, as the distance rows count them.
    static string Seconds(ulong micros) =>
        ((micros + 500000) / 1000000).ToString(CultureInfo.InvariantCulture) + " s";
    static string Attempt(byte code) =>
        code < Attempts.Length ? Attempts[code] : $"unknown outcome {code}";
    // Waiting and cancelled are no fault of the radio; every other outcome but complete is.
    static bool AttemptFailed(byte code) => code >= 2 && code != 7;
    ulong silent = reported && at >= _lps.ReceivedAt ? at - _lps.ReceivedAt : 0;
    if (!reported) {
      Add("no LPS report yet");
    } else if (silent > 3000000) {
      // An old readout must not pass for a current one: only the silence is shown.
      Add($"no LPS report for {Seconds(silent)}", true);
      tip.Add($"Last report {Seconds(silent)} ago.");
    } else {
      DescribeLps(status, at, silent, Add, burst, tip);
    }
    tip.AddRange(plain);
    string counted = string.Join(" · ", burst);
    if (counted.Length != 0) {
      tip.Add(counted);
    }
    return new NvDistanceReadout(mode, string.Join(" · ", reading), string.Join(" · ", alert),
        counted, string.Join(" ", tip));

    void DescribeLps(Nv5LpsStatusMessage report, ulong now, ulong quiet,
        Action<string, bool> add, List<string> rows, List<string> notes) {
      byte owner = report.OwnerFlags;
      if ((owner & Nv5LpsOwnerFlags.Faulted) != 0) {
        add("radio fault", true);
      }
      if ((owner & Nv5LpsOwnerFlags.TransmitSuppressed) != 0) {
        add("transmitter off", true);
      }
      if ((owner & Nv5LpsOwnerFlags.Maintenance) != 0) {
        add("paused for maintenance", false);
      }
      // Calibration is optional. Which displayed ranges carry one is a diagnostic beside the fix,
      // never a condition for showing it.
      int expected = 0;
      int calibrated = 0;
      for (int bit = 0; bit < LpsRows; bit++) {
        expected += (report.BeaconMask >> bit) & 1;
        calibrated += ((report.CalibratedMask & report.BeaconMask) >> bit) & 1;
      }
      bool verified = _lps.Verified(now);
      if (report.Role == Nv5LpsRoles.Rover) {
        if (_lps.PoseCurrent(now)) {
          float[] p = report.PositionEnu;
          float[] c = report.CovarianceEnu;
          double sigma = Math.Sqrt(Math.Max(c[0], Math.Max(c[3], c[5])));
          add(string.Create(CultureInfo.InvariantCulture,
              $"E {p[0]:F2}, N {p[1]:F2}, U {p[2]:F2} m ±{sigma:F2} m"), false);
          add(calibrated == 0 ? "raw ranges"
              : calibrated == expected ? "calibrated ranges"
              : $"{calibrated} of {expected} ranges calibrated", false);
        } else {
          byte code = report.Status;
          // A valid fix is shown only once the modem has confirmed its reports are current.
          string reason = code == Nv5LpsStatuses.Valid && !verified ? "not confirmed current yet"
              : code < NoPositionReasons.Length ? NoPositionReasons[code]
              : $"status {code}";
          add($"no position: {reason}", code is Nv5LpsStatuses.RadioFault
              or Nv5LpsStatuses.StaleFlightController or Nv5LpsStatuses.RangesStale);
        }
        for (int row = 0; row < LpsRows; row++) {
          if ((report.BeaconMask & (1 << row)) != 0) {
            DescribeBeacon(row, report, now, quiet, verified, rows, notes);
          }
        }
      } else if (report.Role == Nv5LpsRoles.Beacon) {
        byte code = report.LastAttemptStatus;
        if (code == 1) {
          add("answering", false);
        } else if (code == 0) {
          add("no request yet", false);
        } else {
          add(Attempt(code), AttemptFailed(code));
        }
        if (code != 0) {
          rows.Add($"last burst {Attempt(code)}");
        }
      }
      notes.Add(string.Create(CultureInfo.InvariantCulture,
          $"Field generation {report.FieldGeneration}, datum 0x{report.DatumId:x16}."));
      // Only the rover measures, so only its ranges can be calibrated.
      if (report.Role == Nv5LpsRoles.Rover) {
        notes.Add($"Calibration is optional; calibrated ranges: {calibrated} of {expected}"
            + (report.CalibrationStatus == Nv5RangingCalibrationStatuses.Applied
                ? "" : $" ({CalibrationText(report.CalibrationStatus)})") + ".");
      }
      notes.Add((verified ? "Reports confirmed current" : "Reports not confirmed current yet")
          + ". Radio "
          + ((owner & Nv5LpsOwnerFlags.Bound) == 0 ? "not in use"
              : (owner & Nv5LpsOwnerFlags.Dedicated) != 0
                  ? "used by LPS alone; its data link is paused"
                  : "shared with the data link") + ".");
    }

    void DescribeBeacon(int row, Nv5LpsStatusMessage report, ulong now, ulong quiet,
        bool verified, List<string> rows, List<string> notes) {
      int name = row + 1;
      if (_lps.Beacon(row) is not { } b) {
        rows.Add($"B{name} missing");
        return;
      }
      bool observed = (b.Flags & Nv5LpsBeaconFlags.Observed) != 0;
      string part;
      if (AttemptFailed(b.AttemptStatus)) {
        part = $"B{name} {Attempt(b.AttemptStatus)}";
      } else if ((b.Flags & Nv5LpsBeaconFlags.Calibrated) != 0) {
        part = string.Create(CultureInfo.InvariantCulture, $"B{name} {b.Metres:F2} m calibrated");
      } else if (observed && float.IsFinite(b.RawMetres)) {
        part = string.Create(CultureInfo.InvariantCulture, $"B{name} {b.RawMetres:F2} m raw");
      } else if (observed) {
        part = $"B{name} raw range unavailable";
      } else {
        part = $"B{name} not measured yet";
      }
      // A range says its age only once it is old enough to matter. A confirmed report counts its
      // transport delay through the conservative bound; an unconfirmed one has no such bound and
      // counts from its receipt.
      if (observed && report.SnapshotTimeUs >= b.AcquiredAtUs) {
        ulong sinceCapture = verified ? _lps.AgeUpperBound(now) : quiet;
        ulong age = report.SnapshotTimeUs - b.AcquiredAtUs + sinceCapture;
        if (age >= 2000000) {
          part += $" ({Seconds(age)} old)";
        }
      }
      if (b.Eligible == 0 && b.Exclusion != Nv5LpsPeerExclusions.Included) {
        part += " · excluded";
      }
      rows.Add(part);
      string eligibility = b.Eligible != 0 ? "eligible for a fresh range"
          : b.Exclusion == Nv5LpsPeerExclusions.RangeUnavailable ? "excluded: range unavailable"
          : "excluded: calibration";
      if (observed && !float.IsFinite(b.RawMetres)) {
        // Without a raw range nothing was calibrated: the status names the ranging fault.
        notes.Add($"B{name} raw range unavailable: "
            + (b.CalibrationStatus == Nv5RangingCalibrationStatuses.InvalidResult
                ? "invalid ranging result" : CalibrationText(b.CalibrationStatus))
            + $"; {eligibility}.");
      } else {
        notes.Add($"B{name} calibration: {CalibrationText(b.CalibrationStatus)}; {eligibility}.");
      }
      notes.Add(string.Create(CultureInfo.InvariantCulture,
          $"B{name} (ID {b.BeaconId}, address 0x{b.Address:x8}): ")
          + (observed && float.IsFinite(b.RawMetres)
              ? string.Create(CultureInfo.InvariantCulture, $"{b.RawMetres:F3} m raw")
              : "not measured")
          + string.Create(CultureInfo.InvariantCulture,
              $". Latest attempt {b.LastAttemptBurst}: {Attempt(b.AttemptStatus)}. ")
          + $"Retained observation: {b.Exchanges} valid exchanges.");
    }
  }

  private bool ReceiveLps(MAVLink.MAVLinkMessage message, object route) {
    // From a commit until its save ends, a report may still describe the configuration being
    // replaced: the commit withdrew the reports, and none is taken until the modem is back.
    if (_target == null || AwaitingReconnect) {
      return false;
    }
    _lps.Observe(_target);
    if (!_lps.Receive(message, route, NowUs())) {
      return false;
    }
    UpdateDistanceRows();
    return true;
  }

  /// <summary>Asks for a fresh LPS report when one is due; never while a commit may be restarting
  /// the modem.</summary>
  private void ServiceLps() {
    // A changed target or route withdraws the reports of the old one.
    _lps.Observe(_target!);
    if (!LpsChallenges || !LpsSupported || _commitSent || _step == Step.Recovery) {
      return;
    }
    ulong now = NowUs();
    if (_lps.ChallengeDue(now) && _lps.Request(now, FreshNonce()) is { } request) {
      SendFrame?.Invoke(new Nv5ConfigurationFrame(
          (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, request), _target!);
    }
  }

  private ulong NowUs() {
    uint now = Now();
    if (!_lpsClockStarted) {
      _lpsClockStarted = true;
      _lpsClockMs = now;
    } else {
      int elapsed = unchecked((int)(now - _lpsClockLast));
      if (elapsed > 0) {
        _lpsClockMs += (ulong)elapsed;
      }
    }
    _lpsClockLast = now;
    return _lpsClockMs * 1000;
  }
}
