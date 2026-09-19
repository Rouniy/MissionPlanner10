using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;

namespace MissionPlanner.Services;

internal sealed record ResolvedVideoSource(string Mrl, FromType FromType, string? TemporaryFile = null);

internal static partial class VideoSourceResolver {
  [GeneratedRegex(@"\bport\s*=\s*(?:\(\s*(?:int|uint)\s*\)\s*)?(\d+)", RegexOptions.IgnoreCase)]
  private static partial Regex PortRegex();

  [GeneratedRegex(@"\bencoding-name\s*=\s*(?:\(\s*string\s*\)\s*)?(H264|H265|HEVC)",
      RegexOptions.IgnoreCase)]
  private static partial Regex EncodingRegex();

  [GeneratedRegex(@"\bpayload\s*=\s*(?:\(\s*(?:int|uint)\s*\)\s*)?(\d+)",
      RegexOptions.IgnoreCase)]
  private static partial Regex PayloadRegex();

  public static ResolvedVideoSource Resolve(string source, Func<string, bool>? fileExists = null) {
    string value = (source ?? "").Trim().Trim('"');
    if (value.Length == 0) {
      throw new ArgumentException("Video source is empty.", nameof(source));
    }

    // A real Linux video device also satisfies File.Exists. Resolve its capture
    // MRL first instead of asking VLC to open the device as a media file.
    if (value.StartsWith("/dev/video", StringComparison.Ordinal)) {
      return new ResolvedVideoSource("v4l2://" + value, FromType.FromLocation);
    }

    if ((fileExists ?? File.Exists)(value)) {
      return new ResolvedVideoSource(Path.GetFullPath(value), FromType.FromPath);
    }

    if (LooksLikeGstreamerPipeline(value)) {
      return ResolveGstreamerRtp(value);
    }

    value = NormalizeListenMrl(value, "udp://");
    value = NormalizeListenMrl(value, "rtp://");

    bool libVlcListenMrl = value.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
                           || value.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase);
    if (!libVlcListenMrl
        && !Uri.TryCreate(value, UriKind.Absolute, out _)
        && !value.StartsWith("dshow://", StringComparison.OrdinalIgnoreCase)
        && !value.StartsWith("screen://", StringComparison.OrdinalIgnoreCase)
        && !value.StartsWith("avcapture://", StringComparison.OrdinalIgnoreCase)
        && !value.StartsWith("v4l2://", StringComparison.OrdinalIgnoreCase)) {
      throw new FormatException(
          "Unsupported video source. Use a URL/MRL such as rtsp://host/path, "
          + "udp://@:5600, rtp://@:5600, v4l2:///dev/video0, or an RTP GStreamer pipeline.");
    }

    return new ResolvedVideoSource(value, FromType.FromLocation);
  }

  private static bool LooksLikeGstreamerPipeline(string value) =>
      value.Contains("udpsrc", StringComparison.OrdinalIgnoreCase)
      || value.Contains("gst-launch", StringComparison.OrdinalIgnoreCase)
      || value.Contains(" ! ", StringComparison.Ordinal);

  private static ResolvedVideoSource ResolveGstreamerRtp(string pipeline) {
    if (!pipeline.Contains("application/x-rtp", StringComparison.OrdinalIgnoreCase)
        && !pipeline.Contains("rtph264depay", StringComparison.OrdinalIgnoreCase)
        && !pipeline.Contains("rtph265depay", StringComparison.OrdinalIgnoreCase)) {
      throw new FormatException(
          "Only RTP GStreamer pipelines can be converted for libVLC. "
          + "Use application/x-rtp with rtph264depay/rtph265depay, or enter a direct URL/MRL.");
    }
    var portMatch = PortRegex().Match(pipeline);
    if (!portMatch.Success
        || !int.TryParse(portMatch.Groups[1].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out int port)
        || port is < 1 or > 65535) {
      throw new FormatException("The GStreamer RTP pipeline does not contain a valid udpsrc port= value.");
    }

    string codec = EncodingRegex().Match(pipeline) is { Success: true } codecMatch
        ? codecMatch.Groups[1].Value.ToUpperInvariant()
        : "H264";
    if (codec == "HEVC") {
      codec = "H265";
    }

    int payload = 96;
    var payloadMatch = PayloadRegex().Match(pipeline);
    if (payloadMatch.Success) {
      _ = int.TryParse(payloadMatch.Groups[1].Value, NumberStyles.None,
          CultureInfo.InvariantCulture, out payload);
    }
    payload = Math.Clamp(payload, 0, 127);

    string path = Path.Combine(Path.GetTempPath(), $"missionplanner-video-{Guid.NewGuid():N}.sdp");
    File.WriteAllText(path,
        "v=0\n"
        + "o=- 0 0 IN IP4 127.0.0.1\n"
        + "s=MissionPlanner RTP video\n"
        + "c=IN IP4 0.0.0.0\n"
        + "t=0 0\n"
        + $"m=video {port} RTP/AVP {payload}\n"
        + $"a=rtpmap:{payload} {codec}/90000\n"
        + "a=recvonly\n");
    return new ResolvedVideoSource(path, FromType.FromPath, path);
  }

  private static string NormalizeListenMrl(string value, string scheme) {
    if (value.StartsWith(scheme + ":", StringComparison.OrdinalIgnoreCase)) {
      return scheme + "@:" + value[(scheme.Length + 1)..];
    }
    if (value.StartsWith(scheme + "0.0.0.0:", StringComparison.OrdinalIgnoreCase)) {
      return scheme + "@:" + value[(scheme.Length + "0.0.0.0:".Length)..];
    }
    return value;
  }
}
