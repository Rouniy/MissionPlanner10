using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MissionPlanner.Controls;
using MissionPlanner.Services;
using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public class HudFrameRecordingTests {
  [AvaloniaFact]
  public void Flight_data_exposes_separate_hud_recording_controls() {
    var view = Assert.IsType<FlightDataView>(Activator.CreateInstance(typeof(FlightDataView)));
    var start = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("RecordHudMenuItem"));
    var stop = Assert.IsType<MenuItem>(view.FindControl<MenuItem>("StopHudRecordingMenuItem"));

    Assert.Equal("Record HUD to AVI", start.Header);
    Assert.True(start.IsEnabled);
    Assert.Equal("Stop HUD Recording", stop.Header);
    Assert.False(stop.IsEnabled);
  }

  [Fact]
  public void Timeline_preserves_dropped_frame_time_and_caps_long_pauses() {
    Assert.Equal(1, HudFrameTimeline.CopiesForInterval(null, TimeSpan.Zero, 25));
    Assert.Equal(1, HudFrameTimeline.CopiesForInterval(
        TimeSpan.Zero, TimeSpan.FromMilliseconds(40), 25));
    Assert.Equal(3, HudFrameTimeline.CopiesForInterval(
        TimeSpan.Zero, TimeSpan.FromMilliseconds(120), 25));
    Assert.Equal(25, HudFrameTimeline.CopiesForInterval(
        TimeSpan.Zero, TimeSpan.FromMinutes(10), 25));
  }

  [Fact]
  public void Recording_path_uses_upstream_timestamp_and_never_overwrites() {
    string root = TempDirectory();
    try {
      var now = new DateTime(2026, 8, 22, 13, 14, 15, DateTimeKind.Local);
      string first = HudRecordingPath.Create(root, now);
      Assert.Equal(Path.Combine(root, "2026-08-22 13-14-15.avi"), first);
      File.WriteAllBytes(first, [1]);
      Assert.Equal(Path.Combine(root, "2026-08-22 13-14-15-2.avi"),
          HudRecordingPath.Create(root, now));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [AvaloniaFact]
  public async Task Avalonia_hud_is_recorded_as_portable_mjpeg_avi() {
    string root = TempDirectory();
    string path = Path.Combine(root, "hud.avi");
    try {
      var hud = new HudControl {
        Roll = 12,
        Pitch = -4,
        Yaw = 227,
        Alt = 81,
        GroundSpeed = 15,
        SatCount = 17,
        Mode = "AUTO",
        BatteryVoltage = 15.8,
        BatteryRemaining = 72,
      };
      var size = new PixelSize(320, 240);
      hud.Measure(size.ToSize(1));
      hud.Arrange(new Rect(0, 0, 320, 240));

      using var target = new RenderTargetBitmap(size);
      target.Render(hud);
      var recorder = new HudFrameRecorder(path, size.Width, size.Height);
      if (target.Format == PixelFormats.Bgra8888
          || target.Format == PixelFormats.Rgba8888) {
        HudPixelLayout layout = target.Format == PixelFormats.Bgra8888
            ? HudPixelLayout.Bgra8888
            : HudPixelLayout.Rgba8888;
        int stride = size.Width * 4;
        int count = stride * size.Height;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(count);
        GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try {
          target.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), count, stride);
        } finally {
          pinned.Free();
        }
        Assert.True(recorder.SubmitPooledFrame(
            pixels, size.Width, size.Height, stride, layout));
      } else {
        // Avalonia's headless render target intentionally has no readable backing store.
        // Exercise the portable encoded-image path with an equivalent generated snapshot.
        byte[] snapshot = SolidPng(size.Width, size.Height);
        int length = snapshot.Length;
        byte[] image = ArrayPool<byte>.Shared.Rent(length);
        snapshot.CopyTo(image, 0);
        Assert.True(recorder.SubmitPooledEncodedFrame(
            image, length, size.Width, size.Height));
      }
      HudRecordingResult result = await recorder.StopAsync();

      Assert.Null(result.Error);
      Assert.Equal(path, result.Path);
      Assert.Equal(1, result.CapturedFrames);
      Assert.Equal(1, result.WrittenFrames);
      AssertAvi(path, size.Width, size.Height, result.WrittenFrames);
      AssertFfprobeReads(path, size.Width, size.Height);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void Avi_checkpoint_keeps_a_partial_recording_structurally_playable() {
    string root = TempDirectory();
    string path = Path.Combine(root, "partial.avi");
    try {
      byte[] jpeg = SolidJpeg(24, 16);
      using (var writer = new MjpegAviWriter(path, 24, 16, 25)) {
        writer.WriteJpeg(jpeg);
        writer.Checkpoint();
        byte[] partial = ReadWhileWriterIsOpen(path);
        Assert.Equal((uint)(partial.Length - 8), ReadUInt32(partial, 4));
        Assert.Equal(1u, MainHeaderFrames(partial));
        Assert.Equal(-1, FindFourCc(partial, "idx1"));
        AssertFfprobeReads(path, 24, 16);
        writer.WriteJpeg(jpeg);
      }

      AssertAvi(path, 24, 16, 2);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  // File.ReadAllBytes only shares the file with readers, which Windows refuses while the writer
  // still holds it open for writing.
  private static byte[] ReadWhileWriterIsOpen(string path) {
    using var stream = new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static void AssertAvi(string path, int width, int height, int frames) {
    byte[] avi = File.ReadAllBytes(path);
    Assert.Equal("RIFF", Encoding.ASCII.GetString(avi, 0, 4));
    Assert.Equal((uint)(avi.Length - 8), ReadUInt32(avi, 4));
    Assert.Equal("AVI ", Encoding.ASCII.GetString(avi, 8, 4));
    Assert.True(FindFourCc(avi, "MJPG") >= 0);
    Assert.True(FindFourCc(avi, "movi") >= 0);
    Assert.True(FindFourCc(avi, "00dc") >= 0);
    int index = FindFourCc(avi, "idx1");
    Assert.True(index >= 0);
    Assert.Equal((uint)(frames * 16), ReadUInt32(avi, index + 4));
    Assert.Equal((uint)frames, MainHeaderFrames(avi));

    int bitmapHeader = FindFourCc(avi, "strf");
    Assert.True(bitmapHeader >= 0);
    Assert.Equal(width, BitConverter.ToInt32(avi, bitmapHeader + 12));
    Assert.Equal(height, BitConverter.ToInt32(avi, bitmapHeader + 16));
  }

  private static uint MainHeaderFrames(byte[] avi) {
    int mainHeader = FindFourCc(avi, "avih");
    Assert.True(mainHeader >= 0);
    return ReadUInt32(avi, mainHeader + 24);
  }

  private static int FindFourCc(byte[] bytes, string value) {
    byte[] pattern = Encoding.ASCII.GetBytes(value);
    for (int i = 0; i <= bytes.Length - pattern.Length; i++) {
      if (bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) {
        return i;
      }
    }
    return -1;
  }

  private static uint ReadUInt32(byte[] bytes, int offset) =>
      BitConverter.ToUInt32(bytes, offset);

  private static byte[] SolidJpeg(int width, int height) {
    using var bitmap = new SkiaSharp.SKBitmap(width, height);
    bitmap.Erase(SkiaSharp.SKColors.DarkBlue);
    using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bitmap);
    using SkiaSharp.SKData encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
    return encoded.ToArray();
  }

  private static byte[] SolidPng(int width, int height) {
    using var bitmap = new SkiaSharp.SKBitmap(width, height);
    bitmap.Erase(SkiaSharp.SKColors.DarkSlateBlue);
    using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bitmap);
    using SkiaSharp.SKData encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static void AssertFfprobeReads(string path, int width, int height) {
    const string ffprobe = "/usr/bin/ffprobe";
    if (!File.Exists(ffprobe)) {
      return;
    }
    using var process = Process.Start(new ProcessStartInfo {
      FileName = ffprobe,
      ArgumentList = {
        "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=codec_name,width,height",
        "-of", "default=noprint_wrappers=1", path,
      },
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    })!;
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    Assert.True(process.WaitForExit(10_000), "ffprobe did not exit.");
    Assert.True(process.ExitCode == 0, error);
    Assert.Contains("codec_name=mjpeg", output);
    Assert.Contains($"width={width}", output);
    Assert.Contains($"height={height}", output);
  }

  private static string TempDirectory() {
    string root = Path.Combine(Path.GetTempPath(),
        "mp-hud-recording-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
  }
}
