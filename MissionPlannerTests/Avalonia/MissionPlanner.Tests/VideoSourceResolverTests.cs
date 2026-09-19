using System;
using System.IO;
using LibVLCSharp.Shared;
using MissionPlanner.Services;
using Xunit;

namespace MissionPlanner.Tests;

public class VideoSourceResolverTests {
  [Theory]
  [InlineData("udp://:5600", "udp://@:5600")]
  [InlineData("rtp://:5600", "rtp://@:5600")]
  [InlineData("rtsp://192.168.1.2/live", "rtsp://192.168.1.2/live")]
  [InlineData("/dev/video0", "v4l2:///dev/video0")]
  public void NormalizesCommonStreamSources(string input, string expected) {
    var resolved = VideoSourceResolver.Resolve(input);
    Assert.Equal(expected, resolved.Mrl);
    Assert.Equal(FromType.FromLocation, resolved.FromType);
  }

  [Fact]
  public void Existing_linux_camera_device_is_not_treated_as_a_media_file() {
    // Deterministic on CI hosts without a physical /dev/video0.
    var resolved = VideoSourceResolver.Resolve("/dev/video0", fileExists: _ => true);
    Assert.Equal("v4l2:///dev/video0", resolved.Mrl);
    Assert.Equal(FromType.FromLocation, resolved.FromType);
  }

  [Fact]
  public void ConvertsRtpGstreamerPipelineToSdp() {
    var resolved = VideoSourceResolver.Resolve(
        "udpsrc port=5600 ! application/x-rtp,encoding-name=H265,payload=97 ! rtph265depay");
    try {
      Assert.Equal(FromType.FromPath, resolved.FromType);
      Assert.True(File.Exists(resolved.Mrl));
      string sdp = File.ReadAllText(resolved.Mrl);
      Assert.Contains("m=video 5600 RTP/AVP 97", sdp);
      Assert.Contains("a=rtpmap:97 H265/90000", sdp);
    } finally {
      if (resolved.TemporaryFile != null && File.Exists(resolved.TemporaryFile)) {
        File.Delete(resolved.TemporaryFile);
      }
    }
  }

  [Fact]
  public void ConvertsTypedH265GstreamerCapsToSdp() {
    var resolved = VideoSourceResolver.Resolve(
        "udpsrc port=(int)5601 ! application/x-rtp,encoding-name=(string)H265,"
        + "payload=(int)98 ! rtph265depay");
    try {
      string sdp = File.ReadAllText(resolved.Mrl);
      Assert.Contains("m=video 5601 RTP/AVP 98", sdp);
      Assert.Contains("a=rtpmap:98 H265/90000", sdp);
    } finally {
      if (resolved.TemporaryFile != null && File.Exists(resolved.TemporaryFile)) {
        File.Delete(resolved.TemporaryFile);
      }
    }
  }

  [Fact]
  public void RejectsPipelineWithoutUdpPort() {
    Assert.Throws<FormatException>(() =>
        VideoSourceResolver.Resolve("udpsrc ! rtph264depay ! avdec_h264"));
  }

  [Fact]
  public void RejectsNonRtpGstreamerPipelineWithActionableError() {
    var error = Assert.Throws<FormatException>(() =>
        VideoSourceResolver.Resolve("udpsrc port=5600 ! video/x-h264 ! avdec_h264"));
    Assert.Contains("Only RTP", error.Message);
  }

  [Fact]
  public void LinuxBackendInitializesWhenRuntimeIsInstalled() {
    if (!OperatingSystem.IsLinux()
        || (!File.Exists("/usr/lib/x86_64-linux-gnu/libvlc.so.5")
            && !File.Exists("/usr/lib/aarch64-linux-gnu/libvlc.so.5"))) {
      return;
    }

    LibVlcBootstrap.Initialize();
    using var libVlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--quiet");
    Assert.NotEmpty(libVlc.Version);
  }

  [Fact]
  public void Mac_runtime_locator_requires_libraries_and_plugin_cache() {
    string root = Path.Combine(Path.GetTempPath(), "mp-vlc-layout-" + Guid.NewGuid());
    try {
      Directory.CreateDirectory(Path.Combine(root, "lib"));
      Directory.CreateDirectory(Path.Combine(root, "plugins"));
      Directory.CreateDirectory(Path.Combine(root, "share", "lua"));
      Assert.Null(LibVlcBootstrap.LocateMacRuntime(root));

      File.WriteAllText(Path.Combine(root, "lib", "libvlc.dylib"), "test");
      File.WriteAllText(Path.Combine(root, "lib", "libvlccore.dylib"), "test");
      File.WriteAllText(Path.Combine(root, "plugins", "plugins.dat"), "test");

      MacVlcRuntimePaths runtime = Assert.IsType<MacVlcRuntimePaths>(
          LibVlcBootstrap.LocateMacRuntime(root));
      Assert.Equal(Path.Combine(root, "lib"), runtime.LibraryDirectory);
      Assert.Equal(Path.Combine(root, "plugins"), runtime.PluginDirectory);
      Assert.Equal(Path.Combine(root, "share"), runtime.DataDirectory);
    } finally {
      if (Directory.Exists(root)) {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public void Mac_bundled_libvlc_loads_and_reports_pinned_version() {
    if (!OperatingSystem.IsMacOS()) {
      return;
    }

    MacVlcRuntimePaths runtime = Assert.IsType<MacVlcRuntimePaths>(
        LibVlcBootstrap.LocateMacRuntime(AppContext.BaseDirectory));
    // Keep verbose native output in this acceptance test so a bootstrap regression reports the
    // exact plugin/configuration failure instead of LibVLCSharp's generic instantiation error.
    using var libVlc = LibVlcBootstrap.CreateInstance(true, "--no-video", "--no-audio");

    Assert.StartsWith("3.0.23", libVlc.Version);
    Assert.Equal(runtime.PluginDirectory,
        Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH"));
    Assert.Equal(runtime.DataDirectory,
        Environment.GetEnvironmentVariable("VLC_DATA_PATH"));
  }
}
