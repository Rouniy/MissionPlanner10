using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Mapsui;
using Mapsui.Projections;
using MissionPlanner.Utilities;
using SkiaSharp;

namespace MissionPlanner.Services;

internal enum PropagationTerrainKind {
  Valid,
  Ocean,
  Missing,
}

internal readonly record struct PropagationTerrainSample(
    PropagationTerrainKind Kind, double Altitude) {
  internal static PropagationTerrainSample Valid(double altitude) =>
      new(PropagationTerrainKind.Valid, altitude);

  internal static PropagationTerrainSample Ocean =>
      new(PropagationTerrainKind.Ocean, 0);

  internal static PropagationTerrainSample Missing =>
      new(PropagationTerrainKind.Missing, 0);
}

internal readonly record struct PropagationPoint(double Lat, double Lng, double Altitude = 0);

internal sealed record PropagationOptions(
    double ClearanceMeters,
    int ResolutionPixels,
    double AzimuthStepDegrees,
    double ConvergenceDegrees,
    double RangeKilometers,
    double BaseHeightMeters,
    double Tolerance,
    double MinimumAltitude,
    double MaximumAltitude,
    bool ElevationMap,
    bool TerrainMap,
    bool RfMap,
    bool HomeDistance,
    bool DroneDistance,
    bool ManualAltitudeRange,
    bool ShowScale) {
  internal const string ClearanceKey = "Propagation_Clearance";
  internal const string ResolutionKey = "Propagation_Resolution";
  internal const string AzimuthStepKey = "Propagation_Rotational";
  internal const string ConvergenceKey = "Propagation_Converge";
  internal const string RangeKey = "Propagation_Range";
  internal const string BaseHeightKey = "Propagation_Height";
  internal const string ToleranceKey = "Propagation_Tolerance";
  internal const string MinimumAltitudeKey = "Propagation_Minalt";
  internal const string MaximumAltitudeKey = "Propagation_Maxalt";
  internal const string ElevationMapKey = "Propagation_Elemap";
  internal const string TerrainMapKey = "Propagation_Termap";
  internal const string RfMapKey = "Propagation_RFmap";
  internal const string HomeDistanceKey = "Propagation_home_kmleft";
  internal const string DroneDistanceKey = "Propagation_drone_kmleft";
  internal const string ManualAltitudeRangeKey = "Propagation_Setalt";
  internal const string ShowScaleKey = "Propagation_ShowScale";

  internal static PropagationOptions Default { get; } = new(
      ClearanceMeters: 5,
      ResolutionPixels: 4,
      AzimuthStepDegrees: 1,
      ConvergenceDegrees: 1,
      RangeKilometers: 2,
      BaseHeightMeters: 2,
      Tolerance: 0.8,
      MinimumAltitude: 100,
      MaximumAltitude: 400,
      ElevationMap: false,
      TerrainMap: false,
      RfMap: false,
      HomeDistance: false,
      DroneDistance: false,
      ManualAltitudeRange: false,
      ShowScale: false);

  internal static PropagationOptions Load() {
    var settings = Settings.Instance;
    PropagationOptions defaults = Default;
    return defaults with {
      ClearanceMeters = ParseStoredDouble(settings[ClearanceKey], defaults.ClearanceMeters),
      ResolutionPixels = settings.GetInt32(ResolutionKey, defaults.ResolutionPixels),
      // Upstream offers 0.5 in this combo but reads it as an integer. Parse the stored
      // value as a double so the published option works without changing its key.
      AzimuthStepDegrees = ParseStoredDouble(settings[AzimuthStepKey], defaults.AzimuthStepDegrees),
      ConvergenceDegrees = ParseStoredDouble(settings[ConvergenceKey], defaults.ConvergenceDegrees),
      RangeKilometers = ParseStoredDouble(settings[RangeKey], defaults.RangeKilometers),
      BaseHeightMeters = ParseStoredDouble(settings[BaseHeightKey], defaults.BaseHeightMeters),
      Tolerance = ParseStoredDouble(settings[ToleranceKey], defaults.Tolerance),
      MinimumAltitude = ParseStoredDouble(settings[MinimumAltitudeKey], defaults.MinimumAltitude),
      MaximumAltitude = ParseStoredDouble(settings[MaximumAltitudeKey], defaults.MaximumAltitude),
      ElevationMap = settings.GetBoolean(ElevationMapKey),
      TerrainMap = settings.GetBoolean(TerrainMapKey),
      RfMap = settings.GetBoolean(RfMapKey),
      HomeDistance = settings.GetBoolean(HomeDistanceKey),
      DroneDistance = settings.GetBoolean(DroneDistanceKey),
      ManualAltitudeRange = settings.GetBoolean(ManualAltitudeRangeKey),
      ShowScale = settings.GetBoolean(ShowScaleKey),
    };
  }

  internal void Save() {
    var settings = Settings.Instance;
    settings[ClearanceKey] = Invariant(ClearanceMeters);
    settings[ResolutionKey] = ResolutionPixels.ToString(CultureInfo.InvariantCulture);
    settings[AzimuthStepKey] = Invariant(AzimuthStepDegrees);
    settings[ConvergenceKey] = Invariant(ConvergenceDegrees);
    settings[RangeKey] = Invariant(RangeKilometers);
    settings[BaseHeightKey] = Invariant(BaseHeightMeters);
    settings[ToleranceKey] = Invariant(Tolerance);
    settings[MinimumAltitudeKey] = Invariant(MinimumAltitude);
    settings[MaximumAltitudeKey] = Invariant(MaximumAltitude);
    settings[ElevationMapKey] = ElevationMap.ToString();
    settings[TerrainMapKey] = TerrainMap.ToString();
    settings[RfMapKey] = RfMap.ToString();
    settings[HomeDistanceKey] = HomeDistance.ToString();
    settings[DroneDistanceKey] = DroneDistance.ToString();
    settings[ManualAltitudeRangeKey] = ManualAltitudeRange.ToString();
    settings[ShowScaleKey] = ShowScale.ToString();
    settings.Save();
    PropagationService.NotifySettingsChanged();
  }

  private static string Invariant(double value) =>
      value.ToString("0.###", CultureInfo.InvariantCulture);

  internal static double ParseStoredDouble(string? text, double fallback,
      CultureInfo? currentCulture = null) {
    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double invariant)) {
      return invariant;
    }
    return double.TryParse(text, NumberStyles.Float,
        currentCulture ?? CultureInfo.CurrentCulture, out double localized)
        ? localized
        : fallback;
  }
}

