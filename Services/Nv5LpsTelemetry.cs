using System;
using System.Linq;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Services;

/// <summary>
/// The LPS reports of one modem, bound to its verified route and to the clock interval a
/// freshness challenge established.
/// </summary>
/// <remarks>
/// A report is accepted only from the modem's own route, system, component and UID. A STATUS that
/// echoes the latest challenge within three seconds anchors the modem's clock to this one: the
/// request's send time and the reply's receipt bound how old any later report can be, allowing
/// 500 ppm of relative drift and 2 ms of clock uncertainty. Challenges are retried after three
/// seconds and refreshed after five; without a fresh anchor, trust ends after ten. BEACON rows join
/// only the exact capture of the latest STATUS, and a report never renews an older one. A STATUS
/// from an earlier radio-owner epoch of the same boot is late, not a change, and withdraws
/// nothing; another boot or a later epoch withdraws the confirmation until a challenge renews it.
/// This is freshness correlation, not authentication. Times are microseconds on the caller's
/// clock.
/// </remarks>
internal sealed class Nv5LpsTelemetry {
  private const ulong ChallengeRetryUs = 3_000_000;
  private const ulong ChallengeRefreshUs = 5_000_000;
  private const ulong TrustUs = 10_000_000;
  private const ulong ClockUncertaintyUs = 2_000;

  private readonly Nv5LpsBeaconMessage[] _rows = new Nv5LpsBeaconMessage[4];
  private Target? _target;
  private Nv5LpsStatusMessage _status;
  private ulong _nonce;
  private ulong _sentAt;
  private ulong _anchorSent;
  private ulong _anchorSender;
  private ulong _anchorReceived;
  private ulong _receivedAt;
  private ulong _boot;
  private ulong _epoch;
  private byte _mask;
  private bool _anchored;
  private bool _received;

  /// <summary>The latest accepted STATUS; all zero until one is.</summary>
  internal Nv5LpsStatusMessage Status => _status;

  internal bool Received => _received;

  /// <summary>Local time of the latest accepted STATUS.</summary>
  internal ulong ReceivedAt => _receivedAt;

  /// <summary>Follows the modem's current route: another modem, route, transport epoch or
  /// identity withdraws every report of the old one.</summary>
  internal void Observe(Target target) {
    if (_target == null || !_target.Uid.AsSpan().SequenceEqual(target.Uid)
        || !Equals(target.Route, _target.Route) || target.TransportEpoch != _target.TransportEpoch
        || target.System != _target.System || target.Component != _target.Component
        || !target.Verified) {
      Invalidate();
    }
    _target = target;
  }

  /// <summary>Withdraws all evidence, as after a local commit before rediscovery reports the
  /// restart.</summary>
  internal void Invalidate() {
    _nonce = 0;
    _anchored = false;
    _received = false;
    _mask = 0;
    _boot = 0;
    _epoch = 0;
    _status = default;
  }

  internal bool ChallengeDue(ulong now) =>
      _target is { Verified: true, Uid.Length: 18 }
      && (_nonce != 0
          ? now < _sentAt || now - _sentAt >= ChallengeRetryUs
          : !_anchored || now < _anchorReceived || now - _anchorReceived >= ChallengeRefreshUs);

  /// <summary>
  /// The exact-target MAV_CMD_REQUEST_MESSAGE for a STATUS that echoes <paramref name="nonce"/>,
  /// or null when no challenge is due. The 64-bit nonce goes in params 2, 3 and 4 as 24, 24 and
  /// 16 bits, least significant first, each an exact integer float. A nonce must be fresh, random
  /// and nonzero.
  /// </summary>
  internal MAVLink.mavlink_command_long_t? Request(ulong now, ulong nonce) {
    if (!ChallengeDue(now) || nonce == 0 || nonce == _nonce) {
      return null;
    }
    _nonce = nonce;
    _sentAt = now;
    return new MAVLink.mavlink_command_long_t {
      target_system = _target!.System,
      target_component = _target.Component,
      command = (ushort)MAVLink.MAV_CMD.REQUEST_MESSAGE,
      confirmation = 0,
      param1 = NvModemMessageIds.Nv5LpsStatus,
      param2 = nonce & 0xffffff,
      param3 = (nonce >> 24) & 0xffffff,
      param4 = nonce >> 48,
    };
  }

  /// <summary>Whether the reports are confirmed current: a challenge answered within the last ten
  /// seconds, by this boot and runtime epoch.</summary>
  internal bool Verified(ulong now) =>
      _anchored && _received && now >= _anchorReceived && now - _anchorReceived < TrustUs
      && _status.BootNonce == _boot && _status.RuntimeEpoch == _epoch;

