using MissionPlanner.Services;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

/// <summary>The freshness of LPS reports and the rows and fixes they may carry.</summary>
public class Nv5LpsTelemetryTests {
  private static readonly Target Peer = Nv5LpsTestData.Target();

  [Fact]
  public void A_covariance_with_a_negative_mode_is_refused_whatever_its_diagonal() {
    Assert.False(Nv5LpsTelemetry.CovarianceValid([1e8, 99999, 0, 0, 0, 0]));
    Assert.False(Nv5LpsTelemetry.CovarianceValid([1, 0.9, 0.9, 1, -0.9, 1]));
    Assert.False(Nv5LpsTelemetry.CovarianceValid([1, 0, 0, 1, 0, double.NaN]));
    Assert.True(Nv5LpsTelemetry.CovarianceValid([1e-8, 0, 0, 1e-8, 0, 1e-8]));
    Assert.True(Nv5LpsTelemetry.CovarianceValid([1, 1, 1, 1, 1, 1]));
  }

  [Fact]
  public void A_challenge_carries_all_64_bits_and_a_fix_expires_between_reports() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status()), "route", 10000));
    Assert.False(model.PoseCurrent(10000));
    Assert.False(model.Verified(10000));
    MAVLink.mavlink_command_long_t request = model.Request(100000, ulong.MaxValue)!.Value;
    Assert.Equal((ushort)MAVLink.MAV_CMD.REQUEST_MESSAGE, request.command);
    Assert.Equal(10, request.target_system);
    Assert.Equal(11, request.target_component);
    Assert.Equal(53030f, request.param1);
    Assert.Equal(16777215f, request.param2);
    Assert.Equal(16777215f, request.param3);
    Assert.Equal(65535f, request.param4);
    Assert.Equal((0f, 0f, 0f), (request.param5, request.param6, request.param7));
    Assert.Equal(ulong.MaxValue, Nv5LpsTestData.Nonce(request));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(ulong.MaxValue, 2)),
        "route", 110000));
    Assert.True(model.Verified(110000));
    Assert.True(model.PoseCurrent(110000));
    Assert.False(model.PoseCurrent(301000));
    Assert.False(model.Verified(10110000));
  }

  [Fact]
  public void A_refused_challenge_cannot_promote_a_retained_passive_report() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(0, 10, 2000000)),
        "route", 90000));
    Assert.NotNull(model.Request(100000, 42));
    Assert.False(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(42, 9, 1000000)),
        "route", 110000));
    Assert.False(model.Verified(110000));
    Assert.False(model.PoseCurrent(110000));
    Assert.False(model.ChallengeDue(110000));
    Assert.Null(model.Request(110000, 43));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(42, 11, 2100000)),
        "route", 120000));
    Assert.True(model.Verified(120000));
  }

  [Fact]
  public void Another_route_uid_epoch_or_boot_withdraws_the_evidence() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(100000, 42));
    Nv5LpsStatusMessage value = Nv5LpsTestData.Status(42);
    Assert.False(model.Receive(Nv5LpsTestData.Packet(value), "wrong-route", 110000));
    Assert.False(model.Receive(Nv5LpsTestData.Packet(value, component: 12), "route", 110000));
    value.Uid[0] = (byte)'V';
    Assert.False(model.Receive(Nv5LpsTestData.Packet(value), "route", 110000));
    value.Uid[0] = (byte)'U';
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 110000));
    Assert.True(model.PoseCurrent(110000));
    value.Challenge = 0;
    value.BootNonce = 456;
    value.SnapshotSeq = 2;
    Assert.False(model.Receive(Nv5LpsTestData.Packet(value), "route", 111000));
    Assert.False(model.PoseCurrent(111000));
    Assert.True(model.ChallengeDue(111000));
    // The new boot's reports are taken, but as passive ones until a challenge confirms them.
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 112000));
    Assert.False(model.Verified(112000));
    model.Observe(Peer with { TransportEpoch = 2 });
    Assert.False(model.Received);
  }

  [Fact]
  public void A_late_report_from_an_earlier_epoch_withdraws_nothing() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(100000, 42));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(42, 5)), "route",
        110000));
    Assert.True(model.PoseCurrent(110000));
    // Sent before the radio owner's epoch advanced to the current one, it arrives late.
    Nv5LpsStatusMessage late = Nv5LpsTestData.Status(0, 4, 900000);
    late.RuntimeEpoch = 6;
    Assert.False(model.Receive(Nv5LpsTestData.Packet(late), "route", 111000));
    Assert.True(model.Verified(111000));
    Assert.True(model.PoseCurrent(111000));
    Assert.False(model.ChallengeDue(111000));
    Assert.Equal(5ul, model.Status.SnapshotSeq);

    // A later epoch is a change: the confirmation is withdrawn until a challenge renews it, and a
    // late report of the epoch before does not take the place of its reports.
    Nv5LpsStatusMessage next = Nv5LpsTestData.Status(0, 6, 1001000);
    next.RuntimeEpoch = 8;
    Assert.False(model.Receive(Nv5LpsTestData.Packet(next), "route", 112000));
    Assert.False(model.Verified(112000));
    Assert.True(model.ChallengeDue(112000));
    next.SnapshotSeq = 7;
    next.SnapshotTimeUs = 1002000;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(next), "route", 113000));
    Assert.False(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(0, 6, 1000500)),
        "route", 113500));
    Assert.Equal((8ul, 7ul), (model.Status.RuntimeEpoch, model.Status.SnapshotSeq));
  }

  [Fact]
  public void Rows_join_only_their_exact_capture_and_another_report_never_renews_them() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(100000, 42));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(42)), "route", 110000));
    Nv5LpsBeaconMessage row = Nv5LpsTestData.Row(Nv5LpsTestData.Status());
    row.Flags = Nv5LpsBeaconFlags.Observed;
    row.RawMetres = -1.25f;
    row.Metres = row.Variance = row.PersistentVariance = float.NaN;
    row.AcquiredAtUs = 900000;
    row.CompletedAtUs = 950000;
    bool Deliver(Nv5LpsBeaconMessage beacon) =>
        model.Receive(Nv5LpsTestData.Packet(beacon), "route", 111000);
    Nv5LpsBeaconMessage wrong = row;
    wrong.FieldGeneration = 5;
    Assert.False(Deliver(wrong));
    wrong = row;
    wrong.CompletedAtUs = 1000001;
    Assert.False(Deliver(wrong));
    Assert.True(Deliver(row));
    Assert.Equal(-1.25f, model.Beacon(0)!.Value.RawMetres);
    Assert.False(Deliver(row));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(0, 2, 1100000)),
        "route", 210000));
    Assert.Null(model.Beacon(0));
    Assert.False(Deliver(row));
    Assert.False(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(0, 1)), "route",
        210000));
    model.Invalidate();
    Assert.Null(model.Beacon(0));
    Assert.False(model.Received);
  }

  [Theory]
  [InlineData(0, true)]
  [InlineData(1, false)]
  [InlineData(2, false)]
  [InlineData(3, false)]
  [InlineData(4, false)]
  [InlineData(5, false)]
  [InlineData(6, false)]
  [InlineData(7, false)]
  [InlineData(8, false)]
  public void A_calibrated_row_needs_its_complete_correction_record(int mutation, bool accepted) {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status()), "route", 1000));
    Nv5LpsBeaconMessage row = Nv5LpsTestData.Row(Nv5LpsTestData.Status());
    row.Flags = Nv5LpsBeaconFlags.Observed | Nv5LpsBeaconFlags.Calibrated;
    row.RawMetres = 10;
    row.Metres = 9;
    row.Variance = 0.04f;
    row.PersistentVariance = 0.09f;
    row.BankRevision = 123;
    row.CalibrationBinding = 456;
    row.CalibrationId[0] = 1;
    row.ReferenceScope = Nv5RangingReferenceScopes.WholeAssembly;
    row.CalibrationStatus = Nv5RangingCalibrationStatuses.Applied;
    switch (mutation) {
      case 1: row.Metres = -1; break;
      case 2: row.CalibrationStatus = Nv5RangingCalibrationStatuses.NoRecord; break;
      case 3: row.ReferenceScope = 2; break;
      case 4: row.BankRevision = 0; break;
      case 5: row.CalibrationBinding = 0; break;
      case 6: row.CalibrationId = new byte[16]; break;
      case 7: row.PersistentVariance = float.PositiveInfinity; break;
      case 8: row.RawMetres = float.NaN; break;
    }
    Assert.Equal(accepted, model.Receive(Nv5LpsTestData.Packet(row), "route", 2000));
  }

  [Fact]
  public void A_disabled_or_uncalibrated_report_is_never_a_fix() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(0, 42));
    Nv5LpsStatusMessage value = Nv5LpsTestData.Status(42);
    value.Role = Nv5LpsRoles.Off;
    value.Channel = 0;
    value.Status = 0;
    value.PoseValidForUs = 0;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 1000));
    Assert.True(model.Verified(1000));
    Assert.False(model.PoseCurrent(1000));
    value = Nv5LpsTestData.Status(0, 2, 1001000);
    value.Status = 2;
    value.CalibrationRevision = 0;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 2000));
    Assert.False(model.PoseCurrent(2000));
    // Role and radio must agree: a role without a radio is malformed.
    value = Nv5LpsTestData.Status(0, 3, 1002000);
    value.Channel = 0;
    Assert.False(model.Receive(Nv5LpsTestData.Packet(value), "route", 3000));
  }

  [Fact]
  public void Calibration_diagnostics_never_hold_back_a_valid_fix() {
    // Calibration is optional: a valid fix from raw ranges, with no bank at all, is a fix.
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(0, 42));
    Nv5LpsStatusMessage value = Nv5LpsTestData.Status(42);
    value.CalibrationRevision = 0;
    value.CalibrationStatus = Nv5RangingCalibrationStatuses.NoRecord;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 1000));
    Assert.True(model.Verified(1000));
    Assert.True(model.PoseCurrent(1000));
    // A refused record changes nothing either; the fix keeps its own conditions.
    value = Nv5LpsTestData.Status(0, 2, 1001000);
    value.CalibrationStatus = Nv5RangingCalibrationStatuses.AssemblyMismatch;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 2000));
    Assert.True(model.PoseCurrent(2000));
    value = Nv5LpsTestData.Status(0, 3, 1002000);
    value.OwnerFlags = Nv5LpsOwnerFlags.Bound | Nv5LpsOwnerFlags.Maintenance;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 3000));
    Assert.False(model.PoseCurrent(3000));
    // Calibrated rows outside the expected ones make the report malformed, not partly calibrated.
    value = Nv5LpsTestData.Status(0, 4, 1003000);
    value.CalibratedMask = 2;
    Assert.False(model.Receive(Nv5LpsTestData.Packet(value), "route", 4000));
  }

  [Fact]
  public void A_late_answer_or_an_invalid_covariance_never_makes_a_fix() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(0, 42));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(42)), "route",
        3000001));
    Assert.False(model.Verified(3000001));
    Assert.False(model.PoseCurrent(3000001));
    Assert.NotNull(model.Request(3000002, 43));
    Nv5LpsStatusMessage value = Nv5LpsTestData.Status(43, 2, 4000000);
    value.CovarianceEnu = [1e8f, 99999, 0, 0, 0, 0];
    Assert.True(model.Receive(Nv5LpsTestData.Packet(value), "route", 3001000));
    Assert.True(model.Verified(3001000));
    Assert.False(model.PoseCurrent(3001000));
  }

  [Fact]
  public void A_report_whose_clock_ran_ahead_of_this_one_is_refused() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(100000, 42));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(42)), "route", 110000));
    // 1 s passed here since the challenge; the modem's clock claims 1.1 s since its answer.
    Assert.False(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(0, 2, 2100000)),
        "route", 1100000));
    Assert.True(model.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status(0, 2, 2001000)),
        "route", 1100000));
  }

  [Fact]
  public void Rows_and_fixes_the_firmware_never_sends_are_refused() {
    var model = new Nv5LpsTelemetry();
    model.Observe(Peer);
    Assert.NotNull(model.Request(0, 42));
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status(42);
    report.BeaconMask = 3;
    report.CalibratedMask = 1;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(report), "route", 1000));
    bool Deliver(byte index, byte flags, float raw, byte calibration) {
      Nv5LpsBeaconMessage row = Nv5LpsTestData.Row(report);
      row.Index = index;
      row.Flags = flags;
      row.RawMetres = raw;
      row.CalibrationStatus = calibration;
      row.Metres = row.Variance = row.PersistentVariance = float.NaN;
      return model.Receive(Nv5LpsTestData.Packet(row), "route", 2000);
    }
    // Counted as calibrated by the report, yet without its correction.
    Assert.False(Deliver(0, Nv5LpsBeaconFlags.Observed, 4.5f,
        Nv5RangingCalibrationStatuses.NoRecord));
    // No raw aggregate, yet "applied".
    Assert.False(Deliver(1, Nv5LpsBeaconFlags.Observed, float.NaN,
        Nv5RangingCalibrationStatuses.Applied));
    // No raw aggregate after an incomplete burst: shown as such.
    Assert.True(Deliver(1, Nv5LpsBeaconFlags.Observed, float.NaN,
        Nv5RangingCalibrationStatuses.Incomplete));
    // A row outside the expected ones.
    Assert.False(Deliver(2, 0, float.NaN, Nv5RangingCalibrationStatuses.NotEvaluated));
    // A rover fix needs surveyed beacons to range to.
    report = Nv5LpsTestData.Status(0, 2, 1001000);
    report.BeaconMask = 0;
    Assert.True(model.Receive(Nv5LpsTestData.Packet(report), "route", 3000));
    Assert.False(model.PoseCurrent(3000));
  }
}
