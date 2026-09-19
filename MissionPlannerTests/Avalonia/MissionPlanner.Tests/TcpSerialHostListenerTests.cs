using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using MissionPlanner.Comms;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class TcpSerialHostListenerTests {
  [Fact]
  public async Task Receive_loop_delivers_client_bytes_without_outbound_traffic() {
    var serial = new TcpSerial();
    var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var host = new TcpSerialHostListener(
        IPAddress.Loopback,
        0,
        serial,
        received: (_, buffer, count) => received.TrySetResult(buffer[..count].ToArray()));
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, host.BoundPort);

    byte[] payload = [0xFD, 0x04, 0x01, 0x02, 0x03, 0x04];
    await client.GetStream().WriteAsync(payload);

    Assert.Equal(payload, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    host.Dispose();
    serial.Dispose();
  }

  [Fact]
  public async Task New_client_replaces_and_closes_the_previous_socket() {
    var serial = new TcpSerial();
    int callbacks = 0;
    using var host = new TcpSerialHostListener(
        IPAddress.Loopback, 0, serial, (_, _) => Interlocked.Increment(ref callbacks));
    using var first = new TcpClient();
    await first.ConnectAsync(IPAddress.Loopback, host.BoundPort);
    await WaitUntilAsync(() => serial.IsOpen && Volatile.Read(ref callbacks) == 1);
    TcpClient firstServer = serial.client;

    using var second = new TcpClient();
    await second.ConnectAsync(IPAddress.Loopback, host.BoundPort);
    await WaitUntilAsync(() => !ReferenceEquals(serial.client, firstServer)
        && Volatile.Read(ref callbacks) == 2);

    await Assert.ThrowsAnyAsync<Exception>(async () =>
        await firstServer.GetStream().WriteAsync(new byte[] { 1 }));
    Assert.True(serial.IsOpen);
    host.Dispose();
    serial.Dispose();
  }

  [Fact]
  public async Task Dispose_stops_a_pending_accept_without_faulting() {
    var serial = new TcpSerial();
    var host = new TcpSerialHostListener(IPAddress.Loopback, 0, serial);

    host.Dispose();

    await host.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(host.Completion.IsCompletedSuccessfully);
    serial.Dispose();
  }

  private static async Task WaitUntilAsync(Func<bool> predicate) {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while (!predicate()) {
      await Task.Delay(10, timeout.Token);
    }
  }

  [Fact]
  public async Task Dispose_is_bounded_when_received_callback_is_blocked() {
    using var serial = new TcpSerial();
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var host = new TcpSerialHostListener(IPAddress.Loopback, 0, serial,
        received: (_, _, _) => {
          entered.TrySetResult();
          release.Wait(TimeSpan.FromSeconds(10));
        });
    using var client = new TcpClient();
    Task? stopping = null;
    try {
      await client.ConnectAsync(IPAddress.Loopback, host.BoundPort);
      await client.GetStream().WriteAsync(new byte[] { 1 });
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
      stopping = Task.Run(host.Dispose);
      await stopping.WaitAsync(TimeSpan.FromSeconds(3));
    } finally {
      release.Set();
      if (stopping != null) {
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
      }
    }
  }

  [Fact]
  public async Task Replacement_client_is_accepted_while_old_callback_is_blocked() {
    using var serial = new TcpSerial();
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int connections = 0;
    using var host = new TcpSerialHostListener(IPAddress.Loopback, 0, serial,
        connected: (_, _) => {
          if (Interlocked.Increment(ref connections) == 2) replaced.TrySetResult();
        },
        received: (_, _, _) => {
          entered.TrySetResult();
          release.Wait(TimeSpan.FromSeconds(10));
        });
    using var first = new TcpClient();
    using var second = new TcpClient();
    try {
      await first.ConnectAsync(IPAddress.Loopback, host.BoundPort);
      await first.GetStream().WriteAsync(new byte[] { 1 });
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
      await second.ConnectAsync(IPAddress.Loopback, host.BoundPort);
      await replaced.Task.WaitAsync(TimeSpan.FromSeconds(3));
      Assert.Equal(2, Volatile.Read(ref connections));
    } finally {
      release.Set();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Dispose_from_callback_does_not_wait_for_its_own_task(bool fromReceive) {
    using var serial = new TcpSerial();
    var elapsed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
    void Stop(TcpSerialHostListener listener) {
      var timer = Stopwatch.StartNew();
      listener.Dispose();
      elapsed.TrySetResult(timer.Elapsed);
    }
    using var host = new TcpSerialHostListener(IPAddress.Loopback, 0, serial,
        connected: fromReceive ? null : (listener, _) => Stop(listener),
        received: fromReceive ? (listener, _, _) => Stop(listener) : null);
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, host.BoundPort);
    if (fromReceive) {
      await client.GetStream().WriteAsync(new byte[] { 1 });
    }
    Assert.True(await elapsed.Task.WaitAsync(TimeSpan.FromSeconds(3)) < TimeSpan.FromMilliseconds(500));
    await host.Completion.WaitAsync(TimeSpan.FromSeconds(3));
  }
}
