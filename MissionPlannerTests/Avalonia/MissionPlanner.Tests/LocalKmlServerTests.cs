using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class LocalKmlServerTests {
  [Fact]
  public void KmlBuildersEscapeNamesAndUseInvariantCoordinates() {
    string vehicle = LocalKmlServer.BuildVehicleKml([
      new LocalKmlVehicle("radio & plane", 35.125, 33.5, 42.25, 10, 2, -3),
    ]);
    string mission = LocalKmlServer.BuildMissionKml([
      new LocalKmlWaypoint("WP <1>", 35.125, 33.5, 42.25),
    ]);

    Assert.Contains("encoding=\"utf-8\"", vehicle, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("radio &amp; plane", vehicle, StringComparison.Ordinal);
    Assert.Contains("WP &lt;1&gt;", mission, StringComparison.Ordinal);
    Assert.Contains("33.5,35.125,42.25", mission, StringComparison.Ordinal);
    Assert.DoesNotContain(33.5.ToString("R", new CultureInfo("de-DE")), mission,
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoopbackServerPublishesLiveVehicleAndMissionLinks() {
    using var server = new LocalKmlServer(() => [
      new LocalKmlVehicle("1:2", 35.1, 33.2, 100, 90, 1, 2),
    ], preferredPort: 0);
    server.UpdateMission([
      new LocalKmlWaypoint("WP 1", 35.3, 33.4, 120),
    ]);
    Uri networkUri = server.EnsureStarted();
    using HttpClient client = Client();

    string network = await client.GetStringAsync(networkUri);
    string vehicles = await client.GetStringAsync(
        $"http://127.0.0.1:{server.BoundPort}/location.kml");
    string mission = await client.GetStringAsync(
        $"http://127.0.0.1:{server.BoundPort}/wps.kml");
    byte[] model = await client.GetByteArrayAsync(
        $"http://127.0.0.1:{server.BoundPort}/block_plane_0.dae");

    Assert.Contains($"http://127.0.0.1:{server.BoundPort}/location.kml", network,
        StringComparison.Ordinal);
    Assert.Contains("1:2", vehicles, StringComparison.Ordinal);
    Assert.Contains("33.4,35.3,120", mission, StringComparison.Ordinal);
    Assert.True(model.Length > 1_000);
  }

  [Fact]
  public async Task LegacyMutationAndWebsocketRoutesAreNotExposed() {
    using var server = new LocalKmlServer(() => [], preferredPort: 0);
    Uri uri = server.EnsureStarted();
    using HttpClient client = Client();

    using HttpResponseMessage guided = await client.GetAsync(
        $"http://127.0.0.1:{uri.Port}/guided?lat=1&lng=2&alt=3");
    using HttpResponseMessage websocket = await client.GetAsync(
        $"http://127.0.0.1:{uri.Port}/websocket/raw");
    using HttpResponseMessage post = await client.PostAsync(uri, new StringContent("ignored"));

    Assert.Equal(HttpStatusCode.NotFound, guided.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, websocket.StatusCode);
    Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
  }

  [Fact]
  public async Task OversizedRequestHeaderIsRejectedBeforeRouting() {
    using var server = new LocalKmlServer(() => [], preferredPort: 0);
    int port = server.EnsureStarted().Port;
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    await using NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Enumerable.Repeat((byte)'A', 8192).ToArray());
    using var reader = new StreamReader(stream);

    string response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));

    Assert.StartsWith("HTTP/1.1 431 ", response, StringComparison.Ordinal);
  }

  // Input left unread when a socket closes makes Windows reset the connection, which drops the
  // response before the client reads it. The next three tests cover the server's drain.
  [Fact]
  public async Task RejectedMethodWithALargeBodyStillGetsItsResponse() {
    using var server = new LocalKmlServer(() => [], preferredPort: 0);
    Uri uri = server.EnsureStarted();
    using HttpClient client = Client();

    // Many receive calls' worth of input, but under the server's 64 KiB drain limit.
    using HttpResponseMessage post = await client.PostAsync(
        uri, new ByteArrayContent(new byte[48 * 1024]));

    Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
  }

  [Fact]
  public async Task OversizedRequestHeaderWithTrailingInputStillGetsItsResponse() {
    using var server = new LocalKmlServer(() => [], preferredPort: 0);
    int port = server.EnsureStarted().Port;
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    await using NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Enumerable.Repeat((byte)'A', 8192 + 4096).ToArray());
    using var reader = new StreamReader(stream);

    string response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));

    Assert.StartsWith("HTTP/1.1 431 ", response, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RejectedRequestIsClosedWhenTheClientStopsSending() {
    using var server = new LocalKmlServer(() => [], preferredPort: 0);
    int port = server.EnsureStarted().Port;
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    await using NetworkStream stream = client.GetStream();
    // The announced body never arrives and the connection stays open.
    await stream.WriteAsync(Encoding.ASCII.GetBytes(
        "POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\n"));
    using var reader = new StreamReader(stream);

    // The drain gives up after about a second, well before the five-second request timeout.
    string response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(4));

    Assert.StartsWith("HTTP/1.1 405 ", response, StringComparison.Ordinal);
  }

  [Fact]
  public void DisposeReleasesTheLoopbackPort() {
    var server = new LocalKmlServer(() => [], preferredPort: 0);
    int port = server.EnsureStarted().Port;

    server.Dispose();

    var rebound = new TcpListener(IPAddress.Loopback, port);
    rebound.Start();
    rebound.Stop();
  }

  private static HttpClient Client() => new(new HttpClientHandler { UseProxy = false }) {
    Timeout = TimeSpan.FromSeconds(5),
  };
}