internal readonly record struct PropagationRasterRequest(
    MRect Extent, int Width, int Height, double VehicleAltitudeAmsl,
    PropagationOptions Options);

internal sealed record PropagationRasterResult(
    MRect Extent,
    int Width,
    int Height,
    byte[] Rgba,
    double MinimumAltitude,
    double MaximumAltitude,
    int ValidSampleCount,
    int MissingSampleCount) {
  internal byte[] EncodePng() {
    using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    System.Runtime.InteropServices.Marshal.Copy(Rgba, 0, bitmap.GetPixels(), Math.Min(Rgba.Length, bitmap.ByteCount));
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }
}

internal sealed record PropagationRfResult(
    IReadOnlyList<PropagationPoint> Points,
    int MissingSampleCount) {
  internal bool Complete => MissingSampleCount == 0 && Points.Count >= 4;
}

internal static class PropagationService {
  private const double EarthRadiusMeters = 6_378_137;

  internal static event Action? SettingsChanged;

  internal static void NotifySettingsChanged() => SettingsChanged?.Invoke();

  internal static PropagationTerrainSample GetSrtmSample(double lat, double lng,
      double zoom = 16) {
    var sample = srtm.getAltitude(lat, lng, zoom);
    return sample.currenttype switch {
      srtm.tiletype.valid => PropagationTerrainSample.Valid(sample.alt),
      srtm.tiletype.ocean => PropagationTerrainSample.Ocean,
      _ => PropagationTerrainSample.Missing,
    };
  }

