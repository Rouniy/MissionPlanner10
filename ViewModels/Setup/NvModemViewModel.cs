using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Services;

namespace MissionPlanner.ViewModels.Setup;

public sealed record NvRadioChoice(int Channel, string Label) {
  public override string ToString() => Label;
}

public partial class NvModemDeviceChoice : ObservableObject {
  internal NvModemDeviceChoice(NvModemDeviceState state) {
    State = state;
  }

  internal NvModemDeviceState State { get; }

  [ObservableProperty]
  private string _label = "NV modem";

  public override string ToString() => Label;
}

public partial class NvModemParameterRow : ObservableObject {
  internal NvModemParameterRow(string name, double original, byte type) {
    Name = name;
    Group = NvModemCatalog.Group(name);
    Description = NvModemCatalog.Description(name);
    Type = type;
    Original = original;
    _valueText = NvModemParameterCodec.Display(original, type);
    Revalidate();
  }

  public string Name { get; }
  public string Group { get; }
  public string Description { get; }
  internal byte Type { get; }
  internal double Original { get; private set; }
  public bool IsReadOnly => NvModemCatalog.IsReadOnly(Name);

  [ObservableProperty]
  private string _valueText;

  [ObservableProperty]
  private bool _isValid;

  [ObservableProperty]
  private bool _isChanged;

  public string EditState => !IsValid ? "Invalid"
      : IsChanged ? "Changed"
      : IsReadOnly ? "Read-only" : "";

  partial void OnValueTextChanged(string value) => Revalidate();

  partial void OnIsValidChanged(bool value) => OnPropertyChanged(nameof(EditState));

  partial void OnIsChangedChanged(bool value) => OnPropertyChanged(nameof(EditState));

  internal bool TryValue(out double value) =>
      NvModemParameterCodec.TryParse(ValueText, Type, out value);

  internal void Accept(double value) {
    Original = value;
    ValueText = NvModemParameterCodec.Display(value, Type);
    Revalidate();
  }

  internal void Revert() {
    ValueText = NvModemParameterCodec.Display(Original, Type);
    Revalidate();
  }

  private void Revalidate() {
    IsValid = TryValue(out double value)
        && NvModemCatalog.IsEditableValueAllowed(Name, value);
    IsChanged = IsValid && (double.IsNaN(Original)
        || !NvModemParameterCodec.ValuesEqual(value, Original, Type));
  }
}

internal partial class NvModemParameterComparisonRow : ObservableObject,
    IParameterComparisonRow {
  internal NvModemParameterComparisonRow(
      string name,
      string currentText,
      string proposedText,
      double proposedValue,
      byte parameterType,
      bool createIfMissing,
      bool isRtspPath = false) {
    Name = name;
    CurrentText = currentText;
    ProposedText = proposedText;
    ProposedValue = proposedValue;
    ParameterType = parameterType;
    CreateIfMissing = createIfMissing;
    IsRtspPath = isRtspPath;
  }

  public string Name { get; }
  public string CurrentText { get; }
  public string ProposedText { get; }
  internal double ProposedValue { get; }
  internal byte ParameterType { get; }
  internal bool CreateIfMissing { get; }
  internal bool IsRtspPath { get; }

  [ObservableProperty]
  private bool _use = true;
}

internal sealed record NvModemParameterComparison(
    NvModemDeviceState Target,
    string SourceLabel,
    IReadOnlyList<NvModemParameterComparisonRow> Rows,
    int Unknown,
    int Invalid,
    int ReadOnly);

public partial class NvRadioStatusRow : ObservableObject {
  internal NvRadioStatusRow(int channel) {
    Channel = channel;
  }

  public int Channel { get; }
  public string Radio => $"Radio {Channel}";

  [ObservableProperty]
  private string _identity = "—";

  [ObservableProperty]
  private string _mode = "—";

  [ObservableProperty]
  private string _link = "—";

  [ObservableProperty]
  private string _traffic = "—";

  [ObservableProperty]
  private string _details = "No live status received.";
}

public sealed record NvRadioCopyChoice(
    object DeviceIdentity, int Channel, string Label) {
  public override string ToString() => Label;
}

internal sealed record NvModemDeviceKey(
    NvModemLink Link, byte SystemId, byte ComponentId);

internal sealed class NvModemDeviceState {
  internal NvModemDeviceState(NvModemDeviceKey key) {
    Key = key;
  }

  internal NvModemDeviceKey Key { get; set; }
  internal NvModemGeneration Generation { get; set; }
  internal uint ProductProfile { get; set; }
  internal DateTime FirstSeenUtc { get; set; }
  internal DateTime LastSeenUtc { get; set; }

  /// <summary>A PARAM list read waits for the passport, which says whether the modem is
  /// configured through native transactions instead.</summary>
  internal bool ParameterRequestDeferred { get; set; }
  internal int ExpectedParameterCount { get; set; }
  internal Dictionary<string, double> Parameters { get; } = new(StringComparer.Ordinal);
  internal Dictionary<string, byte> ParameterTypes { get; } = new(StringComparer.Ordinal);
  internal Dictionary<int, uint> LegacyKeyWords { get; } = [];
  internal string LegacyKeyFingerprint { get; set; } = "";
  internal NvModemInfoMessage ModemInfo { get; set; }
  internal bool ModemInfoReady { get; set; }
  internal Dictionary<int, Nv5LinkStatusMessage> Links { get; } = [];
  internal string LegacyNodeName { get; set; } = "";
  internal NvRxStatMessage LegacyRxStatus { get; set; }
  internal bool LegacyRxReady { get; set; }
  internal MAVLink.mavlink_radio_status_t LegacyRadioStatus { get; set; }
  internal bool LegacyRadioReady { get; set; }
  internal bool ParameterRefreshPending { get; set; }
  internal bool ParameterListInProgress { get; set; }
  internal DateTime ParameterListLastProgressUtc { get; set; }
  internal int ParameterListRetries { get; set; }
  internal DateTime RebootReadyUtc { get; set; }
  internal bool IdentityTransitionExpected { get; set; }
  internal byte ExpectedSystemId { get; set; }
  internal byte ExpectedComponentId { get; set; }
  internal DateTime IdentityTransitionExpiresUtc { get; set; }
  internal string RtspPath { get; set; } = "";
  internal bool RtspPathReady { get; set; }
  internal NvModemDeviceChoice? Choice { get; set; }
}

internal enum NvWriteKind {
  Parameter,
  EncryptionKeys,
  RtspPath,
  Reboot,
  TransmitControl,
}

internal sealed class NvWriteOperation {
  internal NvWriteKind Kind { get; init; }
  internal required NvModemDeviceState Device { get; init; }
  internal string Name { get; init; } = "";
  internal double Value { get; init; }
  internal byte ParameterType { get; init; }
  internal string Path { get; init; } = "";
  internal uint TransactionId { get; init; }
  internal byte ChannelMask { get; init; }
  internal byte[] Channel1Key { get; init; } = [];
  internal byte[] Channel2Key { get; init; } = [];
  internal byte Channel { get; init; }
  internal bool Enabled { get; init; }
  internal bool MismatchedParameterEcho { get; set; }
  internal double ReportedParameterValue { get; set; }
  internal byte ReportedParameterType { get; set; } = (byte)MAVLink.MAV_PARAM_TYPE.REAL32;
  internal int Retries { get; set; }
  internal DateTime SentUtc { get; set; }
}

public partial class NvModemViewModel : ViewModelBase, IDisposable {
  private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan TransactionTimeout = TimeSpan.FromMilliseconds(1200);
  private static readonly TimeSpan ParameterListTimeout = TimeSpan.FromSeconds(3);
  private static readonly TimeSpan ParameterListRetry = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan ExpectedIdentityTransition = TimeSpan.FromSeconds(15);

  // How long a newly seen modem's PARAM read waits for its passport. The page requests the
  // passport on discovery; firmware that never sends one is read after this.
  private static readonly TimeSpan PassportWait = TimeSpan.FromSeconds(3);
  private const int MaximumWriteAttempts = 3;
  private const int MaximumParameterListRetries = 6;
  private const ushort SetTransmitEnabledCommand = 42010;

  private readonly INvModemMavlinkTransport _transport;
  private readonly Func<DateTime> _utcNow;
  private readonly Dictionary<NvModemDeviceKey, NvModemDeviceState> _devices = [];
  private readonly HashSet<NvModemDeviceKey> _retiredDeviceKeys = [];
  private readonly Dictionary<string, NvModemParameterRow> _parameterRows =
      new(StringComparer.Ordinal);
  private readonly Queue<NvWriteOperation> _writeQueue = [];
  private readonly DispatcherTimer? _serviceTimer;
  private readonly DateTime _clockStart;
  private readonly Nv5SettingsSnapshots _nativeSnapshots = new();

  // Per modem UID: the passport uptime last seen and the transport epoch, which advances when the
  // uptime goes back, so a restart ends anything the editor was doing on the old boot.
  private readonly Dictionary<string, (uint Uptime, ulong Epoch)> _nativeBoots =
      new(StringComparer.Ordinal);
  private NvWriteOperation? _currentWrite;
  private uint _nextTransactionId = unchecked((uint)Environment.TickCount64);
  private bool _switchGuard;
  private bool _settingRtsp;
  private bool _rtspEdited;
  private bool _disposed;
  private bool _parameterWriteInTransaction;
  private bool _rebootRequiredInTransaction;
  private bool _keyWriteInTransaction;
  private int _keyWriteChannel;

  public NvModemViewModel()
      : this(new NvModemMavlinkTransport(), () => DateTime.UtcNow, startTimer: true,
          nativeClock: () => unchecked((uint)Environment.TickCount64)) {
  }

