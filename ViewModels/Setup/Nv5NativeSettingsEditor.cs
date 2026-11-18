using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Services;
using Phase = MissionPlanner.Services.Nv5ConfigurationClient.ClientPhase;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;
using Value = MissionPlanner.Services.Nv5ConfigurationClient.Value;

namespace MissionPlanner.ViewModels.Setup;

/// <summary>
/// Settings already read from NV5 modems, shared by the editors of one page so that a modem that
/// reports the same boot, catalogue, committed revision and digest is not read again.
/// </summary>
internal sealed class Nv5SettingsSnapshots {
  internal sealed record Entry(ulong BootNonce, ulong Revision, uint CatalogId, byte[] Digest,
      IReadOnlyDictionary<string, Value> Values) {
    internal static readonly Entry Empty =
        new(0, 0, 0, [], new SortedDictionary<string, Value>(StringComparer.Ordinal));

    /// <summary>Whether the entry names its settings by a digest, which makes its values
    /// reusable.</summary>
    internal bool Identified => Digest.Length == 32 && Digest.Any(value => value != 0);

    internal Entry WithValues(IReadOnlyDictionary<string, Value> values) =>
        this with { Values = new SortedDictionary<string, Value>(
            values.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal) };
  }

  internal Dictionary<string, Entry> ByUid { get; } = new(StringComparer.Ordinal);

  /// <summary>
  /// The editor reading each modem now. A modem serves one read session at a time, so the other
  /// editors of the page wait for its result instead of being refused.
  /// </summary>
  internal Dictionary<string, Nv5NativeSettingsEditor> Readers { get; } =
      new(StringComparer.Ordinal);
}

/// <summary>One native setting as the settings table shows it.</summary>
public sealed partial class Nv5NativeSettingRow : ObservableObject {
  private readonly Action _changed;

  internal Nv5NativeSettingRow(Action changed, string name, string value, string type,
      bool isEditable, bool isSupported) {
    _changed = changed;
    Name = name;
    _valueText = value;
    TypeText = type;
    IsEditable = isEditable;
    IsSupported = isSupported;
  }

  public string Name { get; }
  public string TypeText { get; }
  public bool IsEditable { get; }
  public bool IsSupported { get; }

  [ObservableProperty]
  private string _valueText;

  [ObservableProperty]
  private string _editState = "";

  partial void OnValueTextChanged(string value) => _changed();
}

/// <summary>
/// Native typed settings of one NV5 modem, read and saved through the configuration
/// transaction protocol. A save validates one complete snapshot and succeeds only after a
/// durable receipt and a same-UID activation readback; it never falls back to PARAM_SET.
/// </summary>
/// <remarks>
/// The editor has no timer: the owner calls <see cref="Service"/> periodically, delivers the
/// device's replies to <see cref="Receive"/> and sends every <see cref="SendFrame"/> frame on the
/// target's link.
/// </remarks>
public sealed partial class Nv5NativeSettingsEditor : ObservableObject {
  private enum Step {
    Idle,
    Info,
    ReadBegin,
    Read,
    ReadEnd,
    Begin,
    Stage,
    Validate,
    Prepared,
    Commit,
    Recovery,
    Aborting,
    Failed,
  }

  /// <summary>
  /// How long a failed read keeps being tried again. A modem serves one read session at a time
  /// and frees an abandoned one after 10 s; a restarting modem is back within about the same time.
  /// </summary>
  private const uint ReadRetryBudgetMs = 30000;

  private static readonly Nv5SettingsSnapshots.Entry Empty = Nv5SettingsSnapshots.Entry.Empty;

  private readonly Func<uint> _clock;
  private readonly Func<byte> _system;
  private readonly byte _component;
  private readonly List<Nv5NativeSettingRow> _allRows = [];
  private readonly Dictionary<string, Nv5NativeSettingRow> _rows = new(StringComparer.Ordinal);
  private readonly HashSet<ushort> _known = [];
  private readonly List<Value> _writes = [];
  private Nv5ConfigurationClient _client;
  private Target? _target;
  private Step _step = Step.Idle;
  private SortedDictionary<string, Value> _values = new(StringComparer.Ordinal);
  private SortedDictionary<string, Value> _readValues = new(StringComparer.Ordinal);
  private Nv5SettingsSnapshots.Entry _valuesIdentity = Empty;
  private Nv5SettingsSnapshots.Entry _reading = Empty;
  private Nv5SettingsSnapshots.Entry _resume = Empty;
  private uint _progress;
  private uint _recoveryStarted;
  private uint _recoverAt;
  private uint _retryStarted;
  private uint _retryAt;
  private uint _uptime;
  private uint _restartedAt;
  private ushort _readIndex;
  private ushort _count;
  private int _kept;
  private byte _retries;
  private bool _readBack;
  private bool _fullRead;
  private bool _retryPending;
  private bool _waitingForReader;
  private bool _recoveryAnswered;
  private bool _started;
  private bool _commitSent;
  private bool _autoCommit = true;
  private bool _commitRequested;
  private bool _durableEmitted;
  private bool _restarted;
  private bool _rebuilding;
  private bool _reloadPending;

  // Set when the device refused a save because its settings changed after they were read: the
  // edits were made to values that are no longer current, so only a new read may lift it.
  private bool _refreshRequired;

  /// <param name="clock">Monotonic milliseconds; wraps like the device's uint32 clocks.</param>
  /// <param name="system">MAVLink system id of this application's frames, read whenever a
  /// transaction starts because the application's id can change.</param>
  /// <param name="component">MAVLink component id of this application's frames.</param>
  internal Nv5NativeSettingsEditor(Func<uint> clock, Func<byte> system, byte component) {
    _clock = clock;
    _system = system;
    _component = component;
    _client = NewClient();
  }

  /// <summary>A frame to send on the target's own link.</summary>
  internal event Action<Nv5ConfigurationFrame, Target>? SendFrame;
  internal event Action<bool>? ConfigurationFinished;
  internal event Action<bool>? BusyChanged;

  /// <summary>Unsaved edits appeared, changed or were dropped.</summary>
  internal event Action? EditsChanged;

