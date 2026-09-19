using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Services;

namespace MissionPlanner.ViewModels;

public partial class SerialPassThroughViewModel : ViewModelBase, IDisposable {
  private readonly MAVLinkInterface _comPort = AppState.comPort;
  private readonly DispatcherTimer _poll;

  private TcpSerialHostListener? _tcpHost;
  private CountingCommsSerial? _stream;
  private volatile bool _writeBackEnabled;

  public SerialPassThroughViewModel() {
    RefreshPorts();
    SelectedBaud = 115200;

    _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    _poll.Tick += (_, _) => UpdateStatus();
    _poll.Start();

    UpdateStatus();
  }

  public ObservableCollection<string> Ports { get; } = new();

  public ObservableCollection<int> Bauds { get; } = new() {
      1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 921600,
  };

  [ObservableProperty]
  private string? _selectedPort;

  [ObservableProperty]
  private int _selectedBaud;

  [ObservableProperty]
  private bool _allowWriteBack;

  [ObservableProperty]
  private string _status = "Stopped.";

  [ObservableProperty]
  private string _connectButtonText = "Start";

  [ObservableProperty]
  private long _txBytes;

  [ObservableProperty]
  private long _rxBytes;

  public bool IsRunning =>
      _stream != null && _stream.IsOpen || _tcpHost != null;

  partial void OnAllowWriteBackChanged(bool value) {
    _writeBackEnabled = value;
    _comPort.MirrorStreamWrite = value;
    if (_stream != null) {

      foreach (var m in _comPort.Mirrors.Where(m => ReferenceEquals(m.MirrorStream, _stream))) {
        m.MirrorStreamWrite = value;
      }
    }
  }

  [RelayCommand]
  private void RefreshPorts() {
    var sel = SelectedPort;
    Ports.Clear();
    foreach (var p in SerialPort.GetPortNames().Distinct()) {
      Ports.Add(p);
    }

    Ports.Add("TCP Host - 14550");
    Ports.Add("UDP Host - 14550");
    SelectedPort = sel != null && Ports.Contains(sel) ? sel : Ports.FirstOrDefault();
  }

  [RelayCommand]
  private void ToggleConnect() {
    if (IsRunning) {
      Stop();
      return;
    }

    if (string.IsNullOrEmpty(SelectedPort)) {
      Status = "Pick a port first.";
      return;
    }

    try {
      ICommsSerial inner;
      switch (SelectedPort) {
        case "TCP Host - 14550": {
            var tcp = new TcpSerial();
            _stream = new CountingCommsSerial(tcp);
            _comPort.MirrorStream = _stream;
            _comPort.MirrorStreamWrite = AllowWriteBack;
            _writeBackEnabled = AllowWriteBack;
            foreach (var mirror in _comPort.Mirrors.Where(
                mirror => ReferenceEquals(mirror.MirrorStream, _stream))) {
              mirror.PollInput = false;
            }
            _tcpHost = new TcpSerialHostListener(
                IPAddress.Any, 14550, tcp, OnTcpClientConnected, OnTcpDataReceived);
            ConnectButtonText = "Stop";
            Status = "Listening on TCP 14550 — waiting for a client…";
            return;
          }

        case "UDP Host - 14550": {
            var udp = new UdpSerial(UdpSerial.CreateSharedListener(14550)) { Port = "14550" };
            inner = udp;
            break;
          }

        default:
          inner = new SerialPort { PortName = SelectedPort, BaudRate = SelectedBaud };
          break;
      }

      inner.BaudRate = SelectedBaud;
      _stream = new CountingCommsSerial(inner);
      _stream.Open();

      _comPort.MirrorStream = _stream;
      _comPort.MirrorStreamWrite = AllowWriteBack;

      ConnectButtonText = "Stop";
      Status = $"Mirroring on {SelectedPort}.";
    } catch (Exception ex) {
      Stop();
      Status = "Error connecting: " + ex.Message;
    }
  }

  private void OnTcpClientConnected(TcpSerialHostListener host, string remote) =>
      Dispatcher.UIThread.Post(() => {
        if (ReferenceEquals(_tcpHost, host)) {
          Status = $"Mirroring on TCP 14550 (client {remote} connected).";
        }
      });

