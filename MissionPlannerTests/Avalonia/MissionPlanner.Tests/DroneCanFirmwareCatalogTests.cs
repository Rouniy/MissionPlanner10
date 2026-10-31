using System.IO.Compression;
using System.Net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class DroneCanFirmwareCatalogTests {
  private static APFirmware.FirmwareInfo Info(string platform, string type, string version, string sha, string url,
      string format = "bin", string vehicle = "AP_Periph", string mavType = "CAN_PERIPHERAL", string? name = null) => new() {
    Platform = platform, MavFirmwareVersionType = type, MavFirmwareVersionStr = version, GitSha = sha, Url = new Uri(url),
    Format = format, VehicleType = vehicle, MavType = mavType, FirmwareName = name,
  };

  [Theory]
  [InlineData("org.ardupilot.MatekL431-Periph", "MatekL431-Periph", true)]
  [InlineData("org.ardupilot.MatekL431-Periph", "matekl431-periph", true)]
  [InlineData("org.ardupilot.MatekL431-Periph", "L431-Periph", false)]
  [InlineData("org.ardupilot.MatekL431-Periph", "Periph", false)]
  [InlineData("org.ardupilot.MatekL431-Periph", "MatekL431-Airspeed", false)]
  [InlineData("MatekL431-Periph", "MatekL431-Periph", true)]
  [InlineData("", "MatekL431-Periph", false)]
  [InlineData("org.ardupilot.", "MatekL431-Periph", false)]
  public void Platform_matches_only_the_whole_last_segment_of_the_node_name(string node, string platform, bool expected) =>
      Assert.Equal(expected, DroneCanFirmwareCatalog.PlatformMatches(node, platform));

  [Fact]
  public void Match_filters_the_manifest_orders_channels_and_marks_the_running_commit() {
    var manifest = new[] {
      Info("MatekL431-Periph", "DEV", "V4.8.0-dev", "5b36047100a767093e9a99a76a1997cb1aa91e23", "https://firmware.ardupilot.org/AP_Periph/latest/MatekL431-Periph/AP_Periph.bin"),
      Info("MatekL431-Periph", "BETA", "4.7.2", "fb773567896014da007bca58358704d4a0d8516f", "https://firmware.ardupilot.org/AP_Periph/beta/MatekL431-Periph/AP_Periph.bin"),
      Info("MatekL431-Periph", "OFFICIAL", "4.7.2", "fb773567896014da007bca58358704d4a0d8516f", "https://firmware.ardupilot.org/AP_Periph/stable/MatekL431-Periph/AP_Periph.bin"),
      Info("MatekL431-Periph", "OFFICIAL", "4.7.2", "fb773567896014da007bca58358704d4a0d8516f", "https://firmware.ardupilot.org/AP_Periph/stable/MatekL431-Periph/AP_Periph.bin"),
      Info("MatekL431-Airspeed", "OFFICIAL", "4.7.2", "fb77356789", "https://firmware.ardupilot.org/AP_Periph/stable/MatekL431-Airspeed/AP_Periph.bin"),
      Info("MatekL431-Periph", "OFFICIAL", "4.7.2", "fb77356789", "https://firmware.ardupilot.org/AP_Periph/stable/MatekL431-Periph/AP_Periph.apj", format: "apj"),
      Info("MatekL431-Periph", "OFFICIAL", "4.7.2", "fb77356789", "https://firmware.ardupilot.org/Copter/stable/MatekL431-Periph/arducopter.bin", vehicle: "Copter", mavType: "Copter"),
      Info("MatekL431-Periph", "OFFICIAL", "4.7.1", "aaaaaaaa11", "https://example.org/AP_Periph/MatekL431-Periph/AP_Periph.zip", format: "zip", name: "AP_Periph.bin"),
    };
    var options = DroneCanFirmwareCatalog.Match(manifest, "org.ardupilot.MatekL431-Periph", 0xfb773567);
    Assert.Equal(new[] { "stable 4.7.2", "stable 4.7.1", "beta 4.7.2", "latest (dev) V4.8.0-dev" },
        options.Select(o => o.Channel + " " + o.Version).ToArray());
    Assert.True(options[0].Installed);
    Assert.False(options[1].Installed);
    Assert.True(options[2].Installed);
    Assert.Equal("AP_Periph.bin", options[1].ArchiveEntry);
    Assert.Null(options[0].ArchiveEntry);
    Assert.Equal("firmware.ardupilot.org", options[0].Source);
    Assert.Contains("fb773567", options[0].Label);
    Assert.Contains("installed", options[0].Label);
    Assert.Empty(DroneCanFirmwareCatalog.Match(manifest, "?", 0));
    Assert.Empty(DroneCanFirmwareCatalog.Match(null, "org.ardupilot.MatekL431-Periph", 0));
    Assert.DoesNotContain(DroneCanFirmwareCatalog.Match(manifest, "org.ardupilot.MatekL431-Periph", 0), o => o.Installed);
  }

  [Fact]
  public async Task Download_saves_a_raw_image_unpacks_zip_entries_and_refuses_oversized_files() {
    byte[] image = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
    using var zipBytes = new MemoryStream();
    using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true)) {
      using (var entry = zip.CreateEntry("AP_Periph.bin").Open()) { entry.Write(image); }
      using (var readme = zip.CreateEntry("README.txt").Open()) { readme.Write("notes"u8); }
    }
    var handler = new FakeHandler {
      ["https://example.org/fw/AP_Periph.bin"] = image,
      ["https://example.org/fw/AP_Periph.zip"] = zipBytes.ToArray(),
      ["https://example.org/fw/huge.bin"] = new byte[1],
    };
    handler.ContentLengthOverride["https://example.org/fw/huge.bin"] = DroneCanFirmwareCatalog.MaxFirmwareBytes + 1;
    using var http = new HttpClient(handler);
    string directory = Path.Combine(Path.GetTempPath(), "mp-dronecan-fw-test-" + Guid.NewGuid().ToString("N"));
    try {
      var progress = new List<double>();
      var raw = new DroneCanFirmwareOption("stable", "4.7.2", "fb77", new Uri("https://example.org/fw/AP_Periph.bin"), null, "example.org");
      string file = await DroneCanFirmwareCatalog.DownloadAsync(http, raw, directory, new SynchronousProgress(progress), CancellationToken.None);
      Assert.Equal(image, await File.ReadAllBytesAsync(file));
      Assert.Contains(100, progress);

      var zipped = new DroneCanFirmwareOption("stable", "4.7.1", "aaaa", new Uri("https://example.org/fw/AP_Periph.zip"), "AP_Periph.bin", "example.org");
      string unpacked = await DroneCanFirmwareCatalog.DownloadAsync(http, zipped, directory, null, CancellationToken.None);
      Assert.EndsWith(".bin", unpacked);
      Assert.Equal(image, await File.ReadAllBytesAsync(unpacked));

      var unnamed = zipped with { ArchiveEntry = null };
      Assert.Equal(image, await File.ReadAllBytesAsync(await DroneCanFirmwareCatalog.DownloadAsync(http, unnamed, directory, null, CancellationToken.None)));

      var huge = raw with { Url = new Uri("https://example.org/fw/huge.bin") };
      await Assert.ThrowsAsync<InvalidOperationException>(() => DroneCanFirmwareCatalog.DownloadAsync(http, huge, directory, null, CancellationToken.None));

      var missing = raw with { Url = new Uri("https://example.org/fw/none.bin") };
      await Assert.ThrowsAsync<HttpRequestException>(() => DroneCanFirmwareCatalog.DownloadAsync(http, missing, directory, null, CancellationToken.None));
    } finally {
      try { Directory.Delete(directory, true); } catch { }
    }
  }

  [Fact]
  public async Task Published_dates_come_from_last_modified_and_failures_are_ignored() {
    var handler = new FakeHandler { ["https://example.org/fw/AP_Periph.bin"] = new byte[] { 1 } };
    handler.LastModified["https://example.org/fw/AP_Periph.bin"] = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    using var http = new HttpClient(handler);
    var dated = new DroneCanFirmwareOption("stable", "4.7.2", "fb77", new Uri("https://example.org/fw/AP_Periph.bin"), null, "example.org");
    var undated = dated with { Url = new Uri("https://example.org/fw/none.bin") };
    await DroneCanFirmwareCatalog.FillPublishedDatesAsync(http, new[] { dated, undated }, CancellationToken.None);
    Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc), dated.PublishedUtc);
    Assert.Null(undated.PublishedUtc);
    Assert.Contains("2026-03-04", dated.Label);
    Assert.Equal(1, handler.HeadRequests);
  }

  private sealed class SynchronousProgress(List<double> sink) : IProgress<double> {
    public void Report(double value) { lock (sink) { sink.Add(value); } }
  }

  private sealed class FakeHandler : HttpMessageHandler {
    private readonly Dictionary<string, byte[]> _files = new();
    internal readonly Dictionary<string, long> ContentLengthOverride = new();
    internal readonly Dictionary<string, DateTimeOffset> LastModified = new();
    internal int HeadRequests;
    internal byte[] this[string url] { set => _files[url] = value; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      string url = request.RequestUri!.ToString();
      if (!_files.TryGetValue(url, out var bytes)) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
      if (request.Method == HttpMethod.Head) { HeadRequests++; }
      var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes) };
      response.Content.Headers.ContentLength = ContentLengthOverride.TryGetValue(url, out long length) ? length : bytes.Length;
      if (LastModified.TryGetValue(url, out var modified)) { response.Content.Headers.LastModified = modified; }
      return Task.FromResult(response);
    }
  }
}