  /// <summary>A prepared candidate is ready (true) or was refused (false), with the candidate
  /// link hashes of both radios.</summary>
  internal event Action<bool, uint, uint>? Prepared;

  internal event Action? DurableSaved;

  /// <summary>The settings of the table, filtered by <see cref="FilterText"/>.</summary>
  public ObservableCollection<Nv5NativeSettingRow> Rows { get; } = [];

  [ObservableProperty]
  private string _statusText = "Waiting for a verified device.";

  [ObservableProperty]
  private string _filterText = "";

  [ObservableProperty]
  private bool _isBusy;

  [ObservableProperty]
  private bool _canRead;

  [ObservableProperty]
  private bool _canSave;

  [ObservableProperty]
  private bool _canImport;

  [ObservableProperty]
  private bool _canExport;

  /// <summary>Shared by the editors of a page; without one every read is full.</summary>
  internal Nv5SettingsSnapshots? Snapshots { get; set; }

  /// <summary>Refuses a commit whose candidate the page cannot accept, for example an address
  /// that another device already uses.</summary>
  internal Func<IReadOnlyDictionary<string, Value>, bool>? CommitCheck { get; set; }

  internal Target? Target => _target;
  internal bool Busy => _step is not (Step.Idle or Step.Failed);

  /// <summary>A save or abort is in progress; unlike a read, it must not be cut short.</summary>
  internal bool Saving => Busy && !Reading();

  /// <summary>True once a read or save has ended in failure, until the next one starts.</summary>
  internal bool Failed => _step == Step.Failed;

  internal bool Ready =>
      !Busy && _values.Count != 0 && _client.Phase is Phase.Ready or Phase.Active;

  /// <summary>False while the values are a local merge after a save, before the device is read
  /// again.</summary>
  internal bool ValuesReadBack => _readBack;

  internal bool AwaitingReconnect => _commitSent && Busy;

  /// <summary>True while a failed read is waiting to be tried again on its own.</summary>
  internal bool Retrying => _retryPending;

  /// <summary>Values as last read (or merged after a save), without table edits.</summary>
  internal IReadOnlyDictionary<string, Value> ReadValues => _values;

  /// <summary>True when the device restarted or moved within the last minute.</summary>
  internal bool RestartedRecently => _restarted && unchecked(Now() - _restartedAt) < 60000;

  /// <summary>Settings read so far out of the total, while a read is running.</summary>
  internal string ReadProgress =>
      _step is Step.Read or Step.ReadEnd ? $"{_readValues.Count} / {_count}" : "";

  partial void OnFilterTextChanged(string value) => ShowRows();

  internal void ObserveTarget(Target target, uint uptime) {
    if (!target.Verified || target.Uid is not { Length: 18 } || target.Route is null or "") {
      return;
    }
    bool sameUid = _target != null && Same(target.Uid, _target.Uid)
        && target.Product == _target.Product;
    bool restart = sameUid && unchecked((int)(uptime - _uptime)) < -100;
    bool sameRoute = sameUid && Equals(target.Route, _target!.Route)
        && target.System == _target.System && target.Component == _target.Component
        && target.TransportEpoch == _target.TransportEpoch;
    _uptime = uptime;
    if (sameUid && (restart || !sameRoute)) {
      _restarted = true;
      _restartedAt = Now();
    }
    if ((_step == Step.Recovery || (_commitSent && (restart || !sameRoute))) && sameUid) {
      if (_step != Step.Recovery) {
        SetStep(Step.Recovery);
        SetStatus("Device restarted before save confirmation. Recovering without repeating the "
            + "commit…");
      }
      if (restart || !sameRoute
          || (!_client.Pending && unchecked(Now() - _recoverAt) >= 500)) {
        if (_client.Recover(target, Now())) {
          _target = target;
          _recoverAt = Now();
        }
      }
      return;
    }
    if (sameRoute && !restart) {
      return;
    }
    if (sameUid && Reading()) {
      // A restart or a new route ends what the device was doing for this editor; the read goes
      // on where it stopped.
      EndReadSession();
      _target = target;
      _retries = 0;
      StartRead();
      return;
    }
    if (!sameUid && Reading()) {
      // Another device is chosen: the read of this one ends and frees its session at once.
      EndReadSession();
      ReleaseReader();
      _retryPending = _waitingForReader = false;
      SetStep(Step.Idle);
    }
    if (Busy) {
      Fail("Device or route changed during configuration.", _commitSent);
      return;
    }
    if (sameUid) {
      KeepForResume();
    } else {
      // Nothing read from another device is ever kept for this one.
      _fullRead = false;
      _resume = Empty;
      _reading = Empty;
      _readValues.Clear();
    }
    _target = target;
    _client = NewClient();
    _values = new SortedDictionary<string, Value>(StringComparer.Ordinal);
    _valuesIdentity = Empty;
    _readBack = false;
    _refreshRequired = false;
    ClearRows();
    _started = _commitSent = false;
    Reload();
  }

