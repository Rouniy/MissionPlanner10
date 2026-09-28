using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using MissionPlanner.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace MissionPlanner.Tests;

[Collection("Imported overlay store")]
public sealed class GeoPackageOverlayTests {
  [Fact]
  public void GeoPackage_reads_points_lines_polygons_and_quoted_table_names() {
    WithGeoPackage((path, connection) => {
      const string table = "test features";
      CreateFeatureLayer(connection, table, "shape", 4326);
      var factory = new GeometryFactory();
      InsertGeometry(connection, table, "shape",
          factory.CreatePoint(new Coordinate(30, 40)));
      InsertGeometry(connection, table, "shape", factory.CreateLineString([
        new Coordinate(31, 41), new Coordinate(31.5, 41.5),
      ]));
      InsertGeometry(connection, table, "shape", factory.CreatePolygon([
        new Coordinate(32, 42), new Coordinate(33, 42),
        new Coordinate(33, 43), new Coordinate(32, 42),
      ]));
      connection.Close();

      ImportedMapOverlay overlay = GeoPackageOverlayReader.Read(path);

      ImportedOverlayMarker marker = Assert.Single(overlay.Markers);
      Assert.Equal(40, marker.Point.Lat);
      Assert.Equal(30, marker.Point.Lng);
      Assert.Equal(0, marker.Point.Alt);
      Assert.Equal(2, overlay.Routes.Count);
      Assert.Contains(overlay.Routes, route => route.Closed);
      Assert.Contains(overlay.Routes, route => !route.Closed);
      Assert.Equal(7, overlay.PointCount);
    });
  }

  [Fact]
  public void GeoPackage_reprojects_declared_epsg_coordinates_to_wgs84() {
    WithGeoPackage((path, connection) => {
      const string table = "mercator_features";
      using (SqliteCommand command = connection.CreateCommand()) {
        command.CommandText = """
            INSERT INTO gpkg_spatial_ref_sys VALUES
              ('Web Mercator', 3857, 'EPSG', 3857, 'undefined', 'Web Mercator');
            """;
        command.ExecuteNonQuery();
      }
      CreateFeatureLayer(connection, table, "shape", 3857);
      InsertGeometry(connection, table, "shape",
          new GeometryFactory().CreatePoint(
              new Coordinate(1113194.9079327357, 5621521.486192066)), 3857);
      connection.Close();

      ImportedMapOverlay overlay = GeoPackageOverlayReader.Read(path);

      ImportedOverlayMarker marker = Assert.Single(overlay.Markers);
      Assert.Equal(45, marker.Point.Lat, 5);
      Assert.Equal(10, marker.Point.Lng, 5);
    });
  }

  [Fact]
  public void Geometry_header_supports_big_endian_srs_and_optional_envelope() {
    var point = new GeometryFactory().CreatePoint(new Coordinate(12.5, -34.5));
    byte[] blob = EncodeGeometry(point, 3857, littleEndian: false, envelopeIndicator: 1);

    GeoPackageGeometry result = GeoPackageOverlayReader.ReadGeometry(blob);

    Assert.Equal(3857, result.SrsId);
    Assert.False(result.Empty);
    var decoded = Assert.IsType<Point>(result.Geometry);
    Assert.Equal(12.5, decoded.X);
    Assert.Equal(-34.5, decoded.Y);
  }

  [Fact]
  public void Invalid_or_extended_geometry_headers_are_rejected() {
    Assert.Throws<InvalidDataException>(() =>
        GeoPackageOverlayReader.ReadGeometry([0, 1, 2, 3]));

    byte[] blob = EncodeGeometry(
        new GeometryFactory().CreatePoint(new Coordinate(1, 2)), 4326);
    blob[3] |= 0x20;
    Assert.Throws<InvalidDataException>(() =>
        GeoPackageOverlayReader.ReadGeometry(blob));
  }

  [Fact]
  public void Flight_data_copy_preserves_geopackage_points_and_routes() {
    var source = new ImportedMapOverlay(
        [new ImportedOverlayRoute("line", [
          new ImportedGeoPoint(1, 2), new ImportedGeoPoint(3, 4),
        ], new ImportedMapColor(255, 0, 0))],
        [new ImportedOverlayMarker("point", new ImportedGeoPoint(5, 6))]);
    try {
      ImportedOverlayStore.CopyVectorGeometryToFlightData(source);

      Assert.Single(ImportedOverlayStore.FlightData.Routes);
      Assert.Single(ImportedOverlayStore.FlightData.Markers);
      Assert.Empty(ImportedOverlayStore.FlightData.Rasters);
    } finally {
      ImportedOverlayStore.ClearFlightData();
    }
  }