  /// <summary>Takes a STATUS or BEACON report of the modem; false when it is not this modem's, or
  /// is malformed, older or out of place.</summary>
  internal bool Receive(MAVLink.MAVLinkMessage message, object route, ulong now) {
    if (_target is not { Verified: true } || !Equals(route, _target.Route)
        || message.sysid != _target.System || message.compid != _target.Component) {
      return false;
    }
    if (message.msgid == NvModemMessageIds.Nv5LpsStatus) {
      return ReceiveStatus(message.ToStructure<Nv5LpsStatusMessage>(), now);
    }
    return message.msgid == NvModemMessageIds.Nv5LpsBeacon && _received
        && ReceiveBeacon(message.ToStructure<Nv5LpsBeaconMessage>());
  }

  /// <summary>A beacon row of the latest STATUS, zero-based, or null while it has not
  /// arrived.</summary>
  internal Nv5LpsBeaconMessage? Beacon(int row) =>
      row is >= 0 and < 4 && (_mask & (1 << row)) != 0 ? _rows[row] : null;

  /// <summary>A conservative upper bound of the latest STATUS's age, or
  /// <see cref="ulong.MaxValue"/> while its reports are not confirmed current.</summary>
  internal ulong AgeUpperBound(ulong now) {
    if (!Verified(now) || now < _anchorSent || _status.SnapshotTimeUs < _anchorSender) {
      return ulong.MaxValue;
    }
    ulong senderDelta = _status.SnapshotTimeUs - _anchorSender;
    ulong elapsed = now - _anchorSent;
    ulong drift = (senderDelta + 1999) / 2000 + ClockUncertaintyUs;
    return elapsed >= senderDelta ? elapsed - senderDelta + drift : drift;
  }

  /// <summary>
  /// A confirmed, unexpired rover fix. Calibration is optional: its revision, admission status and
  /// calibrated rows describe the ranges, not whether the fix holds. The fix needs its own
  /// validity, freshness, identity, covariance and radio-owner state, and nothing else.
  /// </summary>
  internal bool PoseCurrent(ulong now) {
    if (!Verified(now) || _status.Role != Nv5LpsRoles.Rover || _status.Status != Nv5LpsStatuses.Valid
        || _status.BeaconMask == 0 || _status.PoseValidForUs == 0
        || (_status.OwnerFlags & (Nv5LpsOwnerFlags.Bound | Nv5LpsOwnerFlags.Maintenance
            | Nv5LpsOwnerFlags.Faulted | Nv5LpsOwnerFlags.TransmitSuppressed))
            != Nv5LpsOwnerFlags.Bound
        || _status.PoseTimeUs > _status.SnapshotTimeUs
        || AgeUpperBound(now) >= _status.PoseValidForUs) {
      return false;
    }
    float[] position = _status.PositionEnu ?? [];
    float[] covariance = _status.CovarianceEnu ?? [];
    return position.Length == 3 && covariance.Length == 6 && position.All(float.IsFinite)
        && covariance.All(float.IsFinite)
        && CovarianceValid(covariance.Select(value => (double)value).ToArray());
  }

  /// <summary>Whether an ENU covariance [EE, EN, EU, NN, NU, UU] is finite and positive
  /// semidefinite, within rounding.</summary>
  internal static bool CovarianceValid(ReadOnlySpan<double> c) {
    if (c.Length != 6) {
      return false;
    }
    foreach (double value in c) {
      if (!double.IsFinite(value)) {
        return false;
      }
    }
    if (c[0] < 0 || c[3] < 0 || c[5] < 0) {
      return false;
    }
    static bool Correlation(double cross, double left, double right, out double result) {
      if (left == 0 || right == 0) {
        result = 0;
        return cross == 0;
      }
      result = cross / (Math.Sqrt(left) * Math.Sqrt(right));
      return double.IsFinite(result) && Math.Abs(result) <= 1.000001;
    }
    if (!Correlation(c[1], c[0], c[3], out double en) || !Correlation(c[2], c[0], c[5], out double eu)
        || !Correlation(c[4], c[3], c[5], out double nu)) {
      return false;
    }
    return 1 + 2 * en * eu * nu - en * en - eu * eu - nu * nu >= -1e-6;
  }

