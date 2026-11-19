using System.Buffers.Binary;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

// Distance pair setup between two NV5 modems. Modem 1 transmits data on radio 1 and answers
// ranging there; modem 2 receives and measures. Radio 2 runs the other way round and is ranging.
public class NvModemDistancePairTests {
  [Fact]
  public void Says_when_a_modem_uses_the_radio_for_LPS() {
    var bench = new PairBench();
    bench.Modems[0].Settings.Add(new Nv5FakeModem.Setting("LPS_ROLE", 1));
    bench.Modems[0].Settings.Add(new Nv5FakeModem.Setting("LPS_RADIO", 1));
    NvModemDistancePairViewModel pair = bench.Open();

    Assert.True(pair.LpsHintVisible);
    Assert.StartsWith("Modem 1 uses Radio 1 for LPS,", pair.LpsHint, StringComparison.Ordinal);
    Assert.Contains("exchanges, span and power", pair.LpsHint, StringComparison.Ordinal);
    // Radio 2 carries no LPS, so the line goes away there.
    pair.SelectedChannelIndex = 1;
    Assert.False(pair.LpsHintVisible);
  }

  [Fact]
  public void The_form_offers_only_values_the_modem_accepts() {
    NvModemDistancePairViewModel pair = new PairBench().Open();

    Assert.Equal(23, pair.Periods.Count);
    for (int i = 0; i < pair.Periods.Count; i++) {
      Assert.Equal(2u << i, pair.Periods[i].Packets);
    }
    Assert.Equal(1024u, pair.SelectedPeriod!.Packets);
    Assert.Equal(29, NvModemDistancePairViewModel.MaximumExchanges);
    Assert.Equal("0x32101222", pair.AddressText);
    Assert.False(pair.SpanEnabled);
    Assert.True(pair.SpanHintVisible);
    Assert.Contains("313 ms", pair.PauseText, StringComparison.Ordinal);
    pair.Exchanges = 29;
    Assert.True(pair.SpanEnabled);
    Assert.False(pair.SpanHintVisible);
    Assert.Contains("1.434 s", pair.PauseText, StringComparison.Ordinal);
  }

  [Fact]
  public void The_shared_margin_changes_the_complete_pause_preview() {
    NvModemDistancePairViewModel pair = new PairBench().Open();
    Assert.True(pair.MarginEnabled);
    pair.MarginMs = 20;
    Assert.Contains("333 ms", pair.PauseText, StringComparison.Ordinal);
    pair.MarginMs = 40;
    Assert.Contains("353 ms", pair.PauseText, StringComparison.Ordinal);
    pair.Exchanges = 29;
    Assert.Contains("1.474 s", pair.PauseText, StringComparison.Ordinal);
    pair.MarginMs = 20;
    Assert.Contains("1.454 s", pair.PauseText, StringComparison.Ordinal);
  }

  [Fact]
  public void Missing_margin_support_refuses_before_either_transaction_begins() {
    var bench = new PairBench();
    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.Settings.RemoveAll(setting => setting.Name == "CH1_RNG_PAD_MS");
    }
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.False(pair.MarginEnabled);
    pair.Apply();
    Assert.Equal(0, bench.Begins);
    Assert.Contains("Update both modems", pair.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void A_stable_readable_pair_without_margin_support_can_still_switch_off() {
    var bench = new PairBench();
    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.Set("CH1_RNG_EN", 1);
      modem.Settings.RemoveAll(setting => setting.Name == "CH1_RNG_PAD_MS");
    }
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.True(pair.RangingEnabled);
    pair.RangingEnabled = false;
    Assert.Contains("without timing-margin support", pair.SwitchHint, StringComparison.Ordinal);
    Assert.DoesNotContain("restarted", pair.SwitchHint, StringComparison.Ordinal);

    pair.Apply();
    Assert.True(bench.Pump(() => bench.Modems[0].Commits == 1 && bench.Modems[1].Commits == 1));
    foreach (Nv5FakeModem modem in bench.Modems) {
      Assert.Equal(["CH1_PEER_HASH", "CH1_RNG_EN"], modem.Staged.Keys.Order(StringComparer.Ordinal));
      Assert.All(modem.Staged.Values, value => Assert.Equal(0u, value));
    }
  }

  [Fact]
  public void A_margin_only_difference_is_classified_as_a_ranging_setting() {
    var bench = new PairBench();
    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.Set("CH1_RNG_EN", 1);
    }
    bench.Modems[1].Set("CH1_RNG_PAD_MS", 20);
    bench.Modems[1].Set("CH1_LINK_HASH", 501);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Contains("distance settings differ", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.DoesNotContain("different firmware", pair.ComparisonSummary, StringComparison.Ordinal);
  }

