using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using MissionPlanner.Views.Setup;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

// The Distance group of the NV Modem page and its pair window, for transaction-capable NV5
// modems. NV4 modems and NV5 firmware without the capability never see either.
public partial class NvModemTests {
  [Theory]
  [InlineData(4)]
  [InlineData(5)]
  public void Pair_setup_is_offered_only_for_transaction_capable_modems(byte generation) {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(generation, NvModemCapabilities.MavlinkParameters,
        uidSeed: 0x40));
    bench.Passport(bench.B, NativePassport(5, uidSeed: 0x60));
    bench.Pump(() => false, steps: 200);

    Assert.False(bench.ViewModel.UsesNativeSettings);
    Assert.False(bench.ViewModel.CanConfigureDistancePair);
    Assert.Equal("", bench.ViewModel.DistancePairHint);
    Assert.Null(bench.ViewModel.OpenDistancePair());
    Assert.DoesNotContain(bench.Transport.Sent, sent => IsNative(sent.Packet)
        && sent.SystemId == bench.A.Peer.System);
  }

  [Fact]
  public void Pair_setup_waits_for_a_read_modem_without_unsaved_edits() {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    Assert.Equal("Available once this modem has been read.", bench.ViewModel.DistancePairHint);
    Assert.False(bench.ViewModel.CanConfigureDistancePair);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    Assert.True(bench.ViewModel.CanConfigureDistancePair);
    Assert.Equal("Switches ranging on or off for both ends of a link, and sets its schedule.",
        bench.ViewModel.DistancePairHint);

    // The pair window saves what its own tables hold; edits here would be left behind.
    Assert.True(bench.ViewModel.NativeSettings.SetValue("CH1_FREQ_HZ", "868100000"));
    Assert.False(bench.ViewModel.CanConfigureDistancePair);
    Assert.StartsWith("This modem has unsaved edits", bench.ViewModel.DistancePairHint,
        StringComparison.Ordinal);
    bench.ViewModel.NativeSettings.Discard();
    Assert.True(bench.ViewModel.CanConfigureDistancePair);

    // Without a second verified modem there is nothing to pair with, and the page says so.
    Assert.Null(bench.ViewModel.OpenDistancePair());
    Assert.True(bench.ViewModel.IsError);
    Assert.Contains("Discover a second verified modem with transaction support.",
        bench.ViewModel.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void The_pair_window_opens_on_the_likely_peer_and_talks_to_each_modem_on_its_own_link() {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    // A receiver on another carrier is heard first; the receiver on the same carrier is the
    // other end of the link.
    bench.Passport(bench.C, NativePassport(uidSeed: 0x70));
    bench.Passport(bench.B, NativePassport(uidSeed: 0x60));
    bench.LinkStatus(bench.A, role: 1, frequency: 868000000);
    bench.LinkStatus(bench.C, role: 0, frequency: 915000000);
    bench.LinkStatus(bench.B, role: 0, frequency: 868000000);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));

    NvModemDistancePairViewModel? opened = bench.ViewModel.OpenDistancePair();
    Assert.True(opened != null, bench.ViewModel.Status);
    NvModemDistancePairViewModel pair = opened!;
    Assert.Same(pair, bench.ViewModel.DistancePair);
    Assert.Equal("Modem 2 · 20:21", pair.Modem2TabName);
    Assert.Equal("10:11 · modem USB", pair.FixedName);
    Assert.Equal(["20:21 · modem LAN", "30:31 · modem LAN"], pair.Peers.Select(peer => peer.Label));
    // The page's own controls wait while the window owns the modems.
    Assert.False(bench.ViewModel.CanSwitchDevice);
    Assert.False(bench.ViewModel.CanSaveNativeSettings);
    Assert.False(bench.ViewModel.CanEdit);
    Assert.False(bench.ViewModel.CanConfigureDistancePair);
    Assert.Equal("The pair window is open.", bench.ViewModel.DistancePairHint);

    Assert.True(bench.Pump(() => pair.Modem1Editor.Ready && pair.Modem2Editor.Ready
        && pair.CanApply), pair.Status);
    List<FakeTransport.SentPacket> native = [.. bench.Transport.Sent.Where(sent =>
        IsNative(sent.Packet))];
    Assert.Contains(native, sent => sent.SystemId == 20);
    Assert.All(native, sent => Assert.Same(sent.SystemId == 10 ? bench.LinkA : bench.LinkB,
        sent.Link));
    Assert.All(native, sent => Assert.Equal(sent.SystemId + 1, sent.ComponentId));
    // No modem outside the pair is read or written.
    Assert.DoesNotContain(native, sent => sent.SystemId == 30);

    // Each modem's telemetry reaches the window and, for the selected one, the page.
    bench.Distance(bench.B, channel: 1, centimetres: 1234);
    bench.Distance(bench.A, channel: 2, centimetres: 5678);
    Assert.True(bench.Pump(() => pair.DistanceRows[1].Reading.StartsWith("12.34 m",
        StringComparison.Ordinal)), pair.DistanceRows[1].Reading);
    Assert.StartsWith("56.78 m", bench.ViewModel.NativeSettings.DistanceRows[1].Reading,
        StringComparison.Ordinal);

    Assert.True(pair.TryClose());
    bench.ViewModel.CloseDistancePair();
    Assert.Null(bench.ViewModel.DistancePair);
    Assert.True(bench.ViewModel.CanSwitchDevice);
    Assert.True(bench.Pump(() => bench.ViewModel.CanConfigureDistancePair));
  }

  [Fact]
  public void A_modem_heard_while_the_window_is_open_joins_its_list_and_restarts_follow_it() {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    bench.Passport(bench.B, NativePassport(uidSeed: 0x60));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    NvModemDistancePairViewModel? opened = bench.ViewModel.OpenDistancePair();
    Assert.True(opened != null, bench.ViewModel.Status);
    NvModemDistancePairViewModel pair = opened!;
    Assert.True(bench.Pump(() => pair.Modem2Editor.Ready));
    Assert.Single(pair.Peers);

    bench.Passport(bench.C, NativePassport(uidSeed: 0x70));
    Assert.Equal(2, pair.Peers.Count);

    // Modem 1 restarts: the window's editor reads it again; the page's editor waits for the
    // window to close.
    int pageReads = bench.A.ReadBegins;
    bench.A.Reboot();
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40, timeBootMs: 50));
    Assert.True(bench.Pump(() => pair.Modem1Editor.RestartedRecently && pair.Modem1Editor.Ready));
    Assert.False(bench.ViewModel.NativeSettings.Busy);
    Assert.False(bench.ViewModel.NativeSettings.RestartedRecently);
    Assert.True(bench.A.ReadBegins > pageReads);

    Assert.True(pair.TryClose());
    bench.ViewModel.CloseDistancePair();
    Assert.True(bench.ViewModel.NativeSettings.RestartedRecently);
  }

  [AvaloniaTheory]
  [InlineData(860)]
  [InlineData(1120)]
  public void The_distance_group_shows_each_radio_and_fits_a_narrow_page(double width) {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    var window = new Window {
      Width = width,
      Height = 900,
      Content = new NvModemView { DataContext = bench.ViewModel },
    };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();
      Expander group = window.GetVisualDescendants().OfType<Expander>()
          .Single(expander => expander.Name == "DistanceGroup");
      Assert.True(group.IsVisible);
      DataGrid rows = group.GetVisualDescendants().OfType<DataGrid>().Single();
      Assert.Equal(["Radio 1", "Radio 2"], rows.ItemsSource!.Cast<NvDistanceRow>()
          .Select(row => row.Name));
      Button configure = group.GetVisualDescendants().OfType<Button>()
          .Single(button => button.Name == "ConfigureDistancePairButton");
      Assert.True(configure.IsEffectivelyEnabled);

      List<string> problems = [];
      LayoutOverflowTests.CollectGridProblems(group, problems);
      LayoutOverflowTests.CollectCollapsedInputs(group, problems);
      CollectButtonsPastTheEdge(group, window, problems);
      Assert.True(problems.Count == 0, LayoutOverflowTests.Report(problems, width));
    } finally {
      window.Close();
    }
  }

  [AvaloniaTheory]
  [InlineData(900, 640)]
  [InlineData(1060, 820)]
  public void The_pair_window_fits_its_minimum_size(double width, double height) {
    var bench = new NvModemDistancePairTests.PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    var window = new NvModemDistancePairWindow { DataContext = pair, Width = width,
        Height = height };
    try {
      Assert.Equal(900, window.MinWidth);
      Assert.Equal(640, window.MinHeight);
      window.Show();
      Dispatcher.UIThread.RunJobs();
      List<string> problems = [];
      LayoutOverflowTests.CollectGridProblems(window, problems);
      LayoutOverflowTests.CollectCollapsedInputs(window, problems);
      CollectButtonsPastTheEdge(window, window, problems);
      Assert.True(problems.Count == 0, LayoutOverflowTests.Report(problems, width));
      // The distance rows run the width of the window, below the setup and the tables.
      Border group = window.FindControl<Border>("PairDistanceGroup")!;
      Assert.True(group.Bounds.Width > width - 40, $"{group.Bounds.Width}");
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void Enter_in_a_field_never_starts_saving_both_modems() {
    var bench = new NvModemDistancePairTests.PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    var window = new NvModemDistancePairWindow { DataContext = pair };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();
      // Saving restarts both modems, so no button is the default one.
      Assert.All(window.GetVisualDescendants().OfType<Button>(),
          button => Assert.False(button.IsDefault, button.Content?.ToString()));
      foreach (string name in (string[])["PairAddress", "PairExchanges", "PairSpan", "PairPower"]) {
        window.FindControl<Control>(name)!.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None);
        window.KeyRelease(Key.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
      }
      Assert.False(bench.Pump(() => bench.Begins != 0 || pair.Active, steps: 200));
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void A_wheel_over_a_field_without_focus_leaves_its_value() {
    var bench = new NvModemDistancePairTests.PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    var window = new NvModemDistancePairWindow { DataContext = pair };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();
      window.FindControl<CheckBox>("PairEnabled")!.Focus();
      foreach (string name in (string[])["PairExchanges", "PairPeriod", "PairPower"]) {
        Control field = window.FindControl<Control>(name)!;
        Assert.False(field.IsKeyboardFocusWithin);
        Point centre = field.TranslatePoint(new Point(field.Bounds.Width / 2,
            field.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre, RawInputModifiers.None);
        window.MouseWheel(centre, new Vector(0, 1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
      }
      Assert.Equal(1, pair.Exchanges);
      Assert.Equal(1024u, pair.SelectedPeriod!.Packets);
      Assert.Equal(-9, pair.PowerDbm);
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void The_modem_tabs_keep_only_review_controls_and_close_waits_for_a_save() {
    var bench = new NvModemDistancePairTests.PairBench();
    NvModemDistancePairViewModel pair = bench.Open();
    var window = new NvModemDistancePairWindow { DataContext = pair };
    try {
      window.Show();
      TabControl tabs = window.FindControl<TabControl>("PairTabs")!;
      foreach (int tab in (int[])[1, 2]) {
        tabs.SelectedIndex = tab;
        Dispatcher.UIThread.RunJobs();
        Nv5NativeSettingsTable table = tabs.GetVisualDescendants()
            .OfType<Nv5NativeSettingsTable>().Single(control => control.IsEffectivelyVisible);
        Assert.Same(tab == 1 ? pair.Modem1Editor : pair.Modem2Editor, table.DataContext);
        // The table's own buttons; the grid's scroll and header parts carry no text.
        List<string> buttons = [.. table.GetVisualDescendants().OfType<Button>()
            .Select(button => button.Content).OfType<string>()];
        Assert.Equal(["Read settings"], buttons);
      }
      Assert.Equal("Modem 1 · 10:10", ((TabItem)tabs.Items[1]!).Header);

      // While a save is under way the window stays open and says why.
      bench.Modems[1].CandidateHash = 701;
      pair.RangingEnabled = true;
      pair.Apply();
      Assert.True(pair.Active);
      window.Close();
      Assert.True(window.IsVisible);
      Assert.Contains("Wait for both save outcomes", pair.Status, StringComparison.Ordinal);
      Assert.True(bench.Pump(() => !pair.Active));
    } finally {
      window.Close();
    }
    Assert.False(window.IsVisible);
  }

  [AvaloniaFact]
  public void Highlighted_comparison_rows_are_marked_and_others_keep_the_theme_colours() {
    var bench = new NvModemDistancePairTests.PairBench();
    bench.Modems[1].Set("CH1_FREQ_HZ", 868500000);
    bench.Modems[1].Set("CH1_LINK_HASH", 501);
    NvModemDistancePairViewModel pair = bench.Open();
    var window = new NvModemDistancePairWindow { DataContext = pair };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();
      List<DataGridRow> rows = [.. window.FindControl<DataGrid>("PairComparison")!
          .GetVisualDescendants().OfType<DataGridRow>()];
      DataGridRow Row(string name) =>
          rows.Single(row => (row.DataContext as NvPairComparisonRow)?.Name == name);
      Assert.Contains("marked", Row("CH1_FREQ_HZ").Classes);
      Assert.Contains("marked", Row("CH1_LINK_HASH").Classes);
      Assert.DoesNotContain("marked", Row("CH1_ROLE").Classes);
      TextBlock role = Row("CH1_ROLE").GetVisualDescendants().OfType<TextBlock>()
          .First(text => text.Text == "CH1_ROLE");
      Assert.NotNull(role.Foreground);
    } finally {
      window.Close();
    }
  }

  private static void CollectButtonsPastTheEdge(Visual root, TopLevel top, List<string> problems) {
    foreach (Button button in root.GetVisualDescendants().OfType<Button>()
        .Where(button => button.IsEffectivelyVisible)) {
      Point topLeft = button.TranslatePoint(new Point(0, 0), top) ?? default;
      if (topLeft.X + button.Bounds.Width > top.ClientSize.Width + 0.5) {
        problems.Add($"{button.Content} ends at {topLeft.X + button.Bounds.Width:F1}");
      }
    }
  }

  /// <summary>
  /// The page with three fake NV5 modems on a fake clock: A at 10:11 on a USB link, B at 20:21
  /// and C at 30:31 on a LAN link. A transmits on radio 1; B and C receive there, C on another
  /// carrier.
  /// </summary>
  private sealed class DistancePageBench : IDisposable {
    private DateTime _now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private int _answered;

    internal DistancePageBench() {
      Transport.Links.Add(LinkA);
      Transport.Links.Add(LinkB);
      ViewModel = new NvModemViewModel(Transport, () => _now, startTimer: false);
      A = Modem(LinkA, 10, 0x40, transmits: true);
      B = Modem(LinkB, 20, 0x60, transmits: false);
      C = Modem(LinkB, 30, 0x70, transmits: false);
      C.Set("CH1_FREQ_HZ", 915000000);
    }

    internal FakeTransport Transport { get; } = new();
    internal NvModemLink LinkA { get; } = new(new MAVLinkInterface(), "modem USB");
    internal NvModemLink LinkB { get; } = new(new MAVLinkInterface(), "modem LAN");
    internal NvModemViewModel ViewModel { get; }
    internal Nv5FakeModem A { get; }
    internal Nv5FakeModem B { get; }
    internal Nv5FakeModem C { get; }

    internal void Passport(Nv5FakeModem modem, NvModemInfoMessage info) =>
        ViewModel.HandlePacket(LinkOf(modem), Packet(NvModemMessageIds.NvModemInfo, info,
            modem.Peer.System, modem.Peer.Component));

    internal void LinkStatus(Nv5FakeModem modem, byte role, uint frequency) =>
        ViewModel.HandlePacket(LinkOf(modem), Packet(NvModemMessageIds.Nv5LinkStatus,
            new Nv5LinkStatusMessage {
              Channel = 1, Role = role, FrequencyHz = frequency, Modulation = 1, LinkQuality = 90,
            }, modem.Peer.System, modem.Peer.Component));

    internal void Distance(Nv5FakeModem modem, byte channel, int centimetres) =>
        ViewModel.HandlePacket(LinkOf(modem), Packet(NvModemMessageIds.Nv5Distance,
            new Nv5DistanceMessage {
              Channel = channel, Status = Nv5DistanceStatuses.Valid, DistanceCm = centimetres,
              AgeMs = 100, ValidCount = 1, Calibration = Nv5DistanceCalibrations.Uncalibrated,
              CalibrationId = new byte[16],
            }, modem.Peer.System, modem.Peer.Component));

    /// <summary>Answers native frames in the order the page sent them, each by the modem at its
    /// address; between frames the clock advances by the page's service tick.</summary>
    internal bool Pump(Func<bool> done, int steps = 20000) {
      for (int step = 0; step < steps && !done(); step++) {
        if (_answered < Transport.Sent.Count) {
          FakeTransport.SentPacket sent = Transport.Sent[_answered++];
          uint? id = sent.Packet switch {
            Nv5ConfigControlMessage => NvModemMessageIds.Nv5ConfigControl,
            Nv5ConfigItemMessage => NvModemMessageIds.Nv5ConfigItem,
            _ => null,
          };
          Nv5FakeModem? modem = new[] { A, B, C }.SingleOrDefault(candidate =>
              candidate.Peer.System == sent.SystemId
              && candidate.Peer.Component == sent.ComponentId);
          if (id == null || modem == null || !ReferenceEquals(LinkOf(modem), sent.Link)) {
            continue;
          }
          foreach (MAVLink.MAVLinkMessage reply in modem.Answer(
              new Nv5ConfigurationFrame(id.Value, sent.Packet))) {
            ViewModel.HandlePacket(sent.Link, reply);
          }
        } else {
          _now = _now.AddMilliseconds(200);
          ViewModel.ServiceTransactions();
        }
      }
      return done();
    }

    public void Dispose() => ViewModel.Dispose();

    private NvModemLink LinkOf(Nv5FakeModem modem) => (NvModemLink)modem.Peer.Route;

    private static Nv5FakeModem Modem(NvModemLink link, byte system, byte uidSeed,
        bool transmits) => new() {
          Peer = new Target(Enumerable.Range(0, 18).Select(index => (byte)(uidSeed + index))
                  .ToArray(), link, 1, NvModemCapabilities.ConfigurationTransactions, system,
              (byte)(system + 1), true),
          Settings = Nv5FakeModem.Catalogue(transmitsOnRadio1: transmits),
          Digest = uidSeed,
        };
  }
}
