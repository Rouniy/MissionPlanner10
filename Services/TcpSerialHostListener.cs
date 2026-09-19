using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MissionPlanner.Comms;

namespace MissionPlanner.Services;

/// <summary>
/// Owns the accept loop for a <see cref="TcpSerial"/> used as a TCP host. Only the newest
/// accepted client remains attached; superseded and late-after-stop sockets are closed promptly.
/// </summary>
internal sealed class TcpSerialHostListener : IDisposable {
  // Callbacks are synchronous, but receive tasks from replacement clients can
  // overlap. A shared thread-id field would be overwritten by another callback.
  [ThreadStatic]
  private static TcpSerialHostListener? _callbackOwner;

  private readonly object _sync = new();
  private readonly TcpSerial _serial;
  private readonly TcpListener _listener;
  private readonly CancellationTokenSource _stop = new();
  private readonly Action<TcpSerialHostListener, string>? _connected;
  private readonly Action<TcpSerialHostListener, byte[], int>? _received;
  private readonly Task _acceptTask;
  private CancellationTokenSource? _clientStop;
  private Task? _clientReadTask;
  private bool _disposed;

  internal TcpSerialHostListener(
      IPAddress address,
      int port,
      TcpSerial serial,
      Action<TcpSerialHostListener, string>? connected = null,
      Action<TcpSerialHostListener, byte[], int>? received = null) {
    ArgumentNullException.ThrowIfNull(address);
    ArgumentNullException.ThrowIfNull(serial);
    _serial = serial;
    _connected = connected;
    _received = received;
    _listener = new TcpListener(address, port);
    try {
      _listener.Start(1);
      BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
      _acceptTask = AcceptLoopAsync(_stop.Token);
    } catch {
      _listener.Stop();
      _stop.Dispose();
      throw;
    }
  }

  internal int BoundPort { get; }
  internal Task Completion => _acceptTask;

  private async Task AcceptLoopAsync(CancellationToken cancellationToken) {
    try {
      while (true) {
        TcpClient accepted = await _listener.AcceptTcpClientAsync(cancellationToken)
            .ConfigureAwait(false);
        TcpClient? previous = null;
        CancellationTokenSource? previousClientStop = null;
        string remote = "client";
        bool keep;
        try {
          accepted.NoDelay = true;
          remote = accepted.Client.RemoteEndPoint?.ToString() ?? remote;
          lock (_sync) {
            keep = !_disposed;
            if (keep) {
              previous = _serial.client;
              previousClientStop = _clientStop;
              _serial.client = accepted;
              if (_received != null) {
                _clientStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken clientToken = _clientStop.Token;
                // Never execute buffered reads/callbacks inline under _sync or
                // on a caller's UI synchronization context.
                _clientReadTask = Task.Run(() => ReadLoopAsync(accepted, clientToken));
              } else {
                _clientStop = null;
                _clientReadTask = null;
              }
            }
          }
        } catch {
          accepted.Dispose();
          throw;
        }

        if (!keep) {
          accepted.Dispose();
          return;
        }
        previousClientStop?.Cancel();
        previousClientStop?.Dispose();
        previous?.Dispose();
        InvokeCallback(() => _connected?.Invoke(this, remote), "connection");
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) {
    } catch (SocketException) when (cancellationToken.IsCancellationRequested) {
    } catch (Exception ex) {
      Trace.WriteLine($"TCP host accept loop stopped: {ex}");
    }
  }

  private async Task ReadLoopAsync(TcpClient client, CancellationToken cancellationToken) {
    var buffer = new byte[8192];
    try {
      NetworkStream stream = client.GetStream();
      while (true) {
        int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (count == 0) {
          return;
        }

        lock (_sync) {
          if (_disposed || !ReferenceEquals(_serial.client, client)) {
            return;
          }
        }
        // Vehicle writes may block. Do not hold the accept/dispose lock while
        // dispatching them; the consumer must also check its current ownership.
        InvokeCallback(() => _received?.Invoke(this, buffer, count), "receive");
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
    } catch (ObjectDisposedException) {
    } catch (IOException) {
    } catch (SocketException) {
    } catch (Exception ex) {
      Trace.WriteLine($"TCP host receive loop stopped: {ex}");
    }
  }

  private void InvokeCallback(Action callback, string kind) {
    TcpSerialHostListener? previous = _callbackOwner;
    _callbackOwner = this;
    try {
      callback();
    } catch (Exception ex) {
      Trace.WriteLine($"TCP host {kind} callback failed: {ex}");
    } finally {
      _callbackOwner = previous;
    }
  }

  public void Dispose() {
    CancellationTokenSource? clientStop;
    Task? clientReadTask;
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      clientStop = _clientStop;
      clientReadTask = _clientReadTask;
      _clientStop = null;
      _clientReadTask = null;
    }
    _stop.Cancel();
    clientStop?.Cancel();
    try {
      _listener.Stop();
    } catch {
    }
    // Task.CurrentId is normally null in async continuations. Both callbacks
    // can call Dispose; neither may wait for the task currently invoking it.
    bool fromCallback = ReferenceEquals(_callbackOwner, this);
    if (!_acceptTask.IsCompleted && !fromCallback) {
      try {
        _acceptTask.Wait(TimeSpan.FromSeconds(1));
      } catch (AggregateException ex) {
        Trace.WriteLine($"TCP host accept loop cleanup failed: {ex.Flatten()}");
      }
    }
    if (clientReadTask != null && !clientReadTask.IsCompleted && !fromCallback) {
      try {
        clientReadTask.Wait(TimeSpan.FromSeconds(1));
      } catch (AggregateException ex) {
        Trace.WriteLine($"TCP host receive loop cleanup failed: {ex.Flatten()}");
      }
    }
    clientStop?.Dispose();
    _stop.Dispose();
  }
}