  [Fact]
  public void A_modem_value_outside_the_list_is_shown_rather_than_replaced() {
    var bench = new PairBench();
    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.Set("CH1_RNG_PER", 1000);
      modem.Set("CH1_RNG_PHASE", 8);
    }
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Equal(1000u, pair.SelectedPeriod!.Packets);
    Assert.Equal(24, pair.Periods.Count);
    Assert.Equal("1000 (not a power of two)", pair.SelectedPeriod.Label);
    // A stored offset opens the advanced section instead of staying out of sight.
    Assert.True(pair.AdvancedShown);
    pair.Apply();
    Assert.Equal(0, bench.Begins);
    Assert.Contains("power of two", pair.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void The_form_is_staged_in_native_units_on_both_modems() {
    var bench = new PairBench();
    bench.Modems[1].CandidateHash = 701; // The differing candidates end the flow after staging.
    NvModemDistancePairViewModel pair = bench.Open();
    pair.RangingEnabled = true;
    pair.SelectedPeriod = pair.Periods.Single(period => period.Packets == 4096);
    pair.PhasePackets = 16;
    pair.Exchanges = 3;
    pair.MarginMs = 20;
    pair.SpanKhz = 1500.5m;
    pair.PowerDbm = 5;
    pair.AddressText = "abcd0001";
    pair.Apply();
    Assert.True(pair.Active);
    Assert.False(pair.PeerEnabled);
    Assert.False(pair.FormEnabled);
    Assert.True(bench.Pump(() => !pair.Active), pair.Status);

    foreach (Nv5FakeModem modem in bench.Modems) {
      Assert.Equal(1u, modem.Staged["CH1_RNG_EN"]);
      Assert.Equal(4096u, modem.Staged["CH1_RNG_PER"]);
      Assert.Equal(16u, modem.Staged["CH1_RNG_PHASE"]);
      Assert.Equal(3u, modem.Staged["CH1_RNG_N"]);
      Assert.Equal(20u, modem.Staged["CH1_RNG_PAD_MS"]);
      Assert.Equal(1500500u, modem.Staged["CH1_RNG_SPAN"]);
      Assert.Equal(5u, modem.Staged["CH1_RNG_PWR"]);
      Assert.Equal(0xabcd0001u, modem.Staged["CH1_RNG_ADDR"]);
    }
    Assert.Contains("Candidate link hashes differ", pair.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void An_unusable_address_is_refused_without_touching_either_modem_or_staged_edits() {
    var bench = new PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.True(pair.Modem1Editor.SetValue("CH1_FREQ_HZ", "868100000"));
    foreach (string text in (string[])["0", "", "123456789", "0xZZ"]) {
      pair.AddressText = text;
      pair.Apply();
      Assert.False(pair.Active);
      Assert.Contains("ranging address", pair.Status, StringComparison.Ordinal);
    }
    Assert.False(bench.Pump(() => bench.Begins != 0, steps: 200));
    Assert.True(pair.Modem1Editor.HasPendingChanges());
  }

  [Fact]
  public void The_comparison_marks_shared_differences_and_never_shows_secrets() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_FREQ_HZ", 868500000);
    bench.Modems[1].Set("CH1_PWR_DBM", 14);
    bench.Modems[1].Set("CH1_KEY_W0", 222);
    bench.Modems[1].Set("CH1_LINK_HASH", 501);
    bench.Modems[1].Set("CH1_RNG_PER", 512);
    NvModemDistancePairViewModel pair = bench.Open();

    Assert.Equal("CH1_ROLE", pair.ComparisonRows[0].Name);
    Assert.Equal("Modem 2 measures", pair.ComparisonRows[0].Note);
    NvPairComparisonRow frequency = bench.Row("CH1_FREQ_HZ")!;
    Assert.Equal("868000000", frequency.First);
    Assert.Equal("868500000", frequency.Second);
    Assert.Equal("Differs", frequency.Note);
    Assert.True(frequency.Marked);
    NvPairComparisonRow power = bench.Row("CH1_PWR_DBM")!;
    Assert.Equal("May differ", power.Note);
    Assert.False(power.Marked);
    NvPairComparisonRow key = bench.Row("CH1_KEY_W0")!;
    Assert.Equal("hidden", key.First);
    Assert.Equal("hidden", key.Second);
    Assert.True(key.Marked);
    Assert.True(bench.Row("CH1_LINK_HASH")!.Marked);
    NvPairComparisonRow written = bench.Row("CH1_RNG_PER")!;
    Assert.Equal("Set by pair setup", written.Note);
    Assert.False(written.Marked);
    // Equal settings and the other radio stay out of the differences view.
    Assert.Null(bench.Row("CH1_RNG_N"));
    Assert.Null(bench.Row("CH2_FREQ_HZ"));

    Assert.Contains("fingerprints differ", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.Contains("settings: 2", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.True(pair.PeerDifferencesVisible);
    Assert.Contains("period 512", pair.PeerDifferences, StringComparison.Ordinal);

    pair.DifferencesOnly = false;
    Assert.NotNull(bench.Row("CH1_RNG_N"));
    Assert.Null(bench.Row("CH2_FREQ_HZ"));
    Assert.Equal("hidden", bench.Row("CH1_KEY_W0")!.First);
  }

  [Fact]
  public void The_comparison_confirms_a_matched_pair() {
    NvModemDistancePairViewModel pair = new PairBench().Open();
    Assert.Contains("link settings match", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.False(pair.PeerDifferencesVisible);
    // Only the opposite roles remain in the differences view.
    Assert.Single(pair.ComparisonRows);
  }

  [Fact]
  public void The_comparison_explains_when_only_distance_settings_differ() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_RNG_EN", 1);
    bench.Modems[1].Set("CH1_LINK_HASH", 501);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Contains("only the distance settings differ", pair.ComparisonSummary,
        StringComparison.Ordinal);
  }

  [Fact]
  public void The_comparison_flags_radios_without_opposite_roles() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_ROLE", 1);
    NvModemDistancePairViewModel pair = bench.Open();
    NvPairComparisonRow role = bench.Row("CH1_ROLE")!;
    Assert.Equal("Not opposite", role.Note);
    Assert.Equal(NvPairComparisonKind.Problem, role.Kind);
    Assert.Contains("opposite roles", pair.ComparisonSummary, StringComparison.Ordinal);
  }

  [Fact]
  public void The_measured_distance_of_the_selected_radio_is_shown_in_the_window() {
    var bench = new PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    bench.SendDistance(1, 1, 1234);
    bench.SendDistance(0, 2, 5678);
    Assert.True(bench.Pump(() => pair.DistanceRows[1].Reading.Contains("12.34",
        StringComparison.Ordinal)), pair.DistanceRows[1].Reading);
    Assert.Equal("Distance on Radio 1", pair.DistanceTitle);
    // Radio 1 is off at the first modem: its row says so instead of showing a reading.
    Assert.Equal("Ranging off", pair.DistanceRows[0].Mode);
    Assert.Equal("", pair.DistanceRows[0].Reading);

    pair.SelectedChannelIndex = 1;
    Assert.Equal("Distance on Radio 2", pair.DistanceTitle);
    Assert.Contains("56.78", pair.DistanceRows[0].Reading, StringComparison.Ordinal);
    Assert.Contains("No distance telemetry", pair.DistanceRows[1].Reading,
        StringComparison.Ordinal);
  }

  [Fact]
  public void The_distance_rows_are_those_of_the_modem_settings() {
    var bench = new PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    bench.SendDistance(1, 1, 1234);
    Assert.True(bench.Pump(() => pair.DistanceRows[1].Reading.StartsWith("12.34 m · VALID",
        StringComparison.Ordinal)), pair.DistanceRows[1].Reading);
    Assert.Equal("Modem 2", pair.DistanceRows[1].Name);
    Assert.Equal("Ranging on · measures", pair.DistanceRows[1].Mode);
    Assert.Equal("last burst 1 of 1 valid", pair.DistanceRows[1].Burst);
  }

  [Fact]
  public void The_other_radio_reloads_the_form_the_comparison_and_the_staging() {
    var bench = new PairBench();
    bench.Modems[1].CandidateHash = 701;
    NvModemDistancePairViewModel pair = bench.Open();
    pair.SelectedChannelIndex = 1;
    Assert.True(pair.RangingEnabled);
    Assert.Equal(2048u, pair.SelectedPeriod!.Packets);
    Assert.Equal(5, pair.Exchanges);
    Assert.Equal(2500m, pair.SpanKhz);
    Assert.Equal(3, pair.PowerDbm);
    Assert.Equal("0x00C0FFEE", pair.AddressText);
    Assert.Equal("CH2_ROLE", pair.ComparisonRows[0].Name);
    Assert.Equal("Modem 1 measures", pair.ComparisonRows[0].Note);
    Assert.Null(bench.Row("CH1_ROLE"));

    pair.Exchanges = 7;
    pair.Apply();
    Assert.True(bench.Pump(() => !pair.Active), pair.Status);
    foreach (Nv5FakeModem modem in bench.Modems) {
      Assert.Equal(7u, modem.Staged["CH2_RNG_N"]);
      Assert.All(modem.Staged.Keys, name => Assert.StartsWith("CH2_", name,
          StringComparison.Ordinal));
    }
  }

  [Fact]
  public void A_phase_beyond_a_shorter_period_is_flagged_not_rewritten() {
    var bench = new PairBench();
    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.Set("CH1_RNG_PHASE", 512);
    }
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Equal(512, pair.PhasePackets);
    pair.SelectedPeriod = pair.Periods.Single(period => period.Packets == 256);
    Assert.Equal(512, pair.PhasePackets);
    Assert.True(pair.AdvancedShown);
    Assert.True(pair.PhaseProblem);
    Assert.Contains("256", pair.PhaseHint, StringComparison.Ordinal);
    pair.Apply();
    Assert.False(pair.Active);
    Assert.Equal(0, bench.Begins);
    Assert.Contains("phase must be below the period", pair.Status, StringComparison.Ordinal);

    pair.SelectedPeriod = pair.Periods.Single(period => period.Packets == 1024);
    Assert.Equal(512, pair.PhasePackets);
    Assert.False(pair.PhaseProblem);
    Assert.DoesNotContain("Must", pair.PhaseHint, StringComparison.Ordinal);
  }

  [Fact]
  public void A_failed_check_puts_back_only_what_the_window_staged() {
    var bench = new PairBench();
    bench.Modems[1].CandidateHash = 701;
    bench.Modems[1].Set("CH1_RNG_PER", 512);
    NvModemDistancePairViewModel pair = bench.Open();
    Nv5NativeSettingsEditor first = pair.Modem1Editor;
    Nv5NativeSettingsEditor second = pair.Modem2Editor;
    Assert.True(first.SetValue("CH2_FREQ_HZ", "433900000"));
    pair.RangingEnabled = true;
    pair.Exchanges = 9;
    pair.Apply();
    Assert.True(pair.Active);
    Assert.True(bench.Pump(() => !pair.Active));
    Assert.Equal(1024u, bench.Modems[1].Staged["CH1_RNG_PER"]);

    // Neither modem was changed, so the tables return to what the modems hold.
    Assert.True(bench.Pump(() => PairBench.Candidate(first, "CH1_RNG_N") == 1
        && PairBench.Candidate(second, "CH1_RNG_N") == 1));
    Assert.Equal(0u, PairBench.Candidate(first, "CH1_RNG_EN"));
    Assert.Equal(0u, PairBench.Candidate(second, "CH1_RNG_EN"));
    Assert.Equal(512u, PairBench.Candidate(second, "CH1_RNG_PER"));
    Assert.Equal(0u, PairBench.Candidate(second, "CH1_PEER_HASH"));
    Assert.Equal(433900000u, PairBench.Candidate(first, "CH2_FREQ_HZ"));
    Assert.False(second.HasPendingChanges());
    // The form keeps the operator's values for the next attempt.
    Assert.Equal(9, pair.Exchanges);
    Assert.True(pair.PeerDifferencesVisible);
    Assert.Contains("period 512", pair.PeerDifferences, StringComparison.Ordinal);
    // The check failed before either commit, so it can be tried again at once.
    Assert.True(bench.Pump(() => pair.CanApply));
  }

  [Fact]
  public void Edits_to_other_settings_keep_the_verdict_and_are_announced() {
    var bench = new PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.True(pair.Modem1Editor.SetValue("CH2_FREQ_HZ", "433900000"));
    Assert.True(bench.Pump(() => pair.ComparisonSummary.Contains("Other unsaved",
        StringComparison.Ordinal)), pair.ComparisonSummary);
    Assert.Contains("link settings match", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.True(pair.Modem1Editor.SetValue("CH1_FREQ_HZ", "868100000"));
    Assert.True(bench.Pump(() => pair.ComparisonSummary.Contains(
        "unsaved edits are included below", StringComparison.Ordinal)), pair.ComparisonSummary);
    Assert.Equal("868100000", bench.Row("CH1_FREQ_HZ")!.First);
  }

  [Fact]
  public void Unused_differences_are_not_blamed_when_the_fingerprints_match() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_FREQ_HZ", 868500000);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Contains("fingerprints match", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.Contains("unused", pair.ComparisonSummary, StringComparison.Ordinal);
  }

  [Fact]
  public void An_unexplained_fingerprint_difference_suggests_firmware() {
    // Neither the ranging power nor the peer proof is part of the link fingerprint.
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_LINK_HASH", 501);
    bench.Modems[1].Set("CH1_PEER_HASH", 7);
    bench.Modems[1].Set("CH1_RNG_PWR", 5);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Contains("firmware versions", pair.ComparisonSummary, StringComparison.Ordinal);
  }

  [Fact]
  public void Shared_differences_are_not_blamed_while_distance_settings_differ() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_RNG_EN", 1);
    bench.Modems[1].Set("CH1_FREQ_HZ", 868500000);
    bench.Modems[1].Set("CH1_LINK_HASH", 501);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Contains("which saving aligns", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.DoesNotContain("Match the highlighted", pair.ComparisonSummary,
        StringComparison.Ordinal);
  }

  [Fact]
  public void The_second_modem_is_chosen_in_the_window_and_switching_rereads_it() {
    var bench = new PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    bench.OfferAll();
    Assert.Equal("10:10 · 192.168.0.10", pair.FixedName);
    Assert.Equal(["10:11 · 192.168.0.11", "10:12 · 192.168.0.12"],
        pair.Peers.Select(peer => peer.Label));
    Assert.Same(pair.Peers[0], pair.SelectedPeer);
    bench.OfferAll();
    Assert.Equal(2, pair.Peers.Count);

    // What the operator typed belongs to the form, not to the modem being replaced.
    pair.Exchanges = 4;
    pair.SelectedPeer = pair.Peers[1];
    Assert.False(pair.CanApply);
    Assert.True(bench.Pump(() => pair.CanApply), pair.Status);
    Assert.Equal(4, pair.Exchanges);
    Assert.Equal("Modem 2 · 10:12", pair.Modem2TabName);
    NvPairComparisonRow frequency = bench.Row("CH1_FREQ_HZ")!;
    Assert.Equal("868000000", frequency.First);
    Assert.Equal("915000000", frequency.Second);
    Assert.Contains("fingerprints differ", pair.ComparisonSummary, StringComparison.Ordinal);

    bench.Third.CandidateHash = 701;
    pair.RangingEnabled = true;
    pair.Apply();
    Assert.True(bench.Pump(() => !pair.Active), pair.Status);
    Assert.Equal(1, bench.Modems[0].Begins);
    Assert.Equal(1, bench.Third.Begins);
    Assert.Equal(4u, bench.Third.Staged["CH1_RNG_N"]);
    // The modem that left the pair is not written.
    Assert.Equal(0, bench.Modems[1].Begins);
    Assert.Empty(bench.Modems[1].Staged);
  }

  [Fact]
  public void A_peer_chosen_during_a_running_read_is_applied_when_that_read_ends() {
    var bench = new PairBench { HoldSecond = true };
    NvModemDistancePairViewModel pair = bench.Create();
    pair.Offer(bench.Third.Peer, 0, "");
    Assert.True(bench.Pump(() => bench.Held.Count != 0));
    Nv5NativeSettingsEditor second = pair.Modem2Editor;
    Assert.True(second.Busy);
    pair.SelectedPeer = pair.Peers.Single(peer => peer.Label == "10:12");
    // The read of the modem being replaced is neither cut off nor reported as a failure.
    bench.Pump(() => false, steps: 15);
    Assert.Equal(bench.Modems[1].Peer.Uid, second.Target!.Uid);
    Assert.False(second.Failed);
    Assert.False(pair.CanApply);

    bench.Release();
    Assert.True(bench.Pump(() => pair.CanApply), pair.Status);
    Assert.Equal(bench.Third.Peer.Uid, second.Target!.Uid);
    Assert.False(second.Failed);
    Assert.Equal("915000000", bench.Row("CH1_FREQ_HZ")!.Second);
  }

  [Fact]
  public void A_busy_second_modem_is_named_and_asked_again_instead_of_read_forever() {
    var bench = new PairBench();
    bench.Third.RefuseReadBegins = int.MaxValue;
    NvModemDistancePairViewModel pair = bench.Open();
    bench.OfferAll();
    pair.SelectedPeer = pair.Peers[1];
    Assert.True(bench.Pump(() => pair.ComparisonSummary.Contains("Another configuration session",
        StringComparison.Ordinal)), pair.ComparisonSummary);
    Assert.StartsWith("Modem 2 · 10:12", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.False(pair.CanApply);
    Assert.Empty(pair.ComparisonRows);
    // Asked again on its own, rather than left for a manual refresh.
    int asked = bench.Third.ReadBegins;
    Assert.True(bench.Pump(() => bench.Third.ReadBegins > asked, steps: 400));

    // Waiting to ask again holds nothing that another choice would lose.
    pair.SelectedPeer = pair.Peers[0];
    Assert.True(bench.Pump(() => pair.CanApply), pair.Status);
    Assert.Contains("link settings match", pair.ComparisonSummary, StringComparison.Ordinal);
  }

  [Fact]
  public void A_modem_that_just_restarted_is_switched_off_on_its_own() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_RNG_EN", 1);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Equal("Switch ranging off on both modems", pair.ApplyText);
    Assert.StartsWith("Ranging is on at Modem 2 only", pair.SwitchHint, StringComparison.Ordinal);

    // Modem 2 restarts with the same settings and is read again at once. A pair save could be cut
    // short by its next restart, so switching off goes modem by modem.
    bench.Modems[1].BootNonce++;
    Target restarted = bench.Modems[1].Peer with {
      TransportEpoch = bench.Modems[1].Peer.TransportEpoch + 1,
    };
    pair.Observe(restarted, 0);
    Assert.True(bench.Pump(() => pair.Modem2Editor.Ready && pair.CanApply), pair.Status);
    Assert.StartsWith("Modem 2 · 10:11 restarted", pair.SwitchHint, StringComparison.Ordinal);
    pair.Apply();
    Assert.True(bench.Pump(() => bench.Modems[1].Commits == 1), pair.Status);
    Assert.Equal(2, bench.Modems[1].Staged.Count);
    Assert.Equal(0u, bench.Modems[1].Staged["CH1_RNG_EN"]);
    Assert.Equal(0, bench.Modems[0].Begins);
  }

  [Fact]
  public void Ranging_is_switched_off_on_a_modem_that_cannot_be_read() {
    // Modem 2 is never read, as one that restarts too often to be read would be. Its telemetry
    // still says that it ranges; modem 1 was read with ranging off.
    var bench = new PairBench();
    bench.Modems[1].RefuseReadBegins = int.MaxValue;
    NvModemDistancePairViewModel pair = bench.Create();
    Assert.True(bench.Pump(() => pair.Modem1Editor.Ready));
    Assert.False(pair.CanApply);
    bench.SendDistance(1, 1, 150);
    Assert.True(bench.Pump(() => pair.CanApply), pair.Status);
    Assert.Equal("Switch ranging off on both modems", pair.ApplyText);
    Assert.StartsWith("Modem 2 · 10:11 is not read", pair.SwitchHint, StringComparison.Ordinal);
    // Switching on still needs both modems read, and the line says so.
    pair.RangingEnabled = true;
    Assert.False(pair.CanApply);
    Assert.Contains("once both modems are read", pair.SwitchHint, StringComparison.Ordinal);
    pair.RangingEnabled = false;
    Assert.True(pair.CanApply);

    int asked = bench.Modems[1].ReadBegins;
    pair.Apply();
    Assert.True(pair.Active);
    Assert.True(bench.Pump(() => bench.Modems[1].Commits == 1), pair.Status);
    // Only the switch is saved, with the peer proof of ranging on cleared, without a read or a
    // peer check; the modem known to be off is left alone.
    Assert.Equal(2, bench.Modems[1].Staged.Count);
    Assert.Equal(0u, bench.Modems[1].Staged["CH1_RNG_EN"]);
    Assert.Equal(0u, bench.Modems[1].Staged["CH1_PEER_HASH"]);
    Assert.Equal(1, bench.Modems[1].Begins);
    Assert.True(bench.Modems[1].ReadBegins <= asked + 1);
    Assert.Equal(0, bench.Modems[0].Begins);
    Assert.Equal(0, bench.Modems[0].Commits);
    Assert.Contains("switching ranging off", pair.ComparisonSummary, StringComparison.Ordinal);
    Assert.StartsWith("Switching ranging off on Modem 2 · 10:11", pair.Status,
        StringComparison.Ordinal);
  }

  [Fact]
  public void A_quick_switch_off_does_not_skip_an_enabled_modem_with_disabled_telemetry() {
    var bench = new PairBench();
    bench.Modems[0].Set("CH1_RNG_EN", 1);
    bench.Modems[1].Set("CH1_RNG_EN", 1);
    bench.Modems[1].RefuseReadBegins = int.MaxValue;
    NvModemDistancePairViewModel pair = bench.Create();
    bench.SendDistance(0, 1, 0, Nv5DistanceStatuses.Disabled);
    bench.SendDistance(1, 1, 150);
    Assert.True(bench.Pump(() => pair.Modem1Editor.Ready && pair.CanApply), pair.Status);
    Assert.Equal(1u, PairBench.Candidate(pair.Modem1Editor, "CH1_RNG_EN"));
    pair.Apply();
    Assert.True(bench.Pump(() => bench.Modems[1].Commits == 1), pair.Status);
    Assert.True(bench.Pump(() => bench.Modems[0].Commits == 1),
        "The switch-off skipped a modem whose committed settings still enable ranging");
  }

  [Fact]
  public void The_measuring_modem_is_chosen_and_named_by_its_data_role() {
    var bench = new PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.True(pair.ManagerEnabled);
    Assert.Equal(0, pair.SelectedManagerIndex);
    Assert.Equal(["Modem 2 · receiver", "Modem 1 · transmitter"],
        pair.ManagerChoices.Select(choice => choice.Label));
    Assert.Equal("Modem 1 answers the requests.", pair.RolesText);
    // The readout is about what the modems do now, and on this radio neither is ranging.
    Assert.Equal("Ranging off", pair.DistanceRows[0].Mode);
    Assert.Equal("Ranging off", pair.DistanceRows[1].Mode);
    pair.SelectedManagerIndex = 1;
    Assert.Equal("Modem 2 answers the requests.", pair.RolesText);

    // The second radio runs the other way round, and is ranging.
    pair.SelectedChannelIndex = 1;
    Assert.Equal(["Modem 1 · receiver", "Modem 2 · transmitter"],
        pair.ManagerChoices.Select(choice => choice.Label));
    Assert.Equal(0, pair.SelectedManagerIndex);
    Assert.Equal("Modem 2 answers the requests.", pair.RolesText);
    Assert.Equal("Ranging on · measures", pair.DistanceRows[0].Mode);
    Assert.Equal("Ranging on · answers", pair.DistanceRows[1].Mode);
    // Choosing the other end changes what will be saved, not what the modems do now.
    pair.SelectedManagerIndex = 1;
    Assert.Equal("Ranging on · measures", pair.DistanceRows[0].Mode);
    Assert.Equal("Ranging on · answers", pair.DistanceRows[1].Mode);
  }

  [Fact]
  public void The_chosen_measuring_end_is_staged_on_both_modems() {
    var bench = new PairBench();
    bench.Modems[1].CandidateHash = 701;
    NvModemDistancePairViewModel pair = bench.Open();
    pair.RangingEnabled = true;
    pair.SelectedManagerIndex = 1;
    pair.Apply();
    Assert.True(pair.Active);
    Assert.False(pair.ManagerEnabled);
    Assert.True(bench.Pump(() => !pair.Active));
    foreach (Nv5FakeModem modem in bench.Modems) {
      Assert.Equal(1u, modem.Staged["CH1_RNG_EN"]);
      Assert.Equal(1u, modem.Staged["CH1_RNG_MGR"]);
    }
  }

  [Fact]
  public void Modems_that_already_measure_on_the_transmitter_are_shown_that_way() {
    var bench = new PairBench();
    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.Set("CH1_RNG_EN", 1);
      modem.Set("CH1_RNG_MGR", 1);
    }
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Equal(1, pair.SelectedManagerIndex);
    Assert.Equal("Modem 2 answers the requests.", pair.RolesText);
    Assert.Equal("Ranging on · measures", pair.DistanceRows[0].Mode);
    Assert.Equal("Ranging on · answers", pair.DistanceRows[1].Mode);
    Assert.Equal("Modem 1 measures", bench.Row("CH1_ROLE")!.Note);
    pair.DifferencesOnly = false;
    Assert.Equal("1 · TX measures", bench.Row("CH1_RNG_MGR")!.First);
  }

  [Fact]
  public void The_measuring_end_of_a_running_pair_changes_only_with_ranging_switched_off() {
    var bench = new PairBench();
    bench.Modems[1].CandidateHash = 701;
    NvModemDistancePairViewModel pair = bench.Open();
    // Radio 2 is ranging with its receiver measuring.
    pair.SelectedChannelIndex = 1;
    Assert.True(pair.RangingEnabled);
    pair.SelectedManagerIndex = 1;
    Assert.Contains("Switch it off and save", pair.RolesText, StringComparison.Ordinal);
    Assert.True(pair.RolesProblem);
    pair.Apply();
    Assert.False(pair.Active);
    Assert.Equal(0, bench.Begins);
    Assert.Contains("Switch it off and save", pair.Status, StringComparison.Ordinal);

    pair.RangingEnabled = false;
    Assert.Equal("Modem 1 answers the requests.", pair.RolesText);
    pair.Apply();
    Assert.True(pair.Active);
    Assert.True(bench.Pump(() => !pair.Active));
    foreach (Nv5FakeModem modem in bench.Modems) {
      Assert.Equal(0u, modem.Staged["CH2_RNG_EN"]);
      Assert.Equal(1u, modem.Staged["CH2_RNG_MGR"]);
    }
  }

  [Fact]
  public void Firmware_without_the_choice_keeps_measuring_on_the_receiver() {
    var bench = new PairBench();
    bench.Modems[1].CandidateHash = 701;
    for (int i = 0; i < 2; i++) {
      bench.Modems[i].Settings = Nv5FakeModem.Catalogue(i == 0, choosesMeasuringEnd: false);
    }
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.False(pair.ManagerEnabled);
    Assert.Equal(0, pair.SelectedManagerIndex);
    Assert.Equal("Modem 2 · receiver", pair.ManagerChoices[0].Label);
    Assert.Contains("always measures on the modem that receives data", pair.RolesText,
        StringComparison.Ordinal);
    // Radio 2 is ranging: its receiver, Modem 1, is the end that measures.
    pair.SelectedChannelIndex = 1;
    Assert.Equal("Ranging on · measures", pair.DistanceRows[0].Mode);
    Assert.Equal("Ranging on · answers", pair.DistanceRows[1].Mode);
    pair.SelectedChannelIndex = 0;
    pair.RangingEnabled = true;
    pair.Apply();
    Assert.True(pair.Active);
    Assert.True(bench.Pump(() => !pair.Active));
    foreach (Nv5FakeModem modem in bench.Modems) {
      Assert.Equal(1u, modem.Staged["CH1_RNG_EN"]);
      Assert.False(modem.Staged.ContainsKey("CH1_RNG_MGR"));
    }
  }

  [Fact]
  public void Mixed_firmware_is_named_and_not_saved() {
    var bench = new PairBench();
    bench.Modems[1].Settings = Nv5FakeModem.Catalogue(false, choosesMeasuringEnd: false);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.False(pair.ManagerEnabled);
    Assert.Contains("different firmware", pair.RolesText, StringComparison.Ordinal);
    pair.Apply();
    Assert.False(pair.Active);
    Assert.Equal(0, bench.Begins);
    Assert.Contains("different firmware", pair.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void Missing_opposite_roles_are_named_beside_the_radio() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_ROLE", 1);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.Contains("do not have opposite data roles", pair.RolesText, StringComparison.Ordinal);
    pair.Apply();
    Assert.Equal(0, bench.Begins);
    Assert.Contains("opposite roles", pair.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void Switching_on_and_off_is_spelled_out_beside_the_switch_and_on_the_button() {
    NvModemDistancePairViewModel pair = new PairBench().Open();
    // Radio 1 is not ranging.
    Assert.False(pair.RangingEnabled);
    Assert.True(pair.SwitchHintVisible);
    Assert.Equal("Ranging is off. To switch it on, tick this and save.", pair.SwitchHint);
    Assert.Equal("Verify and save both modems", pair.ApplyText);
    pair.RangingEnabled = true;
    Assert.Equal("Saving switches ranging on at both modems.", pair.SwitchHint);
    Assert.Equal("Verify and switch ranging on at both modems", pair.ApplyText);
    // Radio 2 is.
    pair.SelectedChannelIndex = 1;
    Assert.True(pair.RangingEnabled);
    Assert.Equal("Ranging is on. To switch it off on both modems, untick this and save.",
        pair.SwitchHint);
    Assert.Equal("Verify and save both modems", pair.ApplyText);
    pair.RangingEnabled = false;
    Assert.Equal("Saving switches ranging off on both modems.", pair.SwitchHint);
    Assert.Equal("Switch ranging off on both modems", pair.ApplyText);
  }

  [Fact]
  public void Ranging_left_on_at_one_modem_is_named() {
    var bench = new PairBench();
    bench.Modems[1].Set("CH1_RNG_EN", 1);
    NvModemDistancePairViewModel pair = bench.Open();
    Assert.StartsWith("Ranging is on at Modem 2 only.", pair.SwitchHint, StringComparison.Ordinal);
    // The form is filled from the first modem, where it is off: saving switches the other off.
    Assert.Equal("Switch ranging off on both modems", pair.ApplyText);
  }

  [Fact]
  public void A_candidate_refused_after_the_peer_proof_names_the_modem_and_its_reason() {
    var bench = new PairBench();
    bench.Modems[1].AcceptedValidations = 1;
    NvModemDistancePairViewModel pair = bench.Open();
    pair.RangingEnabled = true;
    pair.Apply();
    Assert.True(bench.Pump(() => !pair.Active), pair.Status);
    Assert.StartsWith("A complete candidate was rejected after peer verification.", pair.Status,
        StringComparison.Ordinal);
    Assert.Contains("Modem 2 · 10:11: Device rejected the operation (result 17).", pair.Status,
        StringComparison.Ordinal);
    Assert.Contains("radio 1 = 12345678", pair.Status, StringComparison.Ordinal);
    Assert.Equal(0, bench.Modems[0].Commits + bench.Modems[1].Commits);
  }

  [Fact]
  public void A_matched_pair_saves_the_responder_first_and_reads_both_again() {
    var bench = new PairBench();
    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.CommitModel = true;
    }
    var committed = new List<string>();
    NvModemDistancePairViewModel pair = bench.Open();
    pair.CommitSent += (editor, _) => committed.Add(editor == pair.Modem1Editor ? "1" : "2");
    pair.RangingEnabled = true;
    pair.Apply();
    Assert.True(bench.Pump(() => bench.Modems[0].Commits == 1 && bench.Modems[1].Commits == 1),
        pair.Status);
    // Modem 1 transmits on radio 1 and the receiver measures: modem 1 answers and saves first.
    Assert.Equal(["1", "2"], committed);
    foreach (Nv5FakeModem modem in bench.Modems) {
      Assert.Equal(1u, modem.Staged["CH1_RNG_EN"]);
      // Each modem holds the candidate link hash its peer computed.
      Assert.Equal(700u, modem.Staged["CH1_PEER_HASH"]);
    }
    Assert.False(pair.TryClose());
    Assert.Contains("Wait for both save outcomes", pair.Status, StringComparison.Ordinal);

    foreach (Nv5FakeModem modem in bench.Modems) {
      modem.Reboot();
      pair.Observe(modem.Peer with { TransportEpoch = modem.Peer.TransportEpoch + 1 }, 30);
    }
    Assert.True(bench.Pump(() => pair.Status.StartsWith("Both modems saved, restarted and verified",
        StringComparison.Ordinal)), pair.Status);
    Assert.False(pair.Active);
    // The pair can be changed again once both modems are read again.
    Assert.True(bench.Pump(() => pair.CanApply), pair.SwitchHint);
    Assert.Equal("Ranging is on. To switch it off on both modems, untick this and save.",
        pair.SwitchHint);
    Assert.True(pair.TryClose());
  }

  [Fact]
  public void A_modem_keeps_its_route_while_open_and_moves_once_that_route_closes() {
    var bench = new PairBench();
    foreach (Nv5FakeModem each in bench.Modems) {
      each.CommitModel = true;
    }
    NvModemDistancePairViewModel pair = bench.Open();
    bench.OfferAll();
    Nv5FakeModem modem = bench.Modems[1];
    Target usb = modem.Peer;
    Target lan = usb with { Route = "peer-1-lan" };

    // The same modem is heard on a second route that is open as well: the window keeps the one
    // it uses, without reading the modem again or relabelling it.
    int reads = modem.ReadBegins;
    pair.Offer(lan, 0, "192.168.1.11");
    pair.Observe(lan, 0);
    Assert.Same(usb.Route, pair.Modem2Editor.Target!.Route);
    Assert.Equal("10:11 · 192.168.0.11", pair.SelectedPeer!.Label);
    Assert.False(bench.Pump(() => modem.ReadBegins != reads || !pair.Modem2Editor.Ready,
        steps: 200));

    // Nor is a save on its route cut short, before its commit or while it is recovered.
    pair.RangingEnabled = true;
    pair.Apply();
    pair.Observe(lan, 0);
    Assert.True(bench.Pump(() => pair.Editors.All(editor => editor.AwaitingReconnect
        && editor.StatusText.Contains("Waiting for restart", StringComparison.Ordinal))),
        pair.Status);
    pair.Observe(lan, 0);
    foreach (Nv5FakeModem each in bench.Modems) {
      each.Reboot();
      pair.Observe(each.Peer with { TransportEpoch = each.Peer.TransportEpoch + 1 }, 30);
    }
    pair.Observe(lan, 30);
    Assert.True(bench.Pump(() => !pair.Active), pair.Status);
    Assert.StartsWith("Both modems saved, restarted and verified", pair.Status,
        StringComparison.Ordinal);
    Assert.True(bench.Pump(() => pair.CanApply), pair.SwitchHint);

    // Once its route closes, a passport on the other one moves it there.
    bench.Closed.Add(usb.Route);
    modem.Peer = lan;
    pair.Offer(lan, 40, "192.168.1.11");
    pair.Observe(lan, 40);
    Assert.Same(lan.Route, pair.Modem2Editor.Target!.Route);
    Assert.Equal("10:11 · 192.168.1.11", pair.SelectedPeer!.Label);
    Assert.True(bench.Pump(() => pair.Modem2Editor.Ready && pair.CanApply), pair.Status);
  }

  /// <summary>
  /// With MP_NV5_CONFIG_FIXTURE set to the NV5 firmware's host nv5-configuration-fixture, two
  /// instances of the firmware's configuration core make the pair: the window turns the second
  /// one into the transmitter through its tab, both cores check the candidate link contracts and
  /// the peer proof, and both save, restart and are read back. Ranging stays off: the firmware
  /// ranges only on an LR2021 radio, which the host fixture does not have, and refuses to switch
  /// it on there as a whole-configuration error.
  /// </summary>
  [Fact]
  public void Real_firmware_cores_verify_and_save_a_pair_through_the_window() {
    string? fixture = Environment.GetEnvironmentVariable("MP_NV5_CONFIG_FIXTURE");
    if (string.IsNullOrEmpty(fixture)) {
      return;
    }
    NvModemMavlinkDialect.Register();
    var servers = new Dictionary<string, System.Diagnostics.Process>(StringComparer.Ordinal);
    try {
      Target[] targets = [
        new(Enumerable.Repeat((byte)'A', 18).ToArray(), "fixture-a", 1,
            NvModemCapabilities.ConfigurationTransactions, 10, 11, true),
        new(Enumerable.Repeat((byte)'B', 18).ToArray(), "fixture-b", 1,
            NvModemCapabilities.ConfigurationTransactions, 10, 11, true),
      ];
      foreach (Target target in targets) {
        servers[(string)target.Route] = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(fixture) {
              RedirectStandardInput = true,
              RedirectStandardOutput = true,
              UseShellExecute = false,
            })!;
      }
      uint now = 1000;
      var queue = new Queue<(Nv5ConfigurationFrame Frame, Target Target)>();
      var store = new Nv5SettingsSnapshots();
      var pair = new NvModemDistancePairViewModel(targets[0], targets[1],
          () => new Nv5NativeSettingsEditor(() => now, () => 255, 190) { Snapshots = store });
      pair.SendFrame += (frame, target) => queue.Enqueue((frame, target));
      bool Pump(Func<bool> done) {
        for (int step = 0; step < 40000 && !done(); step++) {
          if (!queue.TryDequeue(out (Nv5ConfigurationFrame Frame, Target Target) sent)) {
            now += 20;
            pair.Service();
            continue;
          }
          System.Diagnostics.Process server = servers[(string)sent.Target.Route];
          byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
              (MAVLink.MAVLINK_MSG_ID)sent.Frame.MessageId, sent.Frame.Payload, false, 255, 190);
          server.StandardInput.Write($"{now} {Convert.ToHexString(bytes).ToLowerInvariant()}\n");
          server.StandardInput.Flush();
          while (server.StandardOutput.ReadLine() is { } line && line.Trim() != ".") {
            pair.Receive(new MAVLink.MavlinkParse().ReadPacket(
                new MemoryStream(Convert.FromHexString(line.Trim())))!, sent.Target.Route);
          }
        }
        return done();
      }

      pair.Open();
      Assert.True(Pump(() => pair.Modem1Editor.Ready && pair.Modem2Editor.Ready && pair.CanApply),
          pair.Status);
      Assert.Contains("do not have opposite data roles", pair.RolesText, StringComparison.Ordinal);
      Assert.True(pair.Modem2Editor.SetValue("CH1_ROLE", "1"));
      Assert.True(Pump(() => pair.RolesText == "Modem 2 answers the requests."), pair.RolesText);
      Assert.False(pair.RangingEnabled);
      Assert.Equal("Verify and save both modems", pair.ApplyText);
      pair.Exchanges = 5;
      pair.Apply();
      Assert.True(pair.Active, pair.Status);
      Assert.True(Pump(() => !pair.Active || pair.Editors.All(editor => editor.AwaitingReconnect
          && editor.StatusText.Contains("Waiting for restart", StringComparison.Ordinal))),
          pair.Status);
      Assert.True(pair.Active, $"{pair.Status} | {pair.Modem1Editor.StatusText} | "
          + pair.Modem2Editor.StatusText);

      for (int i = 0; i < 2; i++) {
        System.Diagnostics.Process server = servers[(string)targets[i].Route];
        server.StandardInput.Write("REBOOT\n");
        server.StandardInput.Flush();
        Assert.Equal(".", server.StandardOutput.ReadLine()?.Trim());
        pair.Observe(targets[i] with { TransportEpoch = 2 }, 30);
      }
      Assert.True(Pump(() => !pair.Active), pair.Status);
      Assert.StartsWith("Both modems saved, restarted and verified", pair.Status,
          StringComparison.Ordinal);
      Assert.True(Pump(() => pair.CanApply), pair.SwitchHint);
      uint[] proofs = new uint[2];
      for (int i = 0; i < 2; i++) {
        IReadOnlyDictionary<string, Nv5ConfigurationClient.Value> held = pair.Editors[i].ReadValues;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(held["CH1_RNG_EN"].Bytes));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(held["CH1_RNG_N"].Bytes));
        Assert.Equal((uint)i, BinaryPrimitives.ReadUInt32LittleEndian(held["CH1_ROLE"].Bytes));
        proofs[i] = BinaryPrimitives.ReadUInt32LittleEndian(held["CH1_PEER_HASH"].Bytes);
        // Each core holds the proof of the other, which is the link fingerprint they share.
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(held["CH1_LINK_HASH"].Bytes),
            proofs[i]);
      }
      Assert.NotEqual(0u, proofs[0]);
      Assert.Equal(proofs[0], proofs[1]);
      Assert.Contains("link settings match", pair.ComparisonSummary, StringComparison.Ordinal);
    } finally {
      foreach (System.Diagnostics.Process server in servers.Values) {
        server.StandardInput.Close();
        Assert.True(server.WaitForExit(10_000));
        server.Dispose();
      }
    }
  }

  [Fact]
  public void The_likely_peer_has_the_opposite_role_on_the_same_carrier() {
    static NvPairLink Link(byte role, uint hertz, byte modulation, uint span = 0) =>
        new(role, hertz, span, modulation);
    static IReadOnlyDictionary<int, NvPairLink> Links(params (int Channel, NvPairLink Link)[] links) =>
        links.ToDictionary(link => link.Channel, link => link.Link);
    // Radio 1 transmits FLRC at 868 MHz, radio 2 receives LoRa at 433.92 MHz.
    IReadOnlyDictionary<int, NvPairLink> own = Links((1, Link(1, 868000000, 1)),
        (2, Link(0, 433920000, 0)));
    Assert.Equal(3, NvModemDistancePairViewModel.LikelyPeer(own, [
        Links((1, Link(1, 868000000, 1))),
        Links((1, Link(0, 915000000, 1))),
        Links((1, Link(0, 868000000, 0))),
        Links((1, Link(0, 868000000, 1))),
    ]));
    // The second radio decides when the first has no counterpart.
    Assert.Equal(1, NvModemDistancePairViewModel.LikelyPeer(own, [
        Links((1, Link(1, 868000000, 1))),
        Links((2, Link(1, 433920000, 0))),
    ]));
    // A hopping radio is compared by its span, not by the carrier of the moment.
    IReadOnlyDictionary<int, NvPairLink> hopping = Links((1, Link(1, 868100000, 1, 2000000)));
    Assert.Equal(1, NvModemDistancePairViewModel.LikelyPeer(hopping, [
        Links((1, Link(0, 868100000, 1))),
        Links((1, Link(0, 869300000, 1, 2000000))),
    ]));
    // Without a counterpart or without telemetry the first candidate stands.
    Assert.Equal(0, NvModemDistancePairViewModel.LikelyPeer(own, [
        Links(),
        Links((1, Link(2, 868000000, 1))),
    ]));
    Assert.Equal(0, NvModemDistancePairViewModel.LikelyPeer(Links(), [
        Links((1, Link(0, 868000000, 1))),
    ]));
  }

  [Theory]
  [InlineData(1, 0, 313)]
  [InlineData(1, 20, 333)]
  [InlineData(1, 40, 353)]
  [InlineData(29, 0, 1434)]
  [InlineData(29, 40, 1474)]
  public void The_data_pause_follows_the_modem_timing_budget(int exchanges, int margin,
      int milliseconds) {
    Assert.Equal(milliseconds, NvModemDistancePairViewModel.DistancePauseMs(exchanges, margin));
  }

  /// <summary>Three in-process modems and the pair window's view model on a fake clock.</summary>
  internal sealed class PairBench {
    private readonly Queue<(Nv5ConfigurationFrame Frame, Target Target)> _frames = new();
    private uint _now = 1000;

    internal PairBench() {
      Third.Set("CH1_FREQ_HZ", 915000000);
      Third.Set("CH1_LINK_HASH", 777);
    }

    internal Nv5FakeModem[] Modems { get; } = [Modem(0), Modem(1)];

    /// <summary>A further receiver on another carrier, as a bystander on the bench would
    /// be.</summary>
    internal Nv5FakeModem Third { get; } = Modem(2);

    internal NvModemDistancePairViewModel? Pair { get; private set; }

    /// <summary>While set, requests to the second modem wait in <see cref="Held"/>.</summary>
    internal bool HoldSecond { get; set; }

    internal List<Action> Held { get; } = [];
    internal int Begins => Modems[0].Begins + Modems[1].Begins;

    /// <summary>Routes that have closed; every other route is open.</summary>
    internal HashSet<object> Closed { get; } = [];

    internal NvModemDistancePairViewModel Create() {
      var store = new Nv5SettingsSnapshots();
      Pair = new NvModemDistancePairViewModel(Modems[0].Peer, Modems[1].Peer,
          () => new Nv5NativeSettingsEditor(() => _now, () => 255, 190) { Snapshots = store },
          route => !Closed.Contains(route));
      Pair.SendFrame += (frame, target) => _frames.Enqueue((frame, target));
      Pair.Open();
      return Pair;
    }

    /// <summary>Opens the window and waits until both modems are read. A modem that ranges can
    /// be switched off before then, so the save button alone does not say so.</summary>
    internal NvModemDistancePairViewModel Open() {
      NvModemDistancePairViewModel pair = Create();
      Assert.True(Pump(() => pair.Modem1Editor.Ready && pair.Modem2Editor.Ready && pair.CanApply
          && pair.Status.StartsWith("Both modems read", StringComparison.Ordinal)), pair.Status);
      return pair;
    }

    /// <summary>What the settings page does for every verified modem it knows.</summary>
    internal void OfferAll() {
      Pair!.Offer(Modems[0].Peer, 0, "192.168.0.10");
      Pair.Offer(Modems[1].Peer, 0, "192.168.0.11");
      Pair.Offer(Third.Peer, 0, "192.168.0.12");
    }

    internal void Release() {
      HoldSecond = false;
      foreach (Action answer in Held.ToList()) {
        answer();
      }
      Held.Clear();
    }

    /// <summary>Answers the frames in the order they were sent; between frames the clock
    /// advances.</summary>
    internal bool Pump(Func<bool> done, int steps = 50000) {
      for (int step = 0; step < steps && !done(); step++) {
        if (_frames.TryDequeue(out (Nv5ConfigurationFrame Frame, Target Target) sent)) {
          Nv5FakeModem modem = ModemOf(sent.Target.Uid);
          void Answer() {
            foreach (MAVLink.MAVLinkMessage reply in modem.Answer(sent.Frame)) {
              Pair!.Receive(reply, modem.Peer.Route);
            }
          }
          if (HoldSecond && modem == Modems[1]) {
            Held.Add(Answer);
          } else {
            Answer();
          }
        } else {
          _now += 20;
          Pair!.Service();
        }
      }
      return done();
    }

    internal void SendDistance(int modem, byte channel, int centimetres,
        byte status = Nv5DistanceStatuses.Valid) {
      Nv5FakeModem source = Modems[modem];
      var value = new Nv5DistanceMessage {
        Channel = channel,
        Status = status,
        DistanceCm = centimetres,
        AgeMs = 100,
        ValidCount = (byte)(status == Nv5DistanceStatuses.Valid ? 1 : 0),
        Calibration = Nv5DistanceCalibrations.Uncalibrated,
        CalibrationId = new byte[16],
      };
      NvModemMavlinkDialect.Register();
      byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
          (MAVLink.MAVLINK_MSG_ID)NvModemMessageIds.Nv5Distance, value, false,
          source.Peer.System, source.Peer.Component);
      Pair!.Receive(new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes))!,
          source.Peer.Route);
    }

    internal NvPairComparisonRow? Row(string name) =>
        Pair!.ComparisonRows.FirstOrDefault(row => row.Name == name);

    internal static uint Candidate(Nv5NativeSettingsEditor editor, string name) =>
        BinaryPrimitives.ReadUInt32LittleEndian(editor.CandidateValues()[name].Bytes);

    private Nv5FakeModem ModemOf(byte[] uid) =>
        new[] { Modems[0], Modems[1], Third }.Single(modem => modem.Peer.Uid.SequenceEqual(uid));

    private static Nv5FakeModem Modem(int index) => new() {
      Peer = new Target(Enumerable.Repeat((byte)('U' + index), 18).ToArray(), $"peer-{index}", 1,
          NvModemCapabilities.ConfigurationTransactions, 10, (byte)(10 + index), true),
      Settings = Nv5FakeModem.Catalogue(transmitsOnRadio1: index == 0),
    };
  }
}
