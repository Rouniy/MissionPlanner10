using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MissionPlanner.Utilities;
using SkiaSharp;
using AvaloniaGrid = Avalonia.Controls.Grid;
using AvaloniaImage = Avalonia.Controls.Image;

namespace MissionPlanner.Views;

public sealed class SpectrogramWindow : Window {
  private static readonly string[] _sensors =
      { "ACC1", "ACC2", "ACC3", "ACC4", "ACC5", "GYR1", "GYR2", "GYR3", "GYR4", "GYR5" };

  private readonly ComboBox _sensor = new() { ItemsSource = _sensors, SelectedIndex = 0, Width = 100 };
  private readonly NumericUpDown _minimum = new() { Minimum = -1000, Maximum = 1000, Value = -80, Width = 85 };
  private readonly NumericUpDown _maximum = new() { Minimum = -1000, Maximum = 1000, Value = -20, Width = 85 };
  private readonly TextBlock _status = new() { Text = "Open a DataFlash .bin or .log file.", TextWrapping = TextWrapping.Wrap };
  private readonly AvaloniaImage[] _images = { MakeImage(), MakeImage(), MakeImage() };
  private string? _path;

  public SpectrogramWindow() {
    Title = "DataFlash Spectrogram";
    Width = 1050;
    Height = 820;
    MinWidth = 700;
    MinHeight = 500;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    var open = new Button { Content = "Open log…" };
    open.Click += async (_, _) => await PickLogAsync();
    var redraw = new Button { Content = "Redraw" };
    redraw.Click += async (_, _) => await RenderAsync();

    var toolbar = new StackPanel {
      Orientation = Orientation.Horizontal,
      Spacing = 8,
      Children = {
        open,
        new TextBlock { Text = "Sensor", VerticalAlignment = VerticalAlignment.Center },
        _sensor,
        new TextBlock { Text = "Min dB", VerticalAlignment = VerticalAlignment.Center },
        _minimum,
        new TextBlock { Text = "Max dB", VerticalAlignment = VerticalAlignment.Center },
        _maximum,
        redraw,
      },
    };

    var plots = new AvaloniaGrid { RowDefinitions = new RowDefinitions("*,*,*") };
    for (var i = 0; i < _images.Length; i++) {
      var panel = MakePlotPanel(i == 0 ? "X" : i == 1 ? "Y" : "Z", _images[i]);
      AvaloniaGrid.SetRow(panel, i);
      plots.Children.Add(panel);
    }

    var root = new AvaloniaGrid {
      Margin = new Thickness(12),
      RowDefinitions = new RowDefinitions("Auto,Auto,*"),
    };
    root.Children.Add(toolbar);
    AvaloniaGrid.SetRow(_status, 1);
    _status.Margin = new Thickness(0, 8);
    root.Children.Add(_status);
    AvaloniaGrid.SetRow(plots, 2);
    root.Children.Add(plots);
    Content = root;
  }

  public static void OpenWindow() {
    var window = new SpectrogramWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private async Task PickLogAsync() {
    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Open DataFlash log",
      AllowMultiple = false,
      FileTypeFilter = new[] {
          new FilePickerFileType("DataFlash log") { Patterns = new[] { "*.bin", "*.BIN", "*.log", "*.LOG" } },
          new FilePickerFileType("All files") { Patterns = new[] { "*" } },
      },
    });
    _path = files.FirstOrDefault()?.TryGetLocalPath();
    if (_path != null) {
      await RenderAsync();
    }
  }

  private async Task RenderAsync() {
    if (_path == null) {
      _status.Text = "Open a DataFlash .bin or .log file first.";
      return;
    }

    var sensor = _sensor.SelectedItem as string ?? "ACC1";
    var minimum = (int)(_minimum.Value ?? -80);
    var maximum = (int)(_maximum.Value ?? -20);
    if (minimum >= maximum) {
      _status.Text = "Min dB must be smaller than Max dB.";
      return;
    }

    _status.Text = $"Computing {sensor} spectrogram…";
    try {
      var result = await Task.Run(() => Generate(_path, sensor, minimum, maximum));
      for (var i = 0; i < result.Png.Length; i++) {
        _images[i].Source = new Bitmap(new MemoryStream(result.Png[i], writable: false));
      }
      _status.Text = $"{Path.GetFileName(_path)} — {sensor}; " +
                     $"{result.StartSeconds:0.###}–{result.EndSeconds:0.###} s, " +
                     $"0–{result.MaximumFrequency:0.#} Hz.";
    } catch (Exception ex) {
      _status.Text = "Unable to generate spectrogram: " + ex.Message;
    }
  }

  private static SpectrogramResult Generate(string path, string sensor, int minimum, int maximum) {
    using var log = new DFLogBuffer(path);
    var fields = sensor.StartsWith("GYR", StringComparison.Ordinal)
        ? new[] { "GyrX", "GyrY", "GyrZ" }
        : new[] { "AccX", "AccY", "AccZ" };

    var png = new byte[3][];
    double[] frequencies = Array.Empty<double>();
    System.Collections.Generic.List<(double timeus, double[] value)> samples = new();
    for (var i = 0; i < fields.Length; i++) {
      using var image = Spectrogram.GenerateImage(
          log, out frequencies, out samples, sensor, fields[i], min: minimum, max: maximum);
      using var stream = new MemoryStream();
      using var encoded = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100);
      encoded.SaveTo(stream);
      png[i] = stream.ToArray();
    }

    if (samples.Count == 0 || frequencies.Length == 0) {
      throw new InvalidDataException($"No {sensor} batch/IMU samples were found in this log.");
    }

    return new SpectrogramResult(
        png,
        samples.Min(sample => sample.timeus) / 1_000_000.0,
        samples.Max(sample => sample.timeus) / 1_000_000.0,
        frequencies.Max());
  }

  private static AvaloniaImage MakeImage() => new() {
    Stretch = Stretch.Fill,
    HorizontalAlignment = HorizontalAlignment.Stretch,
    VerticalAlignment = VerticalAlignment.Stretch,
  };

  private static Border MakePlotPanel(string axis, AvaloniaImage image) {
    var grid = new AvaloniaGrid {
      RowDefinitions = new RowDefinitions("Auto,*"),
      Children = {
        new TextBlock { Text = $"{axis} — frequency (low → high)", Margin = new Thickness(6, 3) },
        image,
      },
    };
    AvaloniaGrid.SetRow(image, 1);
    return new Border {
      BorderBrush = Brushes.DimGray,
      BorderThickness = new Thickness(1),
      Margin = new Thickness(0, 2),
      Child = grid,
    };
  }

  private sealed record SpectrogramResult(
      byte[][] Png, double StartSeconds, double EndSeconds, double MaximumFrequency);
}
