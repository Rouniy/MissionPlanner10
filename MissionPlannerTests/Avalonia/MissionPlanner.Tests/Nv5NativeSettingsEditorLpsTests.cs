using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Tests;

// The LPS row of a modem with LPS settings, and the freshness challenges behind it. The modem is
// the rover on radio 2 and ranges to one beacon unless a test says otherwise.
public partial class Nv5NativeSettingsEditorTests {
  [Fact]
  public void A_fix_from_raw_ranges_is_shown_and_says_which_ranges_were_calibrated() {
    var bench = new Bench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0));
    Nv5NativeSettingsEditor editor = bench.Add(null);
    editor.LpsChallenges = true;
    editor.ObserveTarget(bench.Modem.Peer, 10000);
    Assert.True(bench.Pump(() => editor.Ready && bench.Modem.Challenges.Count != 0),
        editor.StatusText);
    // No calibration at all: the fix is shown, from raw ranges, and the tip says why.
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status(
        Nv5LpsTestData.Nonce(bench.Modem.Challenges[0]));
    report.PoseValidForUs = 30000000;
    report.CalibrationRevision = 0;
    report.CalibrationStatus = Nv5RangingCalibrationStatuses.NoRecord;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    NvDistanceReadout shown = editor.LpsRow();
    Assert.Equal("Rover · Radio 2", shown.Mode);
    Assert.Equal("E 0.00, N 0.00, U 0.00 m ±1.00 m · raw ranges", shown.Reading);
    Assert.Equal("", shown.Alert);
    Assert.Contains("Calibration is optional; calibrated ranges: 0 of 1 (no calibration "
        + "record).", shown.Text, StringComparison.Ordinal);
    Assert.Contains("Reports confirmed current. Radio shared with the data link.", shown.Text,
        StringComparison.Ordinal);
    // Some or all of the displayed ranges calibrated: the fix says so, nothing more.
    report.Challenge = 0;
    report.BeaconMask = 3;
    report.CalibratedMask = 1;
    report.CalibrationRevision = 7;
    report.CalibrationStatus = Nv5RangingCalibrationStatuses.Applied;
    report.SnapshotSeq++;
    report.PoseTimeUs = ++report.SnapshotTimeUs;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    Assert.EndsWith(" · 1 of 2 ranges calibrated", editor.LpsRow().Reading,
        StringComparison.Ordinal);
    report.CalibratedMask = 3;
    report.SnapshotSeq++;
    report.PoseTimeUs = ++report.SnapshotTimeUs;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    Assert.EndsWith(" · calibrated ranges", editor.LpsRow().Reading, StringComparison.Ordinal);
  }

  [Fact]
  public void A_valid_fix_waits_for_its_reports_to_be_confirmed_current() {
    var bench = new Bench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0));
    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(Nv5LpsTestData.Status()),
        bench.Modem.Peer.Route));
    Assert.Equal("no position: not confirmed current yet", editor.LpsRow().Reading);
    Assert.Contains("Reports not confirmed current yet.", editor.LpsRow().Text,
        StringComparison.Ordinal);
  }

  [Fact]
  public void A_confirmed_range_counts_its_transport_delay() {
    var bench = new Bench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0));
    Nv5NativeSettingsEditor editor = bench.Add(null);
    editor.LpsChallenges = true;
    editor.ObserveTarget(bench.Modem.Peer, 10000);
    Assert.True(bench.Pump(() => editor.Ready && bench.Modem.Challenges.Count != 0),
        editor.StatusText);
    // The modem answers at once with a range it has just measured; the answer arrives 2.1 s
    // after the challenge.
    bench.Wait(2100);
    Assert.Single(bench.Modem.Challenges);
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status(
        Nv5LpsTestData.Nonce(bench.Modem.Challenges[0]));
    report.Status = 2;
    report.CalibrationRevision = 0;
    report.PoseValidForUs = 0;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    Nv5LpsBeaconMessage row = Nv5LpsTestData.Row(report);
    row.Flags = Nv5LpsBeaconFlags.Observed;
    row.RawMetres = 1.25f;
    row.Metres = row.Variance = row.PersistentVariance = float.NaN;
    row.AttemptStatus = 1;
    row.AcquiredAtUs = row.CompletedAtUs = report.SnapshotTimeUs;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(row), bench.Modem.Peer.Route));
    Assert.True(editor.Lps.Received);
    Assert.Equal("B1 1.25 m raw (2 s old)", editor.LpsRow().Burst);
  }

  [Fact]
  public void Each_radio_row_says_whether_it_ranges_for_lps() {
    Nv5FakeModem modem = Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0);
    modem.Set("CH2_RNG_EN", 0);
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.Equal(2, editor.DistanceRows.Count);
    Assert.Equal("Radio 2", editor.DistanceRows[1].Name);
    Assert.Equal("LPS rover", editor.DistanceRows[1].Mode);
    Assert.Equal("no LPS report yet", editor.DistanceRows[1].Reading);
    Assert.Equal("Ranging off", editor.DistanceRows[0].Mode);
    // One row per radio, and the group names both services.
    Assert.Equal("Distance and LPS", editor.DistanceTitle);
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status();
    report.Status = 2;
    report.CalibrationRevision = 0;
    report.PoseValidForUs = 0;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    Assert.Equal("no position: uncalibrated", editor.DistanceRows[1].Reading);
    // The rows a distance pair window shows stay the radio's own ranging state.
    Assert.Equal("Ranging off", editor.DistanceRow(1).Mode);

    // Ranging on as well on that radio: the row still shows LPS and says so.
    modem.Set("CH2_RNG_EN", 1);
    editor.RereadDevice();
    Assert.True(bench.Pump(() => editor.Ready && editor.ValuesReadBack), editor.StatusText);
    Assert.Equal("LPS rover · ranging on", editor.DistanceRows[1].Mode);
    Assert.Equal("Ranging on · measures", editor.DistanceRow(1).Mode);
  }

  [Fact]
  public void A_beacon_row_names_its_survey_row_and_its_latest_answer() {
    Nv5FakeModem modem = Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, 4294967294);
    modem.Set("CH2_RNG_EN", 0);
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.Equal("Beacon B2 · Radio 2", editor.LpsRow().Mode);
    Assert.Equal("LPS beacon B2", editor.DistanceRows[1].Mode);
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status(uid: 'V');
    report.Role = Nv5LpsRoles.Beacon;
    report.Status = 0;
    report.PoseValidForUs = 0;
    report.BeaconMask = 0;
    report.LastAttemptStatus = 1;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report, component: 12),
        bench.Modem.Peer.Route));
    NvDistanceReadout shown = editor.LpsRow();
    Assert.Equal("answering", shown.Reading);
    Assert.Equal("last burst complete", shown.Burst);
    Assert.DoesNotContain("Calibration is optional", shown.Text, StringComparison.Ordinal);
    report.SnapshotSeq++;
    report.LastAttemptStatus = 4;
    report.OwnerFlags = Nv5LpsOwnerFlags.Bound | Nv5LpsOwnerFlags.Dedicated
        | Nv5LpsOwnerFlags.Faulted;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report, component: 12),
        bench.Modem.Peer.Route));
    shown = editor.LpsRow();
    Assert.Equal("", shown.Reading);
    Assert.Equal("radio fault · timed out", shown.Alert);
    Assert.Contains("Radio used by LPS alone; its data link is paused.", shown.Text,
        StringComparison.Ordinal);
  }

  [Fact]
  public void The_lps_row_follows_the_reports_and_names_their_silence() {
    var bench = new Bench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0));
    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.Equal("Rover · Radio 2", editor.LpsRow().Mode);
    Assert.Equal("no LPS report yet", editor.LpsRow().Reading);
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status();
    report.Status = 2;
    report.CalibrationRevision = 0;
    report.PoseValidForUs = 0;
    report.BeaconMask = 3;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    Nv5LpsBeaconMessage row = Nv5LpsTestData.Row(report);
    row.Flags = Nv5LpsBeaconFlags.Observed;
    row.RawMetres = 1.25f;
    row.Metres = row.Variance = row.PersistentVariance = float.NaN;
    row.AcquiredAtUs = 900000;
    row.CompletedAtUs = 950000;
    row.AttemptStatus = 1;
    row.BeaconId = 1;
    row.Address = 0x32101201;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(row), bench.Modem.Peer.Route));
    row.Index = 1;
    row.Flags = 0;
    row.AttemptStatus = 4;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(row), bench.Modem.Peer.Route));
    NvDistanceReadout shown = editor.LpsRow();
    Assert.Equal("no position: uncalibrated", shown.Reading);
    Assert.Equal("", shown.Alert);
    Assert.Equal("B1 1.25 m raw · B2 timed out", shown.Burst);
    Assert.Contains("B1 (ID 1, address 0x32101201): 1.250 m raw.", shown.Text,
        StringComparison.Ordinal);

    // A new capture without a usable raw range: its row says so, and the tip names the fault.
    report.SnapshotSeq++;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    Assert.Equal("B1 missing · B2 missing", editor.LpsRow().Burst);
    row = Nv5LpsTestData.Row(report);
    row.Flags = Nv5LpsBeaconFlags.Observed;
    row.Eligible = 1;
    row.Exchanges = 1;
    row.AttemptStatus = 1;
    row.RawMetres = row.Metres = row.Variance = row.PersistentVariance = float.NaN;
    row.CalibrationStatus = Nv5RangingCalibrationStatuses.Incomplete;
    Nv5LpsBeaconMessage refused = row;
    refused.RawMetres = float.PositiveInfinity;
    Assert.False(editor.Receive(Nv5LpsTestData.Packet(refused), bench.Modem.Peer.Route));
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(row), bench.Modem.Peer.Route));
    shown = editor.LpsRow();
    Assert.StartsWith("B1 raw range unavailable", shown.Burst, StringComparison.Ordinal);
    Assert.Contains("incomplete ranging burst", shown.Text, StringComparison.Ordinal);

    // When the reports stop, the row shows only the silence, in red.
    bench.Wait(3200);
    shown = editor.LpsRow();
    Assert.Equal("", shown.Reading);
    Assert.Equal("no LPS report for 3 s", shown.Alert);
    Assert.Equal("", shown.Burst);
    Assert.Equal("no LPS report for 3 s", editor.DistanceRows[1].Alert);
  }

  [Fact]
  public void Freshness_is_asked_for_only_when_wanted_and_never_once_a_commit_went_out() {
    var bench = new Bench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0));
    Nv5NativeSettingsEditor editor = bench.Attach();
    // Time passes with every request answered, as on a live link.
    void Elapse(uint milliseconds) {
      uint until = bench.Now + milliseconds;
      bench.Pump(() => unchecked((int)(bench.Now - until)) >= 0);
    }
    Elapse(6000);
    Assert.Empty(bench.Modem.Challenges);
    editor.LpsChallenges = true;
    Elapse(100);
    MAVLink.mavlink_command_long_t first = Assert.Single(bench.Modem.Challenges);
    Assert.Equal(NvModemMessageIds.Nv5LpsStatus, (uint)first.param1);
    Assert.Equal((bench.Modem.Peer.System, bench.Modem.Peer.Component),
        (first.target_system, first.target_component));
    // Unanswered, it is asked again after three seconds with a new nonce.
    Elapse(2800);
    Assert.Single(bench.Modem.Challenges);
    Elapse(400);
    Assert.Equal(2, bench.Modem.Challenges.Count);
    Assert.NotEqual(Nv5LpsTestData.Nonce(first), Nv5LpsTestData.Nonce(bench.Modem.Challenges[1]));
    // Answered, the fix is shown.
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status(
        Nv5LpsTestData.Nonce(bench.Modem.Challenges[1]));
    report.PoseValidForUs = 30000000;
    Assert.True(editor.Receive(Nv5LpsTestData.Packet(report), bench.Modem.Peer.Route));
    Assert.StartsWith("E 0.00, N 0.00", editor.LpsRow().Reading, StringComparison.Ordinal);

    // A commit may restart the modem: its reports are withdrawn at once, and nothing is asked
    // while the commit has no answer.
    bench.Drop = (_, frame) => frame.Payload is Nv5ConfigControlMessage {
      Operation: Nv5ConfigOperations.Commit,
    };
    Assert.True(editor.SetValue("LPS_FC_SYS", "2"));
    editor.Save();
    Assert.True(bench.Pump(() => editor.AwaitingReconnect), editor.StatusText);
    Assert.False(editor.Lps.Received);
    Assert.Equal("no LPS report yet", editor.LpsRow().Reading);
    int asked = bench.Modem.Challenges.Count;
    Elapse(2500);
    Assert.True(editor.AwaitingReconnect, editor.StatusText);
    Assert.Equal(asked, bench.Modem.Challenges.Count);
  }

  [Fact]
  public void A_modem_without_lps_settings_has_no_lps_row_and_is_never_asked() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Add(null);
    editor.LpsChallenges = true;
    editor.ObserveTarget(bench.Modem.Peer, 10000);
    uint until = bench.Now + 4000;
    Assert.True(bench.Pump(() => editor.Ready && unchecked((int)(bench.Now - until)) >= 0),
        editor.StatusText);
    Assert.Empty(bench.Modem.Challenges);
    Assert.False(editor.LpsSupported);
    Assert.Equal("Distance", editor.DistanceTitle);
    Assert.Equal("No LPS in this firmware", editor.LpsRow().Mode);
    Assert.Equal(0, editor.LpsRadio());
  }
}
