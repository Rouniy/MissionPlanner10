using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.Services;

/// <summary>One downloadable ArduPilot build for a flight controller, as the operator picks it.</summary>
public sealed record AutopilotFirmwareOption(
    string Channel, string Version, string GitSha, Uri Url, string Platform, long BoardId,
    string VehicleType, string MavType) {
  /// <summary>HTTP Last-Modified of the file; null until the dates were filled or when the server does not say.</summary>
  public DateTime? PublishedUtc { get; set; }

  /// <summary>True when the manifest commit starts with the commit the vehicle reports as running.</summary>
  public bool Installed { get; init; }

  /// <summary>True when the build's hwdef platform equals the name the board reports (CUAV-X7 vs CUAV-X7-bdshot).</summary>
  public bool PreferredPlatform { get; init; }

  public string ShortSha => GitSha.Length > 8 ? GitSha.Substring(0, 8) : GitSha;

  public string Title => $"{Channel} {Version} · {Platform}" + (Installed ? "  (installed)" : "");

  public string Label {
    get {
      string date = PublishedUtc is DateTime utc ? " · " + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
      string installed = Installed ? " · installed" : "";
      string platform = PreferredPlatform ? "" : " · other hwdef variant";
      return $"{VehicleType} {Version} · {Channel}{date} · {ShortSha} · {Url.Host}{installed}{platform}";
    }
  }
}

/// <summary>
/// Finds ArduPilot builds for a flight controller by its APJ board id. The manifest lists every
/// hwdef with its <c>board_id</c>; a connected vehicle reports that id in the upper 16 bits of
/// AUTOPILOT_VERSION.board_version, and the bootloader reports it as <c>board_type</c>.
/// </summary>
internal static class AutopilotFirmwareCatalog {
  internal const long MaxFirmwareBytes = 64L * 1024 * 1024;

  /// <summary>APJ board id from AUTOPILOT_VERSION.board_version, or null when the vehicle did not say.</summary>
  internal static int? BoardIdFromBoardVersion(uint boardVersion) {
    uint id = boardVersion >> 16;
    return id == 0 ? null : (int)id;
  }

  /// <summary>Board name from the startup banner line "CUAV-X7 002A002C 3033510C 33333437".</summary>
  internal static string? BoardNameFromSerialString(string? serialString) {
    if (string.IsNullOrWhiteSpace(serialString)) { return null; }
    string first = serialString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    return first.Length == 0 ? null : first;
  }