  internal bool Receive(MAVLink.MAVLinkMessage message, object route) {
    if (_target == null || !_client.Receive(message, route, _target.TransportEpoch, Now())) {
      return false;
    }
    _progress = Now();
    if (message.msgid == NvModemMessageIds.Nv5ConfigResult) {
      Nv5ConfigResultMessage result = message.ToStructure<Nv5ConfigResultMessage>();
      if (_step == Step.Recovery && result.Operation == Nv5ConfigOperations.Status) {
        _recoveryAnswered = true;
      }
      // A read session that ran out before its end was acknowledged still delivered every value.
      bool endedAnyway = result.Operation == Nv5ConfigOperations.ReadEnd && _step == Step.ReadEnd
          && result.Result is Nv5ConfigResults.Expired or Nv5ConfigResults.NotFound;
      if (result.Result > Nv5ConfigResults.Accepted && !_autoCommit && _step == Step.Validate
          && result.CommittedKnown != 0 && result.State == Nv5ConfigStates.Staged
          && result.Operation is Nv5ConfigOperations.Validate or Nv5ConfigOperations.Status) {
        SetStep(Step.Prepared);
        Prepared?.Invoke(false, result.Channel1LinkHash, result.Channel2LinkHash);
        return true;
      }
      if (result.Result > Nv5ConfigResults.Accepted && !endedAnyway && _step != Step.Recovery
          && _step != Step.Aborting) {
        string reason = ResultName(result.Result);
        string field = NameOf(result.ErrorName);
        if (field.Length != 0) {
          reason += $" Field: {field}.";
        }
        if (result.Operation is Nv5ConfigOperations.Validate or Nv5ConfigOperations.Status) {
          reason += $" Candidate link hashes: radio 1 = {result.Channel1LinkHash}, radio 2 = "
              + $"{result.Channel2LinkHash}. Compare with the peer before setting CHn_PEER_HASH.";
        }
        if (result.Result == Nv5ConfigResults.StaleRevision && !Reading()) {
          _refreshRequired = true;
        }
        // Refusals that pass on their own: another session holds the device, or a session or
        // revision ran out. A read tries again.
        bool passing = result.Result is Nv5ConfigResults.Busy or Nv5ConfigResults.StaleRevision
            or Nv5ConfigResults.Expired or Nv5ConfigResults.NotFound;
        if (passing && RetryLater(reason, result.Result == Nv5ConfigResults.Busy)) {
          return true;
        }
        Fail(reason, _commitSent);
        return true;
      }
    }
    Service();
    return true;
  }

  internal void Service() {
    if (_target == null) {
      return;
    }
    if (_reloadPending && !Busy) {
      _reloadPending = false;
      Reload();
      return;
    }
    _client.Service(Now());
    if (_client.TakeOutbound(Now()) is { } frame) {
      SendFrame?.Invoke(frame, _target);
    }
    bool recovering = _step == Step.Recovery;
    if (Busy && unchecked(Now() - (recovering ? _recoveryStarted : _progress))
        > (recovering ? 30000u : 12000u)) {
      Fail(_commitSent
          ? "Restart/readback was not confirmed. The save outcome remains uncertain."
          : "Configuration timed out; no save was confirmed.", _commitSent);
      return;
    }
    if (_client.Pending) {
      return;
    }
    Advance();
  }

  /// <summary>Reads the device, or takes its settings from the shared store when it reports no
  /// change.</summary>
  internal void Reload() {
    if (Busy || _target is not { Verified: true }) {
      return;
    }
    _retries = 0;
    StartRead();
  }

  /// <summary>Reads every setting from the device, whatever the store holds.</summary>
  internal void RereadDevice() {
    if (Busy || _target is not { Verified: true }) {
      return;
    }
    _fullRead = true;
    _resume = Empty;
    _readValues.Clear();
    Reload();
  }

  /// <summary>Stops a read in progress and frees the device's read session at once.</summary>
  internal void StopReading() {
    if (!Reading()) {
      return;
    }
    EndReadSession();
    KeepForResume();
    ReleaseReader();
    _retryPending = _waitingForReader = false;
    _client = NewClient();
    SetStep(Step.Idle);
    SetStatus("Reading stopped.");
  }

  internal void Save() => StartSave(commit: true);

  /// <summary>Prepares without committing, for peer-verified pair provisioning.</summary>
  internal void Prepare() => StartSave(commit: false);

  internal bool ContinuePreparation(IReadOnlyDictionary<string, string> values) {
    if (_step != Step.Prepared || _client.Pending) {
      return false;
    }
    _writes.Clear();
    foreach ((string name, string text) in values) {
      if (!_rows.TryGetValue(name, out Nv5NativeSettingRow? row)
          || !_values.TryGetValue(name, out Value? value) || !Editable(value)
          || !Encode(value, text, out byte[] bytes)) {
        return false;
      }
      _writes.Add(value with { Bytes = bytes });
      _rebuilding = true;
      row.ValueText = text;
      _rebuilding = false;
    }
    SetStep(Step.Stage);
    Service();
    return true;
  }

  internal bool CommitPrepared() {
    if ((_step != Step.Prepared && _step != Step.Validate)
        || _client.Phase != Phase.Validated || _client.Pending) {
      return false;
    }
    if (CommitCheck != null && !CommitCheck(CandidateValues())) {
      Fail("The selected device or requested address is no longer available. Review the device "
          + "settings before saving.");
      return false;
    }
    _commitRequested = true;
    if (_step != Step.Prepared) {
      SetStep(Step.Prepared);
    }
    TryCommit();
    return _step is not (Step.Failed or Step.Aborting);
  }

  /// <summary>Drops table edits, or aborts a transaction that has not been committed.</summary>
  internal void Discard() {
    if (Busy) {
      if (!_commitSent && !_client.Pending && _client.Abort(Now())) {
        SetStep(Step.Aborting);
      }
      return;
    }
    Rebuild();
  }

  /// <summary>Stages a native value through the same path as a table edit.</summary>
  internal bool SetValue(string name, string text) {
    if (Busy || !_rows.TryGetValue(name, out Nv5NativeSettingRow? row) || !row.IsEditable
        || !Encode(_values[name], text, out _)) {
      return false;
    }
    row.ValueText = text;
    return true;
  }

  internal IReadOnlyDictionary<string, Value> CandidateValues() {
    var values = new SortedDictionary<string, Value>(_values, StringComparer.Ordinal);
    foreach ((string name, Nv5NativeSettingRow row) in _rows) {
      Value value = values[name];
      if (Editable(value) && Encode(value, row.ValueText, out byte[] bytes)) {
        values[name] = value with { Bytes = bytes };
      }
    }
    return values;
  }

  /// <summary>Each edited setting with the value read from the device and the edited text.</summary>
  internal IReadOnlyList<(string Name, string Before, string After)> PendingChangeList() {
    var changes = new List<(string Name, string Before, string After)>();
    foreach ((string name, Nv5NativeSettingRow row) in _rows) {
      Value value = _values[name];
      if (Editable(value)
          && (!Encode(value, row.ValueText, out byte[] bytes) || !Same(bytes, value.Bytes))) {
        changes.Add((name, Display(value), row.ValueText));
      }
    }
    return changes;
  }

