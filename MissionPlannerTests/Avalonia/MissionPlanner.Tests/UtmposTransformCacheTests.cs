using GeoAPI.CoordinateSystems;
using GeoAPI.CoordinateSystems.Transformations;
using MissionPlanner.Utilities;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace MissionPlanner.Tests;

public class UtmposTransformCacheTests {
  // Zone 0 is what utmpos.Zero and default(utmpos) carry; 61, 321 and -1000 are outside the real
  // range and take the uncached path.
  private static readonly int[] Zones = [0, 1, 30, 31, 32, 60, 61, 321, -1, -30, -31, -32, -60, -1000];

  [Fact]
  public void ToLLA_matches_an_uncached_transform_bit_for_bit() {
    foreach (int zone in Zones) {
      foreach ((double x, double y) in Lattice(zone)) {
        PointLatLngAlt expected = UncachedToLLA(x, y, zone);
        PointLatLngAlt actual = new utmpos(x, y, zone).ToLLA();

        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Lat), BitConverter.DoubleToInt64Bits(actual.Lat));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Lng), BitConverter.DoubleToInt64Bits(actual.Lng));
      }
    }
  }

  [Theory]
  [InlineData(47.3977, 8.5456)]
  [InlineData(-33.8568, 151.2153)]
  [InlineData(0.0001, 5.9999)]
  [InlineData(-0.0001, 6.0001)]
  [InlineData(64.1466, -21.9426)]
  [InlineData(-54.8019, -68.3030)]
  public void ToLLA_round_trips_a_position_through_utm(double lat, double lng) {
    PointLatLngAlt back = new utmpos(new PointLatLngAlt(lat, lng, 0)).ToLLA();

    Assert.Equal(lat, back.Lat, 9);
    Assert.Equal(lng, back.Lng, 9);
  }

  [Fact]
  public void ToLLA_separates_hemispheres_that_share_a_zone_number() {
    PointLatLngAlt north = new utmpos(500000, 1000000, 32).ToLLA();
    PointLatLngAlt south = new utmpos(500000, 9000000, -32).ToLLA();
    PointLatLngAlt northAgain = new utmpos(500000, 1000000, 32).ToLLA();

    Assert.True(north.Lat > 0);
    Assert.True(south.Lat < 0);
    Assert.Equal(north.Lat, northAgain.Lat);
    Assert.Equal(north.Lng, northAgain.Lng);
  }

  [Fact]
  public void Zones_outside_the_real_range_are_not_cached() {
    new utmpos(500000, 4000000, 60).ToLLA();
    new utmpos(500000, 6000000, -60).ToLLA();
    int before = utmpos.CachedTransformCount;

    for (int zone = 61; zone < 400; zone++) {
      new utmpos(500000, 4000000, zone).ToLLA();
      new utmpos(500000, 6000000, -zone).ToLLA();
    }

    Assert.Equal(before, utmpos.CachedTransformCount);
    Assert.InRange(utmpos.CachedTransformCount, 2, 121);
  }

  [Fact]
  public void ToLLA_keeps_the_tag_as_text() {
    var tagged = new utmpos(500000, 5000000, 32) { Tag = 42 };

    Assert.Equal("42", tagged.ToLLA().Tag);
    Assert.Equal("", new utmpos(500000, 5000000, 32).ToLLA().Tag);
  }

  [Fact]
  public void ToLLA_is_consistent_under_concurrent_use() {
    int[] zones = Enumerable.Range(1, 60).SelectMany(zone => new[] { zone, -zone }).ToArray();
    var expected = zones.ToDictionary(zone => zone, zone => UncachedToLLA(400000, NorthingFor(zone), zone));
    int mismatches = 0;

    Parallel.For(0, 20_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index => {
      int zone = zones[index % zones.Length];
      PointLatLngAlt actual = new utmpos(400000, NorthingFor(zone), zone).ToLLA();
      if (actual.Lat != expected[zone].Lat || actual.Lng != expected[zone].Lng) {
        Interlocked.Increment(ref mismatches);
      }
    });

    Assert.Equal(0, mismatches);
  }

  private static double NorthingFor(int zone) => zone < 0 ? 6000000 : 4000000;

  private static IEnumerable<(double X, double Y)> Lattice(int zone) {
    double[] eastings = [170000, 333333.25, 500000, 666666.75, 830000];
    double[] northings = zone < 0
        ? [1200000, 4000000.5, 6500000, 9999000]
        : [1000, 2500000.5, 5000000, 8800000];
    foreach (double x in eastings) {
      foreach (double y in northings) {
        yield return (x, y);
      }
    }
  }

  // The pre-cache body of utmpos.ToLLA: a new coordinate system and transform for every call.
  private static PointLatLngAlt UncachedToLLA(double x, double y, int zone) {
    IProjectedCoordinateSystem utm = ProjectedCoordinateSystem.WGS84_UTM(Math.Abs(zone), zone >= 0);
    ICoordinateTransformation transformation = new CoordinateTransformationFactory()
        .CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, utm);
    double[] lngLat = transformation.MathTransform.Inverse().Transform(new[] { x, y });
    return new PointLatLngAlt(lngLat[1], lngLat[0]);
  }
}
