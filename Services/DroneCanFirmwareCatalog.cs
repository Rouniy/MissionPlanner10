using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.Services;

/// <summary>One downloadable AP_Periph build for a DroneCAN node, as the operator picks it.</summary>
public sealed record DroneCanFirmwareOption(
    string Channel, string Version, string GitSha, Uri Url, string? ArchiveEntry, string Source) {
  /// <summary>HTTP Last-Modified of the file; null until <see cref="DroneCanFirmwareCatalog.FillPublishedDatesAsync"/> ran or when the server does not say.</summary>
  public DateTime? PublishedUtc { get; set; }

  /// <summary>True when the manifest commit starts with the node's reported vcs_commit.</summary>
  public bool Installed { get; init; }

  public string ShortSha => GitSha.Length > 8 ? GitSha.Substring(0, 8) : GitSha;

  public string Label {
    get {
      string date = PublishedUtc is DateTime utc ? " · " + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
      string installed = Installed ? " · installed" : "";
      return $"{Channel} {Version}{date} · {ShortSha} · {Source}{installed}";
    }
  }
}

/// <summary>
/// Finds AP_Periph firmware for a DroneCAN node by its reported name. ArduPilot's manifest lists
/// CAN peripherals by hwdef platform (for example <c>MatekL431-Periph</c>), and AP_Periph reports
/// its name as <c>org.ardupilot.&lt;platform&gt;</c>, so the last dotted segment identifies the board.
/// </summary>
internal static class DroneCanFirmwareCatalog {
  internal const long MaxFirmwareBytes = 64L * 1024 * 1024;

  internal static string? PlatformOf(string? nodeName) {
    if (string.IsNullOrWhiteSpace(nodeName)) { return null; }
    string name = nodeName.Trim();
    int dot = name.LastIndexOf('.');
    string platform = dot >= 0 ? name.Substring(dot + 1) : name;
    return platform.Length == 0 ? null : platform;
  }

  /// <summary>
  /// Matches by whole platform name only: <c>org.ardupilot.MatekL431-Periph</c> must not pick up
  /// <c>L431-Periph</c> or <c>Periph</c> builds for other boards the way a plain EndsWith would.
  /// </summary>
  internal static bool PlatformMatches(string? nodeName, string? platform) {
    string? own = PlatformOf(nodeName);
    return own != null && !string.IsNullOrWhiteSpace(platform)
        && string.Equals(own, platform.Trim(), StringComparison.OrdinalIgnoreCase);
  }

  internal static string ChannelName(string? manifestType) => manifestType?.ToUpperInvariant() switch {
    "OFFICIAL" => "stable",
    "BETA" => "beta",
    "DEV" => "latest (dev)",
    _ => (manifestType ?? "unknown").ToLowerInvariant(),
  };

  private static int ChannelRank(string channel) => channel switch { "stable" => 0, "beta" => 1, "latest (dev)" => 2, _ => 3 };

  /// <summary>Builds the option list for one node from a manifest; newest stable first, one entry per channel/version/url.</summary>
  internal static List<DroneCanFirmwareOption> Match(
      IEnumerable<APFirmware.FirmwareInfo>? manifest, string? nodeName, uint vcsCommit) {
    var result = new List<DroneCanFirmwareOption>();
    if (manifest == null || PlatformOf(nodeName) == null) { return result; }
    string commit = vcsCommit == 0 ? "" : vcsCommit.ToString("x8", CultureInfo.InvariantCulture);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var info in manifest) {
      if (info?.Url == null || !PlatformMatches(nodeName, info.Platform)) { continue; }
      if (!string.Equals(info.VehicleType, "AP_Periph", StringComparison.OrdinalIgnoreCase)
          || !string.Equals(info.MavType, "CAN_PERIPHERAL", StringComparison.OrdinalIgnoreCase)) { continue; }
      string format = info.Format?.ToLowerInvariant() ?? "";
      if (format != "bin" && format != "zip") { continue; }
      string version = string.IsNullOrWhiteSpace(info.MavFirmwareVersionStr)
          ? info.MavFirmwareVersion?.ToString() ?? "?" : info.MavFirmwareVersionStr.Trim();
      string channel = ChannelName(info.MavFirmwareVersionType);
      if (!seen.Add(channel + "|" + version + "|" + info.Url)) { continue; }
      string sha = info.GitSha ?? "";
      result.Add(new DroneCanFirmwareOption(channel, version, sha, info.Url,
          format == "zip" ? info.FirmwareName : null, info.Url.Host) {
        Installed = commit.Length != 0 && sha.StartsWith(commit, StringComparison.OrdinalIgnoreCase),
      });
    }
    return result.OrderBy(o => ChannelRank(o.Channel)).ThenByDescending(o => o.Version, StringComparer.OrdinalIgnoreCase).ToList();
  }

  /// <summary>Asks each server for the file date with a HEAD request; failures leave the date empty.</summary>
  internal static async Task FillPublishedDatesAsync(HttpClient http, IEnumerable<DroneCanFirmwareOption> options, CancellationToken ct) {
    foreach (var option in options) {
      try {
        using var request = new HttpRequestMessage(HttpMethod.Head, option.Url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.LastModified is DateTimeOffset modified) {
          option.PublishedUtc = modified.UtcDateTime;
        }
      } catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) {
      }
    }
  }

  /// <summary>
  /// Downloads one option into <paramref name="directory"/> and returns the path of the raw image.
  /// A zip is unpacked to its named entry, or to its only <c>.bin</c> when the manifest names none.
  /// </summary>
  internal static async Task<string> DownloadAsync(HttpClient http, DroneCanFirmwareOption option,
      string directory, IProgress<double>? progress, CancellationToken ct) {
    Directory.CreateDirectory(directory);
    bool zip = option.Url.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + (zip ? ".zip" : ".bin"));
    using (var response = await http.GetAsync(option.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false)) {
      response.EnsureSuccessStatusCode();
      long? total = response.Content.Headers.ContentLength;
      if (total > MaxFirmwareBytes) { throw new InvalidOperationException("The firmware file is larger than the 64 MB limit."); }
      await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      await using var destination = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
      var buffer = new byte[1 << 16];
      long done = 0; int read;
      while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
        done += read;
        if (done > MaxFirmwareBytes) { throw new InvalidOperationException("The firmware file is larger than the 64 MB limit."); }
        await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        if (total > 0) { progress?.Report(100.0 * done / total.Value); }
      }
    }
    if (!zip) { return file; }
    using var archive = ZipFile.OpenRead(file);
    ZipArchiveEntry? entry = option.ArchiveEntry != null
        ? archive.Entries.FirstOrDefault(e => string.Equals(e.Name, option.ArchiveEntry, StringComparison.OrdinalIgnoreCase))
        : null;
    if (entry == null) {
      var bins = archive.Entries.Where(e => e.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
      if (bins.Count != 1) { throw new InvalidOperationException("The firmware archive does not contain exactly one .bin image; choose the file manually."); }
      entry = bins[0];
    }
    if (entry.Length > MaxFirmwareBytes) { throw new InvalidOperationException("The firmware image is larger than the 64 MB limit."); }
    string image = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
    entry.ExtractToFile(image, false);
    return image;
  }
}