  internal bool HasPendingChanges() {
    foreach ((string name, Nv5NativeSettingRow row) in _rows) {
      Value value = _values[name];
      if (!Editable(value)) {
        continue;
      }
      if (!Encode(value, row.ValueText, out byte[] bytes) || !Same(bytes, value.Bytes)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>Writes the committed values as a JSON document. Keys are included, so the file
  /// is created readable by its owner only.</summary>
  internal bool SaveSnapshot(string path) {
    if (_values.Count == 0 || _target == null) {
      return false;
    }
    var items = new JsonArray();
    foreach (Value value in _values.Values) {
      items.Add(new JsonObject {
        ["name"] = value.Name,
        ["type"] = value.Type,
        ["value"] = Display(value),
      });
    }
    var snapshot = new JsonObject {
      ["schema"] = 1,
      ["product"] = ProductName(),
      ["uid2"] = Convert.ToHexString(_target.Uid).ToLowerInvariant(),
      ["catalog_id"] = _client.Result.CatalogId.ToString(CultureInfo.InvariantCulture),
      ["committed_revision"] =
          _client.Result.CommittedRevision.ToString(CultureInfo.InvariantCulture),
      ["values"] = items,
    };
    // The document holds key words. It is written to a fresh, unpredictably named file that only
    // this call can have created (CreateNew fails on any existing path, including a symbolic
    // link) and that is owner-only before the first byte; the rename then replaces the
    // destination, whose earlier permissions do not carry over.
    string fullPath = Path.GetFullPath(path);
    string temporary = Path.Combine(Path.GetDirectoryName(fullPath) ?? ".",
        $".{Path.GetFileName(fullPath)}."
            + $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}.tmp");
    bool created = false;
    try {
      using (FileStream stream = CreateOwnerOnlyFile(temporary)) {
        created = true;
        JsonSerializer.Serialize(stream, snapshot,
            new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(flushToDisk: true);
      }
      File.Move(temporary, fullPath, overwrite: true);
      return true;
    } catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException) {
      if (created) {
        try {
          File.Delete(temporary);
        } catch (Exception cleanup) when (cleanup is IOException
            or UnauthorizedAccessException) {
        }
      }
      return false;
    }
  }

  private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

  /// <summary>
  /// Creates a new file that only the current user can open, before anything is written to it.
  /// Unix gets mode 0600. Windows gets a protected DACL with one entry for the current user, so
  /// nothing is inherited from a folder that other users may read; the rename into place keeps it.
  /// </summary>
  private static FileStream CreateOwnerOnlyFile(string path) {
    if (OperatingSystem.IsWindows()) {
      SecurityIdentifier user = WindowsIdentity.GetCurrent().User
          ?? throw new UnauthorizedAccessException(
              "The current Windows account has no security identifier.");
      var security = new FileSecurity();
      security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
      security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
          AccessControlType.Allow));
      return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
          FileShare.None, 4096, FileOptions.None, security);
    }
    var stream = new FileStream(path, new FileStreamOptions {
      Mode = FileMode.CreateNew,
      Access = FileAccess.Write,
      Share = FileShare.None,
      UnixCreateMode = OwnerOnly,
    });
    // The creation mode is filtered by the umask; set it exactly while the file is still empty.
    File.SetUnixFileMode(stream.SafeFileHandle, OwnerOnly);
    return stream;
  }

  /// <summary>Exports the committed values and says on the status line whether it worked.</summary>
  internal bool ExportSnapshot(string path) {
    bool saved = SaveSnapshot(path);
    SetStatus(saved
        ? "Committed snapshot exported, including configured encryption keys."
        : "Snapshot export failed.");
    return saved;
  }

  /// <summary>File name offered for an export of this modem's settings.</summary>
  internal string SuggestedSnapshotName() =>
      $"nv5-config-{Convert.ToHexString(_target?.Uid ?? []).ToLowerInvariant()}.json";

  /// <summary>Stages a JSON document's values as table edits; nothing is written to the device
  /// and a document with any invalid field stages nothing.</summary>
  internal bool LoadSnapshot(string path) {
    if (Busy || _values.Count == 0) {
      return false;
    }
    JsonObject? document;
    try {
      var file = new FileInfo(path);
      if (!file.Exists || file.Length > 1024 * 1024) {
        SetStatus("Cannot read the configuration snapshot.");
        return false;
      }
      document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or JsonException or ArgumentException or InvalidOperationException) {
      document = null;
    }
    SortedDictionary<string, string>? edits;
    string problem;
    try {
      edits = SnapshotEdits(document, out problem);
    } catch (Exception exception) when (exception is InvalidOperationException
        or ArgumentException or FormatException) {
      // Duplicate property names and similar defects surface only when a node is read.
      edits = null;
      problem = "Invalid or duplicate snapshot field.";
    }
    if (edits == null) {
      SetStatus(problem);
      return false;
    }
    int staged = 0;
    foreach ((string name, string text) in edits) {
      if (!Editable(_values[name])) {
        continue;
      }
      _rows[name].ValueText = text;
      staged++;
    }
    SetStatus($"Loaded {staged} editable settings locally. Review them and use Save and restart "
        + "to commit.");
    return true;
  }

  /// <summary>The edits a snapshot document stages, or null with the reason it is refused. Any
  /// invalid field refuses the whole document, so nothing is staged partially.</summary>
  private SortedDictionary<string, string>? SnapshotEdits(JsonObject? document,
      out string problem) {
    problem = "The snapshot format or product catalogue does not match this device.";
    if (document == null || Integer(document["schema"]) != 1
        || Text(document["product"]) != ProductName()
        || Text(document["catalog_id"])
            != _client.Result.CatalogId.ToString(CultureInfo.InvariantCulture)
        || document["values"] is not JsonArray entries) {
      return null;
    }
    var edits = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (JsonNode? entry in entries) {
      if (entry is not JsonObject item) {
        problem = "Invalid or duplicate snapshot field.";
        return null;
      }
      string name = Text(item["name"]) ?? "";
      string? text = Text(item["value"]);
      if (!_values.TryGetValue(name, out Value? value) || edits.ContainsKey(name)
          || Integer(item["type"]) != value.Type || text == null) {
        problem = "Invalid or duplicate snapshot field.";
        return null;
      }
      if (!Encode(value, text, out _)) {
        problem = $"Invalid value for {name}.";
        return null;
      }
      edits[name] = text;
    }
    return edits;
  }