  private static void WithGeoPackage(Action<string, SqliteConnection> test) {
    string path = Path.Combine(Path.GetTempPath(), "mp-overlay-" + Guid.NewGuid() + ".gpkg");
    try {
      // A pooled connection keeps the file open after Dispose, and Windows then refuses to
      // delete it.
      using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
      connection.Open();
      using (SqliteCommand command = connection.CreateCommand()) {
        command.CommandText = """
            CREATE TABLE gpkg_spatial_ref_sys (
              srs_name TEXT NOT NULL,
              srs_id INTEGER NOT NULL PRIMARY KEY,
              organization TEXT NOT NULL,
              organization_coordsys_id INTEGER NOT NULL,
              definition TEXT NOT NULL,
              description TEXT);
            INSERT INTO gpkg_spatial_ref_sys VALUES
              ('WGS 84', 4326, 'EPSG', 4326, 'undefined', 'WGS84');
            CREATE TABLE gpkg_contents (
              table_name TEXT NOT NULL PRIMARY KEY,
              data_type TEXT NOT NULL,
              identifier TEXT,
              description TEXT DEFAULT '',
              last_change DATETIME,
              min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE,
              srs_id INTEGER);
            CREATE TABLE gpkg_geometry_columns (
              table_name TEXT NOT NULL,
              column_name TEXT NOT NULL,
              geometry_type_name TEXT NOT NULL,
              srs_id INTEGER NOT NULL,
              z TINYINT NOT NULL,
              m TINYINT NOT NULL,
              PRIMARY KEY (table_name, column_name));
            """;
        command.ExecuteNonQuery();
      }
      test(path, connection);
    } finally {
      File.Delete(path);
    }
  }

  private static void CreateFeatureLayer(
      SqliteConnection connection, string table, string geometryColumn, int srsId) {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = $"CREATE TABLE {Quote(table)} (id INTEGER PRIMARY KEY, " +
                          $"{Quote(geometryColumn)} BLOB);" +
                          "INSERT INTO gpkg_contents " +
                          "(table_name, data_type, identifier, srs_id) " +
                          "VALUES ($table, 'features', $table, $srs);" +
                          "INSERT INTO gpkg_geometry_columns " +
                          "(table_name, column_name, geometry_type_name, srs_id, z, m) " +
                          "VALUES ($table, $column, 'GEOMETRY', $srs, 2, 0);";
    command.Parameters.AddWithValue("$table", table);
    command.Parameters.AddWithValue("$column", geometryColumn);
    command.Parameters.AddWithValue("$srs", srsId);
    command.ExecuteNonQuery();
  }

  private static void InsertGeometry(
      SqliteConnection connection,
      string table,
      string column,
      Geometry geometry,
      int srsId = 4326) {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = $"INSERT INTO {Quote(table)} ({Quote(column)}) VALUES ($geometry)";
    command.Parameters.AddWithValue("$geometry", EncodeGeometry(geometry, srsId));
    command.ExecuteNonQuery();
  }

  private static byte[] EncodeGeometry(
      Geometry geometry,
      int srsId,
      bool littleEndian = true,
      int envelopeIndicator = 1) {
    int envelopeValues = envelopeIndicator switch {
      0 => 0,
      1 => 4,
      2 or 3 => 6,
      4 => 8,
      _ => throw new ArgumentOutOfRangeException(nameof(envelopeIndicator)),
    };
    byte[] wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false,
        emitZ: geometry.Coordinate is CoordinateZ).Write(geometry);
    var result = new byte[8 + envelopeValues * sizeof(double) + wkb.Length];
    result[0] = (byte)'G';
    result[1] = (byte)'P';
    result[2] = 0;
    result[3] = (byte)((envelopeIndicator << 1) | (littleEndian ? 1 : 0));
    WriteInt32(result.AsSpan(4, 4), srsId, littleEndian);
    if (envelopeValues > 0) {
      Envelope envelope = geometry.EnvelopeInternal;
      double[] values = envelopeIndicator switch {
        1 => [envelope.MinX, envelope.MaxX, envelope.MinY, envelope.MaxY],
        2 => [envelope.MinX, envelope.MaxX, envelope.MinY, envelope.MaxY, 0, 0],
        3 => [envelope.MinX, envelope.MaxX, envelope.MinY, envelope.MaxY, 0, 0],
        _ => [envelope.MinX, envelope.MaxX, envelope.MinY, envelope.MaxY, 0, 0, 0, 0],
      };
      for (int index = 0; index < values.Length; index++) {
        WriteDouble(result.AsSpan(8 + index * sizeof(double), sizeof(double)),
            values[index], littleEndian);
      }
    }
    wkb.CopyTo(result, 8 + envelopeValues * sizeof(double));
    return result;
  }

  private static void WriteInt32(Span<byte> destination, int value, bool littleEndian) {
    if (littleEndian) {
      BinaryPrimitives.WriteInt32LittleEndian(destination, value);
    } else {
      BinaryPrimitives.WriteInt32BigEndian(destination, value);
    }
  }

  private static void WriteDouble(Span<byte> destination, double value, bool littleEndian) {
    long bits = BitConverter.DoubleToInt64Bits(value);
    if (littleEndian) {
      BinaryPrimitives.WriteInt64LittleEndian(destination, bits);
    } else {
      BinaryPrimitives.WriteInt64BigEndian(destination, bits);
    }
  }

  private static string Quote(string identifier) =>
      '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