  internal static PropagationRasterResult BuildRaster(
      PropagationRasterRequest request,
      Func<double, double, PropagationTerrainSample> terrainProvider,
      CancellationToken cancellationToken = default) {
    int width = Math.Clamp(request.Width, 1, 4096);
    int height = Math.Clamp(request.Height, 1, 4096);
    int resolution = Math.Clamp(request.Options.ResolutionPixels, 1, 64);
    int columns = (width + resolution - 1) / resolution;
    int rows = (height + resolution - 1) / resolution;
    var samples = new PropagationTerrainSample[columns * rows];

    for (int row = 0; row < rows; row++) {
      cancellationToken.ThrowIfCancellationRequested();
      double py = Math.Min(height - 0.5, row * resolution + resolution / 2.0);
      double worldY = request.Extent.MaxY - py / height * request.Extent.Height;
      for (int column = 0; column < columns; column++) {
        double px = Math.Min(width - 0.5, column * resolution + resolution / 2.0);
        double worldX = request.Extent.MinX + px / width * request.Extent.Width;
        var (lng, lat) = SphericalMercator.ToLonLat(worldX, worldY);
        samples[row * columns + column] = terrainProvider(lat, lng);
      }
    }

    double minimum = double.PositiveInfinity;
    double maximum = double.NegativeInfinity;
    int valid = 0;
    int missing = 0;
    foreach (PropagationTerrainSample sample in samples) {
      // GMapMarkerElevation treats ocean, unavailable DEM and an altitude of zero as
      // transparent. Keep that rendering contract; RF calculations still regard ocean
      // as a known zero-metre surface.
      if (sample.Kind != PropagationTerrainKind.Valid || sample.Altitude == 0) {
        if (sample.Kind == PropagationTerrainKind.Missing) {
          missing++;
        }
        continue;
      }
      minimum = Math.Min(minimum, sample.Altitude);
      maximum = Math.Max(maximum, sample.Altitude);
      valid++;
    }

    if (request.Options.ManualAltitudeRange) {
      minimum = Math.Min(request.Options.MinimumAltitude, request.Options.MaximumAltitude);
      maximum = Math.Max(request.Options.MinimumAltitude, request.Options.MaximumAltitude);
    } else if (valid == 0) {
      minimum = maximum = 0;
    }

    var pixels = new byte[width * height * 4];
    for (int row = 0; row < rows; row++) {
      cancellationToken.ThrowIfCancellationRequested();
      for (int column = 0; column < columns; column++) {
        PropagationTerrainSample sample = samples[row * columns + column];
        if (sample.Kind != PropagationTerrainKind.Valid || sample.Altitude == 0) {
          continue;
        }

        double normalized;
        int paletteIndex;
        if (request.Options.ElevationMap) {
          paletteIndex = ElevationPaletteIndex(request.VehicleAltitudeAmsl,
              sample.Altitude, request.Options.ClearanceMeters);
        } else {
          double span = maximum - minimum;
          normalized = span <= double.Epsilon
              ? 0
              : Math.Clamp((sample.Altitude - minimum) / span, 0, 1);
          paletteIndex = (int)(normalized * 255);
        }

        PaletteColor color = Palette(paletteIndex);
        int x0 = column * resolution;
        int y0 = row * resolution;
        int x1 = Math.Min(width, x0 + resolution);
        int y1 = Math.Min(height, y0 + resolution);
        for (int y = y0; y < y1; y++) {
          for (int x = x0; x < x1; x++) {
            int offset = (y * width + x) * 4;
            pixels[offset] = color.R;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.B;
            pixels[offset + 3] = color.A;
          }
        }
      }
    }

    return new PropagationRasterResult(request.Extent, width, height, pixels,
        minimum, maximum, valid, missing);
  }