  /// <summary>Native value text exactly as the settings table shows it.</summary>
  internal static string Display(Value value) {
    if (value.Type == Nv5ConfigValueTypes.Text) {
      return Encoding.UTF8.GetString(value.Bytes);
    }
    if (value.Bytes.Length != 4) {
      return "";
    }
    uint raw = BinaryPrimitives.ReadUInt32LittleEndian(value.Bytes);
    return value.Type switch {
      Nv5ConfigValueTypes.Int32 => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
      Nv5ConfigValueTypes.Float32 =>
          BitConverter.UInt32BitsToSingle(raw).ToString("G9", CultureInfo.InvariantCulture),
      _ => raw.ToString(CultureInfo.InvariantCulture),
    };
  }

  /// <summary>Encodes table text in the value's native type. Integers accept decimal, 0x
  /// hexadecimal and leading-zero octal like the firmware's own tools.</summary>
  internal static bool Encode(Value value, string text, out byte[] data) {
    data = [];
    if (value.Type == Nv5ConfigValueTypes.Text) {
      data = Encoding.UTF8.GetBytes(text);
      return data.Length <= 95 && data.All(letter => letter >= 0x20 && letter != 0x7f);
    }
    uint raw;
    string trimmed = text.Trim();
    if (value.Type == Nv5ConfigValueTypes.Float32) {
      if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture,
              out float number) || !float.IsFinite(number)) {
        return false;
      }
      // Negative zero is stored as zero.
      raw = number == 0 ? 0 : BitConverter.SingleToUInt32Bits(number);
    } else if (value.Type == Nv5ConfigValueTypes.Int32) {
      if (!TryParseInteger(trimmed, out long number) || number is < int.MinValue
          or > int.MaxValue) {
        return false;
      }
      raw = unchecked((uint)(int)number);
    } else {
      if (trimmed.StartsWith('-') || !TryParseInteger(trimmed, out long number) || number < 0
          || number > uint.MaxValue) {
        return false;
      }
      raw = (uint)number;
    }
    data = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(data, raw);
    return true;
  }

  internal static string ResultName(byte result) => result switch {
    Nv5ConfigResults.Busy =>
        "Device busy; finish the other configuration operation and refresh.",
    Nv5ConfigResults.StaleRevision =>
        "Settings changed on the device. Refresh and review your changes.",
    Nv5ConfigResults.StorageFailed => "Storage failed; settings were not confirmed saved.",
    Nv5ConfigResults.Indeterminate =>
        "Save outcome is uncertain. Reconnect and verify the device.",
    _ => $"Device rejected the operation (result {result}).",
  };

  private uint Now() => _clock();

  private Nv5ConfigurationClient NewClient() => new(_system(), _component);

  private string ProductName() =>
      _target?.Product == Nv5ConfigurationClient.ProductKind.Hub ? "hub" : "modem";

  private void SetStatus(string status) => StatusText = status;

  private void SetStep(Step step) {
    bool wasBusy = Busy;
    if (step == Step.Recovery && _step != step) {
      _recoveryStarted = Now();
      _recoveryAnswered = false;
    }
    _step = step;
    _progress = Now();
    RefreshCommands();
    if (wasBusy != Busy) {
      BusyChanged?.Invoke(Busy);
    }
  }

  private void RefreshCommands() {
    IsBusy = Busy;
    CanRead = !Busy && _target is { Verified: true };
    CanImport = !Busy && _values.Count != 0;
    CanExport = !Busy && _values.Count != 0;
    CanSave = !Busy && _readBack && !_refreshRequired && HasPendingChanges();
  }

  private bool Reading() =>
      _step is Step.Info or Step.ReadBegin or Step.Read or Step.ReadEnd;

  private void StartRead() {
    KeepForResume();
    ReleaseReader();
    _client = NewClient();
    _started = _commitSent = false;
    _readValues.Clear();
    _known.Clear();
    _kept = 0;
    _readIndex = 0;
    _retryPending = _waitingForReader = false;
    if (!_client.Discover(_target!, Now())) {
      Fail("Cannot start configuration discovery.");
      return;
    }
    SetStep(Step.Info);
    SetStatus("Reading verified settings…");
    Service();
  }

  /// <summary>Keeps the values read so far, under their identity, for the next read of this
  /// device.</summary>
  private void KeepForResume() {
    // Each value carries its catalogue index; the identity says which committed settings it is.
    if (_readValues.Count != 0 && _reading.Identified) {
      _resume = _reading.WithValues(_readValues);
    } else if (!_fullRead && _values.Count != 0 && _valuesIdentity.Identified) {
      _resume = _valuesIdentity.WithValues(_values);
    }
  }

  /// <summary>Chooses the values a read may keep, once the device has said which settings it
  /// holds.</summary>
  private void ChooseKept() {
    Nv5ConfigResultMessage info = _client.Result;
    _reading = new Nv5SettingsSnapshots.Entry(info.BootNonce, info.CommittedRevision,
        info.CatalogId, Copy(info.CommittedDigest), Empty.Values);
    _readValues.Clear();
    _known.Clear();
    Nv5SettingsSnapshots.Entry kept = Empty;
    bool Matches(Nv5SettingsSnapshots.Entry entry) =>
        info.CommittedKnown != 0 && entry.Identified && entry.CatalogId == _reading.CatalogId
        && entry.Revision == _reading.Revision && Same(entry.Digest, _reading.Digest);
    if (Matches(_resume)) {
      kept = _resume;
    }
    if (!_fullRead && Snapshots != null
        && Snapshots.ByUid.TryGetValue(UidKey(), out Nv5SettingsSnapshots.Entry? stored)
        && Matches(stored) && stored.Values.Count > kept.Values.Count) {
      kept = stored;
    }
    foreach ((string name, Value value) in kept.Values) {
      // A derived value follows the running firmware, so every new boot reads it again.
      if (kept.BootNonce != _reading.BootNonce
          && (value.Flags & Nv5ConfigValueFlags.ReadOnly) != 0) {
        continue;
      }
      _readValues[name] = value;
      _known.Add(value.Index);
    }
    _kept = _readValues.Count;
  }

  /// <summary>Repeats a failed read after a pause.</summary>
  private bool RetryLater(string reason, bool deviceBusy) {
    if (!Reading()) {
      return false;
    }
    if (_retries == 0) {
      _retryStarted = Now();
    } else if (unchecked(Now() - _retryStarted) > ReadRetryBudgetMs) {
      return false;
    }
    uint delay = Math.Min(4000u, 500u << Math.Min(_retries, (byte)3));
    if (_retries < byte.MaxValue) {
      _retries++;
    }
    EndReadSession();
    KeepForResume();
    ReleaseReader();
    _client = NewClient();
    _retryAt = Now() + delay;
    _retryPending = true;
    _waitingForReader = false;
    SetStep(Step.Info);
    SetStatus(deviceBusy
        ? "Another configuration session is using this modem; it is read once that session "
            + "ends…"
        : $"{reason} Reading again…");
    return true;
  }

  private void EndReadSession() {
    if (_target != null && _client.AbandonRead(Now()) is { } frame) {
      SendFrame?.Invoke(frame, _target);
    }
  }

  private void ReleaseReader() {
    if (Snapshots != null && _target != null
        && Snapshots.Readers.TryGetValue(UidKey(), out Nv5NativeSettingsEditor? reader)
        && ReferenceEquals(reader, this)) {
      Snapshots.Readers.Remove(UidKey());
    }
  }

  private bool OtherReader() =>
      Snapshots != null && _target != null
      && Snapshots.Readers.TryGetValue(UidKey(), out Nv5NativeSettingsEditor? reader)
      && !ReferenceEquals(reader, this) && reader.Reading() && reader._target != null
      && Same(reader._target.Uid, _target.Uid);

  private void Advance() {
    if ((_step == Step.Commit || _step == Step.Recovery) && _client.DurableConfirmed
        && !_durableEmitted) {
      _durableEmitted = true;
      DurableSaved?.Invoke();
    }
    if (_step is Step.Idle or Step.Failed) {
      return;
    }
    if (_step == Step.Aborting) {
      _started = false;
      SetStep(Step.Failed);
      return;
    }
    if (_client.Phase == Phase.Error) {
      Fail("Configuration protocol failed.", _commitSent);
      return;
    }
    switch (_step) {
      case Step.Info:
        AdvanceInfo();
        break;
      case Step.ReadBegin:
        AdvanceReadBegin();
        break;
      case Step.Read:
        AdvanceRead();
        break;
      case Step.ReadEnd:
        FinishRead();
        break;
      case Step.Begin:
        if (_client.Phase != Phase.Staged) {
          Fail(ResultName(_client.Result.Result));
          return;
        }
        _started = true;
        SetStep(Step.Stage);
        break;
      case Step.Stage:
        if (_writes.Count != 0) {
          Value value = _writes[0];
          if (_client.Stage(value.Name, value.Type, value.Bytes, Now())) {
            _writes.RemoveAt(0);
          }
        } else if (_client.Validate(Now())) {
          SetStep(Step.Validate);
        }
        break;
      case Step.Validate:
        if (_client.Phase != Phase.Validated) {
          return;
        }
        if (!_autoCommit) {
          SetStep(Step.Prepared);
          Prepared?.Invoke(true, _client.Result.Channel1LinkHash, _client.Result.Channel2LinkHash);
        } else {
          CommitPrepared();
        }
        break;
      case Step.Prepared:
        if (_commitRequested) {
          TryCommit();
        }
        break;
      case Step.Commit:
        AdvanceCommit();
        break;
      case Step.Recovery:
        AdvanceRecovery();
        break;
    }
  }

  private void AdvanceInfo() {
    if (_retryPending) {
      if (unchecked((int)(Now() - _retryAt)) < 0) {
        return;
      }
      _retryPending = false;
      if (!_client.Discover(_target!, Now())) {
        Fail("Cannot start configuration discovery.");
      }
      return;
    }
    if (_client.Phase is not (Phase.Ready or Phase.RestartRequired)) {
      return;
    }
    if (UseSnapshot()) {
      return;
    }
    if (OtherReader()) {
      // The other editor files its result in the shared store, where UseSnapshot finds it;
      // reading in parallel would only be refused by the device.
      _progress = Now();
      if (!_waitingForReader) {
        _waitingForReader = true;
        SetStatus("Waiting for another read of this modem to finish…");
      }
      return;
    }
    _waitingForReader = false;
    ChooseKept();
    if (!_client.BeginRead(Nv5ConfigScopes.Committed, FreshNonce(), Now())) {
      return;
    }
    // What was kept now sits in the read itself, which keeps it again if it is cut short.
    _resume = Empty;
    if (Snapshots != null) {
      Snapshots.Readers[UidKey()] = this;
    }
    SetStep(Step.ReadBegin);
  }

  private void AdvanceReadBegin() {
    if (!_client.SnapshotReady) {
      return;
    }
    Nv5ConfigResultMessage snapshot = _client.Result;
    byte[] digest = Copy(snapshot.SnapshotDigest);
    // Kept values belong to the settings INFO named; a commit in between makes them another
    // configuration's.
    if (snapshot.CatalogId != _reading.CatalogId || snapshot.SnapshotRevision != _reading.Revision
        || !Same(digest, _reading.Digest)) {
      _readValues.Clear();
      _known.Clear();
      _kept = 0;
    }
    _count = snapshot.SettingCount;
    // The identity of exactly this snapshot; a later commit must not be filed under it.
    _reading = new Nv5SettingsSnapshots.Entry(snapshot.BootNonce, snapshot.SnapshotRevision,
        snapshot.CatalogId, digest, Empty.Values);
    _readIndex = 0;
    SetStep(Step.Read);
    SetStatus($"Reading settings… {ReadProgress}");
  }

  private void AdvanceRead() {
    if (_client.TakeValue() is { } value) {
      _readValues[value.Name] = value;
      _known.Add(value.Index);
      _readIndex++;
      SetStatus($"Reading settings… {ReadProgress}");
    }
    while (_readIndex < _count && _known.Contains(_readIndex)) {
      _readIndex++;
    }
    if (_readIndex < _count) {
      _client.Read(_readIndex, Now());
    } else if (_client.EndRead(Now())) {
      SetStep(Step.ReadEnd);
    }
  }

  private void FinishRead() {
    _values = _readValues;
    _readValues = new SortedDictionary<string, Value>(StringComparer.Ordinal);
    _known.Clear();
    _readBack = true;
    _refreshRequired = false;
    _valuesIdentity = _reading;
    _resume = Empty;
    _fullRead = false;
    _retries = 0;
    ReleaseReader();
    // A device that reports no digest gives nothing to recognise its settings by.
    if (Snapshots != null && _reading.Identified) {
      Snapshots.ByUid[UidKey()] = _reading.WithValues(_values);
    }
    Rebuild();
    SetStep(Step.Idle);
    Nv5ConfigResultMessage result = _client.Result;
    SetStatus((_kept > 0
            ? $"Read {_values.Count} settings at revision {result.CommittedRevision}, {_kept} of "
                + "them kept from an earlier read of the same settings."
            : $"Read {_values.Count} settings at revision {result.CommittedRevision}.")
        + (result.RestartRequired != 0
            ? " A restart is required to activate saved settings."
            : ""));
  }

  private void AdvanceCommit() {
    if (_client.Phase is (Phase.RestartRequired or Phase.Active) && _client.DurableConfirmed) {
      SetStep(Step.Recovery);
      _recoverAt = Now();
      SetStatus("Saved to the device. Waiting for restart and verified activation…");
    } else if (_client.Phase == Phase.RestartRequired && !_client.DurableConfirmed) {
      if (unchecked(Now() - _recoverAt) >= 100) {
        _recoverAt = Now();
        _client.Status(Now());
      }
    } else if (_client.Phase == Phase.Uncertain) {
      SetStep(Step.Recovery);
      SetStatus("Save acknowledgement missing. Reconnecting to verify; the commit will not be "
          + "repeated.");
    }
  }

  private void AdvanceRecovery() {
    if (_client.Phase == Phase.Active && _client.DurableConfirmed) {
      _started = false;
      _values = new SortedDictionary<string, Value>(
          CandidateValues().ToDictionary(pair => pair.Key, pair => pair.Value),
          StringComparer.Ordinal);
      _valuesIdentity = Empty;
      // Derived read-only values such as the link hash stay stale until the next read.
      _readBack = false;
      Rebuild();
      SetStep(Step.Idle);
      SetStatus("Saved, restarted and verified active.");
      _reloadPending = true;
      ConfigurationFinished?.Invoke(true);
    } else if (_client.Phase == Phase.Superseded) {
      Fail("A newer configuration superseded this save. Read the device before retrying.", true);
    } else if (_recoveryAnswered && _client.Phase == Phase.Uncertain) {
      Fail(_client.ConfigurationMatchesExpected()
          ? "Current settings match, but the original save acknowledgement was lost. Save "
              + "attribution remains uncertain."
          : "The original save could not be attributed and current settings differ. Read the "
              + "device before retrying.", true);
    }
  }

  private bool UseSnapshot() {
    if (_fullRead || Snapshots == null) {
      return false;
    }
    // Same boot, catalogue, committed revision and digest: the device holds what was read.
    Nv5ConfigResultMessage info = _client.Result;
    if (!Snapshots.ByUid.TryGetValue(UidKey(), out Nv5SettingsSnapshots.Entry? known)
        || info.CommittedKnown == 0 || known.BootNonce != info.BootNonce
        || known.Revision != info.CommittedRevision || known.CatalogId != info.CatalogId
        || !Same(known.Digest, info.CommittedDigest)) {
      return false;
    }
    _values = new SortedDictionary<string, Value>(
        known.Values.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
    _valuesIdentity = known with { Values = Empty.Values };
    _resume = Empty;
    _readBack = true;
    _refreshRequired = false;
    Rebuild();
    SetStep(Step.Idle);
    SetStatus($"{_values.Count} settings at revision {info.CommittedRevision}, unchanged since "
        + "they were last read."
        + (info.RestartRequired != 0 ? " A restart is required to activate saved settings." : ""));
    return true;
  }

  private void Fail(string reason, bool uncertain = false) {
    // A read given up frees the device's read session at once instead of after its lease.
    if (Reading()) {
      EndReadSession();
    }
    ReleaseReader();
    _retryPending = _waitingForReader = false;
    SetStatus(reason);
    _writes.Clear();
    if (!uncertain && _started && !_client.Pending && _client.Abort(Now())) {
      SetStep(Step.Aborting);
    } else {
      SetStep(Step.Failed);
    }
    ConfigurationFinished?.Invoke(false);
  }

  private void StartSave(bool commit) {
    if (Busy || (commit && !HasPendingChanges())) {
      return;
    }
    if (_refreshRequired) {
      SetStatus("Settings changed on the device since they were read. Use Read settings and "
          + "review your changes before saving.");
      return;
    }
    if (!_readBack) {
      SetStatus("The values shown are not a confirmed readback. Use Read settings before saving.");
      return;
    }
    _autoCommit = commit;
    _commitRequested = false;
    _durableEmitted = false;
    _writes.Clear();
    foreach ((string name, Nv5NativeSettingRow row) in _rows) {
      Value value = _values[name];
      if (!Editable(value)) {
        continue;
      }
      if (!Encode(value, row.ValueText, out byte[] bytes)) {
        Fail($"Invalid value for {name}.");
        return;
      }
      if (!Same(bytes, value.Bytes)) {
        _writes.Add(value with { Bytes = bytes });
      }
    }
    // A key is written as one whole: any changed word stages all four words of that radio.
    for (int channel = 1; channel <= 2; channel++) {
      string prefix = $"CH{channel}_KEY_W";
      if (!_writes.Any(value => value.Name.StartsWith(prefix, StringComparison.Ordinal))) {
        continue;
      }
      for (int word = 0; word < 4; word++) {
        string name = prefix + word.ToString(CultureInfo.InvariantCulture);
        if (_writes.All(value => value.Name != name) && _values.TryGetValue(name, out Value? kept)) {
          _writes.Add(kept);
        }
      }
    }
    // Bound to the revision of the values on screen, so the device refuses the save if anything
    // changed after they were read instead of applying these edits over that change.
    if (!_client.Begin(FreshNonce(), _valuesIdentity.Revision, Now())) {
      Fail("Refresh the device before starting a new transaction.");
      return;
    }
    SetStep(Step.Begin);
    SetStatus("Validating and saving the complete configuration…");
    Service();
  }

  private void TryCommit() {
    if (!_client.Commit(Now())) {
      Fail("Cannot commit the validated configuration.");
      return;
    }
    _commitSent = true;
    SetStep(Step.Commit);
  }

  private void Rebuild() {
    _rebuilding = true;
    _allRows.Clear();
    _rows.Clear();
    foreach (Value value in _values.Values) {
      bool supported = (value.Flags & Nv5ConfigValueFlags.Supported) != 0;
      bool editable = Editable(value);
      string type = value.Type switch {
        Nv5ConfigValueTypes.Text => "text",
        Nv5ConfigValueTypes.Float32 => "float32",
        Nv5ConfigValueTypes.Int32 => "int32",
        _ => "uint32",
      };
      // The unit stands beside the raw value it describes, for names that do not carry one.
      string unit = UnitOf(value.Name);
      var row = new Nv5NativeSettingRow(OnRowChanged, value.Name, Display(value),
          type + (unit.Length == 0 ? "" : " · " + unit)
              + (editable ? "" : supported ? " · read only" : " · unavailable"),
          editable, supported);
      _allRows.Add(row);
      _rows[value.Name] = row;
    }
    ShowRows();
    _rebuilding = false;
    RefreshCommands();
    EditsChanged?.Invoke();
  }

  private void ClearRows() {
    _allRows.Clear();
    _rows.Clear();
    Rows.Clear();
    RefreshCommands();
  }

  private void ShowRows() {
    Rows.Clear();
    foreach (Nv5NativeSettingRow row in _allRows.Where(row => MatchesFilter(row.Name))) {
      Rows.Add(row);
    }
  }

  private void OnRowChanged() {
    if (_rebuilding) {
      return;
    }
    foreach ((string name, Nv5NativeSettingRow row) in _rows) {
      Value value = _values[name];
      row.EditState = !Editable(value) ? ""
          : !Encode(value, row.ValueText, out byte[] bytes) ? "Invalid"
          : Same(bytes, value.Bytes) ? "" : "Changed";
    }
    RefreshCommands();
    EditsChanged?.Invoke();
  }

  private bool MatchesFilter(string name) {
    string text = FilterText;
    if (!text.Contains('*') && !text.Contains('?')) {
      return name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
    string pattern = "^" + Regex.Escape(text).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
    return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
  }

  private string UidKey() => Convert.ToHexString(_target?.Uid ?? []);

  private static bool Editable(Value value) =>
      (value.Flags & Nv5ConfigValueFlags.Supported) != 0
      && (value.Flags & Nv5ConfigValueFlags.ReadOnly) == 0;

  private static string UnitOf(string name) {
    bool Ends(string suffix) => name.EndsWith(suffix, StringComparison.Ordinal);
    if (name.StartsWith("LPS_", StringComparison.Ordinal)
        && (Ends("_CEE") || Ends("_CEN") || Ends("_CEU") || Ends("_CNN") || Ends("_CNU")
            || Ends("_CUU") || Ends("_BVAR") || Ends("_VAR"))) {
      return "m²";
    }
    if (Ends("_E7")) {
      return "degrees × 10⁷";
    }
    if (name == "LPS_RSP_SYM") {
      return "symbols";
    }
    if (!name.StartsWith("CH", StringComparison.Ordinal)) {
      return "";
    }
    if (Ends("_RNG_PER") || Ends("_RNG_PHASE") || Ends("_RSV_PER") || Ends("_RSV_PHASE")) {
      return "data packets";
    }
    if (Ends("_RNG_SPAN")) {
      return "Hz";
    }
    if (Ends("_RNG_PWR")) {
      return "dBm";
    }
    return Ends("_RNG_N") ? "exchanges" : "";
  }

  private static bool TryParseInteger(string text, out long number) {
    number = 0;
    bool negative = text.StartsWith('-');
    string digits = negative || text.StartsWith('+') ? text[1..] : text;
    if (digits.Length == 0) {
      return false;
    }
    ulong magnitude;
    try {
      if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
        if (!ulong.TryParse(digits[2..], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out magnitude)) {
          return false;
        }
      } else if (digits.Length > 1 && digits[0] == '0') {
        if (!digits.All(letter => letter is >= '0' and <= '7')) {
          return false;
        }
        magnitude = Convert.ToUInt64(digits, 8);
      } else if (!digits.All(char.IsAsciiDigit)
          || !ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture,
              out magnitude)) {
        return false;
      }
    } catch (OverflowException) {
      return false;
    }
    if (magnitude > long.MaxValue) {
      return false;
    }
    number = negative ? -(long)magnitude : (long)magnitude;
    return true;
  }

  private static int? Integer(JsonNode? node) =>
      node is JsonValue value && value.TryGetValue(out int number) ? number : null;

  private static string? Text(JsonNode? node) =>
      node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

  private static string NameOf(byte[]? bytes) {
    if (bytes == null) {
      return "";
    }
    int end = Array.IndexOf(bytes, (byte)0);
    return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
  }

  private static ulong FreshNonce() {
    ulong value = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
    return value == 0 ? 1 : value;
  }

  private static bool Same(byte[]? left, byte[]? right) =>
      (left ?? []).AsSpan().SequenceEqual(right ?? []);

  private static byte[] Copy(byte[]? bytes) => bytes == null ? [] : (byte[])bytes.Clone();
}
