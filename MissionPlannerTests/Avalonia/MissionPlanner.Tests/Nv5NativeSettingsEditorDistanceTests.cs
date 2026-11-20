using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

// The Distance group of a transaction-capable modem and the ranging switch-off. The catalogue
// modem transmits data on radio 1 and receives on radio 2, where ranging is on with the receiver
// measuring: there it measures, and on radio 1 it would answer.
public partial class Nv5NativeSettingsEditorTests {
  [Fact]
  public void Distance_rows_follow_the_settings_until_telemetry_arrives() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor unread = bench.Add(null);
    Assert.All(unread.DistanceRows, row => Assert.Equal("Not read yet", row.Mode));

    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.Equal(["Radio 1", "Radio 2"], editor.DistanceRows.Select(row => row.Name));
    NvDistanceRow off = editor.DistanceRows[0];
    Assert.Equal("Ranging off", off.Mode);
    Assert.Equal("", off.Reading);
    Assert.Equal("Ranging off", off.Text);
    NvDistanceRow on = editor.DistanceRows[1];
    Assert.Equal("Ranging on · measures", on.Mode);
    Assert.Equal("No distance telemetry", on.Reading);
    Assert.Equal("", on.Alert);
    Assert.Equal("", on.Burst);
  }

  [Fact]
  public void A_reading_shows_metres_validity_age_and_its_latest_burst() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    bench.Distance(Reading(2, Nv5DistanceStatuses.Valid, centimetres: 1234, valid: 3,
        timedOut: 1, calibrationStatus: Nv5RangingCalibrationStatuses.NoRecord));

    NvDistanceRow row = editor.DistanceRows[1];
    Assert.Equal("Ranging on · measures", row.Mode);
    Assert.Equal("12.34 m · VALID · age 0 s · raw", row.Reading);
    Assert.Equal("", row.Alert);
    Assert.Equal("", row.AlertSeparator);
    Assert.Equal("last burst 3 of 4 valid, 1 timed out", row.Burst);
    // Having no calibration record is an ordinary state, so only the tip says so.
    Assert.Equal("Ranging on · measures · 12.34 m · VALID · age 0 s · raw · no calibration "
        + "record · last burst 3 of 4 valid, 1 timed out", row.Text);

    // A reading of the moment, not a running counter: the age stays whole seconds.
    bench.Wait(1400);
    Assert.Equal("12.34 m · VALID · age 2 s · raw", editor.DistanceRows[1].Reading);
  }

  [Fact]
  public void A_calibrated_reading_shows_the_corrected_range_and_its_reference_plane() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    Nv5DistanceMessage value = Reading(2, Nv5DistanceStatuses.Valid, centimetres: 1234,
        valid: 1, calibrationStatus: Nv5RangingCalibrationStatuses.Applied);
    value.Flags = Nv5DistanceFlags.Restored;
    value.Calibration = Nv5DistanceCalibrations.Calibrated;
    value.CalibrationEpoch = 3;
    value.BankRevision = 9;
    value.CalibrationId[0] = 0x42;
    value.ReferenceScope = Nv5RangingReferenceScopes.WholeAssembly;
    value.CorrectedMetres = 12.5f;
    value.IndependentVariance = 0.01f;
    value.PersistentVariance = 0.02f;
    bench.Distance(value);
    Assert.Equal("12.50 m · calibrated whole assembly · raw 12.34 m · VALID · age 0 s",
        editor.DistanceRows[1].Reading);

    // A correction the modem cannot prove is not shown as one: the raw range says why.
    value.CalibrationEpoch = 0;
    bench.Distance(value);
    Assert.Equal("12.34 m · VALID · age 0 s · raw · invalid calibration result",
        editor.DistanceRows[1].Reading);
  }

  [Fact]
  public void Only_a_failure_is_red_and_silence_is_named_once_it_is_overdue() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    bench.Distance(Reading(2, Nv5DistanceStatuses.Timeout, timedOut: 5));
    NvDistanceRow row = editor.DistanceRows[1];
    Assert.Equal("", row.Reading);
    Assert.Equal("Timed out", row.Alert);
    Assert.Equal("last burst 0 of 5 valid, 5 timed out", row.Burst);

    bench.Wait(1900);
    Assert.Equal("Timed out", editor.DistanceRows[1].Alert);
    bench.Wait(1100);
    Assert.Equal("Timed out · no telemetry for 3 s", editor.DistanceRows[1].Alert);

    // A reading that stopped arriving is stale, which is not a failure.
    bench.Distance(Reading(2, Nv5DistanceStatuses.Valid, centimetres: 250, valid: 1));
    bench.Wait(3000);
    Assert.Equal("2.50 m · STALE · age 3 s · raw", editor.DistanceRows[1].Reading);
    Assert.Equal("", editor.DistanceRows[1].Alert);

    bench.Distance(Reading(2, Nv5DistanceStatuses.Waiting));
    bench.Wait(3000);
    Assert.Equal("waiting for the first result", editor.DistanceRows[1].Reading);
    Assert.Equal("no telemetry for 3 s", editor.DistanceRows[1].Alert);
    Assert.Equal(" · ", editor.DistanceRows[1].AlertSeparator);
  }

  [Fact]
  public void The_answering_modem_counts_the_requests_it_answered() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    // Radio 1 transmits and the receiver measures: this modem answers there.
    bench.Distance(Reading(1, Nv5DistanceStatuses.Waiting));
    Assert.Equal("Ranging on · answers", editor.DistanceRows[0].Mode);
    Assert.Equal("no request answered yet", editor.DistanceRows[0].Reading);

    bench.Distance(Reading(1, Nv5DistanceStatuses.Waiting, valid: 2));
    NvDistanceRow row = editor.DistanceRows[0];
    Assert.Equal("", row.Reading);
    Assert.Equal("last burst 2 of 2 answered", row.Burst);
    Assert.Equal("Ranging on · answers · last burst 2 of 2 answered", row.Text);
  }

  [Fact]
  public void Distance_telemetry_counts_only_from_the_modem_on_its_own_route() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    Target peer = bench.Modem.Peer;
    Assert.False(editor.Receive(DistancePacket(peer.System, (byte)(peer.Component + 1),
        Reading(2, Nv5DistanceStatuses.Valid, centimetres: 100, valid: 1)), peer.Route));
    Assert.False(editor.Receive(DistancePacket(peer.System, peer.Component,
        Reading(2, Nv5DistanceStatuses.Valid, centimetres: 100, valid: 1)), "another link"));
    Assert.Equal("No distance telemetry", editor.DistanceRows[1].Reading);

    // A newly chosen modem starts without the readout of the previous one.
    bench.Distance(Reading(2, Nv5DistanceStatuses.Valid, centimetres: 100, valid: 1));
    Assert.StartsWith("1.00 m", editor.DistanceRows[1].Reading, StringComparison.Ordinal);
    editor.ObserveTarget(peer with { Uid = Enumerable.Repeat((byte)'Z', 18).ToArray() }, 10000);
    Assert.Equal("Not read yet", editor.DistanceRows[1].Mode);
  }

  [Fact]
  public void Ranging_state_is_known_only_from_settings_read_at_the_committed_revision() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor unread = bench.Add(null);
    Assert.Equal(-1, unread.RangingState(0));

    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.Equal(0, editor.RangingState(0));
    Assert.Equal(1, editor.RangingState(1));
    Assert.Equal(-1, editor.RangingState(2));

    // Telemetry that ranging runs counts; telemetry that it is off says nothing about what is
    // saved, which the next boot may switch on.
    bench.Distance(Reading(1, Nv5DistanceStatuses.Disabled));
    Assert.Equal(0, editor.RangingState(0));
    bench.Distance(Reading(1, Nv5DistanceStatuses.Waiting));
    Assert.Equal(1, editor.RangingState(0));
    bench.Distance(Reading(2, Nv5DistanceStatuses.Disabled));
    Assert.Equal(1, editor.RangingState(1));
  }

  [Fact]
  public void Switching_ranging_off_saves_only_the_switch_and_the_peer_proof_without_a_read() {
    Nv5FakeModem modem = CatalogueModem();
    modem.CommitModel = true;
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach(new Nv5SettingsSnapshots());
    bool? finished = null;
    bool commitAnnounced = false;
    editor.ConfigurationFinished += success => finished = success;
    editor.CommitSent += _ => commitAnnounced = true;
    int readBegins = modem.ReadBegins;

    Assert.True(editor.SwitchRangingOff(1));
    Assert.True(editor.Saving);
    Assert.True(bench.Pump(() => editor.StatusText.Contains("Waiting for restart",
        StringComparison.Ordinal)), editor.StatusText);
    Assert.Equal(["CH2_PEER_HASH", "CH2_RNG_EN"], modem.Staged.Keys.Order(StringComparer.Ordinal));
    Assert.All(modem.Staged.Values, value => Assert.Equal(0u, value));
    Assert.Equal(readBegins, modem.ReadBegins);
    Assert.False(commitAnnounced);

    modem.Reboot();
    editor.ObserveTarget(modem.Peer with { TransportEpoch = modem.Peer.TransportEpoch + 1 }, 30);
    Assert.True(bench.Pump(() => finished != null));
    Assert.True(finished, editor.StatusText);
    Assert.Equal("Ranging switched off, restarted and verified active.", editor.StatusText);
    Assert.Equal("Ranging off", editor.DistanceRows[1].Mode);
    Assert.True(bench.Pump(() => editor.Ready && editor.ValuesReadBack));
    Assert.Equal(0u, Number(editor, "CH2_RNG_EN"));
    Assert.Equal(1, modem.Commits);
  }

  [Fact]
  public void A_restart_before_the_commit_begins_the_switch_off_again_on_the_next_boot() {
    Nv5FakeModem modem = CatalogueModem();
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach();
    // The modem restarts while the candidate is staged: nothing it was sent survives.
    bool restarting = false;
    bench.Drop = (_, frame) => restarting
        || (restarting = frame.Payload is Nv5ConfigControlMessage {
          Operation: Nv5ConfigOperations.Validate,
        });

    Assert.True(editor.SwitchRangingOff(0));
    Assert.True(bench.Pump(() => restarting, steps: 2000));
    Assert.Equal(0, modem.Commits);
    bench.Drop = null;
    modem.Reboot();
    editor.ObserveTarget(modem.Peer with { TransportEpoch = modem.Peer.TransportEpoch + 1 }, 30);
    Assert.Contains("switching ranging off again", editor.StatusText, StringComparison.Ordinal);
    Assert.True(bench.Pump(() => modem.Commits == 1), editor.StatusText);
    Assert.Equal(0u, modem.Staged["CH1_RNG_EN"]);
    Assert.Equal(2, modem.Begins);
  }

  [Fact]
  public void A_switch_off_never_drops_unsaved_edits_and_needs_a_verified_modem() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.True(editor.SetValue("CH1_FREQ_HZ", "868100000"));
    Assert.False(editor.SwitchRangingOff(1));
    Assert.False(editor.Busy);

    Nv5NativeSettingsEditor unattached = bench.Add(null);
    Assert.False(unattached.SwitchRangingOff(1));
  }

  /// <summary>
  /// With MP_NV5_CONFIG_FIXTURE set to the NV5 firmware's host nv5-configuration-fixture: a
  /// switch-off begins a transaction from discovery alone, and the firmware's own configuration
  /// core validates, commits and activates it.
  /// </summary>
  [Fact]
  public void Real_firmware_core_accepts_a_switch_off_begun_without_a_read() {
    string? fixture = Environment.GetEnvironmentVariable("MP_NV5_CONFIG_FIXTURE");
    if (string.IsNullOrEmpty(fixture)) {
      return;
    }
    NvModemMavlinkDialect.Register();
    using var server = System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(fixture) {
          RedirectStandardInput = true,
          RedirectStandardOutput = true,
          UseShellExecute = false,
        })!;
    uint now = 1000;
    var editor = new Nv5NativeSettingsEditor(() => now, () => 255, 190);
    var queue = new Queue<Nv5ConfigurationFrame>();
    editor.SendFrame += (frame, _) => queue.Enqueue(frame);
    Target target = new Nv5FakeModem().Peer;
    int begins = 0;
    int reads = 0;
    bool Pump(Func<bool> done) {
      for (int step = 0; step < 20000 && !done(); step++) {
        if (!queue.TryDequeue(out Nv5ConfigurationFrame? frame)) {
          now += 20;
          editor.Service();
          continue;
        }
        if (frame.Payload is Nv5ConfigControlMessage control) {
          begins += control.Operation == Nv5ConfigOperations.Begin ? 1 : 0;
          reads += control.Operation == Nv5ConfigOperations.ReadBegin ? 1 : 0;
        }
        byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
            (MAVLink.MAVLINK_MSG_ID)frame.MessageId, frame.Payload, false, 255, 190);
        server.StandardInput.Write($"{now} {Convert.ToHexString(bytes).ToLowerInvariant()}\n");
        server.StandardInput.Flush();
        while (server.StandardOutput.ReadLine() is { } line && line.Trim() != ".") {
          editor.Receive(new MAVLink.MavlinkParse().ReadPacket(
              new MemoryStream(Convert.FromHexString(line.Trim())))!, target.Route);
        }
      }
      return done();
    }
    bool? finished = null;
    editor.ConfigurationFinished += success => finished = success;
    void Restart() {
      server.StandardInput.Write("REBOOT\n");
      server.StandardInput.Flush();
      Assert.Equal(".", server.StandardOutput.ReadLine()?.Trim());
      target = target with { TransportEpoch = target.TransportEpoch + 1 };
      editor.ObserveTarget(target, 30);
      Assert.True(Pump(() => finished != null), editor.StatusText);
      Assert.True(finished, editor.StatusText);
      finished = null;
      Assert.True(Pump(() => editor.Ready && editor.ValuesReadBack), editor.StatusText);
    }

    editor.ObserveTarget(target, 10000);
    Assert.True(Pump(() => editor.Ready), editor.StatusText);
    Assert.Equal(0u, Number(editor, "CH1_RNG_EN"));
    // A proof left from an earlier pairing, which the switch-off clears with ranging. The firmware
    // accepts only a proof that matches the link fingerprint, as the peer's would on a matched link.
    uint proof = Number(editor, "CH1_LINK_HASH");
    Assert.NotEqual(0u, proof);
    Assert.True(editor.SetValue("CH1_PEER_HASH",
        proof.ToString(System.Globalization.CultureInfo.InvariantCulture)), editor.StatusText);
    editor.Save();
    Assert.True(Pump(() => editor.StatusText.Contains("Waiting for restart",
        StringComparison.Ordinal) || editor.Failed), editor.StatusText);
    Assert.False(editor.Failed, editor.StatusText);
    Restart();
    Assert.Equal(proof, Number(editor, "CH1_PEER_HASH"));

    reads = begins = 0;
    Assert.True(editor.SwitchRangingOff(0));
    Assert.True(Pump(() => editor.StatusText.Contains("Waiting for restart",
        StringComparison.Ordinal) || editor.Failed), editor.StatusText);
    Assert.False(editor.Failed, editor.StatusText);
    Assert.Equal(1, begins);
    Assert.Equal(0, reads);
    Restart();
    Assert.Equal(0u, Number(editor, "CH1_PEER_HASH"));
    Assert.Equal(0u, Number(editor, "CH1_RNG_EN"));
    server.StandardInput.Close();
    Assert.True(server.WaitForExit(10_000));
    Assert.Equal(0, server.ExitCode);
  }

  private static Nv5DistanceMessage Reading(byte channel, byte status, int centimetres = 0,
      byte valid = 0, byte timedOut = 0,
      byte calibrationStatus = Nv5RangingCalibrationStatuses.NotEvaluated) => new() {
        Channel = channel,
        Status = status,
        DistanceCm = centimetres,
        AgeMs = 100,
        ValidCount = valid,
        TimeoutCount = timedOut,
        Calibration = Nv5DistanceCalibrations.Uncalibrated,
        CalibrationStatus = calibrationStatus,
        CalibrationId = new byte[16],
        RawMetres = float.NaN,
        CorrectedMetres = float.NaN,
        IndependentVariance = float.NaN,
        PersistentVariance = float.NaN,
      };

  private static MAVLink.MAVLinkMessage DistancePacket(byte system, byte component,
      Nv5DistanceMessage value) {
    NvModemMavlinkDialect.Register();
    byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
        (MAVLink.MAVLINK_MSG_ID)NvModemMessageIds.Nv5Distance, value, false, system, component);
    return new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes))!;
  }
}