  internal static PropagationRfResult BuildRfCoverage(
      PropagationPoint home,
      double droneAltitudeAmsl,
      PropagationOptions options,
      Func<double, double, PropagationTerrainSample> terrainProvider,
      CancellationToken cancellationToken = default) {
    double rangeMeters = Math.Max(0, options.RangeKilometers) * 1000;
    if (rangeMeters <= 0 || !CoordinatesValid(home.Lat, home.Lng)) {
      return new PropagationRfResult(Array.Empty<PropagationPoint>(), 0);
    }

    double azimuthStep = Math.Clamp(options.AzimuthStepDegrees, 0.5, 10);
    int rayCount = Math.Max(1, (int)Math.Ceiling(360 / azimuthStep));
    double convergence = Math.Max(0.05, options.ConvergenceDegrees);
    double baseHeight = Math.Max(0, options.BaseHeightMeters);
    double clearance = Math.Max(0, options.ClearanceMeters);
    double ceiling = droneAltitudeAmsl;
    if (!double.IsFinite(ceiling) || ceiling == 0) {
      ceiling = home.Altitude + Math.Max(baseHeight, clearance) + 0.1;
    }
    ceiling -= clearance;

    var points = new PropagationPoint[rayCount];
    var missing = new int[rayCount];
    for (int ray = 0; ray < rayCount; ray++) {
      cancellationToken.ThrowIfCancellationRequested();
      double bearing = ray * azimuthStep;
      if (bearing >= 360) {
        bearing = Math.BitDecrement(360.0);
      }
      (points[ray], missing[ray]) = TraceRay(home, bearing, rangeMeters,
          home.Altitude + baseHeight, ceiling, convergence, terrainProvider,
          cancellationToken);
    }

    int missingCount = 0;
    for (int i = 0; i < missing.Length; i++) {
      missingCount += missing[i];
    }
    if (missingCount != 0) {
      // Do not bridge missing DEM sectors with a polygon that looks like verified RF
      // coverage. The controller retries after SRTM's background downloader has run.
      return new PropagationRfResult(Array.Empty<PropagationPoint>(), missingCount);
    }

    var closed = new List<PropagationPoint>(points.Length + 1);
    PropagationPoint? previous = null;
    foreach (PropagationPoint point in points) {
      if (previous is { } last && HaversineMeters(last, point) < 0.01) {
        continue;
      }
      closed.Add(point);
      previous = point;
    }
    if (closed.Count >= 3) {
      closed.Add(closed[0]);
    }
    return new PropagationRfResult(closed, 0);
  }

  internal static IReadOnlyList<PropagationPoint> BuildCircle(
      PropagationPoint center, double radiusMeters, int segments = 72) {
    if (!CoordinatesValid(center.Lat, center.Lng) || !double.IsFinite(radiusMeters)
        || radiusMeters <= 0) {
      return Array.Empty<PropagationPoint>();
    }
    segments = Math.Clamp(segments, 12, 720);
    var result = new PropagationPoint[segments + 1];
    for (int i = 0; i < segments; i++) {
      result[i] = NewPosition(center, i * 360.0 / segments, radiusMeters);
    }
    result[^1] = result[0];
    return result;
  }

  internal static int ElevationPaletteIndex(
      double vehicleAltitudeAmsl, double terrainAltitude, double clearanceMeters) {
    double clearance = Math.Max(0.001, clearanceMeters);
    double normalized = Math.Clamp(
        (vehicleAltitudeAmsl - terrainAltitude) / clearance, 0, 1);
    return (int)((1 - normalized) * 254);
  }