  internal NvModemViewModel(
      INvModemMavlinkTransport transport, Func<DateTime> utcNow, bool startTimer,
      Func<uint>? nativeClock = null) {
    _transport = transport;
    _utcNow = utcNow;
    _clockStart = utcNow();
    // Native transactions time out on a monotonic clock; the injected wall clock serves tests.
    NativeSettings = new Nv5NativeSettingsEditor(nativeClock ?? NativeNowMs,
        () => MAVLinkInterface.gcssysid,
        (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_MISSIONPLANNER) {
      Snapshots = _nativeSnapshots,
    };
    NativeSettings.SendFrame += (frame, target) => {
      if (target.Route is NvModemLink link) {
        _transport.Send(link, frame.Payload, target.System, target.Component);
      }
    };
    NativeSettings.BusyChanged += _ => RefreshControls();
    NativeSettings.EditsChanged += RefreshControls;
    _transport.PacketReceived += OnPacketReceived;
    _transport.LinksChanged += OnLinksChanged;
    RadioStatuses.Add(new NvRadioStatusRow(1));
    RadioStatuses.Add(new NvRadioStatusRow(2));
    SelectedDebugRadio = DebugRadios[0];
    if (startTimer) {
      _serviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
      _serviceTimer.Tick += OnServiceTick;
      _serviceTimer.Start();
    }
    Discover();
  }

  public string Title => "NV Modem";
  public string Instructions =>
      "Uses the MAVLink connections already open in Mission Planner; this page does not open a "
      + "separate UDP, TCP or serial connection. The shared NV modem passport, NV5 status, and "
      + "legacy NV4 status/CAN node information are accepted at any system/component ID.";

  public ObservableCollection<NvModemDeviceChoice> Devices { get; } = [];
  public ObservableCollection<NvModemParameterRow> Parameters { get; } = [];
  public ObservableCollection<NvRadioStatusRow> RadioStatuses { get; } = [];
  public ObservableCollection<NvRadioChoice> PresetRadios { get; } = [];
  public ObservableCollection<NvRadioChoice> KeyRadios { get; } = [];
  public ObservableCollection<NvRadioCopyChoice> CopySources { get; } = [];
  public IReadOnlyList<NvRadioChoice> DebugRadios { get; } = [
    new(1, "Radio 1"), new(2, "Radio 2"),
  ];

  [ObservableProperty]
  private NvModemDeviceChoice? _selectedDevice;

  [ObservableProperty]
  private NvRadioChoice? _selectedPresetRadio;

  [ObservableProperty]
  private NvRadioChoice? _selectedKeyRadio;

  [ObservableProperty]
  private NvRadioChoice? _selectedDebugRadio;

  [ObservableProperty]
  private NvRadioCopyChoice? _selectedCopySource;

  [ObservableProperty]
  private NvModemParameterRow? _selectedParameter;

  [ObservableProperty]
  private string _connectionText = "No modem discovered";

  [ObservableProperty]
  private string _identityText = "Open a MAVLink connection, then press Discover.";

  [ObservableProperty]
  private string _parameterProgress = "Parameters: 0 / 0";

  [ObservableProperty]
  private string _status = "Ready. Discovering NV4/NV5 modems over open MAVLink links…";

  [ObservableProperty]
  private bool _isError;

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private string _keyText = "";

  [ObservableProperty]
  private string _keyFingerprint = "Stored fingerprint: unavailable";

  [ObservableProperty]
  private string _keyHint = "Read parameters to edit an encryption key.";

  [ObservableProperty]
  private string _rtspPath = "";

  /// <summary>Native settings of the selected modem when its firmware advertises configuration
  /// transactions; such a modem is configured through them instead of PARAM_SET.</summary>
  public Nv5NativeSettingsEditor NativeSettings { get; }

  public bool UsesNativeSettings => SelectedState is { } device && SupportsNativeSettings(device);
  public bool CanSwitchDevice => !IsBusy && !NativeSettings.Saving;
  public bool HasSelectedDevice => SelectedDevice != null;
  public bool HasParameters => Parameters.Count != 0;
  public bool HasPendingChanges => _parameterRows.Values.Any(row => row.IsChanged)
      || SelectedState is { } device && RtspPending(device);
  // A native save owns the modem until it settles; a reboot, transmitter command or PARAM write in
  // between could cut the commit short or race its restart.
  public bool CanEdit => SelectedState is { } device && IsOnline(device) && !IsBusy
      && !NativeSettings.Saving;
  public bool CanSave => CanEdit && HasPendingChanges;
  public bool CanUseNv5Controls => CanEdit
      && SelectedState?.Generation == NvModemGeneration.Nv5;
  public bool CanUseRtsp => CanEdit && SelectedState is { } device && SupportsRtsp(device);
  public bool CanReboot => CanUseNv5Controls && !HasPendingChanges
      && SelectedState!.RebootReadyUtc <= _utcNow();
  public bool CanGenerateKey => CanEdit && SelectedKeyRadio != null;
  public bool CanSetKey => CanEdit && SelectedKeyRadio != null && KeyText.Length != 0;
  public bool CanRevertSelectedParameter => CanEdit
      && SelectedParameter is { IsReadOnly: false, IsValid: true, IsChanged: true };
  public bool CanCopyRadio => CanUseNv5Controls
      && SelectedPresetRadio != null && SelectedCopySource != null;
  public bool CanStageRxRolePreset => CanUseNv5Controls
      && TrySelectedPresetInteger("ROLE", out long role)
      && role is >= 0 and <= 2 && role != 0;
  public bool CanStageTxRolePreset => CanUseNv5Controls
      && TrySelectedPresetInteger("ROLE", out long role)
      && role is >= 0 and <= 2 && role != 1;
  public bool CanControlTransmitter => CanUseNv5Controls
      && SelectedDebugRadio is { } selected
      && SelectedState!.Links.TryGetValue(selected.Channel, out var link)
      && link.Role != 0;
  public bool CanEnableTransmitter => CanControlTransmitter
      && SelectedState!.Links[SelectedDebugRadio!.Channel].TxState != 1;
  public bool CanSuppressTransmitter => CanControlTransmitter
      && SelectedState!.Links[SelectedDebugRadio!.Channel].TxState != 2;

  private NvModemDeviceState? SelectedState => SelectedDevice?.State;

  partial void OnSelectedDeviceChanged(
      NvModemDeviceChoice? oldValue, NvModemDeviceChoice? newValue) {
    if (_switchGuard || ReferenceEquals(oldValue, newValue)) {
      return;
    }
    if ((IsBusy || NativeSettings.Saving) && oldValue != null) {
      _switchGuard = true;
      SelectedDevice = oldValue;
      _switchGuard = false;
      return;
    }
    if (oldValue != null && newValue != null && HasPendingChangesFor(oldValue.State)) {
      _switchGuard = true;
      SelectedDevice = oldValue;
      _switchGuard = false;
      _ = ConfirmDeviceSwitchAsync(newValue);
      return;
    }
    ShowSelectedDevice(clearDeviceParameters: newValue != null);
  }

  partial void OnSelectedPresetRadioChanged(NvRadioChoice? value) => RefreshControls();

  partial void OnSelectedDebugRadioChanged(NvRadioChoice? value) => RefreshControls();

  partial void OnSelectedCopySourceChanged(NvRadioCopyChoice? value) => RefreshControls();

  partial void OnSelectedParameterChanged(NvModemParameterRow? value) => RefreshControls();

  partial void OnSelectedKeyRadioChanged(NvRadioChoice? value) {
    SyncKeyText();
    RefreshControls();
  }

  partial void OnKeyTextChanged(string value) => RefreshControls();

  partial void OnRtspPathChanged(string value) {
    if (!_settingRtsp) {
      _rtspEdited = true;
      RefreshControls();
    }
  }

  private async Task ConfirmDeviceSwitchAsync(NvModemDeviceChoice requested) {
    bool discard = await Dialogs.Confirm(
        "Unsaved modem settings",
        "The selected modem has unsaved parameter changes. Discard them and switch devices?");
    if (!discard || _disposed || IsBusy) {
      return;
    }
    _switchGuard = true;
    SelectedDevice = requested;
    _switchGuard = false;
    ShowSelectedDevice(clearDeviceParameters: true);
  }

  [RelayCommand]
  private void Discover() {
    int openLinks = 0;
    foreach (NvModemLink link in _transport.Snapshot()) {
      openLinks++;
      foreach (MAVLink.MAVLinkMessage packet in _transport.CachedDiscoveryPackets(link)) {
        HandlePacket(link, packet);
      }
      ReplayCachedParameters(link, _transport.CachedParameters(link));
      RequestDiscoveryMessages(link, 0, 0);
      foreach (NvModemEndpoint endpoint in _transport.KnownEndpoints(link)) {
        RequestDiscoveryMessages(link, endpoint.SystemId, endpoint.ComponentId);
      }
    }
    SetStatus(openLinks == 0
        ? "No shared MAVLink connection is open. Connect with Mission Planner's main connection "
            + "control, then press Discover."
        : $"Searching {openLinks} shared Mission Planner MAVLink connection(s).", openLinks == 0);
  }

  [RelayCommand]
  private void ReadNativeSettings() => NativeSettings.RereadDevice();

  [RelayCommand]
  private async Task SaveNativeSettings() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || !NativeSettings.CanSave) {
      return;
    }
    if (!await Dialogs.Confirm("Save and restart NV modem", NativeSaveSummary(device))) {
      return;
    }
    if (!ReferenceEquals(device, SelectedState) || !NativeSettings.CanSave) {
      SetStatus("The selected modem changed before confirmation; nothing was saved.", true);
      return;
    }
    NativeSettings.Save();
  }

  internal string NativeSaveSummary(NvModemDeviceState device) {
    IReadOnlyList<(string Name, string Before, string After)> changes =
        NativeSettings.PendingChangeList();
    string details = string.Join(Environment.NewLine,
        changes.Take(20).Select(change => $"{change.Name}: {change.Before} → {change.After}"));
    if (changes.Count > 20) {
      details += $"{Environment.NewLine}…and {changes.Count - 20} more";
    }
    return $"Save {changes.Count} change(s) to {ModemName(device)} "
        + $"{device.Key.SystemId}:{device.Key.ComponentId}? The modem validates the complete "
        + $"configuration, saves it and restarts.{Environment.NewLine}{Environment.NewLine}{details}";
  }

  [RelayCommand]
  private void DiscardNativeSettings() => NativeSettings.Discard();

  [RelayCommand]
  private void RefreshSelected() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || IsBusy) {
      return;
    }
    if (SupportsNativeSettings(device)) {
      // Refresh reads a transaction-capable modem in full, whatever an earlier read left.
      SendIdentityRequest(device.Key.Link, device.Key.SystemId, device.Key.ComponentId);
      NativeSettings.RereadDevice();
      return;
    }
    ClearDeviceParameters(device);
    if (device.Generation == NvModemGeneration.Nv5) {
      SendIdentityRequest(device.Key.Link, device.Key.SystemId, device.Key.ComponentId);
    }
    SendParameterRequestList(device);
    if (SupportsRtsp(device)) {
      SendRtspRequest(device);
    }
    SetStatus($"Reading parameters from {ModemName(device)} "
        + $"{device.Key.SystemId}:{device.Key.ComponentId}…");
  }

  [RelayCommand]
  private void ReadRtsp() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || !CanUseRtsp) {
      SetStatus("Select an online NV5 modem with an LR2021 radio.", true);
      return;
    }
    SendRtspRequest(device);
    SetStatus("RTSP path requested.");
  }

  [RelayCommand]
  private async Task Save() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || IsBusy) {
      return;
    }
    NvModemParameterRow? invalid = _parameterRows.Values.FirstOrDefault(row => !row.IsValid);
    if (invalid != null) {
      SetStatus($"Parameter {invalid.Name} has an invalid value.", true);
      return;
    }
    NvModemParameterRow[] changed = [.. _parameterRows.Values
        .Where(row => row.IsChanged && !row.IsReadOnly && IsApplicable(row.Name))
        .OrderBy(row => WritePriority(row.Name)).ThenBy(row => row.Name, StringComparer.Ordinal)];
    bool rtspChanged = RtspPending(device);
    if (rtspChanged && (!RtspPath.StartsWith("/", StringComparison.Ordinal)
        || Encoding.Latin1.GetByteCount(RtspPath) > 95)) {
      SetStatus("RTSP path must begin with '/' and fit in 95 Latin-1 bytes.", true);
      return;
    }
    if (changed.Length == 0 && !rtspChanged) {
      SetStatus("No changed settings to apply.");
      return;
    }
    string details = string.Join(Environment.NewLine, changed.Take(20)
        .Select(row => $"{row.Name}: {NvModemParameterCodec.Display(row.Original, row.Type)} → {row.ValueText}"));
    if (changed.Length > 20) {
      details += $"{Environment.NewLine}…and {changed.Length - 20} more";
    }
    if (rtspChanged) {
      details += $"{Environment.NewLine}RTSP path: {device.RtspPath} → {RtspPath}";
    }
    if (!await Dialogs.Confirm("Save NV modem settings",
            $"Write {changed.Length + (rtspChanged ? 1 : 0)} change(s) to "
            + $"{ModemName(device)} {device.Key.SystemId}:{device.Key.ComponentId}?\n\n{details}")) {
      return;
    }
    if (!ReferenceEquals(device, SelectedState) || !CanEdit) {
      SetStatus("The selected modem or its MAVLink connection changed before confirmation.", true);
      return;
    }
    if (!QueueParameterWrites(device, changed)) {
      return;
    }
    if (rtspChanged) {
      _writeQueue.Enqueue(new NvWriteOperation {
        Kind = NvWriteKind.RtspPath,
        Device = device,
        Path = RtspPath,
        TransactionId = ++_nextTransactionId,
      });
    }
    BeginQueuedWrites(device, keyOnly: false, keyChannel: 0);
  }

  [RelayCommand]
  private void CancelChanges() {
    RebuildParameters();
    NvModemDeviceState? device = SelectedState;
    SetRtspPath(device is { RtspPathReady: true } ? device.RtspPath : "");
    SyncKeyText();
    SetStatus("Staged changes discarded.");
  }

  [RelayCommand]
  private void RevertSelectedParameter() {
    NvModemParameterRow? row = SelectedParameter;
    if (!CanRevertSelectedParameter || row == null) {
      return;
    }
    row.Revert();
    SyncKeyText();
    SetStatus($"Reverted {row.Name} to the value read from the modem. Nothing was sent.");
  }

  [RelayCommand]
  private void StageRadioPreset(string? preset) {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedPresetRadio?.Channel ?? 0;
    if (device?.Generation != NvModemGeneration.Nv5 || !CanEdit || channel is < 1 or > 2) {
      SetStatus("Select an online NV5 radio before applying a preset.", true);
      return;
    }
    string prefix = $"CH{channel}_";
    long chip = (long)Math.Round(StagedValue(prefix + "CHIP", -1));
    long modulation = (long)Math.Round(StagedValue(prefix + "MOD", -1));
    if (preset == "factory" && chip < 0) {
      SetStatus("Wait until the selected radio chip type has been read.", true);
      return;
    }
    if (preset is "flrc" or "flrc13" or "flrc26" && chip != 0) {
      SetStatus("FLRC presets are supported only by an LR2021 radio.", true);
      return;
    }
    if (preset is "flrc13" or "flrc26" or "fec_on" or "fec_off" && modulation != 1) {
      SetStatus("This setting is available only in FLRC mode. Select FLRC first.", true);
      return;
    }
    int staged = 0;
    void Set(string suffix, double value, MAVLink.MAV_PARAM_TYPE type = MAVLink.MAV_PARAM_TYPE.UINT32,
        bool create = false) {
      if (StageParameter(prefix + suffix, value, (byte)type, create)) {
        staged++;
      }
    }
    string name;
    switch (preset) {
      case "factory":
        bool lora = chip != 0;
        name = "factory defaults";
        Set("MOD", lora ? 0 : 1); Set("FRAME", lora ? 64 : 240);
        Set("FHSS", lora ? 1 : 0); Set("FHSS_KHZ", 40000); Set("GUARD_US", 3000);
        Set("DWELL_SH", lora ? 0 : 5); Set("SYNC_PER", 16); Set("SCAN_DW", lora ? 5 : 2);
        Set("ENCRYPT", 1); Set("RADIO_CRC", 1);
        Set("GUARD_MULT", 0, MAVLink.MAV_PARAM_TYPE.REAL32); Set("OPEN_LOOP", 0);
        Set("LINK_MS", 5000); Set("TX_PERIOD", lora ? 100000 : 20000);
        if (lora) {
          Set("LORA_KHZ", 500, MAVLink.MAV_PARAM_TYPE.REAL32, true);
          Set("LORA_SF", 7, create: true); Set("LORA_CR", 5, create: true);
          Set("LORA_SYNC", 0x41, create: true); Set("LORA_PRE", 8, create: true);
        } else {
          Set("FEC", 1, create: true); Set("FEC_K", 3, create: true);
          Set("FEC_N", 4, create: true); Set("FLRC_RATE", 1300000, create: true);
          Set("FLRC_CR", 1, create: true); Set("FLRC_SHAPE", 2, create: true);
          Set("FLRC_PRE", 32, create: true); Set("FLRC_SYNC0", 0x90, create: true);
          Set("FLRC_SYNC1", 0x56, create: true); Set("FLRC_SYNC2", 0x34, create: true);
          Set("FLRC_SYNC3", 0x12, create: true);
        }
        break;
      case "lora": name = "LoRa mode"; Set("MOD", 0); break;
      case "flrc": name = "FLRC mode"; Set("MOD", 1); break;
      case "flrc13": name = "FLRC rate 1.3M"; Set("FLRC_RATE", 1300000, create: true); break;
      case "flrc26": name = "FLRC rate 2.6M"; Set("FLRC_RATE", 2600000, create: true); break;
      case "rx": name = "RX role"; Set("ROLE", 0); break;
      case "tx": name = "TX role"; Set("ROLE", 1); break;
      case "fhss_on": name = "FHSS ON"; Set("FHSS", 1); break;
      case "fhss_off": name = "FHSS OFF"; Set("FHSS", 0); break;
      case "fec_on": name = "FEC ON"; Set("FEC", 1, create: true); break;
      case "fec_off": name = "FEC OFF"; Set("FEC", 0, create: true); break;
      default: return;
    }
    RebuildVisibleParameters();
    RefreshControls();
    SetStatus($"Staged {name} for Radio {channel}: {staged} parameter value(s). "
        + "Nothing was sent — press Save to selected modem.", staged == 0);
  }

  [RelayCommand]
  private void StageRtspPreset(string? preset) {
    if (!CanUseRtsp) {
      SetStatus("Select an online NV5 modem with an LR2021 radio.", true);
      return;
    }
    string parameter = preset is "rtp" or "annexb" ? "RTSP_OUTPUT" : "RTSP_ENABLE";
    if (parameter == "RTSP_OUTPUT" && StagedValue("RTSP_ENABLE", 0) < 0.5) {
      SetStatus("Stage RTSP ON before selecting RTP or raw H264/H265 transport.", true);
      return;
    }
    double value = preset is "on" or "rtp" ? 1 : 0;
    if (!StageParameter(parameter, value, (byte)MAVLink.MAV_PARAM_TYPE.UINT32, false)) {
      SetStatus($"The selected modem did not publish {parameter}.", true);
      return;
    }
    RefreshControls();
    SetStatus($"Staged {preset}. Nothing was sent — press Save to selected modem.");
  }

  [RelayCommand]
  private void GenerateKey() {
    NvModemDeviceState? device = SelectedState;
    int keyBytes = device?.Generation == NvModemGeneration.Nv4
        ? NvModemCatalog.Nv4KeyBytes : NvModemCatalog.Nv5KeyBytes;
    KeyText = Convert.ToHexString(RandomNumberGenerator.GetBytes(keyBytes));
    if (StageEncryptionKey()) {
      SetStatus($"Generated and staged a new Radio {SelectedKeyRadio?.Channel} encryption key. "
          + "Nothing was sent.");
    }
  }

  [RelayCommand]
  private void SetKey() {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedKeyRadio?.Channel ?? 0;
    if (device == null || !CanEdit || channel == 0 || !StageEncryptionKey()) {
      return;
    }
    if (device.Generation == NvModemGeneration.Nv4) {
      string[] names = [.. Enumerable.Range(1, 8)
          .Select(index => $"ENC_KEY_BYTE{index}")];
      NvModemParameterRow[] rows = [.. names.Select(name => _parameterRows[name])];
      if (!QueueParameterWrites(device, rows, addLegacyRefresh: true)) {
        return;
      }
    } else {
      // Receive diversity does not couple encryption keys. Each explicit SET KEY
      // targets only the radio selected in the key editor.
      byte channelMask = (byte)(1 << (channel - 1));
      if (!QueueEncryptionKeyWrite(device, channelMask)) {
        return;
      }
    }
    BeginQueuedWrites(device, keyOnly: true, keyChannel: channel);
  }

  internal NvModemParameterComparison? BuildCopyParameterComparison() {
    NvModemDeviceState? target = SelectedState;
    int targetChannel = SelectedPresetRadio?.Channel ?? 0;
    if (target?.Generation != NvModemGeneration.Nv5 || SelectedCopySource == null
        || targetChannel is < 1 or > 2
        || SelectedCopySource.DeviceIdentity is not NvModemDeviceState source) {
      SetStatus("Select a target radio and a fully read radio from another NV5 modem.", true);
      return null;
    }
    int sourceChannel = SelectedCopySource.Channel;
    string sourcePrefix = $"CH{sourceChannel}_";
    string targetPrefix = $"CH{targetChannel}_";
    long sourceMode = (long)Math.Round(source.Parameters.GetValueOrDefault(sourcePrefix + "MOD", -1));
    long targetChip = (long)Math.Round(StagedValue(targetPrefix + "CHIP", -1));
    if (sourceMode == 1 && targetChip != 0) {
      SetStatus("Cannot copy an FLRC profile to a radio whose chip does not support FLRC.", true);
      return null;
    }
    var rows = new List<NvModemParameterComparisonRow>();
    foreach ((string sourceName, double value) in source.Parameters) {
      if (!sourceName.StartsWith(sourcePrefix, StringComparison.Ordinal)
          || NvModemCatalog.IsReadOnly(sourceName)) {
        continue;
      }
      string suffix = sourceName[sourcePrefix.Length..];
      string targetName = targetPrefix + suffix;
      bool create = suffix.StartsWith("LORA_", StringComparison.Ordinal)
          || suffix.StartsWith("FLRC_", StringComparison.Ordinal)
          || suffix is "FEC" or "FEC_K" or "FEC_N" or "FREQ_KHZ";
      if (!_parameterRows.TryGetValue(targetName, out NvModemParameterRow? targetRow)
          && !create) {
        continue;
      }
      byte type = targetRow?.Type ?? source.ParameterTypes.GetValueOrDefault(sourceName,
          (byte)MAVLink.MAV_PARAM_TYPE.REAL32);
      if (targetRow != null && targetRow.TryValue(out double current)
          && NvModemParameterCodec.ValuesEqual(current, value, type)) {
        continue;
      }
      rows.Add(new NvModemParameterComparisonRow(
          targetName,
          targetRow?.ValueText ?? "(not published)",
          NvModemParameterCodec.Display(value, type),
          value,
          type,
          create));
    }
    rows.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
    SetStatus(rows.Count == 0
        ? $"No channel-local differences from {SelectedCopySource.Label}."
        : $"Found {rows.Count} channel-local difference(s) from {SelectedCopySource.Label}; "
            + "review them before staging.");
    return new NvModemParameterComparison(
        target, SelectedCopySource.Label, rows, 0, 0, 0);
  }

  [RelayCommand]
  private async Task Reboot() {
    NvModemDeviceState? device = SelectedState;
    if (device == null || !CanReboot
        || !await Dialogs.Confirm("Reboot NV5",
            $"Reboot NV5 {device.Key.SystemId}:{device.Key.ComponentId} now? "
            + "The management link will briefly disappear.")) {
      return;
    }
    if (!ReferenceEquals(device, SelectedState) || !CanReboot) {
      SetStatus("The selected modem or its MAVLink connection changed before confirmation.", true);
      return;
    }
    if (device.IdentityTransitionExpected) {
      device.IdentityTransitionExpiresUtc = _utcNow() + ExpectedIdentityTransition;
    }
    StartSingleWrite(new NvWriteOperation { Kind = NvWriteKind.Reboot, Device = device });
  }

  [RelayCommand]
  private void SetTransmitterEnabled(string? enabledText) {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedDebugRadio?.Channel ?? 0;
    bool enabled = string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase);
    if (device == null || (enabled ? !CanEnableTransmitter : !CanSuppressTransmitter)) {
      SetStatus("Select an online transmitter or transceiver radio first.", true);
      return;
    }
    StartSingleWrite(new NvWriteOperation {
      Kind = NvWriteKind.TransmitControl,
      Device = device,
      Channel = (byte)channel,
      Enabled = enabled,
    });
  }

  internal string ExportParameterFile() {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return "";
    }
    var output = new StringBuilder();
    output.AppendLine(device.Generation == NvModemGeneration.Nv4
        ? "#WARNING: NV4 export contains the readable encryption key words"
        : "#WARNING: NV5 export contains the key-word values currently visible in Mission Planner");
    if (SupportsRtsp(device) && RtspPath.Length != 0) {
      output.Append("#NV5_RTSP_PATH,").AppendLine(RtspPath);
    }
    foreach (NvModemParameterRow row in _parameterRows.Values.OrderBy(row => row.Name,
                 StringComparer.Ordinal)) {
      output.Append(row.Name).Append(',').AppendLine(row.ValueText);
    }
    return output.ToString()
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\n", "\r\n", StringComparison.Ordinal);
  }

  internal string SuggestedParameterFileName() {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return "nv-modem.param";
    }
    string stem = $"{ModemName(device)}_{HardwareModel(device)}_{RoleSummary(device)}";
    string safe = new(stem.Select(character => char.IsLetterOrDigit(character)
        || character is '.' or '-' or '_' ? character : '_').ToArray());
    while (safe.Contains("__", StringComparison.Ordinal)) {
      safe = safe.Replace("__", "_", StringComparison.Ordinal);
    }
    safe = safe.Trim('.', '-', '_');
    return (safe.Length == 0 ? "nv-modem" : safe) + ".param";
  }

  internal NvModemParameterComparison? BuildParameterFileComparison(
      string contents, string sourceLabel) {
    if (SelectedState == null || IsBusy) {
      return null;
    }
    NvModemDeviceState target = SelectedState;
    int unknown = 0;
    int invalid = 0;
    int readOnly = 0;
    var imported = new Dictionary<string, (double Value, byte Type)>(StringComparer.Ordinal);
    string? importedRtspPath = null;
    foreach (string raw in contents.Split('\n')) {
      string line = raw.Trim();
      if (line.Length == 0) {
        continue;
      }

      if (line.StartsWith("#NV5_RTSP_PATH,", StringComparison.Ordinal)) {
        if (CanUseRtsp) {
          string path = line["#NV5_RTSP_PATH,".Length..].Trim();
          if (!path.StartsWith("/", StringComparison.Ordinal)
              || Encoding.Latin1.GetByteCount(path) > 95) {
            invalid++;
          } else {
            importedRtspPath = path;
          }
        }
        continue;
      }
      if (line.StartsWith('#')) {
        continue;
      }

      string[] fields = line.Contains(',')
          ? line.Split(',', StringSplitOptions.TrimEntries)
          : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (fields.Length < 2 || !_parameterRows.TryGetValue(fields[0], out var row)) {
        unknown++;
        continue;
      }
      if (row.IsReadOnly) {
        readOnly++;
        continue;
      }
      if (!NvModemParameterCodec.TryParse(fields[1], row.Type, out double value)
          || !NvModemCatalog.IsEditableValueAllowed(row.Name, value)) {
        invalid++;
      } else {
        imported[row.Name] = (value, row.Type);
      }
    }
    var rows = new List<NvModemParameterComparisonRow>();
    foreach ((string name, (double value, byte type)) in imported) {
      NvModemParameterRow row = _parameterRows[name];
      if (row.TryValue(out double current)
          && NvModemParameterCodec.ValuesEqual(current, value, type)) {
        continue;
      }
      rows.Add(new NvModemParameterComparisonRow(
          name,
          row.ValueText,
          NvModemParameterCodec.Display(value, type),
          value,
          type,
          createIfMissing: false));
    }
    if (importedRtspPath != null && !string.Equals(importedRtspPath, RtspPath,
            StringComparison.Ordinal)) {
      rows.Add(new NvModemParameterComparisonRow(
          "NV5_RTSP_PATH",
          RtspPath,
          importedRtspPath,
          0,
          0,
          createIfMissing: false,
          isRtspPath: true));
    }
    rows.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
    SetStatus(rows.Count == 0
        ? $"{sourceLabel}: no differing supported settings; {unknown} unknown, "
            + $"{invalid} invalid, {readOnly} read-only."
        : $"{sourceLabel}: found {rows.Count} difference(s); review them before staging. "
            + $"{unknown} unknown, {invalid} invalid, {readOnly} read-only.", invalid != 0);
    return new NvModemParameterComparison(
        target, sourceLabel, rows, unknown, invalid, readOnly);
  }

  internal int ApplyParameterComparison(NvModemParameterComparison comparison) {
    if (!ReferenceEquals(comparison.Target, SelectedState) || IsBusy) {
      SetStatus("The selected modem or its MAVLink connection changed while differences were open; "
          + "nothing was staged.", true);
      return -1;
    }
    int staged = 0;
    foreach (NvModemParameterComparisonRow change in comparison.Rows.Where(row => row.Use)) {
      if (change.IsRtspPath) {
        RtspPath = change.ProposedText;
        staged++;
      } else if (StageParameter(
                     change.Name,
                     change.ProposedValue,
                     change.ParameterType,
                     change.CreateIfMissing)) {
        staged++;
      }
    }
    RebuildVisibleParameters();
    SyncKeyText();
    RefreshControls();
    SetStatus($"Staged {staged} selected difference(s) from {comparison.SourceLabel}. "
        + "Nothing was sent — review the Changed rows, then press Save to selected modem.",
        staged == 0);
    return staged;
  }

  private void OnPacketReceived(NvModemLink source, MAVLink.MAVLinkMessage packet) {
    if (_disposed) {
      return;
    }

    if (Dispatcher.UIThread.CheckAccess()) {
      HandlePacket(source, packet);
    } else {
      Dispatcher.UIThread.Post(() => HandlePacket(source, packet));
    }
  }

  private void OnLinksChanged() {
    if (_disposed) {
      return;
    }

    if (Dispatcher.UIThread.CheckAccess()) {
      Discover();
    } else {
      Dispatcher.UIThread.Post(Discover);
    }
  }

  internal void HandlePacket(NvModemLink source, MAVLink.MAVLinkMessage packet) {
    if (_disposed) {
      return;
    }
    if (packet.msgid is NvModemMessageIds.Nv5ConfigResult or NvModemMessageIds.Nv5ConfigValue) {
      // The editor accepts only replies from its target on its route and transport epoch.
      NativeSettings.Receive(packet, source);
      return;
    }

    var key = new NvModemDeviceKey(source, packet.sysid, packet.compid);
    uint msgid = packet.msgid;

    string parameterName = "";
    MAVLink.mavlink_param_value_t parameter = default;
    NvModemInfoMessage modemInfo = default;
    bool supportedModemInfo = false;
    Nv5RtspConfigMessage rtspConfig = default;
    bool nv5Identity = msgid is NvModemMessageIds.Nv5LinkStatus
        or NvModemMessageIds.Nv5RtspConfigAck
        or NvModemMessageIds.NvEncryptionKeysAck;
    bool nv4Identity = msgid == NvModemMessageIds.NvRxStat;
    if (msgid == NvModemMessageIds.Nv5RtspConfig) {
      rtspConfig = packet.ToStructure<Nv5RtspConfigMessage>();
      // Operations 0 (read) and 1 (write) originate at the GCS. A broadcast network may reflect
      // them back with the local 255:190 source and must not turn Mission Planner into a modem.
      // Operation 2 is the modem's report and remains a valid identity at every sysid/compid.
      nv5Identity = rtspConfig.Operation == 2;
    }
    if (msgid == NvModemMessageIds.NvModemInfo) {
      modemInfo = packet.ToStructure<NvModemInfoMessage>();
      supportedModemInfo = IsSupportedModemInfo(modemInfo);
      if (supportedModemInfo) {
        nv4Identity = modemInfo.ModemGeneration == 4;
        nv5Identity = modemInfo.ModemGeneration == 5;
      }
    }
    MAVLink.mavlink_uavcan_node_info_t nodeInfo = default;
    if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.UAVCAN_NODE_INFO) {
      nodeInfo = packet.ToStructure<MAVLink.mavlink_uavcan_node_info_t>();
      nv4Identity = IsLegacyNv4Node(nodeInfo);
    }
    if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE) {
      parameter = packet.ToStructure<MAVLink.mavlink_param_value_t>();
      parameterName = NvModemParameterCodec.Name(parameter.param_id);
      // Parameter names are not identities: older autopilots use CH1_*/CH2_* names too.
      // Import parameters only after a vendor message or the strict NV4 CAN passport has
      // confirmed this exact link/system/component endpoint as a modem.
    }
    if (msgid == NvModemMessageIds.NvModemInfo && !supportedModemInfo) {
      return;
    }
    if (_retiredDeviceKeys.Contains(key) && !supportedModemInfo) {
      return;
    }
    if (supportedModemInfo) {
      if (!ReconcileModemIdentity(key, modemInfo)) {
        return;
      }
      _retiredDeviceKeys.Remove(key);
    }

    _devices.TryGetValue(key, out NvModemDeviceState? device);
    if (device == null && !nv5Identity && !nv4Identity) {
      return;
    }
    bool inserted;
    if (device == null) {
      inserted = true;
      device = new NvModemDeviceState(key) { FirstSeenUtc = _utcNow() };
      _devices.Add(key, device);
      device.Choice = new NvModemDeviceChoice(device);
      Devices.Add(device.Choice);
    } else {
      inserted = false;
    }
    if (nv4Identity) {
      device.Generation = NvModemGeneration.Nv4;
    } else if (nv5Identity) {
      device.Generation = NvModemGeneration.Nv5;
    }
    if (supportedModemInfo) {
      // Known before selection, so a transaction-capable modem is never sent PARAM requests.
      device.ModemInfo = modemInfo;
      device.ModemInfoReady = true;
      ObserveNativeBoot(modemInfo);
      if (device.ParameterRequestDeferred) {
        // The capability is known now: the deferred read goes out, unless it is native.
        device.ParameterRequestDeferred = false;
        SendParameterRequestList(device);
      }
    }

    device.LastSeenUtc = _utcNow();
    UpdateDeviceLabel(device);
    if (SelectedDevice == null) {
      SelectedDevice = device.Choice;
    } else if (inserted) {
      if (device.Generation == NvModemGeneration.Nv5) {
        SendIdentityRequest(device.Key.Link, device.Key.SystemId, device.Key.ComponentId);
      }
      SendParameterRequestList(device);
    }

    if (supportedModemInfo) {
      device.ModemInfo = modemInfo;
      device.ModemInfoReady = true;
      device.Generation = modemInfo.ModemGeneration == 4
          ? NvModemGeneration.Nv4 : NvModemGeneration.Nv5;
      device.ProductProfile = modemInfo.ProductProfile;
      UpdateDeviceLabel(device);
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION
        && device.Generation == NvModemGeneration.Nv5) {
      device.ProductProfile = packet.ToStructure<MAVLink.mavlink_autopilot_version_t>().product_id;
      UpdateDeviceLabel(device);
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.UAVCAN_NODE_INFO
        && device.Generation == NvModemGeneration.Nv4) {
      device.LegacyNodeName = NvModemParameterCodec.Name(nodeInfo.name);
      UpdateDeviceLabel(device);
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE) {
      HandleParameterValue(device, parameter, parameterName);
    } else if (msgid == NvModemMessageIds.NvRxStat
        && device.Generation == NvModemGeneration.Nv4) {
      device.LegacyRxStatus = packet.ToStructure<NvRxStatMessage>();
      device.LegacyRxReady = true;
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.RADIO_STATUS
        && device.Generation == NvModemGeneration.Nv4) {
      device.LegacyRadioStatus = packet.ToStructure<MAVLink.mavlink_radio_status_t>();
      device.LegacyRadioReady = true;
    } else if (msgid == NvModemMessageIds.Nv5LinkStatus) {
      Nv5LinkStatusMessage status = packet.ToStructure<Nv5LinkStatusMessage>();
      device.Links[status.Channel] = status;
      UpdateDeviceLabel(device);
    } else if (msgid == NvModemMessageIds.Nv5RtspConfig) {
      if (rtspConfig.Operation == 2) {
        bool editorDirty = ReferenceEquals(device, SelectedState) && RtspPending(device);
        device.RtspPath = Latin1String(rtspConfig.Path);
        device.RtspPathReady = true;
        if (ReferenceEquals(device, SelectedState) && !editorDirty) {
          SetRtspPath(device.RtspPath);
        }
      }
    } else if (msgid == NvModemMessageIds.Nv5RtspConfigAck) {
      HandleRtspAck(device, packet.ToStructure<Nv5RtspConfigAckMessage>());
    } else if (msgid == NvModemMessageIds.NvEncryptionKeysAck) {
      HandleEncryptionKeysAck(device, packet.ToStructure<NvEncryptionKeysAckMessage>());
    } else if (msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_ACK) {
      HandleCommandAck(device, packet.ToStructure<MAVLink.mavlink_command_ack_t>());
    }

    if (ReferenceEquals(device, SelectedState)) {
      if (supportedModemInfo) {
        ObserveNativeTarget(device);
      }
      UpdateSelectedState();
    }
    RebuildCopySources();
  }

  private bool ReconcileModemIdentity(
      NvModemDeviceKey announcedKey, NvModemInfoMessage identity) {
    if (!HasStableModemIdentity(identity)) {
      return true;
    }

    // A UID that returns on another connection after its own one closed (a re-plugged USB modem,
    // a reopened network link) is the same modem: its record moves with it, so a native save in
    // recovery follows it to the new route. Two open links to one modem stay separate records.
    var openLinks = new HashSet<NvModemLink>(_transport.Snapshot());
    NvModemDeviceState[] matches = [.. _devices.Values.Where(device =>
        device.ModemInfoReady
        && !Equals(device.Key, announcedKey)
        && (Equals(device.Key.Link, announcedKey.Link) || !openLinks.Contains(device.Key.Link))
        && SameModemIdentity(device.ModemInfo, identity))];
    if (matches.Length == 0) {
      return true;
    }
    if (matches.Length != 1) {
      RetireIdentityCandidate(announcedKey);
      SetStatus("Multiple modem records reported the same UID2; identity migration is blocked.",
          true);
      return false;
    }

    NvModemDeviceState previous = matches[0];
    bool expectedTransition = ExpectedIdentityTransitionMatches(previous, announcedKey);
    // A record whose connection has closed cannot be a second live endpoint, however recently
    // it was heard.
    bool previousLive = IsOnline(previous)
        && (Equals(previous.Key.Link, announcedKey.Link) || openLinks.Contains(previous.Key.Link));
    if (!expectedTransition && previousLive) {
      RetireIdentityCandidate(announcedKey);
      SetStatus("Two live modem endpoints reported the same UID2; identity migration is blocked.",
          true);
      return false;
    }
    if (IsBusy || _currentWrite != null || _writeQueue.Count != 0) {
      _retiredDeviceKeys.Add(announcedKey);
      SetStatus("The modem identity changed during a configuration transaction; migration is "
          + "deferred until the transaction finishes.", true);
      return false;
    }

    _devices.TryGetValue(announcedKey, out NvModemDeviceState? target);
    if (target?.ModemInfoReady == true
        && !SameModemIdentity(target.ModemInfo, identity)) {
      SetStatus("The modem's new MAVLink address is already used by another device.", true);
      return false;
    }

    MigrateDeviceIdentity(previous, target, announcedKey, identity, expectedTransition);
    return true;
  }

  private void MigrateDeviceIdentity(
      NvModemDeviceState previous,
      NvModemDeviceState? target,
      NvModemDeviceKey announcedKey,
      NvModemInfoMessage identity,
      bool expectedTransition) {
    NvModemDeviceKey previousKey = previous.Key;
    bool targetWasSelected = target != null && ReferenceEquals(target, SelectedState);
    if (target != null && !ReferenceEquals(target, previous)) {
      foreach ((string name, double value) in target.Parameters) {
        previous.Parameters[name] = value;
      }
      foreach ((string name, byte type) in target.ParameterTypes) {
        previous.ParameterTypes[name] = type;
      }
      foreach ((int channel, Nv5LinkStatusMessage status) in target.Links) {
        previous.Links[channel] = status;
      }
      previous.ExpectedParameterCount = Math.Max(
          previous.ExpectedParameterCount, target.ExpectedParameterCount);
      previous.LastSeenUtc = Later(previous.LastSeenUtc, target.LastSeenUtc);
      previous.RebootReadyUtc = Later(previous.RebootReadyUtc, target.RebootReadyUtc);
      if (target.RtspPathReady) {
        previous.RtspPath = target.RtspPath;
        previous.RtspPathReady = true;
      }
      _devices.Remove(target.Key);
      if (target.Choice != null) {
        Devices.Remove(target.Choice);
      }
    }

    _devices.Remove(previousKey);
    previous.Key = announcedKey;
    previous.ModemInfo = identity;
    previous.ModemInfoReady = true;
    previous.Generation = identity.ModemGeneration == 4
        ? NvModemGeneration.Nv4 : NvModemGeneration.Nv5;
    previous.ProductProfile = identity.ProductProfile;
    previous.IdentityTransitionExpected = false;
    _devices[announcedKey] = previous;
    _retiredDeviceKeys.Add(previousKey);
    _retiredDeviceKeys.Remove(announcedKey);

    if (targetWasSelected && previous.Choice != null) {
      _switchGuard = true;
      SelectedDevice = previous.Choice;
      _switchGuard = false;
      ShowSelectedDevice(clearDeviceParameters: false);
    }
    UpdateDeviceLabel(previous);
    if (expectedTransition) {
      SetStatus($"Modem reconnected with its requested identity "
          + $"{announcedKey.SystemId}:{announcedKey.ComponentId}.");
    }
  }

  private void RetireIdentityCandidate(NvModemDeviceKey key) {
    _retiredDeviceKeys.Add(key);
    if (!_devices.TryGetValue(key, out NvModemDeviceState? candidate)
        || candidate.ModemInfoReady
        || ReferenceEquals(_currentWrite?.Device, candidate)) {
      return;
    }

    bool wasSelected = ReferenceEquals(candidate, SelectedState);
    _devices.Remove(key);
    if (candidate.Choice != null) {
      Devices.Remove(candidate.Choice);
    }
    if (wasSelected) {
      _switchGuard = true;
      SelectedDevice = Devices.FirstOrDefault();
      _switchGuard = false;
      ShowSelectedDevice(clearDeviceParameters: false);
    }
    RebuildCopySources();
  }

  private bool ExpectedIdentityTransitionMatches(
      NvModemDeviceState previous, NvModemDeviceKey announcedKey) {
    DateTime now = _utcNow();
    return previous.IdentityTransitionExpected
        && now >= previous.RebootReadyUtc
        && now <= previous.IdentityTransitionExpiresUtc
        && Equals(previous.Key.Link, announcedKey.Link)
        && announcedKey.SystemId == previous.ExpectedSystemId
        && announcedKey.ComponentId == previous.ExpectedComponentId;
  }

  private void ArmExpectedIdentityTransition(NvModemDeviceState device) {
    device.ExpectedSystemId = ParameterByte(
        device, "MAV_SYS_ID", device.Key.SystemId);
    device.ExpectedComponentId = ParameterByte(
        device, "LOCAL_IP_4", device.Key.ComponentId);
    device.IdentityTransitionExpected = true;
    device.IdentityTransitionExpiresUtc = Later(_utcNow(), device.RebootReadyUtc)
        + ExpectedIdentityTransition;
  }

  private static byte ParameterByte(
      NvModemDeviceState device, string name, byte fallback) {
    double value = device.Parameters.GetValueOrDefault(name, fallback);
    return double.IsFinite(value) && value == Math.Truncate(value)
        && value is >= byte.MinValue and <= byte.MaxValue
            ? (byte)value : fallback;
  }

  private static bool IsIdentityAddressParameter(string name) =>
      name is "MAV_SYS_ID" or "LOCAL_IP_1" or "LOCAL_IP_2" or "LOCAL_IP_3" or "LOCAL_IP_4";

  private static bool IsSupportedModemInfo(NvModemInfoMessage identity) =>
      identity.SchemaVersion == 1
      && identity.ModemGeneration is 4 or 5
      && (identity.Capabilities & NvModemCapabilities.MavlinkParameters) != 0;

  private static bool HasStableModemIdentity(NvModemInfoMessage identity) =>
      identity.Uid2?.Any(value => value != 0) == true;

  private static bool SameModemIdentity(
      NvModemInfoMessage left, NvModemInfoMessage right) =>
      left.ModemGeneration == right.ModemGeneration
      && left.ProductProfile == right.ProductProfile
      && (left.Uid2 ?? []).SequenceEqual(right.Uid2 ?? []);

  private static DateTime Later(DateTime left, DateTime right) =>
      left >= right ? left : right;

  private void ReplayCachedParameters(
      NvModemLink source, IReadOnlyList<NvModemCachedParameter> cached) {
    foreach (IGrouping<NvModemEndpoint, NvModemCachedParameter> endpoint in cached.GroupBy(
                 parameter => new NvModemEndpoint(
                     parameter.SystemId, parameter.ComponentId))) {
      var key = new NvModemDeviceKey(
          source, endpoint.Key.SystemId, endpoint.Key.ComponentId);
      if (!_devices.TryGetValue(key, out NvModemDeviceState? device)) {
        continue;
      }

      foreach (NvModemCachedParameter parameter in endpoint) {
        HandleParameterValue(
            device, parameter.Name, parameter.Value, parameter.Type,
            parameter.ReportedCount, parameter.RawValueBits);
      }
      if (ReferenceEquals(device, SelectedState)) {
        UpdateSelectedState();
      }
    }
    RebuildCopySources();
  }

  private void HandleParameterValue(
      NvModemDeviceState device, MAVLink.mavlink_param_value_t message, string name) {
    double decoded = NvModemParameterCodec.Decode(message.param_value, message.param_type);
    HandleParameterValue(device, name, decoded, message.param_type, message.param_count,
        unchecked((uint)BitConverter.SingleToInt32Bits(message.param_value)));
  }

  private void HandleParameterValue(
      NvModemDeviceState device,
      string name,
      double decoded,
      byte type,
      int reportedCount,
      uint rawValueBits) {
    if (device.ParameterRefreshPending) {
      device.Parameters.Clear();
      device.ParameterTypes.Clear();
      device.LegacyKeyWords.Clear();
      device.LegacyKeyFingerprint = "";
      device.ExpectedParameterCount = 0;
      device.ParameterRefreshPending = false;
      if (ReferenceEquals(device, SelectedState)) {
        ClearParameterRows();
      }
    }
    bool newParameter = !device.Parameters.ContainsKey(name);
    int legacyWord = NvModemCatalog.Nv4KeyWordIndex(name);
    if (device.Generation == NvModemGeneration.Nv4 && legacyWord >= 0) {
      device.LegacyKeyWords[legacyWord] = rawValueBits;
      if (device.LegacyKeyWords.Count == 8) {
        byte[] bytes = new byte[NvModemCatalog.Nv4KeyBytes];
        for (int index = 0; index < 8; index++) {
          BitConverter.GetBytes(device.LegacyKeyWords[index]).CopyTo(bytes, index * 4);
        }
        device.LegacyKeyFingerprint = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
      }
    }
    device.Parameters[name] = decoded;
    device.ParameterTypes[name] = type;
    device.ExpectedParameterCount = reportedCount;
    device.ParameterListLastProgressUtc = _utcNow();
    if (device.ParameterListInProgress && reportedCount > 0
        && device.Parameters.Count >= reportedCount) {
      device.ParameterListInProgress = false;
      if (ReferenceEquals(device, SelectedState) && !IsBusy) {
        SetStatus($"Read {device.Parameters.Count} parameters from {ModemName(device)} "
            + $"{device.Key.SystemId}:{device.Key.ComponentId}.");
      }
    }
    if (name == "MODEM_PROFILE") {
      device.Generation = NvModemGeneration.Nv5;
      device.ProductProfile = (uint)Math.Max(0, decoded);
    } else if (name == "HW_VERSION") {
      device.Generation = NvModemGeneration.Nv4;
    }
    UpdateDeviceLabel(device);

    if (_currentWrite is { Kind: NvWriteKind.Parameter } write
        && ReferenceEquals(write.Device, device) && write.Name == name) {
      bool nv5KeyWrite = device.Generation == NvModemGeneration.Nv5
          && NvModemCatalog.Nv5KeyWordIndex(name) >= 0;
      bool keyTypeAccepted = !nv5KeyWrite
          || write.ParameterType == (byte)MAVLink.MAV_PARAM_TYPE.INT32
          && type == (byte)MAVLink.MAV_PARAM_TYPE.INT32;
      bool accepted = keyTypeAccepted
          && NvModemParameterCodec.ValuesEqual(decoded, write.Value, write.ParameterType);
      if (!accepted) {
        // PARAM_VALUE has no transaction id. A list response for the same name can arrive
        // between PARAM_SET and its typed echo, so retain the staged value and wait for the
        // exact echo or the normal retry timeout.
        write.MismatchedParameterEcho = true;
        write.ReportedParameterValue = decoded;
        write.ReportedParameterType = type;
        return;
      }
      device.Parameters[name] = decoded;
      if (_parameterRows.TryGetValue(name, out var acceptedRow)) {
        acceptedRow.Accept(decoded);
      }
      if (NvModemCatalog.RequiresManualReboot(device.Generation, name)) {
        double saveMs = device.Parameters.GetValueOrDefault("MAV_SAVE_MS", 2000);
        device.RebootReadyUtc = _utcNow().AddMilliseconds(Math.Max(100, saveMs) + 250);
        if (device.IdentityTransitionExpected) {
          device.IdentityTransitionExpiresUtc = Later(
              device.IdentityTransitionExpiresUtc,
              device.RebootReadyUtc + ExpectedIdentityTransition);
        }
      }
      if (IsIdentityAddressParameter(name)) {
        ArmExpectedIdentityTransition(device);
      }
      CompleteCurrentWrite();
    } else if (ReferenceEquals(device, SelectedState)) {
      if (_parameterRows.TryGetValue(name, out var row)) {
        if (!row.IsChanged) {
          row.Accept(decoded);
        }
      } else if (newParameter) {
        AddParameterRow(name, decoded, type);
      }
    }
  }

  private void HandleRtspAck(NvModemDeviceState device, Nv5RtspConfigAckMessage ack) {
    if (_currentWrite is not { Kind: NvWriteKind.RtspPath } write
        || !ReferenceEquals(write.Device, device) || write.TransactionId != ack.TransactionId) {
      return;
    }
    if (ack.Result != 0) {
      FailWrites($"RTSP path rejected: result {ack.Result}, detail {ack.Detail}.");
      return;
    }
    device.RtspPath = write.Path;
    device.RtspPathReady = true;
    if (ReferenceEquals(device, SelectedState)) {
      SetRtspPath(write.Path);
    }
    CompleteCurrentWrite();
  }

  private void HandleEncryptionKeysAck(
      NvModemDeviceState device, NvEncryptionKeysAckMessage ack) {
    if (_currentWrite is not { Kind: NvWriteKind.EncryptionKeys } write
        || !ReferenceEquals(write.Device, device)
        || write.TransactionId != ack.TransactionId) {
      return;
    }
    if (ack.Result != NvEncryptionKeysResults.Applied
        || ack.SchemaVersion != 1 || ack.ChannelMask != write.ChannelMask) {
      FailWrites($"Atomic encryption-key write rejected: result {ack.Result}, "
          + $"detail {ack.Detail}.");
      return;
    }

    for (int channel = 1; channel <= 2; channel++) {
      byte channelBit = (byte)(1 << (channel - 1));
      if ((write.ChannelMask & channelBit) == 0) {
        continue;
      }

      byte[] key = channel == 1 ? write.Channel1Key : write.Channel2Key;
      for (int word = 0; word < NvModemCatalog.Nv5KeyWordCount; word++) {
        string name = NvModemCatalog.Nv5KeyWordName(channel, word);
        double value = NvModemCatalog.Nv5SignedKeyWord(key, word);
        device.Parameters[name] = value;
        device.ParameterTypes[name] = (byte)MAVLink.MAV_PARAM_TYPE.INT32;
        if (_parameterRows.TryGetValue(name, out NvModemParameterRow? row)) {
          row.Accept(value);
        }
      }

      string fingerprintName = $"CH{channel}_KEY_HASH";
      uint fingerprint = channel == 1
          ? ack.Channel1Fingerprint : ack.Channel2Fingerprint;
      if (device.Parameters.ContainsKey(fingerprintName)) {
        device.Parameters[fingerprintName] = fingerprint;
        if (_parameterRows.TryGetValue(fingerprintName, out NvModemParameterRow? row)) {
          row.Accept(fingerprint);
        }
      }
    }

    // APPLIED is sent only after the complete selected key snapshot reaches
    // non-volatile storage, so the modem can be rebooted immediately.
    device.RebootReadyUtc = _utcNow();
    SyncKeyText();
    CompleteCurrentWrite();
  }

  private void HandleCommandAck(NvModemDeviceState device, MAVLink.mavlink_command_ack_t ack) {
    if (_currentWrite is not { } write || !ReferenceEquals(write.Device, device)) {
      return;
    }

    bool relevant = write.Kind == NvWriteKind.Reboot
        && ack.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN
        || write.Kind == NvWriteKind.TransmitControl && ack.command == SetTransmitEnabledCommand;
    if (!relevant) {
      return;
    }

    IsBusy = false;
    _currentWrite = null;
    if (ack.result != (byte)MAVLink.MAV_RESULT.ACCEPTED) {
      SetStatus($"Modem rejected the command (MAV_RESULT {ack.result}).", true);
    } else if (write.Kind == NvWriteKind.Reboot) {
      device.LastSeenUtc = _utcNow() - OnlineTimeout - TimeSpan.FromMilliseconds(1);
      SetStatus($"Reboot accepted by NV5 {device.Key.SystemId}:{device.Key.ComponentId}; "
          + "waiting for it to return…");
      _ = DiscoverAfterRebootAsync();
    } else {
      SetStatus($"Radio {write.Channel} TX {(write.Enabled ? "enabled" : "suppressed")} on "
          + $"NV5 {device.Key.SystemId}:{device.Key.ComponentId}.");
    }
    RefreshControls();
  }

  private void ShowSelectedDevice(bool clearDeviceParameters) {
    ClearParameterRows();
    SetRtspPath("");
    ResetRadioRows();
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      UpdateSelectedState();
      return;
    }
    if (clearDeviceParameters) {
      ClearDeviceParameters(device);
      SendParameterRequestList(device);
      if (device.Generation == NvModemGeneration.Nv5) {
        SendIdentityRequest(device.Key.Link, device.Key.SystemId, device.Key.ComponentId);
      }
    } else {
      RebuildParameters();
    }
    if (SupportsRtsp(device)) {
      SendRtspRequest(device);
    }
    if (SupportsNativeSettings(device)) {
      ObserveNativeTarget(device);
    } else {
      // A modem configured through PARAM_SET needs no native session; free one held elsewhere.
      NativeSettings.StopReading();
    }

    UpdateSelectedState();
  }

  private void ClearDeviceParameters(NvModemDeviceState device) {
    device.Parameters.Clear();
    device.ParameterTypes.Clear();
    device.LegacyKeyWords.Clear();
    device.LegacyKeyFingerprint = "";
    device.ExpectedParameterCount = 0;
    device.ParameterRefreshPending = false;
    device.ParameterListInProgress = false;
    if (ReferenceEquals(device, SelectedState)) {
      ClearParameterRows();
      ParameterProgress = "Parameters: 0 / 0";
      KeyText = "";
    }
  }

  private void ClearParameterRows() {
    SelectedParameter = null;
    foreach (NvModemParameterRow row in _parameterRows.Values) {
      row.PropertyChanged -= OnParameterRowChanged;
    }
    _parameterRows.Clear();
    Parameters.Clear();
    RebuildRadioChoices();
    RefreshControls();
  }

  private void RebuildParameters() {
    ClearParameterRows();
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return;
    }

    foreach ((string name, double value) in device.Parameters.OrderBy(item => item.Key,
                 StringComparer.Ordinal)) {
      AddParameterRow(name, value,
          device.ParameterTypes.GetValueOrDefault(name, (byte)MAVLink.MAV_PARAM_TYPE.REAL32));
    }
    RebuildVisibleParameters();
    SyncKeyText();
  }

  private void AddParameterRow(string name, double value, byte type) {
    if (_parameterRows.ContainsKey(name)) {
      return;
    }

    var row = new NvModemParameterRow(name, value, type);
    row.PropertyChanged += OnParameterRowChanged;
    _parameterRows.Add(name, row);
    RebuildVisibleParameters();
    RebuildRadioChoices();
  }

  private void OnParameterRowChanged(object? sender, PropertyChangedEventArgs e) {
    if (e.PropertyName is nameof(NvModemParameterRow.ValueText)
        or nameof(NvModemParameterRow.IsChanged)
        or nameof(NvModemParameterRow.IsValid)) {
      if (e.PropertyName == nameof(NvModemParameterRow.ValueText)
          && sender is NvModemParameterRow row
          && row.Name.EndsWith("_MOD", StringComparison.Ordinal)) {
        RebuildVisibleParameters();
      }
      RefreshControls();
    }
  }

  private void RebuildVisibleParameters() {
    var staged = _parameterRows.Values
        .Where(row => row.TryValue(out _))
        .ToDictionary(row => row.Name, row => { row.TryValue(out double value); return value; },
            StringComparer.Ordinal);
    NvModemParameterRow[] wanted = [.. _parameterRows.Values
        .Where(row => NvModemCatalog.Applicable(row.Name, staged))
        .OrderBy(row => row.Group, StringComparer.Ordinal)
        .ThenBy(row => row.Name, StringComparer.Ordinal)];
    if (Parameters.SequenceEqual(wanted)) {
      return;
    }

    Parameters.Clear();
    foreach (var row in wanted) {
      Parameters.Add(row);
    }

    OnPropertyChanged(nameof(HasParameters));
  }

  private bool StageParameter(string name, double value, byte type, bool createIfMissing) {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      return false;
    }

    if (!_parameterRows.TryGetValue(name, out var row)) {
      if (!createIfMissing) {
        return false;
      }

      row = new NvModemParameterRow(name, double.NaN, type);
      row.PropertyChanged += OnParameterRowChanged;
      _parameterRows.Add(name, row);
      device.ParameterTypes[name] = type;
    }
    string next = NvModemParameterCodec.Display(value, row.Type);
    row.ValueText = next;
    return row.IsValid;
  }

  private double StagedValue(string name, double fallback) =>
      _parameterRows.TryGetValue(name, out var row) && row.TryValue(out double value)
          ? value : fallback;

  private bool TrySelectedPresetInteger(string suffix, out long value) {
    value = 0;
    int channel = SelectedPresetRadio?.Channel ?? 0;
    if (channel is < 1 or > 2
        || !_parameterRows.TryGetValue($"CH{channel}_{suffix}", out var row)
        || !row.TryValue(out double numeric)
        || !double.IsFinite(numeric)
        || numeric != Math.Truncate(numeric)
        || numeric is < long.MinValue or > long.MaxValue) {
      return false;
    }
    value = (long)numeric;
    return true;
  }

  private bool IsApplicable(string name) {
    var staged = _parameterRows.Values.Where(row => row.TryValue(out _))
        .ToDictionary(row => row.Name, row => { row.TryValue(out double value); return value; },
            StringComparer.Ordinal);
    return NvModemCatalog.Applicable(name, staged);
  }

  private bool StageEncryptionKey() {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedKeyRadio?.Channel ?? 0;
    if (device == null || channel == 0 || IsBusy) {
      SetStatus("Read parameters and select a radio before setting its key.", true);
      return false;
    }
    int expected = device.Generation == NvModemGeneration.Nv4
        ? NvModemCatalog.Nv4KeyBytes : NvModemCatalog.Nv5KeyBytes;
    bool legacy = device.Generation == NvModemGeneration.Nv4;
    if (!TryDecodeKeyText(KeyText, expected, legacy, out byte[] bytes)) {
      SetStatus(legacy
          ? "Enter 32 printable ASCII characters or hex: followed by 64 hexadecimal digits."
          : "Enter exactly 32 hexadecimal digits without spaces (0-9, A-F).", true);
      return false;
    }
    if (legacy) {
      for (int word = 0; word < 8; word++) {
        string name = $"ENC_KEY_BYTE{word + 1}";
        if (!_parameterRows.ContainsKey(name)) {
          SetStatus("The selected NV4 did not publish all eight encryption-key words.", true);
          return false;
        }
        int signed = BitConverter.ToInt32(bytes, word * 4);
        if (!StageParameter(name, signed,
                device.ParameterTypes.GetValueOrDefault(name,
                    (byte)MAVLink.MAV_PARAM_TYPE.INT32), false)) {
          return false;
        }
      }
    } else {
      for (int word = 0; word < NvModemCatalog.Nv5KeyWordCount; word++) {
        string name = NvModemCatalog.Nv5KeyWordName(channel, word);
        if (!_parameterRows.ContainsKey(name)
            || !StageParameter(name, NvModemCatalog.Nv5SignedKeyWord(bytes, word),
                device.ParameterTypes.GetValueOrDefault(name,
                    (byte)MAVLink.MAV_PARAM_TYPE.INT32), false)) {
          SetStatus("The selected NV5 did not publish the complete key-word set.", true);
          return false;
        }
      }
    }
    RebuildVisibleParameters();
    SyncKeyText();
    RefreshControls();
    return true;
  }

  private void SyncKeyText() {
    NvModemDeviceState? device = SelectedState;
    int channel = SelectedKeyRadio?.Channel ?? 0;
    if (device == null || channel == 0) {
      KeyText = "";
      KeyFingerprint = "Stored fingerprint: unavailable";
      return;
    }
    var bytes = new List<byte>();
    if (device.Generation == NvModemGeneration.Nv4) {
      for (int word = 1; word <= 8; word++) {
        if (!_parameterRows.TryGetValue($"ENC_KEY_BYTE{word}", out var row)
            || !row.TryValue(out double numeric)) {
          bytes.Clear(); break;
        }
        bytes.AddRange(BitConverter.GetBytes(unchecked((int)Math.Round(numeric))));
      }
      KeyHint = "NV4 exposes eight INT32 words (-2147483648..2147483647), packed as 32 raw "
          + "bytes in little-endian word order. The key field shows 64 uppercase hexadecimal "
          + "digits; all eight words must match the peer.";
      KeyFingerprint = device.LegacyKeyFingerprint.Length == 0
          ? "Stored fingerprint: unavailable"
          : "Stored fingerprint: " + device.LegacyKeyFingerprint;
    } else {
      var key = new byte[NvModemCatalog.Nv5KeyBytes];
      bool complete = true;
      for (int word = 0; word < NvModemCatalog.Nv5KeyWordCount; word++) {
        if (!_parameterRows.TryGetValue(
                NvModemCatalog.Nv5KeyWordName(channel, word), out var row)
            || !TryNv5KeyWord(row, out uint numeric)) {
          complete = false;
          break;
        }
        NvModemCatalog.WriteNv5KeyWord(key, word, numeric);
      }
      if (complete) {
        bytes.AddRange(key);
      }
      KeyHint = "NV5 exposes four INT32 words (-2147483648..2147483647) per radio. AES-128 keys "
          + "are shown as exactly 32 uppercase hexadecimal digits without spaces or a prefix. "
          + "Only the selected radio is targeted; DIVERSITY never copies or couples keys.";
      string hash = $"CH{channel}_KEY_HASH";
      KeyFingerprint = _parameterRows.TryGetValue(hash, out var hashRow)
          ? "Stored fingerprint: " + hashRow.ValueText
          : "Stored fingerprint: unavailable";
    }
    int expectedBytes = device.Generation == NvModemGeneration.Nv4
        ? NvModemCatalog.Nv4KeyBytes : NvModemCatalog.Nv5KeyBytes;
    KeyText = bytes.Count == expectedBytes ? Convert.ToHexString(bytes.ToArray()) : "";
  }

  private static bool TryDecodeKeyText(
      string text, int expectedBytes, bool legacy, out byte[] bytes) {
    bytes = [];
    if (!legacy) {
      if (text.Length != expectedBytes * 2 || !text.All(Uri.IsHexDigit)) {
        return false;
      }
      bytes = Convert.FromHexString(text);
      return bytes.Length == expectedBytes;
    }

    string hexadecimal = text.StartsWith("hex:", StringComparison.OrdinalIgnoreCase)
        ? text[4..] : text;
    if (hexadecimal.Length == expectedBytes * 2
        && hexadecimal.All(Uri.IsHexDigit)) {
      try {
        bytes = Convert.FromHexString(hexadecimal);
      } catch (FormatException) {
        return false;
      }
      return bytes.Length == expectedBytes;
    }

    if (text.Length != expectedBytes
        || text.Any(character => character is < ' ' or > '~')) {
      return false;
    }
    bytes = Encoding.ASCII.GetBytes(text);
    return bytes.Length == expectedBytes;
  }

  private static bool TryNv5KeyWord(NvModemParameterRow row, out uint value) {
    value = 0;
    if (!row.TryValue(out double numeric) || numeric != Math.Floor(numeric)
        || numeric is < int.MinValue or > int.MaxValue) {
      return false;
    }
    value = unchecked((uint)(int)numeric);
    return true;
  }

  private void RebuildRadioChoices() {
    NvModemDeviceState? device = SelectedState;
    int oldPreset = SelectedPresetRadio?.Channel ?? 0;
    int oldKey = SelectedKeyRadio?.Channel ?? 0;
    PresetRadios.Clear();
    KeyRadios.Clear();
    if (device?.Generation == NvModemGeneration.Nv4) {
      if (Enumerable.Range(1, 8).All(index => _parameterRows.ContainsKey($"ENC_KEY_BYTE{index}"))) {
        KeyRadios.Add(new NvRadioChoice(1, "RFM radio"));
      }
    } else if (device?.Generation == NvModemGeneration.Nv5) {
      for (int channel = 1; channel <= 2; channel++) {
        if (_parameterRows.ContainsKey($"CH{channel}_MOD")) {
          PresetRadios.Add(new NvRadioChoice(channel, $"Radio {channel}"));
        }
        if (Enumerable.Range(0, NvModemCatalog.Nv5KeyWordCount)
            .All(word => _parameterRows.ContainsKey(
                NvModemCatalog.Nv5KeyWordName(channel, word)))) {
          KeyRadios.Add(new NvRadioChoice(channel, $"Radio {channel}"));
        }
      }
    }
    SelectedPresetRadio = PresetRadios.FirstOrDefault(item => item.Channel == oldPreset)
        ?? PresetRadios.FirstOrDefault();
    SelectedKeyRadio = KeyRadios.FirstOrDefault(item => item.Channel == oldKey)
        ?? KeyRadios.FirstOrDefault();
  }

  private void RebuildCopySources() {
    object? previous = SelectedCopySource?.DeviceIdentity;
    int previousChannel = SelectedCopySource?.Channel ?? 0;
    CopySources.Clear();
    foreach (NvModemDeviceState source in _devices.Values) {
      if (ReferenceEquals(source, SelectedState) || source.Generation != NvModemGeneration.Nv5
          || source.ParameterListInProgress || source.ExpectedParameterCount <= 0
          || source.Parameters.Count < source.ExpectedParameterCount) {
        continue;
      }

      for (int channel = 1; channel <= 2; channel++) {
        if (!source.Parameters.ContainsKey($"CH{channel}_MOD")) {
          continue;
        }

        CopySources.Add(new NvRadioCopyChoice(source, channel,
            $"{ModemName(source)} {source.Key.SystemId}:{source.Key.ComponentId} · "
            + $"{HardwareModel(source)} · Radio {channel}"));
      }
    }
    SelectedCopySource = CopySources.FirstOrDefault(item =>
        ReferenceEquals(item.DeviceIdentity, previous) && item.Channel == previousChannel)
        ?? CopySources.FirstOrDefault();
  }

  internal bool QueueParameterWrites(
      NvModemDeviceState device, IEnumerable<NvModemParameterRow> rows,
      bool addLegacyRefresh = true) {
    bool any = false;
    foreach (NvModemParameterRow row in rows) {
      if (!row.TryValue(out double value)) {
        continue;
      }

      _writeQueue.Enqueue(new NvWriteOperation {
        Kind = NvWriteKind.Parameter,
        Device = device,
        Name = row.Name,
        Value = value,
        ParameterType = row.Type,
      });
      any = true;
    }
    string? refreshName = device.Generation == NvModemGeneration.Nv4
        ? NvModemCatalog.Nv4RefreshParameterName(device.Parameters) : null;
    if (any && addLegacyRefresh && refreshName != null) {
      _writeQueue.Enqueue(new NvWriteOperation {
        Kind = NvWriteKind.Parameter,
        Device = device,
        Name = refreshName,
        Value = 1,
        ParameterType = device.ParameterTypes.GetValueOrDefault(refreshName,
            (byte)MAVLink.MAV_PARAM_TYPE.UINT32),
      });
    }
    return true;
  }

  private bool QueueEncryptionKeyWrite(NvModemDeviceState device, byte channelMask) {
    byte[] channel1 = [];
    byte[] channel2 = [];
    for (int channel = 1; channel <= 2; channel++) {
      if ((channelMask & (1 << (channel - 1))) == 0) {
        continue;
      }

      var key = new byte[NvModemCatalog.Nv5KeyBytes];
      for (int word = 0; word < NvModemCatalog.Nv5KeyWordCount; word++) {
        string name = NvModemCatalog.Nv5KeyWordName(channel, word);
        if (!_parameterRows.TryGetValue(name, out NvModemParameterRow? row)
            || !TryNv5KeyWord(row, out uint value)) {
          SetStatus($"Could not build the complete Radio {channel} encryption key: "
              + $"{name} is missing or invalid.", true);
          return false;
        }
        NvModemCatalog.WriteNv5KeyWord(key, word, value);
      }

      if (channel == 1) {
        channel1 = key;
      } else {
        channel2 = key;
      }
    }

    _writeQueue.Enqueue(new NvWriteOperation {
      Kind = NvWriteKind.EncryptionKeys,
      Device = device,
      TransactionId = ++_nextTransactionId,
      ChannelMask = channelMask,
      Channel1Key = channel1,
      Channel2Key = channel2,
    });
    return true;
  }

  internal void BeginQueuedWrites(NvModemDeviceState device, bool keyOnly, int keyChannel) {
    if (_writeQueue.Count == 0) {
      return;
    }

    _parameterWriteInTransaction = _writeQueue.Any(item =>
        item.Kind is NvWriteKind.Parameter or NvWriteKind.EncryptionKeys);
    _rebootRequiredInTransaction = _writeQueue.Any(item =>
        item.Kind == NvWriteKind.EncryptionKeys
        || item.Kind == NvWriteKind.Parameter
        && NvModemCatalog.RequiresManualReboot(device.Generation, item.Name));
    NvWriteOperation? keyWrite = _writeQueue.FirstOrDefault(item =>
        item.Kind == NvWriteKind.EncryptionKeys);
    _keyWriteInTransaction = keyOnly || keyWrite != null;
    _keyWriteChannel = keyChannel != 0 ? keyChannel : keyWrite?.ChannelMask switch {
      0x01 => 1,
      0x02 => 2,
      _ => 0,
    };
    IsBusy = true;
    SetStatus($"Applying {_writeQueue.Count} change(s) to {ModemName(device)} "
        + $"{device.Key.SystemId}:{device.Key.ComponentId}…");
    SendNextWrite();
  }

  private void StartSingleWrite(NvWriteOperation operation) {
    if (IsBusy) {
      return;
    }

    IsBusy = true;
    _currentWrite = operation;
    SendOperation(operation);
    RefreshControls();
  }

  private void SendNextWrite() {
    if (_currentWrite != null) {
      return;
    }

    if (_writeQueue.Count == 0) {
      IsBusy = false;
      NvModemDeviceState? device = SelectedState;
      if (_keyWriteInTransaction) {
        SyncKeyText();
        if (device?.Generation == NvModemGeneration.Nv4) {
          SetStatus("NV4 RFM key accepted, stored and applied. Set the same key text on its peer.");
        } else if (_keyWriteChannel > 0) {
          SetStatus($"Radio {_keyWriteChannel} key was stored atomically. "
              + "Set the same key on its peer, then reboot the modem.");
        } else {
          SetStatus("The selected radio keys were stored atomically. "
              + "Set the matching keys on their peers, then reboot the modem.");
        }
      } else if (_rebootRequiredInTransaction) {
        SetStatus("Parameters accepted. Flash commit is in progress; settings that restart the "
            + "modem will follow its UID when it returns with the requested identity.");
      } else if (_parameterWriteInTransaction) {
        SetStatus(device?.Generation == NvModemGeneration.Nv4
            ? "NV4 parameters accepted, stored and applied."
            : "Parameters accepted and will take effect after the flash commit.");
      } else {
        SetStatus("RTSP configuration applied successfully.");
      }
      _parameterWriteInTransaction = false;
      _rebootRequiredInTransaction = false;
      _keyWriteInTransaction = false;
      _keyWriteChannel = 0;
      RefreshControls();
      return;
    }
    _currentWrite = _writeQueue.Dequeue();
    SendOperation(_currentWrite);
  }

  private void CompleteCurrentWrite() {
    _currentWrite = null;
    SendNextWrite();
  }

  private void SendOperation(NvWriteOperation operation) {
    object packet = operation.Kind switch {
      NvWriteKind.Parameter => new MAVLink.mavlink_param_set_t {
        target_system = operation.Device.Key.SystemId,
        target_component = operation.Device.Key.ComponentId,
        param_id = NvModemParameterCodec.NameBytes(operation.Name),
        param_value = NvModemParameterCodec.Encode(operation.Value, operation.ParameterType),
        param_type = operation.ParameterType,
      },
      NvWriteKind.EncryptionKeys => new NvEncryptionKeysSetMessage {
        TransactionId = operation.TransactionId,
        TargetSystem = operation.Device.Key.SystemId,
        TargetComponent = operation.Device.Key.ComponentId,
        SchemaVersion = 1,
        ChannelMask = operation.ChannelMask,
        Channel1Key = FixedKey(operation.Channel1Key),
        Channel2Key = FixedKey(operation.Channel2Key),
      },
      NvWriteKind.RtspPath => new Nv5RtspConfigMessage {
        TransactionId = operation.TransactionId,
        TargetSystem = operation.Device.Key.SystemId,
        TargetComponent = operation.Device.Key.ComponentId,
        Operation = 1,
        Path = FixedLatin1(operation.Path, 96, 95),
      },
      NvWriteKind.Reboot => CommandLong(operation.Device,
          MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 1, 0),
      NvWriteKind.TransmitControl => CommandLong(operation.Device,
          (MAVLink.MAV_CMD)SetTransmitEnabledCommand, operation.Channel,
          operation.Enabled ? 1 : 0),
      _ => throw new ArgumentOutOfRangeException(),
    };
    operation.SentUtc = _utcNow();
    if (!_transport.Send(operation.Device.Key.Link, packet,
            operation.Device.Key.SystemId, operation.Device.Key.ComponentId)) {
      FailWrites("The selected MAVLink connection is no longer open.");
    }
  }

  private void FailWrites(string message) {
    _writeQueue.Clear();
    _currentWrite = null;
    IsBusy = false;
    _parameterWriteInTransaction = false;
    _rebootRequiredInTransaction = false;
    _keyWriteInTransaction = false;
    _keyWriteChannel = 0;
    SetStatus(message, true);
    RefreshControls();
  }

  private void OnServiceTick(object? sender, EventArgs e) => ServiceTransactions();

  internal void ServiceTransactions() {
    DateTime now = _utcNow();
    NativeSettings.Service();
    foreach (NvModemDeviceState device in _devices.Values) {
      UpdateDeviceLabel(device);
      if (SupportsNativeSettings(device)) {
        // Its settings are read through configuration transactions, never as a PARAM list.
        device.ParameterListInProgress = false;
      } else if (device.ParameterRequestDeferred && now - device.FirstSeenUtc >= PassportWait) {
        // No passport arrived: older firmware, read as before.
        SendParameterRequestList(device);
      }
      if (!device.ParameterListInProgress) {
        continue;
      }

      TimeSpan silent = now - device.ParameterListLastProgressUtc;
      if (silent >= ParameterListRetry && device.ParameterListRetries < MaximumParameterListRetries) {
        SendParameterRequestList(device, retry: true);
        if (ReferenceEquals(device, SelectedState) && !IsBusy) {
          SetStatus($"No new parameter response; retrying request {device.ParameterListRetries} "
              + $"/ {MaximumParameterListRetries}…");
        }
      } else if (silent >= ParameterListTimeout) {
        device.ParameterListInProgress = false;
        if (ReferenceEquals(device, SelectedState) && !IsBusy) {
          SetStatus($"Parameter read stopped at {device.Parameters.Count} / "
              + $"{device.ExpectedParameterCount}. Press Refresh selected to retry.", true);
        }
      }
    }
    if (_currentWrite != null && now - _currentWrite.SentUtc >= TransactionTimeout) {
      _currentWrite.Retries++;
      if (_currentWrite.Retries >= MaximumWriteAttempts) {
        if (_currentWrite.Kind == NvWriteKind.Parameter
            && _currentWrite.MismatchedParameterEcho) {
          FailWrites($"Modem did not accept parameter {_currentWrite.Name}: requested "
              + $"{NvModemParameterCodec.Display(_currentWrite.Value, _currentWrite.ParameterType)} "
              + $"(type {_currentWrite.ParameterType}), modem reports "
              + $"{NvModemParameterCodec.Display(
                  _currentWrite.ReportedParameterValue, _currentWrite.ReportedParameterType)} "
              + $"(type {_currentWrite.ReportedParameterType}).");
        } else if (_currentWrite.Kind == NvWriteKind.Parameter) {
          FailWrites($"Modem did not acknowledge parameter {_currentWrite.Name}.");
        } else {
          FailWrites("Modem did not acknowledge the configuration command.");
        }
      } else {
        SendOperation(_currentWrite);
      }
    }
    UpdateSelectedState();
  }

  private void SendParameterRequestList(NvModemDeviceState device, bool retry = false) {
    if (SupportsNativeSettings(device)) {
      device.ParameterListInProgress = false;
      device.ParameterRequestDeferred = false;
      return;
    }
    if (!device.ModemInfoReady && _utcNow() - device.FirstSeenUtc < PassportWait) {
      device.ParameterRequestDeferred = true;
      return;
    }
    device.ParameterRequestDeferred = false;
    if (!retry) {
      device.ParameterRefreshPending = true;
    }
    device.ParameterListInProgress = true;
    device.ParameterListLastProgressUtc = _utcNow();
    device.ParameterListRetries = retry ? device.ParameterListRetries + 1 : 0;
    var request = new MAVLink.mavlink_param_request_list_t {
      target_system = device.Key.SystemId,
      target_component = device.Key.ComponentId,
    };
    _transport.Send(device.Key.Link, request, device.Key.SystemId, device.Key.ComponentId);
  }

  private void RequestDiscoveryMessages(
      NvModemLink link, byte systemId, byte componentId) {
    SendMessageRequest(link, systemId, componentId, NvModemMessageIds.NvModemInfo);
    SendMessageRequest(link, systemId, componentId, NvModemMessageIds.Nv5LinkStatus);
    SendMessageRequest(link, systemId, componentId,
        (uint)MAVLink.MAVLINK_MSG_ID.UAVCAN_NODE_INFO);

    // Old NV4 implementations use the dedicated UAVCAN command rather than MAV_CMD_REQUEST_MESSAGE.
    var nodeInfo = new MAVLink.mavlink_command_long_t {
      target_system = systemId,
      target_component = componentId,
      command = (ushort)MAVLink.MAV_CMD.UAVCAN_GET_NODE_INFO,
      confirmation = 0,
    };
    _transport.Send(link, nodeInfo, systemId, componentId);
  }

  private void SendIdentityRequest(NvModemLink link, byte systemId, byte componentId) {
    SendMessageRequest(link, systemId, componentId, NvModemMessageIds.NvModemInfo);
    SendMessageRequest(link, systemId, componentId,
        (uint)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION);
  }

  private void SendMessageRequest(
      NvModemLink link, byte systemId, byte componentId, uint messageId) {
    var command = new MAVLink.mavlink_command_long_t {
      target_system = systemId,
      target_component = componentId,
      command = (ushort)MAVLink.MAV_CMD.REQUEST_MESSAGE,
      confirmation = 0,
      param1 = messageId,
    };
    _transport.Send(link, command, systemId, componentId);
  }

  private static bool IsLegacyNv4Node(MAVLink.mavlink_uavcan_node_info_t info) {
    string name = NvModemParameterCodec.Name(info.name);
    bool currentSettingsSignature = info.hw_version_major == 4
        && info.sw_version_major == 4
        && (name.StartsWith("TX_", StringComparison.Ordinal)
            || name.StartsWith("RX_", StringComparison.Ordinal));

    // Old NV4 RFM firmware predates the passport. It publishes NV_TX/NV_RX node
    // names and does not reliably fill the version majors, so the legacy status
    // check identifies it from the first five characters only.
    bool legacyRfmSignature = name.StartsWith("NV_TX", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("NV_RX", StringComparison.OrdinalIgnoreCase);
    return currentSettingsSignature || legacyRfmSignature;
  }

  private void SendRtspRequest(NvModemDeviceState device) {
    var request = new Nv5RtspConfigMessage {
      TransactionId = ++_nextTransactionId,
      TargetSystem = device.Key.SystemId,
      TargetComponent = device.Key.ComponentId,
      Operation = 0,
      Path = new byte[96],
    };
    _transport.Send(device.Key.Link, request, device.Key.SystemId, device.Key.ComponentId);
  }

  private static MAVLink.mavlink_command_long_t CommandLong(
      NvModemDeviceState device, MAVLink.MAV_CMD command, float param1, float param2) => new() {
        target_system = device.Key.SystemId,
        target_component = device.Key.ComponentId,
        command = (ushort)command,
        confirmation = 0,
        param1 = param1,
        param2 = param2,
      };

  private void UpdateSelectedState() {
    NvModemDeviceState? device = SelectedState;
    if (device == null) {
      ConnectionText = "No modem discovered";
      IdentityText = "Uses Mission Planner's current shared MAVLink connection. Press Discover.";
      ParameterProgress = "Parameters: 0 / 0";
      ResetRadioRows();
      RefreshControls();
      return;
    }
    TimeSpan age = _utcNow() - device.LastSeenUtc;
    ConnectionText = age <= OnlineTimeout ? "Online" : $"Offline ({Math.Max(0, age.TotalSeconds):F0} s ago)";
    IdentityText = $"System {device.Key.SystemId}, component {device.Key.ComponentId}, "
        + $"hardware {HardwareModel(device)}, link {device.Key.Link.Name}";
    ParameterProgress = $"Parameters: {device.Parameters.Count} / {device.ExpectedParameterCount}";
    UpdateRadioRows(device);
    RefreshControls();
  }

  private void UpdateRadioRows(NvModemDeviceState device) {
    ResetRadioRows();
    if (device.Generation == NvModemGeneration.Nv4) {
      bool transmitter = device.Parameters.GetValueOrDefault("TX_ON") >= 0.5;
      string frequency = device.LegacyRxReady
          ? $"{device.LegacyRxStatus.Frequency / 1e6:F3} MHz" : "—";
      string rssi = transmitter ? "n/a" : device.LegacyRxReady
          ? $"{device.LegacyRxStatus.Snr:F1} dBm"
          : device.LegacyRadioReady ? device.LegacyRadioStatus.rssi.ToString(CultureInfo.InvariantCulture) : "—";
      string quality = transmitter ? "n/a" : device.LegacyRxReady
          ? $"{device.LegacyRxStatus.Quality:F0}%" : "—";
      var row = RadioStatuses[0];
      row.Identity = $"RFM/SX1278 · {(transmitter ? "TX" : "RX")}";
      row.Mode = $"LoRa · {frequency}";
      row.Link = $"RSSI {rssi} · Q {quality}";
      row.Traffic = device.LegacyRxReady
          ? $"RX {device.LegacyRxStatus.BytesReceived} B/s · {device.LegacyRxStatus.MavParsed} msg/s"
          : "—";
      row.Details = $"Role: {(transmitter ? "Transmitter" : "Receiver")}\nFrequency: {frequency}\n"
          + $"RSSI: {rssi}\nQuality: {quality}";
      return;
    }
    foreach ((int channel, Nv5LinkStatusMessage status) in device.Links) {
      if (channel is < 1 or > 2) {
        continue;
      }

      var row = RadioStatuses[channel - 1];
      bool receives = status.Role != 1;
      bool locked = receives && (status.Flags & (1 << 2)) != 0;
      short? currentRssi = receives ? CurrentNv5Rssi(status, locked) : null;
      short? currentSnr = receives && locked && status.PacketSnrDbX10 != short.MinValue
          ? status.PacketSnrDbX10 : null;
      string rssi = receives ? currentRssi is { } rssiValue
          ? $"{rssiValue / 10.0:F1}" : "—" : "n/a";
      string snr = receives ? currentSnr is { } snrValue
          ? $"{snrValue / 10.0:F1}" : "—" : "n/a";
      string quality = receives ? $"{status.LinkQuality}%" : "n/a";
      double sample = Math.Max(1, status.SampleMs);
      double txKbps = status.TxRadioBytes * 8 / sample;
      double rxKbps = status.RxRadioBytes * 8 / sample;
      string txState = status.Role == 0 ? "—" : status.TxState switch {
        1 => "On",
        2 => "Off",
        _ => "?",
      };
      row.Identity = $"{NvModemCatalog.ChipName(status.RadioChip)} · "
          + $"{NvModemCatalog.RoleName(status.Role)} · {txState}";
      row.Mode = $"{NvModemCatalog.ModulationName(status.Modulation)} · {status.FrequencyHz / 1e6:F3} MHz";
      row.Link = $"L {(locked ? "yes" : receives ? "no" : "n/a")} · R {rssi} · S {snr} · Q {quality}";
      row.Traffic = $"RF {txKbps:F1}/{rxKbps:F1} kbit/s · E {status.Errors}/{status.DroppedBytes}";
      row.Details = $"Chip: {NvModemCatalog.ChipName(status.RadioChip)}\n"
          + $"Role: {NvModemCatalog.RoleName(status.Role)}\n"
          + $"Mode: {NvModemCatalog.ModulationName(status.Modulation)}\n"
          + $"Frequency: {status.FrequencyHz / 1e6:F3} MHz\nRSSI: {rssi} dBm\n"
          + $"SNR: {snr} dB\nQuality: {quality}\nQueues: {status.TxQueueBytes}/{status.RxQueueBytes} B\n"
          + $"FEC recovered: {status.FecRecovered}; hop missed: {status.HopMissed}; sync lost: {status.SyncLost}";
    }
  }

  private static short? CurrentNv5Rssi(Nv5LinkStatusMessage status, bool locked) {
    if (!locked) {
      return status.ChannelRssiDbmX10 == short.MinValue
          ? null : status.ChannelRssiDbmX10;
    }
    if (status.PacketRssiDbmX10 != short.MinValue) {
      return status.PacketRssiDbmX10;
    }
    return status.ChannelRssiDbmX10 == short.MinValue
        ? null : status.ChannelRssiDbmX10;
  }

  private void ResetRadioRows() {
    foreach (NvRadioStatusRow row in RadioStatuses) {
      row.Identity = "—"; row.Mode = "—"; row.Link = "—"; row.Traffic = "—";
      row.Details = "No live status received.";
    }
  }

  private void UpdateDeviceLabel(NvModemDeviceState device) {
    if (device.Choice == null) {
      return;
    }

    string label = $"{ModemName(device)} {device.Key.SystemId}:{device.Key.ComponentId}  "
        + HardwareModel(device);
    string roles = RoleSummary(device);
    if (roles.Length != 0) {
      label += " · " + roles;
    }

    if (!IsOnline(device)) {
      label += " [offline]";
    }

    device.Choice.Label = $"{label} — {device.Key.Link.Name}";
  }

  private string HardwareModel(NvModemDeviceState device) {
    IEnumerable<byte> chips = device.Links.Values.Select(link => link.RadioChip);
    if (device.ModemInfoReady) {
      var passportChips = new List<byte>();
      if ((device.ModemInfo.Flags & NvModemInfoFlags.Channel1Active) != 0) {
        passportChips.Add(device.ModemInfo.Channel1RadioChip);
      }
      if ((device.ModemInfo.Flags & NvModemInfoFlags.Channel2Active) != 0) {
        passportChips.Add(device.ModemInfo.Channel2RadioChip);
      }
      chips = chips.Concat(passportChips);
    }
    return NvModemCatalog.HardwareModel(device.Generation, device.ProductProfile, chips,
        device.ModemInfoReady);
  }

  private static string ModemName(NvModemDeviceState device) => device.Generation switch {
    NvModemGeneration.Nv4 => "NV4",
    NvModemGeneration.Nv5 => "NV5",
    _ => "NV modem",
  };

  private static string RoleSummary(NvModemDeviceState device) {
    if (device.ModemInfoReady) {
      var passportRoles = new List<string>();
      if ((device.ModemInfo.Flags & NvModemInfoFlags.Channel1Active) != 0) {
        passportRoles.Add(NvModemCatalog.RoleName(device.ModemInfo.Channel1Role));
      }
      if ((device.ModemInfo.Flags & NvModemInfoFlags.Channel2Active) != 0) {
        passportRoles.Add(NvModemCatalog.RoleName(device.ModemInfo.Channel2Role));
      }
      if (passportRoles.Count != 0) {
        return string.Join('/', passportRoles);
      }
    }
    if (device.Generation == NvModemGeneration.Nv4
        && device.Parameters.TryGetValue("TX_ON", out double tx)) {
      return tx >= 0.5 ? "TX" : "RX";
    }
    if (device.Generation == NvModemGeneration.Nv4) {
      if (device.LegacyNodeName.StartsWith("TX_", StringComparison.Ordinal)) {
        return "TX";
      }
      if (device.LegacyNodeName.StartsWith("RX_", StringComparison.Ordinal)) {
        return "RX";
      }
      if (device.LegacyNodeName.StartsWith("NV_TX", StringComparison.OrdinalIgnoreCase)) {
        return "TX";
      }
      if (device.LegacyNodeName.StartsWith("NV_RX", StringComparison.OrdinalIgnoreCase)) {
        return "RX";
      }
    }

    if (device.Generation != NvModemGeneration.Nv5) {
      return "";
    }

    var roles = new List<string>();
    for (int channel = 1; channel <= 2; channel++) {
      if (device.Links.TryGetValue(channel, out var status)) {
        roles.Add(NvModemCatalog.RoleName(status.Role));
      } else if (device.Parameters.TryGetValue($"CH{channel}_ROLE", out double role)
                 && Math.Round(role) is >= 0 and <= 2) {
        roles.Add(NvModemCatalog.RoleName((byte)Math.Round(role)));
      }
    }
    return string.Join('/', roles);
  }

  private bool IsOnline(NvModemDeviceState device) => _utcNow() - device.LastSeenUtc <= OnlineTimeout;

  private static bool SupportsRtsp(NvModemDeviceState device) =>
      device.Generation == NvModemGeneration.Nv5
      && ((device.ModemInfoReady
              && (device.ModemInfo.Capabilities & NvModemCapabilities.Rtsp) != 0)
          || Enumerable.Range(1, 2).Any(channel =>
              device.Parameters.GetValueOrDefault($"CH{channel}_CHIP", -1) == 0)
          || device.Links.Values.Any(link => link.RadioChip == 0));

  private static int WritePriority(string name) => name.EndsWith("_MOD", StringComparison.Ordinal)
      ? 0 : name == "RTSP_ENABLE" ? 2 : 1;

  private void SetRtspPath(string path) {
    _settingRtsp = true;
    RtspPath = path;
    _settingRtsp = false;
    _rtspEdited = false;
  }

  private bool RtspPending(NvModemDeviceState device) => SupportsRtsp(device)
      && (device.RtspPathReady ? RtspPath != device.RtspPath : _rtspEdited);

  private bool HasPendingChangesFor(NvModemDeviceState device) =>
      _parameterRows.Values.Any(row => row.IsChanged) || RtspPending(device)
      || (SupportsNativeSettings(device) && NativeSettings.HasPendingChanges());

  private static bool SupportsNativeSettings(NvModemDeviceState device) =>
      device.ModemInfoReady
      && (device.ModemInfo.Capabilities & NvModemCapabilities.ConfigurationTransactions) != 0;

  private uint NativeNowMs() => unchecked((uint)(long)(_utcNow() - _clockStart).TotalMilliseconds);

  private void ObserveNativeBoot(NvModemInfoMessage info) {
    if ((info.Capabilities & NvModemCapabilities.ConfigurationTransactions) == 0
        || !HasStableModemIdentity(info)) {
      return;
    }
    string uid = Convert.ToHexString(info.Uid2);
    _nativeBoots.TryGetValue(uid, out (uint Uptime, ulong Epoch) boot);
    ulong epoch = boot.Epoch == 0 ? 1 : boot.Epoch;
    if (boot.Epoch != 0 && unchecked((int)(info.TimeBootMs - boot.Uptime)) < -100) {
      epoch++;
    }
    _nativeBoots[uid] = (info.TimeBootMs, epoch);
  }

  private void ObserveNativeTarget(NvModemDeviceState device) {
    if (!SupportsNativeSettings(device)) {
      return;
    }
    byte[] uid = (byte[])(device.ModemInfo.Uid2 ?? []).Clone();
    _nativeBoots.TryGetValue(Convert.ToHexString(uid), out (uint Uptime, ulong Epoch) boot);
    NativeSettings.ObserveTarget(new Nv5ConfigurationClient.Target(uid, device.Key.Link,
        boot.Epoch, device.ModemInfo.Capabilities, device.Key.SystemId, device.Key.ComponentId,
        HasStableModemIdentity(device.ModemInfo)), device.ModemInfo.TimeBootMs);
  }

  private async Task DiscoverAfterRebootAsync() {
    await Task.Delay(1500);
    if (_disposed) {
      return;
    }
    if (Dispatcher.UIThread.CheckAccess()) {
      Discover();
    } else {
      Dispatcher.UIThread.Post(Discover);
    }
  }

  private void SetStatus(string text, bool error = false) {
    Status = error ? "Error: " + text : text;
    IsError = error;
  }

  private void RefreshControls() {
    OnPropertyChanged(nameof(UsesNativeSettings));
    OnPropertyChanged(nameof(CanSwitchDevice));
    OnPropertyChanged(nameof(HasSelectedDevice));
    OnPropertyChanged(nameof(HasParameters));
    OnPropertyChanged(nameof(HasPendingChanges));
    OnPropertyChanged(nameof(CanEdit));
    OnPropertyChanged(nameof(CanSave));
    OnPropertyChanged(nameof(CanUseNv5Controls));
    OnPropertyChanged(nameof(CanUseRtsp));
    OnPropertyChanged(nameof(CanReboot));
    OnPropertyChanged(nameof(CanGenerateKey));
    OnPropertyChanged(nameof(CanSetKey));
    OnPropertyChanged(nameof(CanRevertSelectedParameter));
    OnPropertyChanged(nameof(CanCopyRadio));
    OnPropertyChanged(nameof(CanStageRxRolePreset));
    OnPropertyChanged(nameof(CanStageTxRolePreset));
    OnPropertyChanged(nameof(CanControlTransmitter));
    OnPropertyChanged(nameof(CanEnableTransmitter));
    OnPropertyChanged(nameof(CanSuppressTransmitter));
  }

  private static byte[] FixedLatin1(string text, int size, int maximumBytes) {
    byte[] output = new byte[size];
    byte[] input = Encoding.Latin1.GetBytes(text);
    Array.Copy(input, output, Math.Min(maximumBytes, input.Length));
    return output;
  }

  private static byte[] FixedKey(byte[] source) {
    byte[] output = new byte[NvModemCatalog.Nv5KeyBytes];
    Array.Copy(source, output, Math.Min(source.Length, output.Length));
    return output;
  }

  private static string Latin1String(byte[]? value) {
    if (value == null) {
      return "";
    }

    int length = Array.IndexOf(value, (byte)0);
    return Encoding.Latin1.GetString(value, 0, length < 0 ? value.Length : length);
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }

    // Frees a modem's single read session at once instead of after its lease.
    NativeSettings.StopReading();
    _disposed = true;
    _serviceTimer?.Stop();
    _transport.PacketReceived -= OnPacketReceived;
    _transport.LinksChanged -= OnLinksChanged;
    _transport.Dispose();
    foreach (var row in _parameterRows.Values) {
      row.PropertyChanged -= OnParameterRowChanged;
    }
  }
}
