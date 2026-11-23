using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using MissionPlanner.Views.Setup;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

// The LPS field window as the operator sees it, and the NV Modem page that opens it.
public partial class NvModemTests {
  [AvaloniaTheory]
  [InlineData(1000, 700)]
  [InlineData(1200, 860)]
  public void The_lps_window_fits_its_minimum_size(double width, double height) {
    NvModemLpsFieldTests.LpsFieldBench bench = ThreeLpsModems();
    NvModemLpsFieldViewModel field = bench.Open();
    field.RoverAdvancedShown = true;
    field.RadioSettingsShown = true;
    field.ShowCovariance = true;
    var window = new NvModemLpsFieldWindow { DataContext = field, Width = width, Height = height };
    try {
      Assert.Equal(1000, window.MinWidth);
      Assert.Equal(700, window.MinHeight);
      window.Show();
      Dispatcher.UIThread.RunJobs();
      List<string> problems = [];
      LayoutOverflowTests.CollectGridProblems(window, problems);
      LayoutOverflowTests.CollectCollapsedInputs(window, problems);
      CollectButtonsPastTheEdge(window, window, problems);
      Assert.True(problems.Count == 0, LayoutOverflowTests.Report(problems, width));
      // The LPS rows run the width of the window, below the setup and the tabs.
      Border group = window.FindControl<Border>("LpsReadoutGroup")!;
      Assert.True(group.Bounds.Width > width - 40, $"{group.Bounds.Width}");
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void Enter_in_an_lps_field_only_ends_the_edit() {
    NvModemLpsFieldTests.LpsFieldBench bench = ThreeLpsModems();
    NvModemLpsFieldViewModel field = bench.Open();
    var window = new NvModemLpsFieldWindow { DataContext = field };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();
      // Saving restarts modems, so no button is the default one.
      Assert.All(window.GetVisualDescendants().OfType<Button>(),
          button => Assert.False(button.IsDefault, button.Content?.ToString()));
      // A typed coordinate is taken when Enter ends the edit; nothing is saved.
      TextBox latitude = window.FindControl<TextBox>("LpsLatitude")!;
      latitude.Focus();
      latitude.Text = "12.5";
      window.KeyPress(Key.Enter, RawInputModifiers.None);
      window.KeyRelease(Key.Enter, RawInputModifiers.None);
      Dispatcher.UIThread.RunJobs();
      Assert.Equal("125000000", field.Field["LPS_LAT_E7"]);
      Assert.Equal("12.5000000", latitude.Text);
      Assert.True(field.CanApply, field.ApplyTip);
      foreach (string name in (string[])["LpsLongitude", "LpsDatum", "LpsEllipsoidHeight",
          "LpsFcSystem", "LpsLaunchHeight"]) {
        window.FindControl<Control>(name)!.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None);
        window.KeyRelease(Key.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
      }
      Assert.False(bench.Pump(() => bench.Begins != 0 || field.Active, steps: 200));
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void A_wheel_over_an_lps_field_without_focus_leaves_its_value() {
    NvModemLpsFieldTests.LpsFieldBench bench = ThreeLpsModems();
    NvModemLpsFieldViewModel field = bench.Open();
    var window = new NvModemLpsFieldWindow { DataContext = field };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();
      window.FindControl<CheckBox>("LpsShowCovariance")!.Focus();
      foreach (string name in (string[])["LpsFcSystem", "LpsEllipsoidHeight", "LpsBeaconCount"]) {
        Control control = window.FindControl<Control>(name)!;
        Assert.False(control.IsKeyboardFocusWithin);
        Point centre = control.TranslatePoint(new Point(control.Bounds.Width / 2,
            control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre, RawInputModifiers.None);
        window.MouseWheel(centre, new Vector(0, 1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
      }
      Assert.Equal(1m, field.FlightControllerSystem);
      Assert.Equal(0m, field.EllipsoidHeight);
      Assert.Equal(3, field.SelectedBeaconCount!.Value);
      Assert.Equal("Nothing to save", field.ApplyText);
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void The_modem_tabs_show_each_modem_s_settings_for_review_only() {
    NvModemLpsFieldTests.LpsFieldBench bench = ThreeLpsModems();
    NvModemLpsFieldViewModel field = bench.Open();
    var window = new NvModemLpsFieldWindow { DataContext = field };
    try {
      window.Show();
      Dispatcher.UIThread.RunJobs();
      TabControl tabs = window.FindControl<TabControl>("LpsTabs")!;
      Assert.Equal(["Field", "Modem 1 · 10:11", "Modem 2 · 10:12", "Modem 3 · 10:13"],
          tabs.Items.OfType<TabItem>().Select(tab => tab.Header as string));
      tabs.SelectedIndex = 2;
      Dispatcher.UIThread.RunJobs();
      Nv5NativeSettingsTable table = tabs.GetVisualDescendants()
          .OfType<Nv5NativeSettingsTable>().Single(control => control.IsEffectivelyVisible);
      Assert.Same(field.Modems[1].Editor, table.DataContext);
      Assert.True(table.IsReadOnly);
      // A value is never edited here: the window saves what its form holds.
      DataGrid grid = table.FindControl<DataGrid>("SettingsGrid")!;
      grid.SelectedItem = field.Modems[1].Editor.Rows.First(row => row.IsEditable);
      grid.CurrentColumn = grid.Columns[1];
      Assert.False(grid.BeginEdit());
      Assert.False(field.Modems[1].Editor.HasPendingChanges());

      // The covariance terms have their own switch.
      DataGrid beacons = window.FindControl<DataGrid>("LpsBeacons")!;
      Assert.Equal(13, beacons.Columns.Count);
      Assert.All(beacons.Columns.Skip(10), column => Assert.False(column.IsVisible));
      field.ShowCovariance = true;
      Assert.All(beacons.Columns.Skip(10), column => Assert.True(column.IsVisible));
      Assert.Equal(3, beacons.ItemsSource!.Cast<object>().Count());

      // A modem heard while the window is open gets a tab of its own.
      field.Offer(new Target(Enumerable.Repeat((byte)'Z', 18).ToArray(), "route-9", 1,
          NvModemCapabilities.ConfigurationTransactions, 10, 19, true), 0, "192.168.144.29");
      Assert.Equal("Modem 4 · 10:19", tabs.Items.OfType<TabItem>().Last().Header);
    } finally {
      window.Close();
    }
  }

  [AvaloniaFact]
  public void The_lps_window_stays_open_until_a_refused_check_is_aborted_on_its_modem() {
    NvModemLpsFieldTests.LpsFieldBench bench = ThreeLpsModems();
    bench.Modems[1].AcceptedValidations = 0;
    NvModemLpsFieldViewModel field = bench.Open();
    var window = new NvModemLpsFieldWindow { DataContext = field };
    try {
      window.Show();
      field.Modems[1].Role = field.Modems[1].Roles[0];
      field.Apply();
      Assert.True(field.Active);
      window.Close();
      Assert.True(window.IsVisible);
      Assert.Equal("Wait until the modem being saved has finished.", field.Status);
      Assert.True(bench.Pump(() => bench.Modems[1].Validations == 1));
      bench.Holding.Add(bench.Modems[1]);
      Assert.True(bench.Pump(() => !field.Active && bench.Held.Count != 0), field.Status);
      window.Close();
      Assert.True(window.IsVisible);
      Assert.Equal("Closing once Modem 2 · 10:12 has cancelled its unfinished save…",
          field.Status);
      bench.Release();
      Assert.True(bench.Pump(() => !window.IsVisible, steps: 100), field.Status);
      Assert.Contains(bench.Modems[1].Seen, frame => frame.Payload is Nv5ConfigControlMessage {
        Operation: Nv5ConfigOperations.Abort,
      });
    } finally {
      window.Close();
    }
    Assert.False(window.IsVisible);
  }

  [Theory]
  [InlineData(4)]
  [InlineData(5)]
  public void Lps_setup_is_offered_only_for_transaction_capable_modems(byte generation) {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(generation, NvModemCapabilities.MavlinkParameters,
        uidSeed: 0x40));
    bench.Pump(() => false, steps: 200);
    Assert.False(bench.ViewModel.CanConfigureLpsField);
    Assert.Equal("", bench.ViewModel.LpsFieldHint);
    Assert.Null(bench.ViewModel.OpenLpsField());
  }

  [Fact]
  public void The_lps_field_window_opens_with_every_online_modem_and_holds_the_page() {
    using var bench = new DistancePageBench();
    Nv5LpsTestData.AddLps(bench.A, Nv5LpsRoles.Rover, 0);
    Nv5LpsTestData.AddLps(bench.B, Nv5LpsRoles.Beacon, uint.MaxValue);
    Nv5LpsTestData.AddLps(bench.C, Nv5LpsRoles.Beacon, uint.MaxValue - 1);
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    Assert.Equal("Available once this modem has been read.", bench.ViewModel.LpsFieldHint);
    bench.Passport(bench.B, NativePassport(uidSeed: 0x60));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    Assert.True(bench.ViewModel.CanConfigureLpsField);
    Assert.Equal("Sets the surveyed field and the LPS role of each modem.",
        bench.ViewModel.LpsFieldHint);

    NvModemLpsFieldViewModel? opened = bench.ViewModel.OpenLpsField();
    Assert.True(opened != null, bench.ViewModel.Status);
    NvModemLpsFieldViewModel field = opened!;
    Assert.Same(field, bench.ViewModel.LpsField);
    Assert.Equal(["Modem 1 · 10:11", "Modem 2 · 20:21"], field.Modems.Select(modem => modem.Name));
    Assert.Equal("modem LAN", field.Modems[1].Address);
    // The page's own controls wait while the window owns the modems.
    Assert.False(bench.ViewModel.CanSwitchDevice);
    Assert.False(bench.ViewModel.CanSaveNativeSettings);
    Assert.False(bench.ViewModel.CanEdit);
    Assert.False(bench.ViewModel.CanConfigureDistancePair);
    Assert.False(bench.ViewModel.CanConfigureLpsField);
    Assert.Equal("The LPS field window is open.", bench.ViewModel.DistancePairHint);
    Assert.Equal("The LPS field window is open.", bench.ViewModel.LpsFieldHint);
    Assert.Null(bench.ViewModel.OpenDistancePair());
    // The window asks its modems whether their reports are current; the page's editor does not.
    Assert.False(bench.ViewModel.NativeSettings.LpsChallenges);

    Assert.True(bench.Pump(() => field.Modems.All(modem => modem.Editable)), field.Status);
    List<FakeTransport.SentPacket> native = [.. bench.Transport.Sent.Where(sent =>
        IsNative(sent.Packet))];
    Assert.Contains(native, sent => sent.SystemId == 20);
    Assert.All(native, sent => Assert.Same(sent.SystemId == 10 ? bench.LinkA : bench.LinkB,
        sent.Link));
    // A modem heard while the window is open joins it.
    Assert.DoesNotContain(native, sent => sent.SystemId == 30);
    bench.Passport(bench.C, NativePassport(uidSeed: 0x70));
    Assert.Equal("Modem 3 · 30:31", field.Modems.Last().Name);
    Assert.True(bench.Pump(() => field.Modems.All(modem => modem.Editable)), field.Status);
    Assert.Equal("Every modem is read. Choose the roles, check the field, then verify and save.",
        field.Status);

    // Each modem's LPS reports reach its row of the window.
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status();
    report.Uid = bench.B.Peer.Uid.ToArray();
    report.Role = Nv5LpsRoles.Beacon;
    report.Status = 0;
    report.PoseValidForUs = 0;
    report.BeaconMask = 0;
    report.LastAttemptStatus = 1;
    bench.ViewModel.HandlePacket(bench.LinkB, Nv5LpsTestData.Packet(report, bench.B.Peer.System,
        bench.B.Peer.Component));
    Assert.True(bench.Pump(() => field.LpsRows[1].Reading == "answering", steps: 5),
        field.LpsRows[1].Reading);

    Assert.True(field.TryClose());
    bench.ViewModel.CloseLpsField();
    Assert.Null(bench.ViewModel.LpsField);
    Assert.True(bench.ViewModel.NativeSettings.LpsChallenges);
    Assert.True(bench.ViewModel.CanSwitchDevice);
    Assert.True(bench.Pump(() => bench.ViewModel.CanConfigureDistancePair
        && bench.ViewModel.CanConfigureLpsField));
  }

  [Fact]
  public void The_lps_button_says_when_the_selected_modem_has_no_lps_settings() {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    Assert.True(bench.ViewModel.CanConfigureLpsField);
    Assert.Equal("This modem's firmware has no LPS settings; the field can still be set on the "
        + "modems that have them.", bench.ViewModel.LpsFieldHint);
    // The pair window and the LPS window are never open together.
    Assert.NotNull(bench.ViewModel.OpenLpsField());
    Assert.Null(bench.ViewModel.OpenDistancePair());
    Assert.True(bench.ViewModel.LpsField!.TryClose());
    bench.ViewModel.CloseLpsField();
  }

  private static NvModemLpsFieldTests.LpsFieldBench ThreeLpsModems() => new(
      Nv5LpsTestData.Modem(0, Nv5LpsRoles.Rover, 0),
      Nv5LpsTestData.Modem(1, Nv5LpsRoles.Beacon, uint.MaxValue),
      Nv5LpsTestData.Modem(2, Nv5LpsRoles.Beacon, uint.MaxValue - 1));
}
