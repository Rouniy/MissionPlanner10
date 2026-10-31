using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MissionPlanner.Services;

internal sealed record LocalKmlVehicle(
    string Name,
    double Latitude,
    double Longitude,
    double Altitude,
    double Heading,
    double Roll,
    double Pitch);

internal sealed record LocalKmlWaypoint(string Name, double Latitude, double Longitude, double Altitude);

/// <summary>
/// Read-only, loopback-only replacement for Mission Planner's always-on legacy HTTP server.
/// It retains the visible live Google Earth workflow without exposing guided-mode writes, raw
/// MAVLink injection, filesystem routing or unauthenticated telemetry on network interfaces.
/// </summary>
internal sealed class LocalKmlServer : IDisposable {
  internal const int DefaultPort = 56781;
  private const int MaxHeaderBytes = 8192;
  private const int MaxResponseBytes = 8 * 1024 * 1024;
  private const int MaxDrainBytes = 64 * 1024;
  private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);
  private readonly object _lifecycle = new();
  private readonly Func<IReadOnlyList<LocalKmlVehicle>> _vehicleSource;
  private readonly int _preferredPort;
  private readonly SemaphoreSlim _clients = new(8, 8);
  private readonly ConcurrentDictionary<int, TcpClient> _activeClients = new();
  private readonly CancellationTokenSource _stop = new();
  private LocalKmlWaypoint[] _mission = [];
  private TcpListener? _listener;
  private Task? _runTask;
  private int _nextClientId;
  private int _disposed;

  internal LocalKmlServer(
      Func<IReadOnlyList<LocalKmlVehicle>>? vehicleSource = null,
      int preferredPort = DefaultPort) {
    if (preferredPort is < 0 or > 65535) {
      throw new ArgumentOutOfRangeException(nameof(preferredPort));
    }
    _vehicleSource = vehicleSource ?? CaptureAppVehicles;
    _preferredPort = preferredPort;
  }

  internal int BoundPort { get; private set; }

  internal Uri EnsureStarted() {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    lock (_lifecycle) {
      if (_listener == null) {
        var listener = new TcpListener(IPAddress.Loopback, _preferredPort);
        listener.Start(backlog: 8);
        _listener = listener;
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _runTask = Task.Run(() => RunAsync(listener, _stop.Token));
      }
      return new Uri($"http://127.0.0.1:{BoundPort}/network.kml", UriKind.Absolute);
    }
  }

  internal void UpdateMission(IEnumerable<LocalKmlWaypoint> mission) {
    ArgumentNullException.ThrowIfNull(mission);
    LocalKmlWaypoint[] snapshot = mission
        .Where(point => double.IsFinite(point.Latitude)
            && double.IsFinite(point.Longitude)
            && double.IsFinite(point.Altitude)
            && (point.Latitude != 0 || point.Longitude != 0))
        .Take(100_000)
        .ToArray();
    Volatile.Write(ref _mission, snapshot);
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) {
      return;
    }
    _stop.Cancel();
    lock (_lifecycle) {
      _listener?.Stop();
      _listener = null;
    }
    foreach (TcpClient client in _activeClients.Values) {
      client.Dispose();
    }
    try {
      _runTask?.Wait(TimeSpan.FromSeconds(2));
    } catch (AggregateException error) when (error.InnerExceptions.All(
        exception => exception is OperationCanceledException or ObjectDisposedException
            or SocketException)) {
    }
    _stop.Dispose();
  }

  private async Task RunAsync(TcpListener listener, CancellationToken cancellationToken) {
    try {
      while (!cancellationToken.IsCancellationRequested) {
        TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!_clients.Wait(0)) {
          client.Dispose();
          continue;
        }
        int id = Interlocked.Increment(ref _nextClientId);
        _activeClients[id] = client;
        _ = HandleAndReleaseAsync(id, client, cancellationToken);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) {
    } catch (SocketException) when (cancellationToken.IsCancellationRequested) {
    }
  }

  private async Task HandleAndReleaseAsync(
      int id, TcpClient client, CancellationToken cancellationToken) {
    try {
      await HandleAsync(client, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (IOException) {
    } catch (SocketException) {
    } catch (ObjectDisposedException) {
      // Dispose() closed the socket under a drain in progress.
    } finally {
      _activeClients.TryRemove(id, out _);
      client.Dispose();
      _clients.Release();
    }
  }

  private async Task HandleAsync(TcpClient client, CancellationToken applicationStop) {
    if (client.Client.RemoteEndPoint is not IPEndPoint endpoint
        || !IPAddress.IsLoopback(endpoint.Address)) {
      return;
    }
    client.NoDelay = true;
    await using NetworkStream stream = client.GetStream();
    using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(applicationStop);
    requestStop.CancelAfter(RequestTimeout);
    string? header = await ReadHeaderAsync(stream, requestStop.Token).ConfigureAwait(false);
    if (header == null) {
      await WriteResponseAsync(stream, 431, "Request Header Fields Too Large", "text/plain",
          Encoding.UTF8.GetBytes("Request header is too large."), headOnly: false,
          requestStop.Token).ConfigureAwait(false);
      await DrainRejectedRequestAsync(client.Client, requestStop.Token).ConfigureAwait(false);
      return;
    }

    string requestLine = header.Split("\r\n", 2, StringSplitOptions.None)[0];
    string[] request = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (request.Length != 3 || (request[0] != "GET" && request[0] != "HEAD")) {
      await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain",
          Encoding.UTF8.GetBytes("Only GET and HEAD are supported."), headOnly: false,
          requestStop.Token, "Allow: GET, HEAD\r\n").ConfigureAwait(false);
      await DrainRejectedRequestAsync(client.Client, requestStop.Token).ConfigureAwait(false);
      return;
    }
    if (!Uri.TryCreate("http://127.0.0.1" + request[1], UriKind.Absolute, out Uri? target)) {
      await WriteNotFoundAsync(stream, request[0] == "HEAD", requestStop.Token).ConfigureAwait(false);
      return;
    }

    byte[] body;
    string contentType;
    switch (target.AbsolutePath) {
      case "/network.kml":
        body = Utf8(BuildNetworkKml(BoundPort));
        contentType = "application/vnd.google-earth.kml+xml; charset=utf-8";
        break;
      case "/location.kml":
        body = Utf8(BuildVehicleKml(SafeVehicles()));
        contentType = "application/vnd.google-earth.kml+xml; charset=utf-8";
        break;
      case "/wps.kml":
        body = Utf8(BuildMissionKml(Volatile.Read(ref _mission)));
        contentType = "application/vnd.google-earth.kml+xml; charset=utf-8";
        break;
      case "/block_plane_0.dae":
        string modelPath = Path.Combine(AppContext.BaseDirectory, "block_plane_0.dae");
        if (!File.Exists(modelPath)) {
          await WriteNotFoundAsync(stream, request[0] == "HEAD", requestStop.Token)
              .ConfigureAwait(false);
          return;
        }
        var info = new FileInfo(modelPath);
        if (info.Length > MaxResponseBytes) {
          await WriteResponseAsync(stream, 413, "Content Too Large", "text/plain",
              Encoding.UTF8.GetBytes("Model is too large."), request[0] == "HEAD",
              requestStop.Token).ConfigureAwait(false);
          return;
        }
        body = await File.ReadAllBytesAsync(modelPath, requestStop.Token).ConfigureAwait(false);
        contentType = "model/vnd.collada+xml";
        break;
      default:
        await WriteNotFoundAsync(stream, request[0] == "HEAD", requestStop.Token)
            .ConfigureAwait(false);
        return;
    }

    if (body.Length > MaxResponseBytes) {
      await WriteResponseAsync(stream, 413, "Content Too Large", "text/plain",
          Encoding.UTF8.GetBytes("KML response is too large."), request[0] == "HEAD",
          requestStop.Token).ConfigureAwait(false);
      return;
    }
    await WriteResponseAsync(stream, 200, "OK", contentType, body, request[0] == "HEAD",
        requestStop.Token).ConfigureAwait(false);
  }

  private IReadOnlyList<LocalKmlVehicle> SafeVehicles() {
    try {
      return _vehicleSource();
    } catch {
      return [];
    }
  }

  private static async Task<string?> ReadHeaderAsync(Stream stream, CancellationToken token) {
    byte[] buffer = new byte[MaxHeaderBytes];
    int used = 0;
    while (used < buffer.Length) {
      int read = await stream.ReadAsync(buffer.AsMemory(used, 1), token).ConfigureAwait(false);
      if (read == 0) {
        return used == 0 ? null : Encoding.ASCII.GetString(buffer, 0, used);
      }
      used += read;
      if (used >= 4 && buffer[used - 4] == '\r' && buffer[used - 3] == '\n'
          && buffer[used - 2] == '\r' && buffer[used - 1] == '\n') {
        return Encoding.ASCII.GetString(buffer, 0, used);
      }
    }
    return null;
  }

  // A request rejected before all of it was read leaves input in the receive buffer. Closing
  // such a socket makes Windows send a reset, which can discard the response before the client
  // reads it, so finish sending first and then read the leftover input for a bounded time.
  private static async Task DrainRejectedRequestAsync(Socket socket, CancellationToken token) {
    socket.Shutdown(SocketShutdown.Send);
    using var drainStop = CancellationTokenSource.CreateLinkedTokenSource(token);
    drainStop.CancelAfter(DrainTimeout);
    byte[] buffer = new byte[4096];
    int drained = 0;
    try {
      while (drained < MaxDrainBytes) {
        int read = await socket.ReceiveAsync(buffer, SocketFlags.None, drainStop.Token)
            .ConfigureAwait(false);
        if (read == 0) {
          return;
        }
        drained += read;
      }
    } catch (OperationCanceledException) {
      // The client is still sending or keeps the connection open; close it anyway.
    }
  }

  private static Task WriteNotFoundAsync(
      Stream stream, bool headOnly, CancellationToken token) =>
      WriteResponseAsync(stream, 404, "Not Found", "text/plain",
          Encoding.UTF8.GetBytes("Not found."), headOnly, token);

  private static async Task WriteResponseAsync(
      Stream stream,
      int status,
      string reason,
      string contentType,
      byte[] body,
      bool headOnly,
      CancellationToken token,
      string extraHeaders = "") {
    string header = $"HTTP/1.1 {status} {reason}\r\n" +
        "Connection: close\r\n" +
        "Cache-Control: no-store\r\n" +
        "X-Content-Type-Options: nosniff\r\n" +
        extraHeaders +
        $"Content-Type: {contentType}\r\n" +
        $"Content-Length: {body.Length}\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).ConfigureAwait(false);
    if (!headOnly) {
      await stream.WriteAsync(body, token).ConfigureAwait(false);
    }
  }

  internal static string BuildNetworkKml(int port) => BuildXml(writer => {
    writer.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
    writer.WriteStartElement("Folder");
    writer.WriteElementString("name", "Mission Planner live links");
    WriteNetworkLink(writer, "Vehicles", $"http://127.0.0.1:{port}/location.kml", 1,
        flyToView: true);
    WriteNetworkLink(writer, "Mission", $"http://127.0.0.1:{port}/wps.kml", 1,
        flyToView: false);
    writer.WriteEndElement();
    writer.WriteEndElement();
  });

  internal static string BuildVehicleKml(IEnumerable<LocalKmlVehicle> vehicles) =>
      BuildXml(writer => {
        writer.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
        writer.WriteStartElement("Document");
        writer.WriteElementString("name", "Mission Planner vehicles");
        foreach (LocalKmlVehicle vehicle in vehicles.Where(IsValidVehicle).Take(1_000)) {
          writer.WriteStartElement("Placemark");
          writer.WriteElementString("name", vehicle.Name);
          writer.WriteStartElement("Model");
          writer.WriteElementString("altitudeMode", "absolute");
          writer.WriteStartElement("Location");
          WriteNumber(writer, "longitude", vehicle.Longitude);
          WriteNumber(writer, "latitude", vehicle.Latitude);
          WriteNumber(writer, "altitude", Math.Max(0.01, vehicle.Altitude));
          writer.WriteEndElement();
          writer.WriteStartElement("Orientation");
          WriteNumber(writer, "heading", vehicle.Heading);
          WriteNumber(writer, "roll", -vehicle.Roll);
          WriteNumber(writer, "tilt", -vehicle.Pitch);
          writer.WriteEndElement();
          writer.WriteStartElement("Scale");
          writer.WriteElementString("x", "2");
          writer.WriteElementString("y", "2");
          writer.WriteElementString("z", "2");
          writer.WriteEndElement();
          writer.WriteStartElement("Link");
          writer.WriteElementString("href", "block_plane_0.dae");
          writer.WriteEndElement();
          writer.WriteEndElement();
          writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
      });

  internal static string BuildMissionKml(IEnumerable<LocalKmlWaypoint> mission) =>
      BuildXml(writer => {
        LocalKmlWaypoint[] points = mission.Where(IsValidWaypoint).Take(100_000).ToArray();
        writer.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
        writer.WriteStartElement("Document");
        writer.WriteElementString("name", "Mission Planner mission");
        foreach (LocalKmlWaypoint point in points) {
          writer.WriteStartElement("Placemark");
          writer.WriteElementString("name", point.Name);
          writer.WriteStartElement("Point");
          writer.WriteElementString("altitudeMode", "absolute");
          writer.WriteElementString("coordinates", Coordinates(point));
          writer.WriteEndElement();
          writer.WriteEndElement();
        }
        if (points.Length > 0) {
          writer.WriteStartElement("Placemark");
          writer.WriteElementString("name", "Mission route");
          writer.WriteStartElement("LineString");
          writer.WriteElementString("altitudeMode", "absolute");
          writer.WriteElementString("tessellate", "1");
          writer.WriteElementString("coordinates", string.Join(' ', points.Select(Coordinates)));
          writer.WriteEndElement();
          writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
      });

  private static string BuildXml(Action<XmlWriter> write) {
    var output = new StringBuilder();
    using var text = new Utf8StringWriter(output);
    using (XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings {
      Encoding = Encoding.UTF8,
      Indent = true,
      OmitXmlDeclaration = false,
    })) {
      write(writer);
    }
    return output.ToString();
  }

  private static void WriteNetworkLink(
      XmlWriter writer, string name, string href, int refreshSeconds, bool flyToView) {
    writer.WriteStartElement("NetworkLink");
    writer.WriteElementString("name", name);
    writer.WriteElementString("flyToView", flyToView ? "1" : "0");
    writer.WriteStartElement("Link");
    writer.WriteElementString("href", href);
    writer.WriteElementString("refreshMode", "onInterval");
    writer.WriteElementString("refreshInterval", refreshSeconds.ToString(CultureInfo.InvariantCulture));
    writer.WriteEndElement();
    writer.WriteEndElement();
  }

  private static void WriteNumber(XmlWriter writer, string name, double value) =>
      writer.WriteElementString(name, value.ToString("R", CultureInfo.InvariantCulture));

  private static string Coordinates(LocalKmlWaypoint point) => string.Join(',',
      point.Longitude.ToString("R", CultureInfo.InvariantCulture),
      point.Latitude.ToString("R", CultureInfo.InvariantCulture),
      point.Altitude.ToString("R", CultureInfo.InvariantCulture));

  private static bool IsValidWaypoint(LocalKmlWaypoint point) =>
      double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude)
      && double.IsFinite(point.Altitude) && (point.Latitude != 0 || point.Longitude != 0);

  private static bool IsValidVehicle(LocalKmlVehicle vehicle) =>
      double.IsFinite(vehicle.Latitude) && double.IsFinite(vehicle.Longitude)
      && double.IsFinite(vehicle.Altitude) && double.IsFinite(vehicle.Heading)
      && double.IsFinite(vehicle.Roll) && double.IsFinite(vehicle.Pitch)
      && (vehicle.Latitude != 0 || vehicle.Longitude != 0);

  private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

  private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder) {
    public override Encoding Encoding => Encoding.UTF8;
  }

  private static IReadOnlyList<LocalKmlVehicle> CaptureAppVehicles() {
    var vehicles = new List<LocalKmlVehicle>();
    foreach (MavLinkConnection connection in AppState.Connections.Snapshot()) {
      try {
        foreach (MAVState state in connection.Link.MAVlist.ToArray()) {
          CurrentState current = state.cs;
          vehicles.Add(new LocalKmlVehicle(
              $"{state.sysid}:{state.compid}",
              current.lat,
              current.lng,
              current.altasl,
              current.yaw,
              current.roll,
              current.pitch));
        }
      } catch {
      }
    }
    return vehicles;
  }
}