  private static (PropagationPoint Point, int Missing) TraceRay(
      PropagationPoint home,
      double bearing,
      double rangeMeters,
      double startAltitude,
      double ceiling,
      double convergenceDegrees,
      Func<double, double, PropagationTerrainSample> terrainProvider,
      CancellationToken cancellationToken) {
    PropagationPoint endpoint = NewPosition(home, bearing, rangeMeters);
    double minimumAngle = 0;
    double maximumAngle = Math.PI / 2;
    double angle = Math.PI / 4;
    PropagationPoint answer = endpoint;

    // Upstream samples every 10 m. Preserve that at normal RF ranges and use an
    // adaptive step only for extreme user-entered ranges to keep cancellation timely.
    int pathSegments = Math.Max(1, (int)Math.Ceiling(rangeMeters / 10));
    pathSegments = Math.Min(pathSegments, 20_000);

    for (int iteration = 0; iteration < 32
         && minimumAngle + convergenceDegrees * Math.PI / 180 < maximumAngle;
         iteration++) {
      cancellationToken.ThrowIfCancellationRequested();
      bool hit = false;
      bool moveUp = false;
      PropagationTerrainSample lastSample = PropagationTerrainSample.Missing;
      double lastLineAltitude = startAltitude;
      double lastDistance = 0;

      for (int i = 0; i <= pathSegments; i++) {
        if ((i & 127) == 0) {
          cancellationToken.ThrowIfCancellationRequested();
        }
        double fraction = i / (double)pathSegments;
        double lat = home.Lat + (endpoint.Lat - home.Lat) * fraction;
        double lng = home.Lng + (endpoint.Lng - home.Lng) * fraction;
        PropagationTerrainSample sample = terrainProvider(lat, lng);
        if (sample.Kind == PropagationTerrainKind.Missing) {
          return (answer, 1);
        }
        double terrainAltitude = sample.Kind == PropagationTerrainKind.Ocean
            ? 0
            : sample.Altitude;
        double distance = HaversineMeters(home, new PropagationPoint(lat, lng));
        double lineAltitude = startAltitude + distance * Math.Tan(angle);
        answer = new PropagationPoint(lat, lng, terrainAltitude);
        lastSample = sample;
        lastLineAltitude = lineAltitude;
        lastDistance = distance;
        if (terrainAltitude >= lineAltitude || terrainAltitude >= ceiling) {
          break;
        }
      }

      double lastTerrain = lastSample.Kind == PropagationTerrainKind.Ocean
          ? 0
          : lastSample.Altitude;
      if (Math.Abs(lastTerrain - lastLineAltitude) <= 0.1
          && Math.Abs(lastTerrain - ceiling) <= 0.1) {
        hit = true;
      } else {
        // This is the same branch decision used by SightGen: raise the ray only when
        // terrain is above both the current ray and the aircraft altitude ceiling.
        moveUp = lastTerrain > lastLineAltitude && lastTerrain > ceiling;
      }

      if (hit) {
        break;
      }
      if (moveUp) {
        minimumAngle = angle;
      } else {
        maximumAngle = angle;
      }
      angle = (maximumAngle + minimumAngle) / 2;

      if (lastDistance >= rangeMeters && maximumAngle - minimumAngle <= 1e-12) {
        break;
      }
    }
    return (answer, 0);
  }

  private readonly record struct PaletteColor(byte R, byte G, byte B, byte A);

  private static PaletteColor Palette(int index) {
    if (index is <= 0 or >= 255) {
      return new PaletteColor(0, 0, 0, 0);
    }
    float progress = 1.0f - index / 256.0f;
    float div = Math.Abs(progress % 1) * 5;
    byte ascending = (byte)((div % 1) * 255);
    byte descending = (byte)(255 - ascending);
    return (int)div switch {
      0 => new PaletteColor(255, ascending, 0, 100),
      1 => new PaletteColor(descending, 255, 0, 100),
      2 => new PaletteColor(0, 255, ascending, 100),
      3 => new PaletteColor(0, descending, 255, 100),
      4 => new PaletteColor(ascending, 0, 255, 100),
      _ => new PaletteColor(255, 0, descending, 100),
    };
  }

  private static PropagationPoint NewPosition(
      PropagationPoint origin, double bearingDegrees, double distanceMeters) {
    double angularDistance = distanceMeters / EarthRadiusMeters;
    double bearing = bearingDegrees * Math.PI / 180;
    double lat1 = origin.Lat * Math.PI / 180;
    double lng1 = origin.Lng * Math.PI / 180;
    double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angularDistance)
                            + Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(bearing));
    double lng2 = lng1 + Math.Atan2(
        Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(lat1),
        Math.Cos(angularDistance) - Math.Sin(lat1) * Math.Sin(lat2));
    return new PropagationPoint(lat2 * 180 / Math.PI,
        ((lng2 * 180 / Math.PI + 540) % 360) - 180, origin.Altitude);
  }

  private static double HaversineMeters(PropagationPoint a, PropagationPoint b) {
    double lat1 = a.Lat * Math.PI / 180;
    double lat2 = b.Lat * Math.PI / 180;
    double dLat = lat2 - lat1;
    double dLng = (b.Lng - a.Lng) * Math.PI / 180;
    double h = Math.Pow(Math.Sin(dLat / 2), 2)
               + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLng / 2), 2);
    return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
  }

  private static bool CoordinatesValid(double lat, double lng) =>
      double.IsFinite(lat) && double.IsFinite(lng)
      && lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
}
