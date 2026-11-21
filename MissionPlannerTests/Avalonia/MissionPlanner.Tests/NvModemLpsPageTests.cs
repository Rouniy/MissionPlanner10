using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Tests;

// The LPS rows of the NV Modem page, for transaction-capable NV5 modems with LPS settings.
public partial class NvModemTests {
  [Fact]
  public void The_selected_modem_shows_its_lps_reports_once_it_confirms_they_are_current() {
    using var bench = new DistancePageBench();
    Nv5LpsTestData.AddLps(bench.A, Nv5LpsRoles.Rover, 0);
    bench.A.Set("CH2_RNG_EN", 0);
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    Nv5NativeSettingsEditor editor = bench.ViewModel.NativeSettings;
    Assert.Equal("Distance and LPS", editor.DistanceTitle);
    Assert.Equal("LPS rover", editor.DistanceRows[1].Mode);
    Assert.Equal("no LPS report yet", editor.DistanceRows[1].Reading);

    // The page asks the modem itself, on its own link, whether its reports are current.
    MAVLink.mavlink_command_long_t? challenge = null;
    Assert.True(bench.Pump(() => (challenge = LpsChallenge(bench, bench.A)) != null));
    Nv5LpsStatusMessage report = Nv5LpsTestData.Status(Nv5LpsTestData.Nonce(challenge!.Value));
    report.Uid = bench.A.Peer.Uid.ToArray();
    // The same report from another link is not this modem's.
    bench.ViewModel.HandlePacket(bench.LinkB, Nv5LpsTestData.Packet(report, bench.A.Peer.System,
        bench.A.Peer.Component));
    Assert.Equal("no LPS report yet", editor.DistanceRows[1].Reading);
    bench.ViewModel.HandlePacket(bench.LinkA, Nv5LpsTestData.Packet(report, bench.A.Peer.System,
        bench.A.Peer.Component));
    Assert.Equal("E 0.00, N 0.00, U 0.00 m ±1.00 m · raw ranges", editor.DistanceRows[1].Reading);
  }

  [Fact]
  public void A_modem_without_lps_settings_keeps_its_distance_group_and_is_never_asked() {
    using var bench = new DistancePageBench();
    bench.Passport(bench.A, NativePassport(uidSeed: 0x40));
    Assert.True(bench.Pump(() => bench.ViewModel.NativeSettings.Ready));
    bench.Pump(() => false, steps: 60);
    Assert.Equal("Distance", bench.ViewModel.NativeSettings.DistanceTitle);
    Assert.Null(LpsChallenge(bench, bench.A));
  }

  /// <summary>The latest LPS freshness challenge the page sent to a modem.</summary>
  private static MAVLink.mavlink_command_long_t? LpsChallenge(DistancePageBench bench,
      Nv5FakeModem modem) =>
      bench.Transport.Sent
          .Where(sent => ReferenceEquals(sent.Link, modem.Peer.Route)
              && sent.SystemId == modem.Peer.System && sent.ComponentId == modem.Peer.Component
              && sent.Packet is MAVLink.mavlink_command_long_t command
              && command.command == (ushort)MAVLink.MAV_CMD.REQUEST_MESSAGE
              && (uint)command.param1 == NvModemMessageIds.Nv5LpsStatus)
          .Select(sent => (MAVLink.mavlink_command_long_t?)sent.Packet)
          .LastOrDefault();
}