  private bool ReceiveStatus(Nv5LpsStatusMessage value, ulong now) {
    // The calibrated rows are always some of the expected rows; a report saying otherwise is
    // malformed rather than partly calibrated.
    if (!_target!.Uid.AsSpan().SequenceEqual(value.Uid ?? []) || value.BootNonce == 0
        || value.RuntimeEpoch == 0 || value.SnapshotSeq == 0 || value.BeaconMask > 15
        || (value.CalibratedMask & ~value.BeaconMask) != 0 || value.Role > Nv5LpsRoles.Beacon
        || value.Channel > 2 || (value.Channel == 0 && value.Role != Nv5LpsRoles.Off)) {
      return false;
    }
    bool response = _nonce != 0 && value.Challenge == _nonce && now >= _sentAt
        && now - _sentAt <= ChallengeRetryUs;
    // Within a boot the radio-owner epoch only advances, and within an epoch so do the sequence
    // and the capture time. A report behind the latest, from an earlier epoch or earlier in this
    // one, is late or repeated: it is refused before it could withdraw the current confirmation.
    if (_received && value.BootNonce == _status.BootNonce
        && (value.RuntimeEpoch < _status.RuntimeEpoch
            || (value.RuntimeEpoch == _status.RuntimeEpoch
                && (value.SnapshotSeq <= _status.SnapshotSeq
                    || value.SnapshotTimeUs < _status.SnapshotTimeUs)))) {
      return false;
    }
    if (!response && _anchored && (value.BootNonce != _boot || value.RuntimeEpoch != _epoch)) {
      // Another boot or radio-owner epoch: nothing known about this one is trusted.
      _anchored = false;
      _nonce = 0;
      _mask = 0;
      _received = false;
      return false;
    }
    if (response || _anchored) {
      // The sender's clock cannot have run further since the anchor than this one has.
      ulong sender = response ? value.SnapshotTimeUs : _anchorSender;
      ulong sent = response ? _sentAt : _anchorSent;
      if (value.SnapshotTimeUs < sender || now < sent
          || value.SnapshotTimeUs - sender > now - sent + ClockUncertaintyUs) {
        return false;
      }
    }
    if (response) {
      _boot = value.BootNonce;
      _epoch = value.RuntimeEpoch;
      _anchorSent = _sentAt;
      _anchorSender = value.SnapshotTimeUs;
      _anchorReceived = now;
      _nonce = 0;
      _anchored = true;
    }
    _status = value;
    _receivedAt = now;
    _received = true;
    _mask = 0;
    return true;
  }

  private bool ReceiveBeacon(Nv5LpsBeaconMessage value) {
    int bit = value.Index < 4 ? 1 << value.Index : 0;
    bool observed = (value.Flags & Nv5LpsBeaconFlags.Observed) != 0;
    bool calibrated = (value.Flags & Nv5LpsBeaconFlags.Calibrated) != 0;
    // Without a usable majority aggregate the raw range is NaN, whatever the calibration
    // diagnostics say; such a row cannot also be calibrated.
    bool rawUnavailable = float.IsNaN(value.RawMetres) && !calibrated
        && value.CalibrationStatus != Nv5RangingCalibrationStatuses.Applied
        && (_status.CalibratedMask & bit) == 0;
    bool recordedCorrection = value.CalibrationStatus == Nv5RangingCalibrationStatuses.Applied
        && value.ReferenceScope == Nv5RangingReferenceScopes.WholeAssembly
        && value.BankRevision != 0 && value.CalibrationBinding != 0
        && (value.CalibrationId ?? []).Any(part => part != 0)
        && float.IsFinite(value.Metres) && value.Metres > 0
        && float.IsFinite(value.Variance) && value.Variance >= 0
        && float.IsFinite(value.PersistentVariance) && value.PersistentVariance >= 0;
    if (!_target!.Uid.AsSpan().SequenceEqual(value.Uid ?? []) || bit == 0
        || (_status.BeaconMask & bit) == 0 || value.BootNonce != _status.BootNonce
        || value.RuntimeEpoch != _status.RuntimeEpoch || value.SnapshotSeq != _status.SnapshotSeq
        || value.SnapshotTimeUs != _status.SnapshotTimeUs || value.DatumId != _status.DatumId
        || value.FieldGeneration != _status.FieldGeneration
        || (value.Flags & ~(Nv5LpsBeaconFlags.Observed | Nv5LpsBeaconFlags.Calibrated)) != 0
        || (calibrated && !observed)
        // A row the report counts as calibrated carries its correction.
        || ((_status.CalibratedMask & bit) != 0 && !calibrated)
        || (observed && ((!float.IsFinite(value.RawMetres) && !rawUnavailable)
            || value.AcquiredAtUs > value.CompletedAtUs
            || value.CompletedAtUs > value.SnapshotTimeUs))
        || (calibrated && !recordedCorrection)
        || (_mask & bit) != 0) {
      return false;
    }
    _rows[value.Index] = value;
    _mask |= (byte)bit;
    return true;
  }
}