  /// <summary>Git commit from a version string such as "ArduRover V4.7.0-dev (eb9a1b3c)"; 0 when absent.</summary>
  internal static uint RunningCommit(string? versionString) {
    if (string.IsNullOrWhiteSpace(versionString)) { return 0; }
    int open = versionString.LastIndexOf('(');
    int close = versionString.LastIndexOf(')');
    if (open < 0 || close <= open + 1) { return 0; }
    string hex = versionString.Substring(open + 1, close - open - 1).Trim();
    return hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint commit) ? commit : 0;
  }

  /// <summary>Firmware family from a version string: "ArduRover V4.7.2 (fb773567)" gives "ArduRover".</summary>
  internal static string? RunningFirmwareName(string? versionString) {
    if (string.IsNullOrWhiteSpace(versionString)) { return null; }
    string first = versionString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    return first.Length == 0 ? null : first;
  }

  /// <summary>
  /// True when the running firmware belongs to the manifest vehicle type: "ArduRover" to Rover,
  /// "ArduCopter" to Copter (heli builds share it), "AntennaTracker" to AntennaTracker. A release
  /// commit is the same for every vehicle, so the commit alone cannot tell them apart.
  /// </summary>
  internal static bool RunningVehicleMatches(string? versionString, string? vehicleType) {
    string? name = RunningFirmwareName(versionString);
    return name != null && !string.IsNullOrWhiteSpace(vehicleType)
        && name.Contains(vehicleType.Trim(), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>The bootloader accepts an fmuv3 (9) image on a Cube Black (33); nothing else crosses ids.</summary>
  internal static bool BoardMatches(long firmwareBoardId, int boardId) =>
      firmwareBoardId == boardId || (boardId == 33 && firmwareBoardId == 9);

  internal static string ChannelName(string? manifestType) {
    string type = manifestType?.Trim().ToUpperInvariant() ?? "";
    return type switch {
      "OFFICIAL" => "stable",
      "BETA" => "beta",
      "DEV" => "latest (dev)",
      _ when type.StartsWith("STABLE", StringComparison.Ordinal) => "stable",
      _ => type.Length == 0 ? "unknown" : type.ToLowerInvariant(),
    };
  }

  private static int ChannelRank(string channel) => channel switch { "stable" => 0, "beta" => 1, "latest (dev)" => 2, _ => 3 };

  internal static Version VersionOf(APFirmware.FirmwareInfo info) {
    if (info.MavFirmwareVersion != null) { return info.MavFirmwareVersion; }
    string text = (info.MavFirmwareVersionStr ?? "").TrimStart('v', 'V');
    int end = 0;
    while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) { end++; }
    return Version.TryParse(text.Substring(0, end).TrimEnd('.'), out var version) ? version : new Version(0, 0);
  }

  /// <summary>
  /// Builds the option list for one vehicle type and board: every stable release the server
  /// still carries, then beta, then latest, newest first; builds for the board's own hwdef
  /// variant come before sibling variants that share the board id.
  /// </summary>
  internal static List<AutopilotFirmwareOption> Match(
      IEnumerable<APFirmware.FirmwareInfo>? manifest, string? mavType, int? boardId, string? boardName, string? runningVersionString) {
    var result = new List<AutopilotFirmwareOption>();
    if (manifest == null || string.IsNullOrWhiteSpace(mavType) || boardId == null) { return result; }
    uint runningCommit = RunningCommit(runningVersionString);
    string commit = runningCommit == 0 ? "" : runningCommit.ToString("x8", CultureInfo.InvariantCulture);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var versions = new Dictionary<AutopilotFirmwareOption, Version>();
    foreach (var info in manifest) {
      if (info?.Url == null || !string.Equals(info.MavType, mavType, StringComparison.OrdinalIgnoreCase)) { continue; }
      if (!string.Equals(info.Format, "apj", StringComparison.OrdinalIgnoreCase) || !BoardMatches(info.BoardId, boardId.Value)) { continue; }
      string versionText = string.IsNullOrWhiteSpace(info.MavFirmwareVersionStr)
          ? VersionOf(info).ToString() : info.MavFirmwareVersionStr.Trim();
      string sha = info.GitSha ?? "";
      string platform = info.Platform ?? "";
      string channel = ChannelName(info.MavFirmwareVersionType);
      // The server lists the current stable twice (stable/ and stable-X.Y.Z/); one entry per build.
      if (!seen.Add($"{channel}|{versionText}|{platform}|{sha}")) { continue; }
      var option = new AutopilotFirmwareOption(channel, versionText, sha, info.Url, platform,
          info.BoardId, info.VehicleType ?? "", info.MavType ?? "") {
        Installed = commit.Length != 0 && sha.StartsWith(commit, StringComparison.OrdinalIgnoreCase)
            && RunningVehicleMatches(runningVersionString, info.VehicleType),
        PreferredPlatform = boardName == null || string.Equals(platform, boardName, StringComparison.OrdinalIgnoreCase),
      };
      versions[option] = VersionOf(info);
      result.Add(option);
    }
    return result
        .OrderByDescending(o => o.PreferredPlatform)
        .ThenBy(o => ChannelRank(o.Channel))
        .ThenByDescending(o => versions[o])
        .ThenBy(o => o.Platform, StringComparer.OrdinalIgnoreCase)
        .ToList();
  }

  /// <summary>The newest stable build for the board's own hwdef, else the first option.</summary>
  internal static AutopilotFirmwareOption? Recommended(IReadOnlyList<AutopilotFirmwareOption> options) =>
      options.FirstOrDefault(o => o.PreferredPlatform && o.Channel == "stable") ?? options.FirstOrDefault();

  /// <summary>ArduPilot bootloaders enumerate as "&lt;board&gt;-BL"; PX4 ones as "PX4 BL FMU".</summary>
  internal static bool LooksLikeBootloaderPort(string? portName) {
    if (string.IsNullOrEmpty(portName)) { return false; }
    string name = Path.GetFileName(portName);
    return name.Contains("-BL", StringComparison.OrdinalIgnoreCase)
        || name.Contains("_BL_", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_BL", StringComparison.OrdinalIgnoreCase)
        || name.Contains("bootloader", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// One entry per physical device (Linux lists /dev/serial/by-id links and /dev/ttyACM* for the
  /// same port), bootloader-looking names first, the by-id spelling kept when both exist.
  /// </summary>
  internal static List<string> OrderPortsForBootloaderScan(IEnumerable<string> portNames, Func<string, string>? resolve = null) {
    resolve ??= ResolveDevice;
    var byDevice = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (string port in portNames.Where(p => !string.IsNullOrWhiteSpace(p))) {
      string device = resolve(port);
      if (!byDevice.TryGetValue(device, out string? existing)
          || (!existing.Contains("/by-id/", StringComparison.Ordinal) && port.Contains("/by-id/", StringComparison.Ordinal))
          || (!LooksLikeBootloaderPort(existing) && LooksLikeBootloaderPort(port))) {
        byDevice[device] = port;
      }
    }
    return byDevice.Values
        .OrderByDescending(LooksLikeBootloaderPort)
        .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToList();
  }

  internal static string ResolveDevice(string port) {
    try {
      var target = File.ResolveLinkTarget(port, true);
      return target?.FullName ?? Path.GetFullPath(port);
    } catch {
      return port;
    }
  }

  /// <summary>Asks each server for the file date with a HEAD request; failures leave the date empty.</summary>
  internal static async Task FillPublishedDatesAsync(HttpClient http, IEnumerable<AutopilotFirmwareOption> options, CancellationToken ct) {
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

  /// <summary>Downloads one option into <paramref name="directory"/> and returns the path of the .apj file.</summary>
  internal static async Task<string> DownloadAsync(HttpClient http, AutopilotFirmwareOption option,
      string directory, IProgress<double>? progress, CancellationToken ct) {
    Directory.CreateDirectory(directory);
    string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".apj");
    using var response = await http.GetAsync(option.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
    return file;
  }
}
