using System.Net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class AutopilotFirmwareCatalogTests {
  private static APFirmware.FirmwareInfo Info(string platform, long boardId, string type, string version, string sha, string url,
      string mavType = "GROUND_ROVER", string vehicle = "Rover", string format = "apj") => new() {
    Platform = platform, BoardId = boardId, MavFirmwareVersionType = type, MavFirmwareVersionStr = version,
    MavFirmwareVersion = Version.Parse(version.TrimStart('V').Split('-')[0]), GitSha = sha, Url = new Uri(url),
    Format = format, VehicleType = vehicle, MavType = mavType,
  };

  [Theory]
  [InlineData(0x03f20000u, 1010)]
  [InlineData(0x00090000u, 9)]
  [InlineData(0u, null)]
  [InlineData(0x0000ffffu, null)]
  public void Board_id_is_the_upper_half_of_autopilot_version_board_version(uint boardVersion, int? expected) =>
      Assert.Equal(expected, AutopilotFirmwareCatalog.BoardIdFromBoardVersion(boardVersion));

  [Theory]
  [InlineData("CUAV-X7 002A002C 3033510C 33333437", "CUAV-X7")]
  [InlineData("  CubeOrange 00280034 ", "CubeOrange")]
  [InlineData("", null)]
  [InlineData(null, null)]
  public void Board_name_is_the_first_banner_token(string? serial, string? expected) =>
      Assert.Equal(expected, AutopilotFirmwareCatalog.BoardNameFromSerialString(serial));

  [Theory]
  [InlineData("ArduRover V4.7.0-dev (eb9a1b3c)", 0xeb9a1b3cu)]
  [InlineData("ArduCopter V4.5.7 (52bed8d5)", 0x52bed8d5u)]
  [InlineData("ArduPlane V4.6.0", 0u)]
  [InlineData("weird (zz)", 0u)]
  [InlineData(null, 0u)]
  public void Running_commit_is_parsed_from_the_version_string(string? version, uint expected) =>
      Assert.Equal(expected, AutopilotFirmwareCatalog.RunningCommit(version));

  [Theory]
  [InlineData("OFFICIAL", "stable")]
  [InlineData("STABLE-4.7.1", "stable")]
  [InlineData("BETA", "beta")]
  [InlineData("DEV", "latest (dev)")]
  [InlineData("", "unknown")]
  public void Channel_names_follow_the_manifest_types(string type, string expected) =>
      Assert.Equal(expected, AutopilotFirmwareCatalog.ChannelName(type));

  [Fact]
  public void Match_keeps_builds_for_the_board_id_prefers_its_hwdef_and_orders_stable_versions_first() {
    var manifest = new[] {
      Info("CUAV-X7-bdshot", 1010, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Rover/stable/CUAV-X7-bdshot/ardurover.apj"),
      Info("CUAV-X7", 1010, "DEV", "V4.8.0-dev", "5b36047100a76709", "https://firmware.ardupilot.org/Rover/latest/CUAV-X7/ardurover.apj"),
      Info("CUAV-X7", 1010, "STABLE-4.7.1", "V4.7.1", "dbe7921600000000", "https://firmware.ardupilot.org/Rover/stable-4.7.1/CUAV-X7/ardurover.apj"),
      Info("CUAV-X7", 1010, "STABLE-4.7.2", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Rover/stable-4.7.2/CUAV-X7/ardurover.apj"),
      Info("CUAV-X7", 1010, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Rover/stable/CUAV-X7/ardurover.apj"),
      Info("CUAV-X7", 1010, "BETA", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Rover/beta/CUAV-X7/ardurover.apj"),
      Info("CUAV-X7", 1010, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Copter/stable/CUAV-X7/arducopter.apj", mavType: "Copter", vehicle: "Copter"),
      Info("CUAV-X7", 1010, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Rover/stable/CUAV-X7/ardurover_with_bl.hex", format: "hex"),
      Info("CubeOrange", 140, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Rover/stable/CubeOrange/ardurover.apj"),
    };
    var options = AutopilotFirmwareCatalog.Match(manifest, "ground_rover", 1010, "CUAV-X7", "ArduRover V4.7.2 (fb773567)");
    Assert.Equal(new[] {
      "stable V4.7.2 CUAV-X7", "stable V4.7.1 CUAV-X7", "beta V4.7.2 CUAV-X7", "latest (dev) V4.8.0-dev CUAV-X7",
      "stable V4.7.2 CUAV-X7-bdshot",
    }, options.Select(o => $"{o.Channel} {o.Version} {o.Platform}").ToArray());
    Assert.True(options[0].Installed);
    Assert.False(options[1].Installed);
    Assert.Contains("stable-4.7.2", options[0].Url.ToString());
    Assert.True(options[0].PreferredPlatform);
    Assert.False(options[4].PreferredPlatform);
    Assert.Contains("other hwdef variant", options[4].Label);
    Assert.Contains("installed", options[0].Label);
    Assert.Equal("Rover", options[0].VehicleType);
    Assert.Same(options[0], AutopilotFirmwareCatalog.Recommended(options));

    // Unknown board name: every variant counts as preferred and stays in channel/version order.
    var unnamed = AutopilotFirmwareCatalog.Match(manifest, "GROUND_ROVER", 1010, null, null);
    Assert.All(unnamed, o => Assert.True(o.PreferredPlatform));
    Assert.DoesNotContain(unnamed, o => o.Installed);
    Assert.Equal("stable", unnamed[0].Channel);

    Assert.Empty(AutopilotFirmwareCatalog.Match(manifest, "GROUND_ROVER", 999, null, null));
    Assert.Empty(AutopilotFirmwareCatalog.Match(manifest, "GROUND_ROVER", null, null, null));
    Assert.Empty(AutopilotFirmwareCatalog.Match(null, "GROUND_ROVER", 1010, null, null));
  }

  [Fact]
  public void A_release_commit_shared_by_every_vehicle_is_installed_only_for_the_running_vehicle() {
    var manifest = new[] {
      Info("CUAV-X7", 1010, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Plane/stable/CUAV-X7/arduplane.apj", mavType: "FIXED_WING", vehicle: "Plane"),
      Info("CUAV-X7", 1010, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Rover/stable/CUAV-X7/ardurover.apj"),
      Info("CUAV-X7", 1010, "OFFICIAL", "V4.7.2", "fb773567896014da", "https://firmware.ardupilot.org/Copter/stable/CUAV-X7/arducopter-heli.apj", mavType: "HELICOPTER", vehicle: "Copter"),
    };
    const string rover = "ArduRover V4.7.2 (fb773567)";
    Assert.False(Assert.Single(AutopilotFirmwareCatalog.Match(manifest, "FIXED_WING", 1010, "CUAV-X7", rover)).Installed);
    Assert.True(Assert.Single(AutopilotFirmwareCatalog.Match(manifest, "GROUND_ROVER", 1010, "CUAV-X7", rover)).Installed);
    Assert.True(Assert.Single(AutopilotFirmwareCatalog.Match(manifest, "HELICOPTER", 1010, "CUAV-X7", "ArduCopter V4.7.2 (fb773567)")).Installed);
    Assert.Equal("ArduRover", AutopilotFirmwareCatalog.RunningFirmwareName(rover));
    Assert.True(AutopilotFirmwareCatalog.RunningVehicleMatches("AntennaTracker V4.7.2 (fb773567)", "AntennaTracker"));
    Assert.False(AutopilotFirmwareCatalog.RunningVehicleMatches(rover, "Plane"));
    Assert.False(AutopilotFirmwareCatalog.RunningVehicleMatches(null, "Rover"));

    Assert.Equal("", MissionPlanner.ViewModels.Setup.InstallFirmwareViewModel.VehicleChangeWarning(rover, "Rover"));
    Assert.Equal("", MissionPlanner.ViewModels.Setup.InstallFirmwareViewModel.VehicleChangeWarning(null, "Plane"));
    Assert.Contains("The board runs ArduRover; installing Plane changes the vehicle type",
        MissionPlanner.ViewModels.Setup.InstallFirmwareViewModel.VehicleChangeWarning(rover, "Plane"));
  }

  [Fact]
  public void Cube_black_accepts_fmuv3_images_but_no_other_cross_board_match() {
    Assert.True(AutopilotFirmwareCatalog.BoardMatches(9, 33));
    Assert.True(AutopilotFirmwareCatalog.BoardMatches(1010, 1010));
    Assert.False(AutopilotFirmwareCatalog.BoardMatches(33, 9));
    Assert.False(AutopilotFirmwareCatalog.BoardMatches(1010, 140));
  }

  [Theory]
  [InlineData("/dev/serial/by-id/usb-ArduPilot_CUAV-X7-BL_2C002A000C51333037343333-if00", true)]
  [InlineData("/dev/serial/by-id/usb-3D_Robotics_PX4_BL_FMU_v2.x_0-if00", true)]
  [InlineData("/dev/serial/by-id/usb-ArduPilot_CUAV-X7_2C002A000C51333037343333-if00", false)]
  [InlineData("/dev/ttyACM0", false)]
  [InlineData("COM7", false)]
  [InlineData("", false)]
  public void Bootloader_ports_are_recognised_by_their_usb_name(string port, bool expected) =>
      Assert.Equal(expected, AutopilotFirmwareCatalog.LooksLikeBootloaderPort(port));

  [Fact]
  public void Port_scan_order_dedupes_devices_and_puts_bootloaders_first() {
    var names = new[] {
      "/dev/ttyACM1", "/dev/ttyACM0", "/dev/ttyUSB0",
      "/dev/serial/by-id/usb-ArduPilot_CUAV-X7-BL_2C00-if00",
      "/dev/serial/by-id/usb-FTDI_FT232R_USB_UART_A1-if00-port0",
    };
    string Resolve(string p) => p switch {
      "/dev/serial/by-id/usb-ArduPilot_CUAV-X7-BL_2C00-if00" => "/dev/ttyACM0",
      "/dev/serial/by-id/usb-FTDI_FT232R_USB_UART_A1-if00-port0" => "/dev/ttyUSB0",
      _ => p,
    };
    var ordered = AutopilotFirmwareCatalog.OrderPortsForBootloaderScan(names, Resolve);
    Assert.Equal(new[] {
      "/dev/serial/by-id/usb-ArduPilot_CUAV-X7-BL_2C00-if00",
      "/dev/serial/by-id/usb-FTDI_FT232R_USB_UART_A1-if00-port0",
      "/dev/ttyACM1",
    }, ordered);
  }

  [Fact]
  public async Task Download_and_dates_use_the_option_url() {
    byte[] apj = "{\"board_id\":1010,\"image_size\":0,\"image\":\"\"}"u8.ToArray();
    var handler = new FakeHandler { ["https://example.org/Rover/stable/CUAV-X7/ardurover.apj"] = apj };
    handler.LastModified["https://example.org/Rover/stable/CUAV-X7/ardurover.apj"] = new DateTimeOffset(2026, 10, 13, 1, 2, 3, TimeSpan.Zero);
    handler.ContentLengthOverride["https://example.org/huge.apj"] = AutopilotFirmwareCatalog.MaxFirmwareBytes + 1;
    handler["https://example.org/huge.apj"] = new byte[1];
    using var http = new HttpClient(handler);
    var option = new AutopilotFirmwareOption("stable", "V4.7.2", "fb773567", new Uri("https://example.org/Rover/stable/CUAV-X7/ardurover.apj"),
        "CUAV-X7", 1010, "Rover", "GROUND_ROVER");
    var missing = option with { Url = new Uri("https://example.org/none.apj") };
    await AutopilotFirmwareCatalog.FillPublishedDatesAsync(http, new[] { option, missing }, CancellationToken.None);
    Assert.Equal(new DateTime(2026, 10, 13, 1, 2, 3, DateTimeKind.Utc), option.PublishedUtc);
    Assert.Null(missing.PublishedUtc);
    Assert.Contains("2026-10-13", option.Label);

    string directory = Path.Combine(Path.GetTempPath(), "mp-autopilot-fw-test-" + Guid.NewGuid().ToString("N"));
    try {
      var progress = new List<double>();
      string file = await AutopilotFirmwareCatalog.DownloadAsync(http, option, directory, new ListProgress(progress), CancellationToken.None);
      Assert.EndsWith(".apj", file);
      Assert.Equal(apj, await File.ReadAllBytesAsync(file));
      Assert.Contains(100, progress);
      await Assert.ThrowsAsync<HttpRequestException>(() => AutopilotFirmwareCatalog.DownloadAsync(http, missing, directory, null, CancellationToken.None));
      var huge = option with { Url = new Uri("https://example.org/huge.apj") };
      await Assert.ThrowsAsync<InvalidOperationException>(() => AutopilotFirmwareCatalog.DownloadAsync(http, huge, directory, null, CancellationToken.None));
    } finally {
      try { Directory.Delete(directory, true); } catch { }
    }
  }

  private sealed class ListProgress(List<double> sink) : IProgress<double> {
    public void Report(double value) { lock (sink) { sink.Add(value); } }
  }

  private sealed class FakeHandler : HttpMessageHandler {
    private readonly Dictionary<string, byte[]> _files = new();
    internal readonly Dictionary<string, long> ContentLengthOverride = new();
    internal readonly Dictionary<string, DateTimeOffset> LastModified = new();
    internal byte[] this[string url] { set => _files[url] = value; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      string url = request.RequestUri!.ToString();
      if (!_files.TryGetValue(url, out var bytes)) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
      var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes) };
      response.Content.Headers.ContentLength = ContentLengthOverride.TryGetValue(url, out long length) ? length : bytes.Length;
      if (LastModified.TryGetValue(url, out var modified)) { response.Content.Headers.LastModified = modified; }
      return Task.FromResult(response);
    }
  }
}
