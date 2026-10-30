using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;

namespace MissionPlanner.ViewModels;

public partial class DroneCANInspectorViewModel : ViewModelBase, IDisposable {
  private readonly DroneCanBridge _bridge = new();
  private readonly PacketInspector<(DroneCAN.CANFrame frame, object message)> _pkt = new();
  private readonly DispatcherTimer _timer;
  private DroneCAN.DroneCAN.MessageRecievedDel? _onMessage;

  public ObservableCollection<InspectorNode> Tree { get; } = new();
  private readonly Dictionary<string, InspectorNode> _rootMap = new();

  public string[] BusOptions { get; } = { "MAVLink CAN1", "MAVLink CAN2" };

  [ObservableProperty]
  private int _selectedBusIndex;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(GraphSelectedCommand))]
  [NotifyCanExecuteChangedFor(nameof(SubscribeSelectedCommand))]
  private InspectorNode? _selectedNode;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(GraphSelectedCommand))]
  [NotifyCanExecuteChangedFor(nameof(SubscribeSelectedCommand))]
  private bool _isConnected;

  [ObservableProperty]
  private bool _isPaused;

  [ObservableProperty]
  private string _status = "Connect over the active MAVLink link to inspect the DroneCAN bus.";

  public DroneCANInspectorViewModel() {

    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(333) };
    _timer.Tick += (_, _) => Rebuild();
  }

  public string ConnectLabel => IsConnected ? "Disconnect" : "Connect";

  partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(ConnectLabel));

  public string PauseLabel => IsPaused ? "Resume" : "Pause";

  partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));

  [RelayCommand]
  private void ToggleConnect() {
    if (IsConnected) {
      Stop();
      Status = "Disconnected.";
      return;
    }

    byte bus = (byte)(SelectedBusIndex == 1 ? 2 : 1);
    if (!_bridge.Start(bus)) {
      Status = _bridge.LastError ?? "Not connected — open the MAVLink link first.";
      return;
    }

    _onMessage = (frame, msg, _) =>
        _pkt.Add(frame.SourceNode, 0, frame.MsgTypeID, (frame, msg), frame.SizeofEntireMsg);
    _bridge.Can!.MessageReceived += _onMessage;

    _timer.Start();
    IsConnected = true;
    Status = $"Listening on MAVLink CAN{bus}…";
  }

  [RelayCommand]
  private void TogglePause() => IsPaused = !IsPaused;

  [RelayCommand]
  private void Clear() {
    _pkt.Clear();
    SelectedNode = null;
    Tree.Clear();
    _rootMap.Clear();
  }

  private bool CanGraphSelected() =>
      IsConnected && SelectedNode?.DroneCanGraphSelection != null;

  [RelayCommand(CanExecute = nameof(CanGraphSelected))]
  private async Task GraphSelected() {
    if (_bridge.Can == null || SelectedNode?.DroneCanGraphSelection is not { } selection) {
      return;
    }
    string? entered = await Services.Dialogs.InputBox(
        "DroneCAN Graph", "Points of history (10..100000)", "500");
    if (entered == null) {
      return;
    }
    if (!int.TryParse(entered, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int history) || history is < 10 or > 100000) {
      await Services.Dialogs.Alert("DroneCAN Graph", "Enter a point count from 10 to 100000.");
      return;
    }

    var window = new Views.DroneCanFieldGraphWindow(_bridge.Can, selection, history);
    if (Services.Dialogs.Owner is { } owner) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private bool CanSubscribeSelected() =>
      IsConnected && SelectedNode?.DroneCanMessageSelection != null;

  [RelayCommand(CanExecute = nameof(CanSubscribeSelected))]
  private void SubscribeSelected() {
    if (_bridge.Can == null || SelectedNode?.DroneCanMessageSelection is not { } selection) {
      return;
    }
    var window = new Views.DroneCanSubscriberWindow(_bridge.Can, selection);
    if (Services.Dialogs.Owner is { } owner) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }

  private void Rebuild() {
    if (IsPaused || _bridge.Can == null) {
      return;
    }

    foreach (var dcMsg in _pkt.GetPacketMessages()) {
      var nodeKey = dcMsg.frame.SourceNode.ToString();
      var nodeNode = GetOrAdd(_rootMap, Tree, nodeKey,
          () => new InspectorNode { Key = nodeKey, Header = "ID " + dcMsg.frame.SourceNode });

      nodeNode.Header = "ID " + dcMsg.frame.SourceNode + " - " +
                        _bridge.Can.GetNodeName(dcMsg.frame.SourceNode) + " " +
                        _pkt.SeenBps(dcMsg.frame.SourceNode, 0).ToString("~0Bps");

      var msgKey = dcMsg.frame.MsgTypeID.ToString();
      var msgNode = GetOrAdd(nodeNode.Map, nodeNode.Children, msgKey,
          () => new InspectorNode { Key = msgKey, Header = msgKey });
      var messageSelection = new DroneCanMessageSelection(
          dcMsg.frame.SourceNode, dcMsg.frame.MsgTypeID, dcMsg.message.GetType().Name);
      msgNode.DroneCanMessageSelection = messageSelection;

      var header = dcMsg.message.GetType().Name + " (" +
                   _pkt.SeenRate(dcMsg.frame.SourceNode, 0, dcMsg.frame.MsgTypeID).ToString("0.0 Hz") +
                   ", #" + dcMsg.frame.MsgTypeID + ") " +
                   _pkt.SeenBps(dcMsg.frame.SourceNode, 0, dcMsg.frame.MsgTypeID).ToString("~0Bps");
      if (msgNode.Header != header) {
        msgNode.Header = header;
      }

      var fields = dcMsg.message.GetType().GetFields().Where(f => !f.IsLiteral).ToArray();
      PopulateMsg(fields, msgNode, dcMsg.message, messageSelection, []);
    }
  }

  private static void PopulateMsg(
      FieldInfo[] fields,
      InspectorNode node,
      object message,
      DroneCanMessageSelection messageSelection,
      IReadOnlyList<string> parentPath) {
    foreach (var field in fields) {
      var fieldNode = GetOrAdd(node.Map, node.Children, field.Name,
          () => new InspectorNode { Key = field.Name });
      fieldNode.DroneCanMessageSelection = messageSelection;
      string[] fieldPath = [.. parentPath, field.Name];
      if (Views.DroneCanGraphSampleExtractor.IsSupportedType(field.FieldType)) {
        fieldNode.DroneCanGraphSelection = new DroneCanGraphSelection(
            messageSelection.NodeId, messageSelection.MessageId,
            messageSelection.MessageName, fieldPath);
      }

      object? value = field.GetValue(message);

      if (field.Name == "time_unix_usec") {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try {
          value = epoch.AddMilliseconds((ulong)value! / 1000);
        } catch {
        }
      }

      if (field.FieldType.IsArray && value is Array arr) {
        if (field.Name is "param_id" or "text" or "string_value" or "name") {
          value = Encoding.ASCII.GetString((byte[])arr);
        } else if (arr.Length > 0) {
          var elementType = field.FieldType.GetElementType();
          if (elementType != null && !elementType.IsPrimitive && elementType.IsClass &&
              elementType != typeof(string)) {
            var elementFields = elementType.GetFields().Where(f => !f.IsLiteral).ToArray();
            fieldNode.Header = field.Name;
            int a = 0;
            foreach (var element in arr) {
              var name = field.Name + "[" + a + "]";
              var elementNode = GetOrAdd(fieldNode.Map, fieldNode.Children, name,
                  () => new InspectorNode { Key = name, Header = name });
              elementNode.DroneCanMessageSelection = messageSelection;
              if (element != null) {
                PopulateMsg(elementFields, elementNode, element, messageSelection,
                    [.. parentPath, name]);
              }

              a++;
            }

            continue;
          }

          value = string.Join(",", arr.Cast<object?>().Select(item => item?.ToString() ?? ""));
        } else {
          value = null;
        }
      }

      if (!field.FieldType.IsArray && field.FieldType.IsClass && field.FieldType != typeof(string)) {
        fieldNode.Header = field.Name;
        if (value != null) {
          PopulateMsg(field.FieldType.GetFields().Where(f => !f.IsLiteral).ToArray(),
              fieldNode, value, messageSelection, fieldPath);
        }

        continue;
      }

      var text = string.Format("{0,-32} {1,20} {2,-20}", field.Name, value, field.FieldType.Name);
      if (fieldNode.Header != text) {
        fieldNode.Header = text;
      }
    }
  }

  private static InspectorNode GetOrAdd(Dictionary<string, InspectorNode> map,
      ObservableCollection<InspectorNode> children, string key, Func<InspectorNode> factory) {
    if (map.TryGetValue(key, out var existing)) {
      return existing;
    }

    var node = factory();
    map[key] = node;
    int i = 0;
    while (i < children.Count && string.CompareOrdinal(children[i].Key, node.Key) < 0) {
      i++;
    }

    children.Insert(i, node);
    return node;
  }

  private void Stop() {
    _timer.Stop();
    if (_onMessage != null && _bridge.Can != null) {
      _bridge.Can.MessageReceived -= _onMessage;
    }

    _onMessage = null;
    _bridge.Stop();
    IsConnected = false;
  }

  public void Dispose() => Stop();
}

public readonly record struct DroneCanMessageSelection(
    byte NodeId, uint MessageId, string MessageName);

public readonly record struct DroneCanGraphSelection(
    byte NodeId, uint MessageId, string MessageName, IReadOnlyList<string> FieldPath) {
  public string FieldName => string.Join(".", FieldPath);
}
