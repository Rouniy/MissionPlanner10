using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MissionPlanner;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using MissionPlanner.Views.Setup;

namespace MissionPlanner.Tests;

// The NV Modem page with a modem whose firmware advertises configuration transactions: its
// settings are read and saved natively, the way the reference ground station does, while NV4 and
// NV5 firmware without the capability keep the PARAM page.
public partial class NvModemTests {
  [Fact]
  public void Transaction_capable_modem_is_configured_natively_and_never_sent_params() {
    var bench = new NativeBench();
    bench.Passport(NativePassport());

    Assert.True(bench.ViewModel.UsesNativeSettings);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready),
        bench.ViewModel.NativeSettings.StatusText);
    Assert.Equal(bench.Modem.Settings.Count, bench.ViewModel.NativeSettings.Rows.Count);
    Assert.Empty(bench.ViewModel.Parameters);
    Assert.DoesNotContain(bench.Transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_request_list_t
            or MAVLink.mavlink_param_set_t);
    Assert.All(bench.Transport.Sent.Where(sent => IsNative(sent.Packet)), sent => {
      Assert.Same(bench.Link, sent.Link);
      Assert.Equal(10, sent.SystemId);
      Assert.Equal(11, sent.ComponentId);
    });

    // Refresh reads the modem in full, even though it reports the same settings.
    int reads = bench.Modem.Reads;
    bench.ViewModel.RefreshSelectedCommand.Execute(null);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready
        && bench.Modem.Reads == reads + bench.Modem.Settings.Count));
  }

  [Theory]
  [InlineData(4)]
  [InlineData(5)]
  public void Modems_without_configuration_transactions_keep_the_parameter_page(byte generation) {
    var bench = new NativeBench();
    bench.Passport(NativePassport(generation, NvModemCapabilities.MavlinkParameters));
    bench.Pump(() => false, steps: 200);

    Assert.False(bench.ViewModel.UsesNativeSettings);
    Assert.False(bench.ViewModel.NativeSettings.Busy);
    Assert.Contains(bench.Transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_request_list_t);
    Assert.DoesNotContain(bench.Transport.Sent, sent => IsNative(sent.Packet));
  }

  [Fact]
  public void A_native_save_keeps_its_modem_selected_until_it_settles() {
    var bench = new NativeBench();
    bench.Modem.CommitModel = true;
    bench.Passport(NativePassport());
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    Assert.True(bench.ViewModel.NativeSettings.SetValue("CH1_FREQ_HZ", "915000000"));
    string summary = bench.ViewModel.NativeSaveSummary(bench.ViewModel.SelectedDevice!.State);
    Assert.Contains("CH1_FREQ_HZ: 868000000 → 915000000", summary, StringComparison.Ordinal);
    Assert.Contains("restarts", summary, StringComparison.Ordinal);
    // The page asks before saving; the editor's save is what the confirmation runs.
    bench.ViewModel.NativeSettings.Save();
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.StatusText.Contains(
        "Waiting for restart", StringComparison.Ordinal)),
        bench.ViewModel.NativeSettings.StatusText);
    Assert.True(bench.ViewModel.NativeSettings.AwaitingReconnect);
    Assert.False(bench.ViewModel.CanSwitchDevice);

    bench.ViewModel.HandlePacket(bench.Link, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(uidSeed: 0x70), 20, 21));
    NvModemDeviceChoice selected = bench.ViewModel.SelectedDevice!;
    bench.ViewModel.SelectedDevice =
        bench.ViewModel.Devices.Single(choice => !ReferenceEquals(choice, selected));
    Assert.Same(selected, bench.ViewModel.SelectedDevice);
    Assert.Equal(1, bench.Modem.Commits);

    // The restart is seen in the next passport: its uptime starts again.
    bench.Modem.Reboot();
    bench.Passport(NativePassport(timeBootMs: 40));
    Assert.True(bench.Pump(() => !bench.ViewModel.NativeSettings.Busy),
        bench.ViewModel.NativeSettings.StatusText);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready
        && bench.ViewModel.NativeSettings.ValuesReadBack));
    Assert.True(bench.ViewModel.CanSwitchDevice);
    Assert.Equal(1, bench.Modem.Commits);
    Assert.Equal("915000000", bench.ViewModel.NativeSettings.Rows
        .Single(row => row.Name == "CH1_FREQ_HZ").ValueText);
  }

  [Fact]
  public void A_native_save_disables_reboot_and_transmitter_commands_until_it_settles() {
    var bench = new NativeBench();
    bench.Modem.CommitModel = true;
    bench.Passport(NativePassport());
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    bench.Passport(NativePassport(timeBootMs: 11_000));
    Assert.True(bench.ViewModel.CanReboot);

    Assert.True(bench.ViewModel.NativeSettings.SetValue("CH1_FREQ_HZ", "915000000"));
    bench.ViewModel.NativeSettings.Save();
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.StatusText.Contains(
        "Waiting for restart", StringComparison.Ordinal)));
    // The modem is still online: its passport keeps arriving while the commit waits.
    bench.Passport(NativePassport(timeBootMs: 12_000));
    Assert.False(bench.ViewModel.CanEdit);
    Assert.False(bench.ViewModel.CanReboot);
    Assert.False(bench.ViewModel.CanUseNv5Controls);

    bench.Modem.Reboot();
    bench.Passport(NativePassport(timeBootMs: 40));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready
        && bench.ViewModel.NativeSettings.ValuesReadBack));
    bench.Passport(NativePassport(timeBootMs: 1_000));
    Assert.True(bench.ViewModel.CanReboot);
  }

  [Fact]
  public void A_native_save_recovers_on_the_link_that_replaced_its_own() {
    var bench = new NativeBench();
    bench.Modem.CommitModel = true;
    bench.Passport(NativePassport());
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    bool? finished = null;
    bench.ViewModel.NativeSettings.ConfigurationFinished += success => finished = success;
    Assert.True(bench.ViewModel.NativeSettings.SetValue("CH1_FREQ_HZ", "915000000"));
    bench.ViewModel.NativeSettings.Save();
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.StatusText.Contains(
        "Waiting for restart", StringComparison.Ordinal)));

    // The restart replaces the modem's connection, as a re-enumerated USB port does.
    var replacement = new NvModemLink(new MAVLinkInterface(), "modem USB, reopened");
    bench.Transport.Links.Remove(bench.Link);
    bench.Transport.Links.Add(replacement);
    bench.Modem.Reboot();
    int sentBefore = bench.Transport.Sent.Count;
    bench.ViewModel.HandlePacket(replacement, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(timeBootMs: 40), 10, 11));

    NvModemDeviceChoice modem = Assert.Single(bench.ViewModel.Devices);
    Assert.Same(modem, bench.ViewModel.SelectedDevice);
    Assert.Same(replacement, modem.State.Key.Link);
    Assert.True(bench.Pump(() => finished != null), bench.ViewModel.NativeSettings.StatusText);
    Assert.True(finished, bench.ViewModel.NativeSettings.StatusText);
    Assert.All(bench.Transport.Sent.Skip(sentBefore).Where(sent => IsNative(sent.Packet)),
        sent => Assert.Same(replacement, sent.Link));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready
        && bench.ViewModel.NativeSettings.ValuesReadBack));
    Assert.Equal(1, bench.Modem.Commits);
  }

  [Fact]
  public void A_native_save_recovers_after_both_earlier_links_to_its_modem_closed() {
    var bench = new NativeBench();
    bench.Modem.CommitModel = true;
    var ethernet = new NvModemLink(new MAVLinkInterface(), "modem Ethernet");
    bench.Transport.Links.Add(ethernet);
    bench.Passport(NativePassport());
    bench.ViewModel.HandlePacket(ethernet, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(), 10, 11));
    Assert.Equal(2, bench.ViewModel.Devices.Count);
    NvModemDeviceChoice saving = bench.ViewModel.SelectedDevice!;
    Assert.Same(bench.Link, saving.State.Key.Link);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    bool? finished = null;
    bench.ViewModel.NativeSettings.ConfigurationFinished += success => finished = success;
    Assert.True(bench.ViewModel.NativeSettings.SetValue("CH1_FREQ_HZ", "915000000"));
    bench.ViewModel.NativeSettings.Save();
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.StatusText.Contains(
        "Waiting for restart", StringComparison.Ordinal)));

    // Both earlier connections close during the restart and the modem returns on a third.
    var replacement = new NvModemLink(new MAVLinkInterface(), "modem USB, reopened");
    bench.Transport.Links.Remove(bench.Link);
    bench.Transport.Links.Remove(ethernet);
    bench.Transport.Links.Add(replacement);
    bench.Modem.Reboot();
    int sentBefore = bench.Transport.Sent.Count;
    bench.ViewModel.HandlePacket(replacement, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(timeBootMs: 40), 10, 11));

    NvModemDeviceChoice modem = Assert.Single(bench.ViewModel.Devices);
    Assert.Same(saving, modem);
    Assert.Same(modem, bench.ViewModel.SelectedDevice);
    Assert.Same(replacement, modem.State.Key.Link);
    Assert.True(bench.Pump(() => finished != null), bench.ViewModel.NativeSettings.StatusText);
    Assert.True(finished, bench.ViewModel.NativeSettings.StatusText);
    Assert.All(bench.Transport.Sent.Skip(sentBefore).Where(sent => IsNative(sent.Packet)),
        sent => Assert.Same(replacement, sent.Link));
    Assert.Equal(1, bench.Modem.Commits);
  }

  [Fact]
  public void A_modem_whose_earlier_links_all_closed_returns_as_one_record() {
    var bench = new NativeBench();
    var ethernet = new NvModemLink(new MAVLinkInterface(), "modem Ethernet");
    bench.Transport.Links.Add(ethernet);
    NvModemInfoMessage parameters = NativePassport(capabilities: NvModemCapabilities.MavlinkParameters);
    bench.Passport(parameters);
    bench.ViewModel.HandlePacket(ethernet, Packet(NvModemMessageIds.NvModemInfo, parameters,
        10, 11));
    Assert.Equal(2, bench.ViewModel.Devices.Count);
    NvModemDeviceChoice selected = bench.ViewModel.SelectedDevice!;

    var replacement = new NvModemLink(new MAVLinkInterface(), "modem USB, reopened");
    bench.Transport.Links.Remove(bench.Link);
    bench.Transport.Links.Remove(ethernet);
    bench.Transport.Links.Add(replacement);
    bench.ViewModel.HandlePacket(replacement, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(capabilities: NvModemCapabilities.MavlinkParameters, timeBootMs: 40),
        10, 11));

    NvModemDeviceChoice modem = Assert.Single(bench.ViewModel.Devices);
    Assert.Same(selected, modem);
    Assert.Same(replacement, modem.State.Key.Link);
  }

  [Fact]
  public void A_native_save_waits_for_a_pending_modem_command() {
    var bench = new NativeBench();
    bench.Passport(NativePassport());
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    // Radio 1 transmits with its transmitter suppressed, so it can be enabled.
    bench.ViewModel.HandlePacket(bench.Link, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1, TxState = 2 }, 10, 11));
    Assert.True(bench.ViewModel.NativeSettings.SetValue("CH1_FREQ_HZ", "915000000"));
    Assert.True(bench.ViewModel.CanSaveNativeSettings);
    Assert.True(bench.ViewModel.CanEnableTransmitter);

    bench.ViewModel.SetTransmitterEnabledCommand.Execute("true");
    Assert.True(bench.ViewModel.IsBusy);
    Assert.False(bench.ViewModel.CanSaveNativeSettings);
    int begins = bench.Transport.Sent.Count(sent =>
        sent.Packet is Nv5ConfigControlMessage { Operation: Nv5ConfigOperations.Begin });
    bench.ViewModel.SaveNativeSettingsCommand.Execute(null);
    Assert.Equal(begins, bench.Transport.Sent.Count(sent =>
        sent.Packet is Nv5ConfigControlMessage { Operation: Nv5ConfigOperations.Begin }));
    Assert.False(bench.ViewModel.NativeSettings.Busy);
    Assert.Contains("pending modem command", bench.ViewModel.Status, StringComparison.Ordinal);

    // The acknowledgement settles the command; only then may the save go ahead.
    bench.ViewModel.HandlePacket(bench.Link, Packet((uint)MAVLink.MAVLINK_MSG_ID.COMMAND_ACK,
        new MAVLink.mavlink_command_ack_t {
          command = 42010,
          result = (byte)MAVLink.MAV_RESULT.ACCEPTED,
          target_system = 255,
          target_component = 190,
        }, 10, 11));
    Assert.False(bench.ViewModel.IsBusy);
    Assert.True(bench.ViewModel.CanSaveNativeSettings);
  }

  [Fact]
  public void An_address_changing_native_save_recovers_beside_a_closed_alias() {
    var bench = new NativeBench();
    bench.Modem.CommitModel = true;
    bench.Modem.Settings.Add(new Nv5FakeModem.Setting("MAV_SYS_ID", 10));
    var ethernet = new NvModemLink(new MAVLinkInterface(), "modem Ethernet");
    bench.Transport.Links.Add(ethernet);
    bench.Passport(NativePassport());
    bench.ViewModel.HandlePacket(ethernet, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(), 10, 11));
    // The Ethernet connection closes; its record stays behind as an earlier route.
    bench.Transport.Links.Remove(ethernet);
    Assert.Equal(2, bench.ViewModel.Devices.Count);
    NvModemDeviceChoice saving = bench.ViewModel.SelectedDevice!;
    Assert.Same(bench.Link, saving.State.Key.Link);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    bool? finished = null;
    bench.ViewModel.NativeSettings.ConfigurationFinished += success => finished = success;
    Assert.True(bench.ViewModel.NativeSettings.SetValue("MAV_SYS_ID", "12"));
    bench.ViewModel.NativeSettings.Save();
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.StatusText.Contains(
        "Waiting for restart", StringComparison.Ordinal)));
    // Its telemetry is still heard at the old address just before the restart.
    bench.ViewModel.HandlePacket(bench.Link, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 }, 10, 11));

    // The modem restarts on the same USB connection under its new system ID, well within the
    // online window of its old address.
    bench.Modem.Reboot();
    bench.Modem.Peer = bench.Modem.Peer with { System = 12 };
    bench.ViewModel.HandlePacket(bench.Link, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(timeBootMs: 40), 12, 11));

    NvModemDeviceChoice modem = Assert.Single(bench.ViewModel.Devices);
    Assert.Same(saving, modem);
    Assert.Same(modem, bench.ViewModel.SelectedDevice);
    Assert.Equal(12, modem.State.Key.SystemId);
    Assert.True(bench.Pump(() => finished != null), bench.ViewModel.NativeSettings.StatusText);
    Assert.True(finished, bench.ViewModel.NativeSettings.StatusText);
    Assert.Equal(1, bench.Modem.Commits);
  }

  [Fact]
  public void A_save_keeps_its_record_when_the_modem_returns_where_an_older_one_waits() {
    var bench = new NativeBench();
    bench.Modem.CommitModel = true;
    bench.Modem.Settings.Add(new Nv5FakeModem.Setting("MAV_SYS_ID", 10));
    var ethernet = new NvModemLink(new MAVLinkInterface(), "modem Ethernet");
    bench.Transport.Links.Add(ethernet);
    bench.ViewModel.HandlePacket(ethernet, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(), 10, 11));
    NvModemDeviceChoice saving = bench.ViewModel.SelectedDevice!;
    Assert.Same(ethernet, saving.State.Key.Link);
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    // The same modem is also heard once over USB, then that record stays quiet.
    bench.Passport(NativePassport());
    Assert.Equal(2, bench.ViewModel.Devices.Count);
    bench.Pump(() => false, steps: 60);
    bool? finished = null;
    bench.ViewModel.NativeSettings.ConfigurationFinished += success => finished = success;
    Assert.True(bench.ViewModel.NativeSettings.SetValue("MAV_SYS_ID", "12"));
    bench.ViewModel.NativeSettings.Save();
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.StatusText.Contains(
        "Waiting for restart", StringComparison.Ordinal)));

    // Ethernet drops during the restart and the modem returns over USB alone.
    bench.Transport.Links.Remove(ethernet);
    bench.Modem.Reboot();
    bench.Modem.Peer = bench.Modem.Peer with { System = 12 };
    bench.ViewModel.HandlePacket(bench.Link, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(timeBootMs: 40), 12, 11));

    // The saving, selected record moves; the older USB record is retired.
    NvModemDeviceChoice modem = Assert.Single(bench.ViewModel.Devices);
    Assert.Same(saving, modem);
    Assert.Same(modem, bench.ViewModel.SelectedDevice);
    Assert.Same(bench.Link, modem.State.Key.Link);
    Assert.True(bench.Pump(() => finished != null), bench.ViewModel.NativeSettings.StatusText);
    Assert.True(finished, bench.ViewModel.NativeSettings.StatusText);
    Assert.Equal(1, bench.Modem.Commits);
  }

  [Fact]
  public void An_unexpected_address_beside_a_closed_alias_is_still_a_live_conflict() {
    var bench = new NativeBench();
    var ethernet = new NvModemLink(new MAVLinkInterface(), "modem Ethernet");
    bench.Transport.Links.Add(ethernet);
    NvModemInfoMessage parameters = NativePassport(capabilities: NvModemCapabilities.MavlinkParameters);
    bench.Passport(parameters);
    bench.ViewModel.HandlePacket(ethernet, Packet(NvModemMessageIds.NvModemInfo, parameters,
        10, 11));
    bench.Transport.Links.Remove(ethernet);

    // The modem was just heard at 10:11 on USB when a second address there claims its UID.
    bench.ViewModel.HandlePacket(bench.Link, Packet(NvModemMessageIds.NvModemInfo, parameters,
        12, 11));

    Assert.Equal(2, bench.ViewModel.Devices.Count);
    Assert.All(bench.ViewModel.Devices, device => Assert.Equal(10, device.State.Key.SystemId));
    Assert.Contains("Two live modem endpoints", bench.ViewModel.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void One_modem_on_two_open_links_keeps_a_record_per_link() {
    var bench = new NativeBench();
    var second = new NvModemLink(new MAVLinkInterface(), "modem Ethernet");
    bench.Transport.Links.Add(second);
    bench.Passport(NativePassport());
    bench.ViewModel.HandlePacket(second, Packet(NvModemMessageIds.NvModemInfo,
        NativePassport(), 10, 11));

    Assert.Equal(2, bench.ViewModel.Devices.Count);
    Assert.Same(bench.Link, bench.ViewModel.SelectedDevice!.State.Key.Link);
  }

  [Fact]
  public void A_capable_modem_heard_before_its_passport_is_never_sent_params() {
    var bench = new NativeBench();
    bench.ViewModel.HandlePacket(bench.Link, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 }, 10, 11));
    bench.Pump(() => false, steps: 5);
    Assert.DoesNotContain(bench.Transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_request_list_t);

    bench.Passport(NativePassport());
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    bench.Pump(() => false, steps: 50);
    Assert.True(bench.ViewModel.UsesNativeSettings);
    Assert.DoesNotContain(bench.Transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_request_list_t);
  }

  [AvaloniaTheory]
  [InlineData(860)]
  [InlineData(1120)]
  public void Native_settings_replace_the_parameter_groups_and_fit_a_narrow_page(double width) {
    var bench = new NativeBench();
    bench.Passport(NativePassport());
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
          .Single(expander => expander.Name == "NativeSettingsGroup");
      Assert.True(group.IsVisible);
      Assert.DoesNotContain(window.GetVisualDescendants().OfType<Expander>(),
          expander => expander.IsVisible && expander.Header is "Mission Planner compatible "
              + "parameters" or "Radio encryption key" or "RTSP stream client");

      List<string> problems = [];
      LayoutOverflowTests.CollectGridProblems(group, problems);
      LayoutOverflowTests.CollectCollapsedInputs(group, problems);
      foreach (Button button in group.GetVisualDescendants().OfType<Button>()
          .Where(button => button.IsEffectivelyVisible)) {
        Point topLeft = button.TranslatePoint(new Point(0, 0), window) ?? default;
        if (topLeft.X + button.Bounds.Width > window.ClientSize.Width + 0.5) {
          problems.Add($"{button.Content} ends at {topLeft.X + button.Bounds.Width:F1}");
        }
      }
      Assert.True(problems.Count == 0, LayoutOverflowTests.Report(problems, width));
    } finally {
      window.Close();
    }
  }

  private static bool IsNative(object packet) =>
      packet is Nv5ConfigControlMessage or Nv5ConfigItemMessage;

  private static NvModemInfoMessage NativePassport(byte generation = 5,
      ulong capabilities = NvModemCapabilities.MavlinkParameters
          | NvModemCapabilities.ConfigurationTransactions,
      uint timeBootMs = 10_000, byte uidSeed = 0x40) {
    NvModemInfoMessage info = ModemInfo(generation, 7,
        NvModemInfoFlags.Channel1Active | NvModemInfoFlags.Channel2Active,
        channel1Role: 1, channel2Role: 2, uidSeed: uidSeed);
    info.Capabilities = capabilities;
    info.TimeBootMs = timeBootMs;
    return info;
  }

  /// <summary>The page with one link to a fake NV5 modem at 10:11 on a fake clock.</summary>
  private sealed class NativeBench : IDisposable {
    private DateTime _now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private int _answered;

    internal NativeBench() {
      Transport.Links.Add(Link);
      ViewModel = new NvModemViewModel(Transport, () => _now, startTimer: false);
    }

    internal FakeTransport Transport { get; } = new();
    internal NvModemLink Link { get; } = new(new MAVLinkInterface(), "modem USB");
    internal NvModemViewModel ViewModel { get; }
    internal Nv5FakeModem Modem { get; } = new() {
      Settings = Nv5FakeModem.Catalogue(transmitsOnRadio1: true),
      Digest = 0x5a,
    };

    internal void Passport(NvModemInfoMessage info) => ViewModel.HandlePacket(Link,
        Packet(NvModemMessageIds.NvModemInfo, info, Modem.Peer.System, Modem.Peer.Component));

    /// <summary>Answers native frames in the order the page sent them; between frames the clock
    /// advances by the page's service tick.</summary>
    internal bool Pump(Func<bool> done, int steps = 20000) {
      for (int step = 0; step < steps && !done(); step++) {
        if (_answered < Transport.Sent.Count) {
          FakeTransport.SentPacket sent = Transport.Sent[_answered++];
          uint? id = sent.Packet switch {
            Nv5ConfigControlMessage => NvModemMessageIds.Nv5ConfigControl,
            Nv5ConfigItemMessage => NvModemMessageIds.Nv5ConfigItem,
            _ => null,
          };
          if (id == null) {
            continue;
          }
          foreach (MAVLink.MAVLinkMessage reply in Modem.Answer(
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
  }
}