  private void OnTcpDataReceived(TcpSerialHostListener host, byte[] buffer, int count) {
    CountingCommsSerial? stream = Volatile.Read(ref _stream);
    if (!ReferenceEquals(Volatile.Read(ref _tcpHost), host) || stream == null) {
      return;
    }

    stream.CountReceived(count);
    if (_writeBackEnabled) {
      _comPort.WriteMirrorDataToVehicle(buffer, 0, count);
    }
  }

  private void Stop() {
    try {
      Interlocked.Exchange(ref _tcpHost, null)?.Dispose();
    } catch {
    }

    CountingCommsSerial? stream = Interlocked.Exchange(ref _stream, null);
    try {
      if (stream != null) {
        _comPort.Mirrors.RemoveAll(m => ReferenceEquals(m.MirrorStream, stream));
        stream.Dispose();
      }
    } catch {
    }
    ConnectButtonText = "Start";
    Status = "Stopped.";
  }

  private void UpdateStatus() {
    if (_stream != null) {
      TxBytes = _stream.TxCount;
      RxBytes = _stream.RxCount;
    }

    OnPropertyChanged(nameof(IsRunning));
  }

  public void Dispose() {
    _poll.Stop();
    Stop();
  }

  private sealed class CountingCommsSerial : ICommsSerial {
    private readonly ICommsSerial _inner;
    private long _txCount;
    private long _rxCount;

    public CountingCommsSerial(ICommsSerial inner) => _inner = inner;

    public long TxCount => Interlocked.Read(ref _txCount);
    public long RxCount => Interlocked.Read(ref _rxCount);

    public void CountReceived(int count) => Interlocked.Add(ref _rxCount, count);

    public Stream BaseStream => _inner.BaseStream;
    public int BaudRate { get => _inner.BaudRate; set => _inner.BaudRate = value; }
    public int BytesToRead => _inner.BytesToRead;
    public int BytesToWrite => _inner.BytesToWrite;
    public int DataBits { get => _inner.DataBits; set => _inner.DataBits = value; }
    public bool DtrEnable { get => _inner.DtrEnable; set => _inner.DtrEnable = value; }
    public bool IsOpen => _inner.IsOpen;
    public string PortName { get => _inner.PortName; set => _inner.PortName = value; }
    public int ReadBufferSize { get => _inner.ReadBufferSize; set => _inner.ReadBufferSize = value; }
    public int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
    public bool RtsEnable { get => _inner.RtsEnable; set => _inner.RtsEnable = value; }
    public int WriteBufferSize {
      get => _inner.WriteBufferSize;
      set => _inner.WriteBufferSize = value;
    }
    public int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }

    public void Close() => _inner.Close();
    public void DiscardInBuffer() => _inner.DiscardInBuffer();
    public void Open() => _inner.Open();

    public int Read(byte[] buffer, int offset, int count) {
      int n = _inner.Read(buffer, offset, count);
      Interlocked.Add(ref _rxCount, n);
      return n;
    }

    public int ReadByte() {
      int b = _inner.ReadByte();
      if (b >= 0) {
        Interlocked.Increment(ref _rxCount);
      }

      return b;
    }

    public int ReadChar() {
      int c = _inner.ReadChar();
      if (c >= 0) {
        Interlocked.Increment(ref _rxCount);
      }

      return c;
    }

    public string ReadExisting() {
      string s = _inner.ReadExisting() ?? string.Empty;
      Interlocked.Add(ref _rxCount, s.Length);
      return s;
    }

    public string ReadLine() {
      string s = _inner.ReadLine() ?? string.Empty;
      Interlocked.Add(ref _rxCount, s.Length);
      return s;
    }

    public void Write(string text) {
      _inner.Write(text);
      Interlocked.Add(ref _txCount, text?.Length ?? 0);
    }

    public void Write(byte[] buffer, int offset, int count) {
      _inner.Write(buffer, offset, count);
      Interlocked.Add(ref _txCount, count);
    }

    public void WriteLine(string text) {
      _inner.WriteLine(text);
      Interlocked.Add(ref _txCount, (text?.Length ?? 0) + 1);
    }

    public void toggleDTR() => _inner.toggleDTR();
    public void Dispose() => _inner.Dispose();
  }
}
