using System.Globalization;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

// The LPS field window: one surveyed field and each modem's role in it. Unless a test says
// otherwise, Modem 1 is the rover and Modems 2 and 3 are beacons B1 and B2, all on radio 2 and
// holding the same field.
public class NvModemLpsFieldTests {
  [Fact]
  public void Modems_tabs_and_rows_are_named_as_the_pair_window_names_them() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    NvModemLpsFieldViewModel field = bench.Open();
    Assert.Equal(["Modem 1 · 10:11", "Modem 2 · 10:12"], field.Modems.Select(modem => modem.Name));
    Assert.Equal("192.168.144.22", field.Modems[1].Address);
    Assert.Equal(["Modem 1", "Modem 2"], field.LpsRows.Select(row => row.Name));
    Assert.Equal("Rover · Radio 2", field.LpsRows[0].Mode);
    Assert.Equal("Beacon B1 · Radio 2", field.LpsRows[1].Mode);
    Assert.Equal(["Modem 1 · 10:11", "Modem 2 · 10:12"],
        field.FieldSources.Select(source => source.Label));
    Assert.Equal("Every modem is read. Choose the roles, check the field, then verify and save.",
        field.Status);
    // The field shown is the one the first modem holds.
    Assert.Equal(Nv5LpsField.Canonical(Nv5LpsTestData.Field()), field.Field);
    Assert.Same(field.FieldSources[0], field.SelectedFieldSource);
    // Each modem is asked whether its LPS reports are current.
    Assert.True(bench.Pump(() => bench.Modems.All(modem => modem.Challenges.Count != 0)));
  }

  [Fact]
  public void Unchanged_modems_are_not_saved_and_old_firmware_is_left_alone() {
    var old = new Nv5FakeModem {
      Peer = new Target(Enumerable.Repeat((byte)'W', 18).ToArray(), "route-2", 1,
          NvModemCapabilities.ConfigurationTransactions, 10, 13, true),
      Settings = Nv5FakeModem.Catalogue(transmitsOnRadio1: false),
    };
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue), old);
    NvModemLpsFieldViewModel field = bench.Open();
    Assert.Equal("Nothing to save", field.ApplyText);
    Assert.False(field.CanApply);
    Assert.Contains("already holds", field.ApplyTip, StringComparison.Ordinal);
    Assert.Contains("nothing to change", field.Modems[0].Line, StringComparison.Ordinal);
    Assert.Contains("no LPS settings", field.Modems[2].Line, StringComparison.Ordinal);
    Assert.False(field.Modems[2].LoadedShown);
    Assert.False(field.FieldSources[2].IsEnabled);

    // Switching the beacon off saves that modem alone, and only its role.
    field.Modems[1].Role = field.Modems[1].Roles[0];
    Assert.True(field.CanApply, field.ApplyTip);
    Assert.Equal("Verify and save 1 modem", field.ApplyText);
    Assert.Contains("switches LPS off", field.Modems[1].Line, StringComparison.Ordinal);
    Assert.False(field.Modems[1].RadioShown);
    field.Apply();
    Assert.True(bench.Pump(() => bench.Modems[1].Commits == 1), field.Status);
    Assert.Equal(0, bench.Modems[0].Begins);
    Assert.Equal(0, bench.Modems[2].Begins);
    Assert.Equal(["LPS_ROLE"], bench.Modems[1].Staged.Keys);
    Assert.Equal(0u, bench.Modems[1].Staged["LPS_ROLE"]);
  }

  [Fact]
  public void A_survey_change_saves_the_beacons_before_the_rover_and_reads_each_back() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue),
        Nv5LpsTestData.Modem(2, Nv5LpsRoles.Beacon, uint.MaxValue - 1)) { Restarts = true };
    NvModemLpsFieldViewModel field = bench.Open();
    Assert.Equal("Nothing to save", field.ApplyText);
    field.BeaconRows[0].East = "1.5";
    Assert.Equal("1500", field.Field["LPS_B1_E_MM"]);
    Assert.Equal("1.500", field.BeaconRows[0].East);
    Assert.True(field.CanApply, field.ApplyTip);
    Assert.Equal("Verify and save 3 modems", field.ApplyText);
    Assert.Contains("Saving sets 1", field.GenerationText, StringComparison.Ordinal);
    var committed = new List<int>();
    field.CommitSent += (editor, _) => committed.Add(field.Editors.ToList().IndexOf(editor));
    field.Apply();
    Assert.True(field.Active);
    Assert.False(field.InputsEnabled);
    Assert.True(bench.Pump(() => bench.Modems[1].Commits == 1), field.Status);
    // One at a time: nothing else began before the first beacon committed.
    Assert.Equal(0, bench.Modems[0].Begins);
    Assert.Equal(0, bench.Modems[2].Begins);
    Assert.Equal("Waiting for its turn.", field.Modems[0].Line);
    Assert.False(field.TryClose());
    Assert.Equal("Wait until the modem being saved has finished.", field.Status);

    Assert.True(bench.Pump(() => !field.Active), field.Status);
    Assert.Equal([1, 2, 0], committed);
    Assert.StartsWith("Saved. Every changed modem restarted and read back what was saved.",
        field.Status, StringComparison.Ordinal);
    Assert.True(bench.Pump(() => field.ApplyText == "Nothing to save" && field.InputsEnabled),
        field.ApplyTip);
    foreach (NvLpsModem modem in field.Modems) {
      Assert.Equal("1", Text(modem.Editor, "LPS_FIELD_GEN"));
      Assert.Equal("1500", Text(modem.Editor, "LPS_B1_E_MM"));
    }
    Assert.Equal("Rover on Radio 2; nothing to change. LPS has Radio 2 to itself: its data link "
        + "is paused.", field.Modems[0].Line);
    Assert.True(field.TryClose());
  }

  [Fact]
  public void The_survey_shows_operator_units_and_keeps_exact_native_values() {
    NvModemLpsFieldViewModel field = new LpsFieldBench().Create();
    SortedDictionary<string, string> survey = Nv5LpsTestData.Field();
    survey["LPS_LAT_E7"] = "-338567890";
    survey["LPS_B2_ADDR"] = "3203334145";
    Assert.True(field.SetField(survey, out string error), error);
    Assert.Equal("-33.8567890", field.LatitudeText);
    Assert.Equal("0xBEEF0001", field.BeaconRows[1].Address);
    Assert.Equal("-2147483.648", field.BeaconRows[0].East);
    Assert.Equal("0.0001", field.BeaconRows[0].SigmaEast);
    Assert.Equal("0xFFFFFFFEFFFFFFFF", field.DatumText);
    Assert.Equal(3, field.SelectedBeaconCount!.Value);
    Assert.Equal([true, true, true, false], field.BeaconRows.Select(row => row.IsShown));
    // Showing a value never rewrites it.
    Assert.Equal(survey, field.Field);

    field.LatitudeText = "50.1234567";
    Assert.Equal("501234567", field.Field["LPS_LAT_E7"]);
    field.BeaconRows[1].Address = "00c0ffee";
    Assert.Equal("12648430", field.Field["LPS_B2_ADDR"]);
    Assert.Equal("0x00C0FFEE", field.BeaconRows[1].Address);
    field.BeaconRows[0].SigmaEast = "0.02";
    Assert.Equal(((float)(0.02 * 0.02)).ToString("G9", CultureInfo.InvariantCulture),
        field.Field["LPS_B1_CEE"]);
    field.EllipsoidHeight = 123.456m;
    Assert.Equal("123456", field.Field["LPS_HAE_MM"]);
    field.DatumText = "0x10";
    Assert.Equal(("16", "0"), (field.Field["LPS_DATUM_LO"], field.Field["LPS_DATUM_HI"]));
    Assert.Equal("0x0000000000000010", field.DatumText);

    // A value that does not parse leaves the field as it was and says what to enter.
    var before = new Dictionary<string, string>(field.Field);
    field.BeaconRows[0].North = "north";
    Assert.Equal(before, field.Field);
    Assert.Equal("0.000", field.BeaconRows[0].North);
    Assert.Equal("B1 North (m): enter metres with up to 3 decimals.", field.Status);
    field.LatitudeText = "50.12345678";
    Assert.Equal(before, field.Field);
    Assert.Equal("50.1234567", field.LatitudeText);
    Assert.Contains("up to 7 decimals", field.Status, StringComparison.Ordinal);

    // A fourth beacon shows its row.
    field.SelectedBeaconCount = field.BeaconCounts.Single(choice => choice.Value == 4);
    Assert.Equal("4", field.Field["LPS_BCOUNT"]);
    Assert.True(field.BeaconRows[3].IsShown);
  }

  [Fact]
  public void Hexadecimal_imports_show_and_save_the_same_value() {
    NvModemLpsFieldViewModel field = new LpsFieldBench().Create();
    SortedDictionary<string, string> survey = Nv5LpsTestData.Field();
    survey["LPS_B1_E_MM"] = "0x3E8";
    survey["LPS_B2_ADDR"] = "0xBEEF0001";
    survey["LPS_DATUM_LO"] = "0x10";
    survey["LPS_BCOUNT"] = "0x3";
    Assert.Equal("", Nv5LpsField.Validate(survey));
    Assert.True(field.SetField(survey, out _));
    Assert.Equal("1.000", field.BeaconRows[0].East);
    Assert.Equal("0xBEEF0001", field.BeaconRows[1].Address);
    Assert.Equal("0xFFFFFFFE00000010", field.DatumText);
    Assert.Equal("1000", field.Field["LPS_B1_E_MM"]);
    Assert.Equal("3203334145", field.Field["LPS_B2_ADDR"]);
    Assert.Equal("16", field.Field["LPS_DATUM_LO"]);
    Assert.Equal("3", field.Field["LPS_BCOUNT"]);
  }

  [Fact]
  public void The_radio_choice_says_whether_the_data_link_stops() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Off, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Off, 0, reservation: true));
    NvModemLpsFieldViewModel field = bench.Open();
    Assert.Equal("LPS off; nothing to change.", field.Modems[0].Line);
    Assert.False(field.Modems[0].RadioShown);
    field.Modems[0].Role = field.Modems[0].Roles[1];
    field.Modems[1].Role = field.Modems[1].Roles[2];
    Assert.True(field.Modems[1].BeaconShown);
    Assert.Contains("Choose the surveyed beacon", field.Modems[1].Line, StringComparison.Ordinal);
    Assert.True(field.Modems[1].LineIsProblem);
    field.Modems[1].Beacon = field.Modems[1].Beacons.Single(choice => choice.Value == 1);
    Assert.Equal("ID 4294967295", field.Modems[1].Beacon!.TipText);
    Assert.Contains("beacon B1", field.Modems[1].Line, StringComparison.Ordinal);
    Assert.Contains("its data link stops", field.Modems[0].Line, StringComparison.Ordinal);
    Assert.Contains("keeps its data link", field.Modems[1].Line, StringComparison.Ordinal);
    Assert.Equal("Rover settings for Modem 1 · 10:11", field.RoverName);
    Assert.True(field.RoverEnabled);
    Assert.Equal("Bursts on Radio 2 use its distance settings: 5 exchanges over 2500 kHz at 3 dBm. "
        + "Configure distance pair… changes them.", field.BurstText);

    // Two rovers block the save and say why.
    field.Modems[1].Role = field.Modems[1].Roles[1];
    Assert.False(field.CanApply);
    Assert.Contains("Only one modem can be the rover", field.ApplyTip, StringComparison.Ordinal);
    Assert.True(field.ProblemsShown);
    Assert.Equal("Rover settings wait until only one modem is the rover.", field.RoverName);
    Assert.False(field.RoverEnabled);
  }

  [Fact]
  public void A_radio_that_is_not_an_lr2021_cannot_take_lps() {
    Nv5FakeModem modem = Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0);
    modem.Settings.Add(new Nv5FakeModem.Setting("CH1_CHIP", 1));
    modem.Settings.Add(new Nv5FakeModem.Setting("CH2_CHIP", 0));
    var bench = new LpsFieldBench(modem, Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon,
        uint.MaxValue));
    NvModemLpsFieldViewModel field = bench.Open();
    NvLpsChoice first = field.Modems[0].Radios[0];
    Assert.False(first.IsEnabled);
    Assert.Equal("Radio 1 is not an LR2021, which LPS needs.", first.TipText);
    Assert.True(field.Modems[0].Radios[1].IsEnabled);
    field.Modems[0].Radio = first;
    Assert.Equal("Radio 1 is not an LR2021, which LPS needs.", field.Modems[0].Line);
    Assert.False(field.CanApply);
  }

  [Fact]
  public void The_rover_settings_follow_the_rover_and_are_saved_with_it() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    NvModemLpsFieldViewModel field = bench.Open();
    Assert.Equal(1m, field.FlightControllerSystem);
    field.LaunchHeight = 12.345m;
    field.FlightControllerComponent = 7;
    Assert.Equal("Verify and save 1 modem", field.ApplyText);
    Assert.Contains("rover on Radio 2", field.Modems[0].Line, StringComparison.Ordinal);
    field.Apply();
    Assert.True(bench.Pump(() => bench.Modems[0].Commits == 1), field.Status);
    Assert.Equal(12345u, bench.Modems[0].Staged["LPS_Z_MM"]);
    Assert.Equal(7u, bench.Modems[0].Staged["LPS_FC_COMP"]);
    Assert.Equal(0, bench.Modems[1].Begins);
  }

  [Fact]
  public void A_modem_being_read_again_holds_the_save() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue),
        Nv5LpsTestData.Modem(2, Nv5LpsRoles.Beacon, uint.MaxValue - 1));
    NvModemLpsFieldViewModel field = bench.Open();
    field.BeaconRows[0].East = "1.5";
    Assert.True(field.CanApply);
    // Modem 2 reconnects: until it is read again its settings are unknown, and it refuses reads.
    Nv5FakeModem second = bench.Modems[1];
    second.RefuseReadBegins = 1000;
    second.Peer = second.Peer with { TransportEpoch = second.Peer.TransportEpoch + 1 };
    field.Offer(second.Peer, 0, "192.168.144.22");
    Assert.True(bench.Pump(() => !field.CanApply, steps: 200));
    Assert.Contains("Wait until every modem is read", field.ApplyTip, StringComparison.Ordinal);
    Assert.Contains("again", field.Modems[1].Line, StringComparison.Ordinal);
    // A save asked for anyway saves nothing either.
    field.Apply();
    Assert.False(field.Active);
    Assert.Equal(0, bench.Begins);
    Assert.Equal("Not saved: wait until every modem is read.", field.Status);
  }

  [Fact]
  public void A_survey_row_taken_twice_blocks_the_save() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue),
        Nv5LpsTestData.Modem(2, Nv5LpsRoles.Beacon, uint.MaxValue - 1));
    NvModemLpsFieldViewModel field = bench.Open();
    field.BeaconRows[0].East = "1.5";
    Assert.True(field.CanApply);
    field.Modems[2].Beacon = field.Modems[2].Beacons.Single(choice => choice.Value == 1);
    field.Apply();
    Assert.Equal(0, bench.Begins);
    Assert.False(field.Active);
    Assert.Equal("B1 is Modem 2 · 10:12 already.", field.Modems[2].Line);
    Assert.False(field.CanApply);
  }

  [Fact]
  public void An_unused_fourth_row_matches_what_the_modems_store() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    NvModemLpsFieldViewModel field = bench.Open();
    // Left over from a four-beacon field: the modems store the unused row as zeros.
    var shown = new SortedDictionary<string, string>(field.Field.ToDictionary(pair => pair.Key,
        pair => pair.Value), StringComparer.Ordinal) {
      ["LPS_B4_ID"] = "9", ["LPS_B4_ADDR"] = "123", ["LPS_B4_E_MM"] = "5000",
    };
    Assert.True(field.SetField(shown, out _));
    bench.Pump(() => false, steps: 40);
    Assert.Equal("Nothing to save", field.ApplyText);
    Assert.Contains("nothing to change", field.Modems[0].Line, StringComparison.Ordinal);
  }

  [Fact]
  public void An_unconfirmed_commit_is_reported_as_uncertain_until_the_modem_is_read_again() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    bench.Modems[1].IndeterminateCommit = true;
    NvModemLpsFieldViewModel field = bench.Open();
    field.Modems[1].Role = field.Modems[1].Roles[0];
    field.Apply();
    Assert.True(bench.Pump(() => !field.Active), field.Status);
    Assert.Equal(1, bench.Modems[1].Commits);
    Assert.StartsWith("Outcome unknown", field.Modems[1].Line, StringComparison.Ordinal);
    Assert.StartsWith("Saving stopped at Modem 2 · 10:12:", field.Status,
        StringComparison.Ordinal);
    // Read again, the modem shows its role did not change, and the window says so.
    Assert.True(bench.Pump(() => field.Status.Contains("did not take effect",
        StringComparison.Ordinal)), field.Status);
    Assert.True(field.CanApply, field.ApplyTip);
    Assert.True(field.InputsEnabled);
  }

  [Fact]
  public void A_refused_check_stops_the_save_before_any_later_modem() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue),
        Nv5LpsTestData.Modem(2, Nv5LpsRoles.Beacon, uint.MaxValue - 1)) { Restarts = true };
    bench.Modems[2].RejectValidate = true;
    NvModemLpsFieldViewModel field = bench.Open();
    field.BeaconRows[0].East = "1.5";
    field.Apply();
    Assert.True(bench.Pump(() => !field.Active), field.Status);
    Assert.Equal([0, 1, 0], bench.Modems.Select(modem => modem.Commits));
    Assert.Equal("Saved, restarted and read back.", field.Modems[1].Line);
    Assert.StartsWith("Not saved: the modem refused the new settings.", field.Modems[2].Line,
        StringComparison.Ordinal);
    Assert.Equal("Not attempted.", field.Modems[0].Line);
    Assert.Contains("Modems saved before it keep their new settings.", field.Status,
        StringComparison.Ordinal);
    // The refused modem is read again; then the same window can save the other two.
    Assert.True(bench.Pump(() => field.CanApply), field.ApplyTip);
    Assert.Equal("Verify and save 2 modems", field.ApplyText);
  }

  [Fact]
  public void A_save_replaced_before_its_readback_stops_the_field() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue),
        Nv5LpsTestData.Modem(2, Nv5LpsRoles.Beacon, uint.MaxValue - 1)) { Restarts = true };
    NvModemLpsFieldViewModel field = bench.Open();
    // Another save reaches Modem 2 after its own was verified active and before it is read back.
    field.Modems[1].Editor.ConfigurationFinished += success => {
      if (success) {
        bench.Modems[1].Commit("LPS_FC_SYS", 9);
      }
    };
    field.BeaconRows[0].East = "1.5";
    field.Apply();
    Assert.True(bench.Pump(() => !field.Active), field.Status);
    Assert.Equal("Saved, but its restart or read-back was not confirmed: what it reads back is "
        + "not what was saved; another save replaced it.", field.Modems[1].Line);
    Assert.Equal([0, 1, 0], bench.Modems.Select(modem => modem.Commits));
    Assert.Equal("Not attempted.", field.Modems[2].Line);
  }

  [Fact]
  public void Close_waits_for_a_refused_check_to_be_aborted_on_its_modem() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    bench.Modems[1].AcceptedValidations = 0;
    NvModemLpsFieldViewModel field = bench.Open();
    field.Modems[1].Role = field.Modems[1].Roles[0];
    int closes = 0;
    field.CloseRequested += () => closes++;
    field.Apply();
    Assert.True(bench.Pump(() => bench.Modems[1].Validations == 1));
    // The refusal arrives and its abort waits for an answer from the modem.
    bench.Holding.Add(bench.Modems[1]);
    Assert.True(bench.Pump(() => !field.Active && bench.Held.Count != 0), field.Status);
    Assert.False(field.TryClose());
    Assert.Equal("Closing once Modem 2 · 10:12 has cancelled its unfinished save…", field.Status);
    Assert.Equal(0, closes);
    bench.Release();
    Assert.True(bench.Pump(() => closes == 1, steps: 200));
    Assert.Contains(bench.Modems[1].Seen, frame => frame.Payload is Nv5ConfigControlMessage
        { Operation: Nv5ConfigOperations.Abort });
    Assert.Equal(0, bench.Modems[1].Commits);
    Assert.True(field.TryClose());
  }

  [Fact]
  public void Close_during_the_reads_goes_at_once_and_frees_every_read_session() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    NvModemLpsFieldViewModel field = bench.Create();
    Assert.True(bench.Pump(() => bench.Modems.All(modem => modem.Reads > 2)));
    Assert.True(field.TryClose());
    bench.Pump(() => false, steps: 20);
    Assert.All(bench.Modems, modem => {
      Assert.True(modem.Reads < modem.Settings.Count);
      Assert.Contains(modem.Seen, frame => frame.Payload is Nv5ConfigControlMessage
          { Operation: Nv5ConfigOperations.ReadEnd });
    });
  }

  [Fact]
  public void A_field_document_is_exported_and_imported_whole() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    NvModemLpsFieldViewModel field = bench.Open();
    string directory = Directory.CreateTempSubdirectory("lps-field-").FullName;
    try {
      string path = Path.Combine(directory, "field.json");
      field.ExportField(path);
      Assert.Equal("Field exported.", field.Status);
      Assert.Equal([path], Directory.GetFiles(directory));
      Assert.True(Nv5LpsField.FromJson(File.ReadAllBytes(path), out SortedDictionary<string,
          string> exported, out string error), error);
      Assert.Equal(field.Field, exported);

      field.BeaconRows[0].East = "1.5";
      Assert.Equal("Verify and save 2 modems", field.ApplyText);
      field.ImportField(path);
      Assert.Equal("Field imported. Check the roles, then verify and save.", field.Status);
      Assert.Equal(exported, field.Field);
      Assert.Equal("Nothing to save", field.ApplyText);

      File.WriteAllText(path, "{\"format\": \"nv5-settings\"}");
      field.ImportField(path);
      Assert.Equal("Not an NV5 LPS field document (version 1).", field.Status);
      Assert.Equal(exported, field.Field);
      field.SelectedBeaconCount = field.BeaconCounts.Single(choice => choice.Value == 4);
      field.ExportField(path);
      Assert.Equal("Not exported. Field: Beacon IDs and nonzero addresses must be unique.",
          field.Status);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void An_export_replaces_its_document_and_removes_only_the_file_it_made() {
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
        Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue));
    NvModemLpsFieldViewModel field = bench.Open();
    string directory = Directory.CreateTempSubdirectory("lps-field-").FullName;
    try {
      string path = Path.Combine(directory, "field.json");
      string blocked = Path.Combine(directory, "blocked.json");
      // Files that merely share a document's name belong to someone else.
      string[] kept = [path + ".part", path + ".tmp", Path.Combine(directory, ".field.json.tmp"),
          blocked + ".part"];
      foreach (string file in kept) {
        File.WriteAllText(file, "kept");
      }
      File.WriteAllText(path, "old");
      string[] Files() => [.. Directory.GetFiles(directory).Order(StringComparer.Ordinal)];
      string[] expected = [.. kept.Append(path).Order(StringComparer.Ordinal)];

      field.ExportField(path);
      Assert.Equal("Field exported.", field.Status);
      Assert.True(Nv5LpsField.FromJson(File.ReadAllBytes(path), out _, out string error), error);
      Assert.Equal(expected, Files());
      Assert.All(kept, file => Assert.Equal("kept", File.ReadAllText(file)));

      // A folder in the document's place cannot be replaced: the export fails and removes only
      // the file it made.
      Directory.CreateDirectory(blocked);
      field.ExportField(blocked);
      Assert.Equal("Could not write the field document.", field.Status);
      Assert.Equal(expected, Files());
      Assert.All(kept, file => Assert.Equal("kept", File.ReadAllText(file)));
      Assert.Empty(Directory.EnumerateFileSystemEntries(blocked));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void A_modem_shows_the_field_another_modem_holds() {
    Nv5FakeModem other = Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue);
    other.Set("LPS_B3_E_MM", 2500);
    var bench = new LpsFieldBench(Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0), other);
    NvModemLpsFieldViewModel field = bench.Open();
    Assert.Equal("0", field.Field["LPS_B3_E_MM"]);
    field.SelectedFieldSource = field.FieldSources[1];
    field.ShowSourceField();
    Assert.Equal("2500", field.Field["LPS_B3_E_MM"]);
    Assert.Equal("Showing the field saved on Modem 2 · 10:12.", field.Status);
    // A field edited here stays when more modems are read.
    Assert.Equal("2.500", field.BeaconRows[2].East);
  }

  /// <summary>
  /// Three instances of the modem firmware's host configuration core, saved through the window
  /// as one field: two beacons, then the rover, each restarted and read back before the next.
  /// Runs when MP_NV5_CONFIG_FIXTURE names the fixture executable.
  /// </summary>
  [Fact]
  public void Real_firmware_cores_save_an_lps_field_beacons_first_through_the_window() {
    string? fixture = Environment.GetEnvironmentVariable("MP_NV5_CONFIG_FIXTURE");
    if (string.IsNullOrEmpty(fixture)) {
      return;
    }
    NvModemMavlinkDialect.Register();
    var servers = new List<System.Diagnostics.Process>();
    try {
      Target[] targets = [.. Enumerable.Range(0, 3).Select(index => new Target(
          Enumerable.Repeat((byte)('A' + index), 18).ToArray(), $"fixture-{index}", 1,
          NvModemCapabilities.ConfigurationTransactions, 10, 11, true))];
      foreach (Target target in targets) {
        servers.Add(System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            fixture) {
          RedirectStandardInput = true,
          RedirectStandardOutput = true,
          UseShellExecute = false,
        })!);
      }
      uint now = 10000;
      var queue = new Queue<(Nv5ConfigurationFrame Frame, Target Target)>();
      var restarts = new List<(uint Due, int Index)>();
      var commits = new List<int>();
      var store = new Nv5SettingsSnapshots();
      var field = new NvModemLpsFieldViewModel(
          () => new Nv5NativeSettingsEditor(() => now, () => 255, 190) { Snapshots = store });
      field.SendFrame += (frame, target) => queue.Enqueue((frame, target));
      int IndexOf(Target target) => Array.FindIndex(targets, candidate =>
          candidate.Uid.SequenceEqual(target.Uid));
      bool Pump(Func<bool> done, int steps = 60000) {
        for (int step = 0; step < steps && !done(); step++) {
          if (restarts.FirstOrDefault(restart => unchecked((int)(now - restart.Due)) >= 0) is
              { Due: not 0 } due) {
            restarts.Remove(due);
            targets[due.Index] = targets[due.Index] with {
              TransportEpoch = targets[due.Index].TransportEpoch + 1,
            };
            field.Offer(targets[due.Index], 0, $"fixture {due.Index}");
            continue;
          }
          if (!queue.TryDequeue(out (Nv5ConfigurationFrame Frame, Target Target) sent)) {
            now += 20;
            field.Service();
            continue;
          }
          // The host core has no LPS reports to confirm; freshness challenges go unanswered.
          if (sent.Frame.Payload is MAVLink.mavlink_command_long_t) {
            continue;
          }
          int index = IndexOf(sent.Target);
          if (sent.Frame.Payload is Nv5ConfigControlMessage { Operation: Nv5ConfigOperations.Commit }) {
            commits.Add(index);
          }
          System.Diagnostics.Process server = servers[index];
          byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
              (MAVLink.MAVLINK_MSG_ID)sent.Frame.MessageId, sent.Frame.Payload, false, 255, 190);
          server.StandardInput.Write($"{now} {Convert.ToHexString(bytes).ToLowerInvariant()}\n");
          server.StandardInput.Flush();
          while (server.StandardOutput.ReadLine() is { } line && line.Trim() != ".") {
            field.Receive(new MAVLink.MavlinkParse().ReadPacket(
                new MemoryStream(Convert.FromHexString(line.Trim())))!, sent.Target.Route);
          }
        }
        return done();
      }

      for (int i = 0; i < 3; i++) {
        field.Offer(targets[i], 10000, $"fixture {i}");
        int index = i;
        field.Modems[i].Editor.DurableSaved += () => {
          // The modem restarts once its save is durable, and is heard again a little later.
          System.Diagnostics.Process server = servers[index];
          server.StandardInput.Write("REBOOT\n");
          server.StandardInput.Flush();
          Assert.Equal(".", server.StandardOutput.ReadLine()?.Trim());
          restarts.Add((now + 650, index));
        };
      }
      Assert.True(Pump(() => field.Modems.All(modem => modem.Editable)), field.Status
          + " | " + string.Join(" | ", field.Modems.Select(modem => modem.Editor.StatusText)));
      SortedDictionary<string, string> survey = Nv5LpsTestData.Field();
      survey["LPS_B1_ID"] = "0";
      survey["LPS_B2_ID"] = "1";
      survey["LPS_B3_ID"] = "2";
      survey["LPS_B1_E_MM"] = "-100000";
      survey["LPS_B1_N_MM"] = "-100000";
      survey["LPS_B2_E_MM"] = "100000";
      survey["LPS_B2_N_MM"] = "-100000";
      survey["LPS_B3_E_MM"] = "100000";
      survey["LPS_B3_N_MM"] = "100000";
      Assert.True(field.SetField(survey, out string error), error);
      for (int i = 0; i < 3; i++) {
        field.Modems[i].Role = field.Modems[i].Roles[i == 0 ? 1 : 2];
        if (i > 0) {
          field.Modems[i].Beacon = field.Modems[i].Beacons.Single(choice => choice.Value == i);
        }
      }
      Assert.True(Pump(() => field.CanApply, steps: 100),
          field.ApplyTip + " | " + string.Join(" | ", field.Modems.Select(modem => modem.Line)));
      Assert.Equal("Verify and save 3 modems", field.ApplyText);
      field.Apply();
      Assert.True(Pump(() => !field.Active), field.Status + " | "
          + string.Join(" | ", field.Modems.Select(modem => modem.Line)));
      Assert.Equal([1, 2, 0], commits);
      Assert.StartsWith("Saved. Every changed modem restarted and read back what was saved.",
          field.Status, StringComparison.Ordinal);
      Assert.True(Pump(() => field.ApplyText == "Nothing to save" && field.InputsEnabled),
          field.ApplyTip);
      for (int i = 0; i < 3; i++) {
        Nv5NativeSettingsEditor editor = field.Modems[i].Editor;
        Assert.True(editor.Ready && editor.ValuesReadBack, editor.StatusText);
        Assert.Equal("1", Text(editor, "LPS_FIELD_GEN"));
        Assert.Equal("4294967295", Text(editor, "LPS_B1_ADDR"));
        Assert.Equal("100000", Text(editor, "LPS_B3_N_MM"));
        Assert.Equal(i == 0 ? "1" : "2", Text(editor, "LPS_ROLE"));
        Assert.Equal(i == 0 ? "0" : (i - 1).ToString(CultureInfo.InvariantCulture),
            Text(editor, "LPS_BEACON"));
      }
    } finally {
      foreach (System.Diagnostics.Process server in servers) {
        server.StandardInput.Close();
        Assert.True(server.WaitForExit(10_000));
        server.Dispose();
      }
    }
  }

  private static string Text(Nv5NativeSettingsEditor editor, string name) =>
      Nv5NativeSettingsEditor.Display(editor.ReadValues[name]);

  /// <summary>LPS modems on their own routes, answering the window's requests in order.</summary>
  internal sealed class LpsFieldBench(params Nv5FakeModem[] modems) {
    private readonly Queue<(Nv5ConfigurationFrame Frame, Target Target)> _frames = new();
    private readonly Queue<Action> _later = new();
    private uint _now = 1000;

    internal Nv5FakeModem[] Modems { get; } = modems;
    internal NvModemLpsFieldViewModel? Field { get; private set; }

    /// <summary>Restarts each modem once its save is durable, as the firmware does.</summary>
    internal bool Restarts { get; init; }

    /// <summary>Requests to these modems wait in <see cref="Held"/>.</summary>
    internal HashSet<Nv5FakeModem> Holding { get; } = [];

    internal List<Action> Held { get; } = [];
    internal int Begins => Modems.Sum(modem => modem.Begins);

    internal NvModemLpsFieldViewModel Create() {
      foreach (Nv5FakeModem modem in Modems) {
        modem.CommitModel = modem.CommitModel || Restarts;
      }
      var store = new Nv5SettingsSnapshots();
      Field = new NvModemLpsFieldViewModel(
          () => new Nv5NativeSettingsEditor(() => _now, () => 255, 190) { Snapshots = store });
      Field.SendFrame += (frame, target) => _frames.Enqueue((frame, target));
      for (int i = 0; i < Modems.Length; i++) {
        Field.Offer(Modems[i].Peer, 0, $"192.168.144.{21 + i}");
        Nv5FakeModem modem = Modems[i];
        int index = i;
        Field.Modems[i].Editor.DurableSaved += () => {
          if (Restarts) {
            _later.Enqueue(() => {
              modem.Reboot();
              modem.Peer = modem.Peer with { TransportEpoch = modem.Peer.TransportEpoch + 1 };
              Field.Offer(modem.Peer, 0, $"192.168.144.{21 + index}");
            });
          }
        };
      }
      return Field;
    }

    /// <summary>Opens the window and waits until every LPS modem is read and in the
    /// form.</summary>
    internal NvModemLpsFieldViewModel Open() {
      NvModemLpsFieldViewModel field = Create();
      Assert.True(Pump(() => field.Modems.All(modem => modem.Editor.Ready
          && modem.Editor.ValuesReadBack && (modem.Editable || !modem.Editor.LpsSupported))),
          field.Status);
      return field;
    }

    internal void Release() {
      Holding.Clear();
      foreach (Action answer in Held.ToList()) {
        answer();
      }
      Held.Clear();
    }

    /// <summary>Answers the frames in the order they were sent; between frames the clock
    /// advances.</summary>
    internal bool Pump(Func<bool> done, int steps = 50000) {
      for (int step = 0; step < steps && !done(); step++) {
        if (_later.TryDequeue(out Action? action)) {
          action();
        } else if (_frames.TryDequeue(out (Nv5ConfigurationFrame Frame, Target Target) sent)) {
          Nv5FakeModem modem = Modems.Single(candidate =>
              candidate.Peer.Uid.SequenceEqual(sent.Target.Uid));
          void Answer() {
            foreach (MAVLink.MAVLinkMessage reply in modem.Answer(sent.Frame)) {
              Field!.Receive(reply, modem.Peer.Route);
            }
          }
          if (Holding.Contains(modem)) {
            Held.Add(Answer);
          } else {
            Answer();
          }
        } else {
          _now += 20;
          Field!.Service();
        }
      }
      return done();
    }
  }
}
