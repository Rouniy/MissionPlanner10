using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MissionPlanner.Controls;
using MissionPlanner.Services;
using SkiaSharp;

namespace MissionPlanner.Views;

internal sealed class AvaloniaOsdVideoFrameRenderer : IOsdVideoFrameRenderer {
  private readonly HudControl _hud = new() {
    OverlayEnabled = true,
    ShowIcons = true,
    DisplayConnection = true,
  };
  private readonly Image _preview;
  private RenderTargetBitmap? _previewFrame;
  private bool _disposed;

  internal AvaloniaOsdVideoFrameRenderer(Image preview) {
    _preview = preview ?? throw new ArgumentNullException(nameof(preview));
  }

  public async ValueTask<byte[]> RenderJpegAsync(
      byte[] bgraPixels,
      int sourceWidth,
      int sourceHeight,
      int sourceStride,
      int outputWidth,
      int outputHeight,
      OsdTelemetrySample sample,
      int jpegQuality,
      CancellationToken cancellationToken) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    cancellationToken.ThrowIfCancellationRequested();
    if (Dispatcher.UIThread.CheckAccess()) {
      return Render(
          bgraPixels, sourceWidth, sourceHeight, sourceStride, outputWidth, outputHeight,
          sample, jpegQuality, cancellationToken);
    }
    return await Dispatcher.UIThread.InvokeAsync(
        () => Render(
            bgraPixels, sourceWidth, sourceHeight, sourceStride, outputWidth, outputHeight,
            sample, jpegQuality, cancellationToken),
        DispatcherPriority.Render);
  }

  private byte[] Render(
      byte[] bgraPixels,
      int sourceWidth,
      int sourceHeight,
      int sourceStride,
      int outputWidth,
      int outputHeight,
      OsdTelemetrySample sample,
      int jpegQuality,
      CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    int sourceRowBytes = checked(sourceWidth * 4);
    if (sourceWidth < 1 || sourceHeight < 1 || sourceStride < sourceRowBytes
        || bgraPixels.Length < checked(sourceStride * sourceHeight)) {
      throw new ArgumentException("The decoded libVLC frame dimensions are invalid.", nameof(bgraPixels));
    }

    using var background = new WriteableBitmap(
        new PixelSize(sourceWidth, sourceHeight),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Opaque);
    using (ILockedFramebuffer locked = background.Lock()) {
      for (int row = 0; row < sourceHeight; row++) {
        Marshal.Copy(
            bgraPixels,
            row * sourceStride,
            IntPtr.Add(locked.Address, row * locked.RowBytes),
            sourceRowBytes);
      }
    }

    Apply(sample);
    _hud.VideoBackground = background;
    var logicalSize = new Size(outputWidth, outputHeight);
    _hud.Measure(logicalSize);
    _hud.Arrange(new Rect(logicalSize));
    var target = new RenderTargetBitmap(
        new PixelSize(outputWidth, outputHeight), new Vector(96, 96));
    bool transferredToPreview = false;
    try {
      target.Render(_hud);
      byte[] jpeg = EncodeJpeg(target, outputWidth, outputHeight, jpegQuality);
      RenderTargetBitmap? previous = _previewFrame;
      _preview.Source = target;
      _previewFrame = target;
      transferredToPreview = true;
      previous?.Dispose();
      return jpeg;
    } finally {
      if (!transferredToPreview) {
        target.Dispose();
      }
      _hud.VideoBackground = null;
    }
  }

  private void Apply(OsdTelemetrySample sample) {
    _hud.Roll = sample.Roll;
    _hud.Pitch = sample.Pitch;
    _hud.Yaw = sample.Yaw;
    _hud.Alt = sample.Alt;
    _hud.AirSpeed = sample.AirSpeed;
    _hud.GroundSpeed = sample.GroundSpeed;
    _hud.VerticalSpeed = sample.VerticalSpeed;
    _hud.SatCount = sample.SatCount;
    _hud.Armed = sample.Armed;
    _hud.PrearmOk = sample.PrearmOk;
    _hud.GpsFixType = sample.GpsFixType;
    _hud.Mode = sample.Mode;
    _hud.BatteryVoltage = sample.BatteryVoltage;
    _hud.BatteryRemaining = sample.BatteryRemaining;
    _hud.CurrentAmps = sample.CurrentAmps;
    _hud.NavBearing = sample.NavBearing;
    _hud.TargetAlt = sample.TargetAlt;
    _hud.TargetSpeed = sample.TargetSpeed;
    _hud.WindDir = sample.WindDir;
    _hud.WindVel = sample.WindVel;
    _hud.Aoa = sample.Aoa;
    _hud.Ssa = sample.Ssa;
    _hud.XTrackError = sample.XTrackError;
    _hud.TurnRate = sample.TurnRate;
    _hud.BatteryVoltage2 = sample.BatteryVoltage2;
    _hud.BatteryRemaining2 = sample.BatteryRemaining2;
    _hud.CurrentAmps2 = sample.CurrentAmps2;
    _hud.ThrottlePercent = sample.ThrottlePercent;
    _hud.Failsafe = sample.Failsafe;
    _hud.SafetyActive = sample.SafetyActive;
    _hud.LinkQuality = sample.LinkQuality;
    _hud.WpDist = sample.WpDist;
    _hud.WpNo = sample.WpNo;
  }

  private static byte[] EncodeJpeg(
      RenderTargetBitmap target, int width, int height, int quality) {
    PixelFormat? format = target.Format;
    if (format != PixelFormats.Bgra8888 && format != PixelFormats.Rgba8888) {
      using var portable = new MemoryStream();
      target.Save(portable);
      portable.Position = 0;
      using SKBitmap decoded = SKBitmap.Decode(portable)
          ?? throw new InvalidDataException(
              $"Avalonia returned unsupported HUD pixel format {format} and no portable image.");
      using SKImage decodedImage = SKImage.FromBitmap(decoded);
      using SKData decodedJpeg = decodedImage.Encode(SKEncodedImageFormat.Jpeg, quality);
      return decodedJpeg.ToArray();
    }
    int stride = checked(width * 4);
    var pixels = new byte[checked(stride * height)];
    GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
    try {
      target.CopyPixels(
          new PixelRect(0, 0, width, height),
          pinned.AddrOfPinnedObject(),
          pixels.Length,
          stride);
    } finally {
      pinned.Free();
    }

    SKColorType colorType = format == PixelFormats.Bgra8888
        ? SKColorType.Bgra8888
        : SKColorType.Rgba8888;
    using var bitmap = new SKBitmap(
        new SKImageInfo(width, height, colorType, SKAlphaType.Opaque));
    Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
    return encoded.ToArray();
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _hud.VideoBackground = null;
    _preview.Source = null;
    _previewFrame?.Dispose();
    _previewFrame = null;
  }
}
