using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;

namespace MissionPlanner.Services;

internal static class Nv5ConfigOperations {
  internal const byte Info = 0;
  internal const byte Begin = 1;
  internal const byte Validate = 2;
  internal const byte Commit = 3;
  internal const byte Abort = 4;
  internal const byte Status = 5;
  internal const byte ReadBegin = 6;
  internal const byte Read = 7;
  internal const byte ReadEnd = 8;
  internal const byte Set = 9;
}

internal static class Nv5ConfigScopes {
  internal const byte Committed = 0;
  internal const byte Active = 1;
  internal const byte Candidate = 2;
}

internal static class Nv5ConfigStates {
  internal const byte Unavailable = 0;
  internal const byte Idle = 1;
  internal const byte Staged = 2;
  internal const byte Validating = 3;
  internal const byte Validated = 4;
  internal const byte Committing = 5;
  internal const byte CommittedRestartRequired = 6;
  internal const byte Active = 7;
  internal const byte Aborted = 8;
  internal const byte Expired = 9;
  internal const byte Failed = 10;
  internal const byte Indeterminate = 11;
  internal const byte CommittedNotActive = 12;
  internal const byte CommittedSuperseded = 13;
  internal const byte Unknown = 14;
}

internal static class Nv5ConfigResults {
  internal const byte Ok = 0;
  internal const byte Accepted = 1;
  internal const byte Busy = 2;
  internal const byte BadTarget = 3;
  internal const byte BadVersion = 4;
  internal const byte StaleBoot = 5;
  internal const byte BadOwner = 6;
  internal const byte BadSequence = 7;
  internal const byte ConflictingRetry = 8;
  internal const byte StaleRevision = 9;
  internal const byte BadDigest = 10;
  internal const byte UnknownSetting = 11;
  internal const byte ReadOnly = 12;
  internal const byte Unsupported = 13;
  internal const byte WrongType = 14;
  internal const byte InvalidValue = 15;
  internal const byte Incomplete = 16;
  internal const byte InvalidConfiguration = 17;
  internal const byte TooLate = 18;
  internal const byte NotFound = 19;
  internal const byte Expired = 20;
  internal const byte StorageFailed = 21;
  internal const byte Indeterminate = 22;
  internal const byte Unavailable = 23;
  internal const byte LimitExceeded = 24;
}

internal static class Nv5ConfigValueTypes {
  internal const byte UInt32 = 5;
  internal const byte Int32 = 6;
  internal const byte Float32 = 9;
  internal const byte Text = 128;
}

internal static class Nv5ConfigValueFlags {
  internal const byte ReadOnly = 1;
  internal const byte Secret = 2;
  internal const byte Supported = 4;
}

/// <summary>One request of a native transaction, kept as its payload so a retry resends it
/// unchanged.</summary>
internal sealed record Nv5ConfigurationFrame(uint MessageId, object Payload);

/// <summary>
/// Explicitly serviced client for one NV5 native transaction resource: the operational settings
/// (NV5_CONFIG_*) or the modem's calibration bank (NV5_CAL_*). Both use the same operations,
/// leases and retry rules; a client never mixes the two resources' messages.
/// </summary>
/// <remarks>
/// The client has no timers or threads. The caller passes the time, sends every frame from
/// <see cref="TakeOutbound"/> on the target's link and delivers that link's replies to
/// <see cref="Receive"/>. A retry resends the identical payload, which the firmware matches by
/// its client nonce, token and sequence. Read and recovery requests are retried for eight
/// one-second attempts, so a reply survives the radio's five-second link-loss interval; COMMIT is
/// sent four times and then left to receipt recovery, which never replays it.
/// </remarks>
internal sealed class Nv5ConfigurationClient {
  internal enum ProductKind {
    Modem,
    Hub,
  }

  internal enum ResourceKind {
    Configuration,
    Calibration,
  }

  internal enum ClientPhase {
    Offline,
    Ready,
    Staged,
    Validating,
    Validated,
    Committing,
    RestartRequired,
    Active,
    Superseded,
    Uncertain,
    Error,
  }

  /// <summary>A device address on one link, bound to the 18-byte UID its passport reported.
  /// <see cref="Verified"/> is the caller's precondition, not authentication.</summary>
  internal sealed record Target(
      byte[] Uid,
      object Route,
      ulong TransportEpoch,
      ulong Capabilities,
      byte System,
      byte Component,
      bool Verified,
      ProductKind Product = ProductKind.Modem,
      bool ViaHub = false);

  /// <summary>One complete, metadata-consistent setting read from a snapshot.</summary>
  internal sealed record Value(string Name, byte[] Bytes, ushort Id, ushort Index, byte Type,
      byte Flags);

  /// <summary>Fixed calibration-bank catalogue: LENGTH, W000-W511 and six read-only words.</summary>
  internal const uint CalibrationCatalog = 0x43414c01;
  internal const ushort CalibrationEntries = 519;

  /// <summary>NV_HUB_CAPABILITY_CONFIGURATION_TRANSACTIONS of a HUB passport.</summary>
  internal const ulong HubConfigurationTransactions = 1UL << 11;

  private const uint RetryMs = 1000;
  private const uint PollMs = 100;
  private const byte Retries = 7;
  private const byte CommitRetries = 3;

  private readonly byte _system;
  private readonly byte _component;
  private readonly ResourceKind _resource;

  private Target? _target;
  private Nv5ConfigResultMessage _result;
  private Nv5ConfigValueMessage _readHeader;
  private Nv5ConfigurationFrame? _outbound;
  private byte[] _stagedName = [];
  private byte[] _stagedBytes = [];
  private byte[] _validatedDigest = [];
  private byte[] _expectedDigest = [];
  private byte[] _readBytes = [];
  private Value? _completedValue;
  private ulong _boot;
  private ulong _clientNonce;
  private ulong _token;
  private ulong _baseRevision;
  private ulong _commitBoot;
  private ulong _commitClientNonce;
  private ulong _commitToken;
  private ulong _commitRevision;
  private uint _commitSequence;
  private byte[] _commitUid = [];
  private ProductKind _commitProduct;
  private ulong _readNonce;
  private ulong _readToken;
  private ulong _readRevision;
  private ulong _pendingNonce;
  private ulong _pendingToken;
  private ulong _probedBoot;
  private uint _nextSequence = 1;
  private uint _correlation = 0x80000000;
  private uint _pendingSequence;
  private uint _sentMs;
  private uint _activityMs;
  private uint _readCatalog;
  private ushort _readCount;
  private ushort _readIndex;
  private ushort _stageOffset;
  private byte _operation;
  private byte _readScope;
  private byte _stageType;
  private byte _retries;
  private byte _readChunks;
  private ClientPhase _phase = ClientPhase.Offline;
  private bool _pending;
  private bool _outboundReady;
  private bool _sent;
  private bool _recovering;
  private bool _readReady;
  private bool _readPreparing;
  private bool _readResult;
  private bool _valueReady;
  private bool _moreStage;
  private bool _headerReady;
  private bool _durableConfirmed;
  private bool _probing;

  /// <param name="system">MAVLink system id this client's frames carry, which replies target.</param>
  /// <param name="component">MAVLink component id this client's frames carry.</param>
  internal Nv5ConfigurationClient(byte system = 255, byte component = 190,
      ResourceKind resource = ResourceKind.Configuration) {
    _system = system;
    _component = component;
    _resource = resource;
  }

  internal ResourceKind Resource => _resource;
  internal ClientPhase Phase => _phase;
  internal bool Pending => _pending;

  /// <summary>A matching durable receipt of this process's commit was actually received.</summary>
  internal bool DurableConfirmed => _durableConfirmed;

  internal bool SnapshotReady => _readReady;

  /// <summary>The boot nonce of the last probe's answer, or zero before one arrives.</summary>
  internal ulong ProbedBoot => _probedBoot;

  internal Nv5ConfigResultMessage Result => _result;

  /// <summary>Explains why the route cannot carry modem calibration bank or capture messages.</summary>
  internal static string CalibrationRouteProblem(Target target) =>
      target.Product != ProductKind.Modem || target.ViaHub
          ? "Connect directly to the modem's USB or Ethernet. HUB relay routes do not support "
              + "calibration bank or capture messages."
          : "";

  /// <summary>
  /// Forgets every session and anything in flight, keeping the request correlation so that a
  /// late reply to an earlier request cannot match a new one.
  /// </summary>
  internal void Reset() {
    _target = null;
    _result = default;
    _readHeader = default;
    _outbound = null;
    Wipe(ref _stagedBytes);
    Wipe(ref _readBytes);
    _stagedName = [];
    _validatedDigest = [];
    _expectedDigest = [];
    _completedValue = null;
    _boot = _clientNonce = _token = _baseRevision = 0;
    _commitBoot = _commitClientNonce = _commitToken = _commitRevision = 0;
    _commitSequence = 0;
    _commitUid = [];
    _commitProduct = ProductKind.Modem;
    _readNonce = _readToken = _readRevision = 0;
    _pendingNonce = _pendingToken = _probedBoot = 0;
    _nextSequence = 1;
    _pendingSequence = 0;
    _sentMs = _activityMs = 0;
    _readCatalog = 0;
    _readCount = _readIndex = _stageOffset = 0;
    _operation = _readScope = _stageType = 0;
    _retries = _readChunks = 0;
    _phase = ClientPhase.Offline;
    _pending = _outboundReady = _sent = _recovering = false;
    _readReady = _readPreparing = _readResult = _valueReady = false;
    _moreStage = _headerReady = false;
    _durableConfirmed = _probing = false;
  }

  internal bool Discover(Target target, uint now) {
    if (_pending
        || (_phase != ClientPhase.Offline && _phase != ClientPhase.Ready
            && _phase != ClientPhase.Active)
        || !ValidTarget(target, _resource) || _system == 0 || _component == 0) {
      return false;
    }
    Reset();
    _target = target;
    return Control(Nv5ConfigOperations.Info, 0, 0, ++_correlation, 0, 0, 0, [], now);
  }

  internal bool Begin(ulong nonce, uint now) {
    if (_phase != ClientPhase.Ready || _pending || nonce == 0 || nonce == _clientNonce) {
      return false;
    }
    _moreStage = false;
    _validatedDigest = [];
    _expectedDigest = [];
    _durableConfirmed = false;
    Wipe(ref _stagedBytes);
    _stagedName = [];
    _clientNonce = nonce;
    _token = 0;
    _baseRevision = _result.CommittedRevision;
    return Control(Nv5ConfigOperations.Begin, nonce, 0, 0, 0, _baseRevision, 0, [], now);
  }

  internal bool Stage(string name, byte type, byte[] data, uint now) {
    if (_pending || _moreStage
        || (_phase != ClientPhase.Staged && _phase != ClientPhase.Validated)
        || !CanonicalName(name) || !ValidBytes(type, data) || _nextSequence == uint.MaxValue) {
      return false;
    }
    _stagedName = Encoding.ASCII.GetBytes(name);
    _stagedBytes = (byte[])data.Clone();
    _stageType = type;
    _stageOffset = 0;
    _validatedDigest = [];
    return SendChunk(now);
  }

  internal bool Validate(uint now) {
    if (_phase != ClientPhase.Staged || _pending || _moreStage || _nextSequence == uint.MaxValue) {
      return false;
    }
    return Control(Nv5ConfigOperations.Validate, _clientNonce, _token, _nextSequence, 0, 0, 0, [],
        now);
  }

  internal bool Commit(uint now) {
    if (_phase != ClientPhase.Validated || _pending || _validatedDigest.Length != 32
        || _nextSequence == uint.MaxValue || _target == null) {
      return false;
    }
    _expectedDigest = _validatedDigest;
    _commitBoot = _boot;
    _commitClientNonce = _clientNonce;
    _commitToken = _token;
    _commitSequence = _nextSequence;
    _commitRevision = _baseRevision + 1;
    _commitUid = (byte[])_target.Uid.Clone();
    _commitProduct = _target.Product;
    return Control(Nv5ConfigOperations.Commit, _clientNonce, _token, _nextSequence, 0,
        _baseRevision, 0, _expectedDigest, now);
  }

  internal bool Abort(uint now) {
    if (_pending
        || (_phase != ClientPhase.Staged && _phase != ClientPhase.Validated
            && _phase != ClientPhase.Validating)) {
      return false;
    }
    _moreStage = false;
    Wipe(ref _stagedBytes);
    _stagedName = [];
    return Control(Nv5ConfigOperations.Abort, _clientNonce, _token, _nextSequence, 0, 0, 0, [],
        now);
  }

  internal bool Status(uint now) {
    if (_pending || _token == 0 || _boot == 0) {
      return false;
    }
    return Control(Nv5ConfigOperations.Status, _clientNonce, _token, ++_correlation, 0, 0, 0, [],
        now);
  }

  /// <summary>Only the same verified UID may replace the route during receipt recovery.</summary>
  internal bool Recover(Target rediscovered, uint now) {
    if ((_pending && _operation != Nv5ConfigOperations.Commit
            && _operation != Nv5ConfigOperations.Status
            && !(_recovering && _operation == Nv5ConfigOperations.Info) && !_probing)
        || _expectedDigest.Length != 32 || _token == 0 || _target == null
        || !ValidTarget(rediscovered, _resource) || rediscovered.Product != _target.Product
        || !Same(rediscovered.Uid, _target.Uid)) {
      return false;
    }
    // Rediscovery ends retries on the old route. Accepted server work is queried by INFO and
    // STATUS; COMMIT is never replayed onto the new route.
    _pending = _outboundReady = _sent = _probing = false;
    _outbound = null;
    _target = rediscovered;
    _recovering = true;
    _readReady = _readPreparing = false;
    _readToken = _readNonce = 0;
    _readChunks = 0;
    _headerReady = _readResult = _valueReady = false;
    Wipe(ref _readBytes);
    _completedValue = null;
    return Control(Nv5ConfigOperations.Info, 0, 0, ++_correlation, 0, 0, 0, [], now);
  }

  /// <summary>
  /// While a commit awaits the device's restart, asks INFO which boot answers, leaving the
  /// commit, its receipt and this client's state as they are; <see cref="ProbedBoot"/> then names
  /// that boot.
  /// </summary>
  internal bool Probe(uint now) {
    if (_pending || _recovering || _expectedDigest.Length != 32 || _token == 0) {
      return false;
    }
    _probedBoot = 0;
    _probing = Control(Nv5ConfigOperations.Info, 0, 0, ++_correlation, 0, 0, 0, [], now);
    return _probing;
  }

  internal bool BeginRead(byte scope, ulong nonce, uint now) {
    if (_pending || _readToken != 0 || _boot == 0 || scope > Nv5ConfigScopes.Candidate) {
      return false;
    }
    if (scope == Nv5ConfigScopes.Candidate) {
      if (_phase != ClientPhase.Staged && _phase != ClientPhase.Validated) {
        return false;
      }
      nonce = _clientNonce;
    } else if (nonce == 0 || nonce == _readNonce) {
      return false;
    }
    _readScope = scope;
    _readNonce = nonce;
    _readReady = false;
    return Control(Nv5ConfigOperations.ReadBegin, nonce,
        scope == Nv5ConfigScopes.Candidate ? _token : 0, ++_correlation, scope, 0, 0, [], now);
  }

  internal bool Read(ushort index, uint now) {
    if (_pending || !_readReady || index >= _readCount || _valueReady) {
      return false;
    }
    _readIndex = index;
    _readChunks = 0;
    _headerReady = _readResult = false;
    _readBytes = [];
    return Control(Nv5ConfigOperations.Read, _readNonce, _readToken, ++_correlation, _readScope, 0,
        index, [], now);
  }

  internal bool EndRead(uint now) {
    if (_pending || _readToken == 0) {
      return false;
    }
    return Control(Nv5ConfigOperations.ReadEnd, _readNonce, _readToken, ++_correlation,
        _readScope, 0, 0, [], now);
  }

  /// <summary>
  /// Gives up this client's read session without waiting for anything in flight: returns the
  /// READ_END frame to send once, or null when no session is open, so the device frees its single
  /// read session at once instead of holding it until its lease runs out.
  /// </summary>
  internal Nv5ConfigurationFrame? AbandonRead(uint now) {
    if (_readToken == 0 || _boot == 0) {
      return null;
    }
    // A late reply to the request in flight no longer matches anything pending.
    _pending = _outboundReady = _sent = false;
    _outbound = null;
    Nv5ConfigurationFrame? frame = null;
    if (Control(Nv5ConfigOperations.ReadEnd, _readNonce, _readToken, ++_correlation, _readScope,
            0, 0, [], now)) {
      frame = _outbound;
    }
    _pending = _outboundReady = _sent = false;
    _outbound = null;
    _readToken = 0;
    _readReady = _readPreparing = false;
    _headerReady = _readResult = _valueReady = false;
    Wipe(ref _readBytes);
    return frame;
  }

  /// <summary>The request to send now, or null. A retry returns the same frame again.</summary>
  internal Nv5ConfigurationFrame? TakeOutbound(uint now) {
    if (!_outboundReady) {
      return null;
    }
    _outboundReady = false;
    _sent = true;
    _sentMs = now;
    return _outbound;
  }

  internal void Service(uint now) {
    if (_pending) {
      if (_sent && !_outboundReady && unchecked(now - _sentMs) >= RetryMs) {
        byte retries = _operation == Nv5ConfigOperations.Commit ? CommitRetries : Retries;
        if (_retries++ < retries) {
          _outboundReady = true;
        } else {
          _pending = false;
          _outbound = null;
          _sent = false;
          // An unanswered probe says nothing about the commit; the next one asks again.
          if (_probing) {
            _probing = false;
          } else {
            _phase = _token != 0 ? ClientPhase.Uncertain : ClientPhase.Error;
          }
        }
      }
      return;
    }
    if (_moreStage) {
      _moreStage = false;
      SendChunk(now);
      return;
    }
    if (unchecked(now - _activityMs) < PollMs) {
      return;
    }
    if (_recovering) {
      Status(now);
      return;
    }
    if (_readPreparing) {
      Control(Nv5ConfigOperations.Status, _readNonce, _readToken, ++_correlation, _readScope, 0,
          0, [], now);
    } else if (_phase is ClientPhase.Validating or ClientPhase.Committing) {
      Status(now);
    }
  }

  internal bool Receive(MAVLink.MAVLinkMessage message, object route, ulong epoch, uint now) {
    if (!Addressed(message, route, epoch)) {
      return false;
    }
    bool isResult = DecodeResult(message, out Nv5ConfigResultMessage receipt);
    if ((_recovering || _phase == ClientPhase.Uncertain) && _expectedDigest.Length == 32
        && _target != null && Same(_target.Uid, _commitUid) && _target.Product == _commitProduct
        && isResult) {
      if (receipt.SchemaVersion == 1 && receipt.TargetSystem == _system
          && receipt.TargetComponent == _component
          && receipt.Operation == Nv5ConfigOperations.Commit
          && receipt.Result == Nv5ConfigResults.Ok && receipt.CommittedKnown != 0
          && receipt.BootNonce == _commitBoot && receipt.ReceiptBootNonce == _commitBoot
          && receipt.ClientNonce == _commitClientNonce && receipt.Token == _commitToken
          && receipt.Sequence == _commitSequence
          && receipt.TransactionRevision == _commitRevision
          && receipt.CommittedRevision == _commitRevision
          && Same(receipt.TransactionDigest, _expectedDigest)
          && Same(receipt.CommittedDigest, _expectedDigest)
          && receipt.State is Nv5ConfigStates.CommittedRestartRequired or Nv5ConfigStates.Active
              or Nv5ConfigStates.CommittedNotActive) {
        _durableConfirmed = true;
        return true;
      }
    }
    if (!_pending) {
      return false;
    }
    if (isResult) {
      Nv5ConfigResultMessage wire = receipt;
      if (wire.SchemaVersion != 1 || wire.TargetSystem != _system
          || wire.TargetComponent != _component || wire.Operation != _operation
          || wire.Sequence != _pendingSequence || wire.ClientNonce != _pendingNonce
          || wire.BootNonce == 0
          || (_operation != Nv5ConfigOperations.Info && wire.BootNonce != _boot)) {
        return false;
      }
      bool issuedToken = _operation is Nv5ConfigOperations.Begin or Nv5ConfigOperations.ReadBegin;
      if ((!issuedToken && wire.Token != _pendingToken)
          || (issuedToken && wire.Result <= Nv5ConfigResults.Accepted && wire.Token == 0)) {
        return false;
      }
      return HandleResult(wire, now);
    }
    bool calibrationValue = _resource == ResourceKind.Calibration
        && message.msgid == NvModemMessageIds.Nv5CalValue && message.payloadlength == 121;
    bool settingsValue = _resource == ResourceKind.Configuration
        && message.msgid == NvModemMessageIds.Nv5ConfigValue && message.payloadlength == 121;
    if ((calibrationValue || settingsValue) && _operation == Nv5ConfigOperations.Read) {
      Nv5ConfigValueMessage wire = calibrationValue
          ? Settings(message.ToStructure<Nv5CalValueMessage>())
          : message.ToStructure<Nv5ConfigValueMessage>();
      if (wire.SchemaVersion != 1 || wire.TargetSystem != _system
          || wire.TargetComponent != _component || wire.BootNonce != _boot
          || wire.Token != _readToken || wire.ClientNonce != _readNonce
          || wire.Sequence != _pendingSequence || wire.Scope != _readScope
          || wire.Revision != _readRevision || wire.CatalogId != _readCatalog
          || wire.SettingCount != _readCount || wire.Index != _readIndex) {
        return false;
      }
      return HandleValue(wire);
    }
    return false;
  }

  /// <summary>Publishes a complete value once; the caller owns the returned bytes.</summary>
  internal Value? TakeValue() {
    if (!_valueReady) {
      return null;
    }
    Value? output = _completedValue;
    _completedValue = null;
    _valueReady = false;
    return output;
  }

  /// <summary>Compares readback digests; this does not establish transaction attribution.</summary>
  internal bool ConfigurationMatchesExpected() =>
      _result.CommittedKnown != 0 && _expectedDigest.Length == 32
      && Same(_result.CommittedDigest, _expectedDigest)
      && Same(_result.ActiveDigest, _expectedDigest);

  internal static bool CanonicalName(string name) =>
      name.Length is > 0 and <= 16 && name.All(letter => letter is >= '\x21' and <= '\x7e');

  internal static bool ValidBytes(byte type, byte[] bytes) {
    if (type == Nv5ConfigValueTypes.Text) {
      return bytes.Length <= 95 && bytes.All(value => value >= 0x20 && value != 0x7f);
    }
    if (bytes.Length != 4) {
      return false;
    }
    if (type is Nv5ConfigValueTypes.UInt32 or Nv5ConfigValueTypes.Int32) {
      return true;
    }
    return type == Nv5ConfigValueTypes.Float32
        && float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes));
  }

  private static bool ValidTarget(Target target, ResourceKind resource) {
    if (resource == ResourceKind.Calibration && CalibrationRouteProblem(target).Length != 0) {
      return false;
    }
    ulong capability;
    switch (target.Product) {
      case ProductKind.Modem:
        capability = resource == ResourceKind.Calibration
            ? NvModemCapabilities.CalibrationTransactions
            : NvModemCapabilities.ConfigurationTransactions;
        break;
      case ProductKind.Hub:
        // The calibration bank is a modem resource; a HUB has none.
        if (resource == ResourceKind.Calibration) {
          return false;
        }
        capability = HubConfigurationTransactions;
        break;
      default:
        return false;
    }
    return target.Verified && target.Uid is { Length: 18 } uid && uid.Any(value => value != 0)
        && target.Route is not (null or "") && target.System != 0 && target.Component != 0
        && (target.Capabilities & capability) != 0;
  }

  private bool Queue(Nv5ConfigurationFrame frame, byte operation, ulong nonce, ulong token,
      uint sequence, uint now) {
    if (_pending) {
      return false;
    }
    _outbound = frame;
    _operation = operation;
    _pendingNonce = nonce;
    _pendingToken = token;
    _pendingSequence = sequence;
    _retries = 0;
    _pending = _outboundReady = true;
    _sent = false;
    _activityMs = now;
    return true;
  }

  private bool Control(byte operation, ulong nonce, ulong token, uint sequence, byte scope,
      ulong revision, ushort index, byte[] digest, uint now) {
    if (_pending || _target == null || (digest.Length != 0 && digest.Length != 32)) {
      return false;
    }
    var wire = new Nv5ConfigControlMessage {
      BootNonce = operation == Nv5ConfigOperations.Info ? 0 : _boot,
      ClientNonce = nonce,
      Token = token,
      Sequence = sequence,
      Operation = operation,
      Scope = scope,
      ExpectedRevision = revision,
      Index = index,
      Digest = new byte[32],
      TargetSystem = _target.System,
      TargetComponent = _target.Component,
      SchemaVersion = 1,
    };
    digest.CopyTo(wire.Digest, 0);
    Nv5ConfigurationFrame frame = _resource == ResourceKind.Calibration
        ? new(NvModemMessageIds.Nv5CalControl, Calibration(wire))
        : new(NvModemMessageIds.Nv5ConfigControl, wire);
    return Queue(frame, operation, nonce, token, sequence, now);
  }

  private bool SendChunk(uint now) {
    if (_target == null) {
      return false;
    }
    int length = Math.Min(48, _stagedBytes.Length - _stageOffset);
    var wire = new Nv5ConfigItemMessage {
      BootNonce = _boot,
      ClientNonce = _clientNonce,
      Token = _token,
      Sequence = _nextSequence,
      Offset = _stageOffset,
      TotalLength = (ushort)_stagedBytes.Length,
      Length = (byte)length,
      ValueType = _stageType,
      Name = new byte[16],
      Data = new byte[48],
      TargetSystem = _target.System,
      TargetComponent = _target.Component,
      SchemaVersion = 1,
    };
    _stagedName.CopyTo(wire.Name, 0);
    Array.Copy(_stagedBytes, _stageOffset, wire.Data, 0, length);
    Nv5ConfigurationFrame frame = _resource == ResourceKind.Calibration
        ? new(NvModemMessageIds.Nv5CalItem, Calibration(wire))
        : new(NvModemMessageIds.Nv5ConfigItem, wire);
    return Queue(frame, Nv5ConfigOperations.Set, _clientNonce, _token, _nextSequence, now);
  }

  private bool Addressed(MAVLink.MAVLinkMessage message, object route, ulong epoch) =>
      _target != null && message.sysid == _target.System && message.compid == _target.Component
      && Equals(route, _target.Route) && epoch == _target.TransportEpoch;

  /// <summary>Decodes this resource's RESULT; false for any other message or length.</summary>
  private bool DecodeResult(MAVLink.MAVLinkMessage message, out Nv5ConfigResultMessage wire) {
    wire = default;
    if (_resource == ResourceKind.Calibration) {
      if (message.msgid != NvModemMessageIds.Nv5CalResult || message.payloadlength != 243) {
        return false;
      }
      wire = Settings(message.ToStructure<Nv5CalResultMessage>());
      return true;
    }
    if (message.msgid != NvModemMessageIds.Nv5ConfigResult || message.payloadlength != 251) {
      return false;
    }
    wire = message.ToStructure<Nv5ConfigResultMessage>();
    return true;
  }

  /// <summary>A calibration client talks only to the fixed bank catalogue; settings catalogues
  /// vary.</summary>
  private bool ExpectedCatalog(Nv5ConfigResultMessage wire) =>
      _resource != ResourceKind.Calibration
      || (wire.CatalogId == CalibrationCatalog && wire.SettingCount == CalibrationEntries);

  private void UpdatePhase(Nv5ConfigResultMessage wire) {
    if (wire.CommittedKnown == 0 || wire.State == Nv5ConfigStates.Indeterminate) {
      _phase = ClientPhase.Uncertain;
      return;
    }
    switch (wire.State) {
      case Nv5ConfigStates.Staged:
        _phase = ClientPhase.Staged;
        break;
      case Nv5ConfigStates.Validating:
        _phase = ClientPhase.Validating;
        break;
      case Nv5ConfigStates.Validated:
        _phase = ClientPhase.Validated;
        _validatedDigest = Copy(wire.TransactionDigest);
        break;
      case Nv5ConfigStates.Committing:
        _phase = ClientPhase.Committing;
        break;
      // Stored durably but not running, and no restart follows: a restart is still required.
      case Nv5ConfigStates.CommittedRestartRequired:
      case Nv5ConfigStates.CommittedNotActive:
        _phase = ClientPhase.RestartRequired;
        break;
      case Nv5ConfigStates.Active:
        _phase = ClientPhase.Active;
        break;
      case Nv5ConfigStates.CommittedSuperseded:
        _phase = ClientPhase.Superseded;
        break;
      case Nv5ConfigStates.Idle:
      case Nv5ConfigStates.Aborted:
      case Nv5ConfigStates.Expired:
      case Nv5ConfigStates.Failed:
        _phase = ClientPhase.Ready;
        break;
      default:
        _phase = ClientPhase.Error;
        break;
    }
  }

  private bool HandleResult(Nv5ConfigResultMessage wire, uint now) {
    if (_probing) {
      // A probe learns only which boot answers; the commit's state stays as it was.
      _probing = _pending = _outboundReady = _sent = false;
      _outbound = null;
      _probedBoot = wire.BootNonce;
      return true;
    }
    _result = wire;
    _activityMs = now;
    if (_operation == Nv5ConfigOperations.Read && wire.Result == Nv5ConfigResults.Ok) {
      if (wire.SnapshotRevision != _readRevision || wire.CatalogId != _readCatalog) {
        return false;
      }
      _readResult = true;
      FinishRead();
      return true;
    }
    _pending = _outboundReady = _sent = false;
    _outbound = null;
    if (_operation == Nv5ConfigOperations.Info) {
      if (wire.Result != Nv5ConfigResults.Ok || (wire.Capabilities & 3u) != 3u
          || wire.CommittedKnown == 0 || !ExpectedCatalog(wire)) {
        _phase = _recovering ? ClientPhase.Uncertain : ClientPhase.Error;
        _recovering = false;
        return true;
      }
      _boot = wire.BootNonce;
      if (!_recovering) {
        _phase = wire.RestartRequired != 0 ? ClientPhase.RestartRequired : ClientPhase.Ready;
      }
      return true;
    }
    if (_operation == Nv5ConfigOperations.ReadBegin) {
      if (wire.Result <= Nv5ConfigResults.Accepted && !ExpectedCatalog(wire)) {
        // A snapshot of another catalogue cannot be read as this resource; the token is kept
        // only so that AbandonRead() can free the session at once.
        _readToken = wire.Token;
        _readReady = _readPreparing = false;
        _phase = ClientPhase.Error;
        return true;
      }
      if (wire.Result <= Nv5ConfigResults.Accepted) {
        _readToken = wire.Token;
        _readRevision = wire.SnapshotRevision;
        _readCatalog = wire.CatalogId;
        _readCount = wire.SettingCount;
        _readReady = wire.Result == Nv5ConfigResults.Ok;
        _readPreparing = !_readReady;
      }
      return true;
    }
    if (_operation == Nv5ConfigOperations.ReadEnd) {
      if (wire.Result is Nv5ConfigResults.Ok or Nv5ConfigResults.Expired
          or Nv5ConfigResults.NotFound) {
        _readToken = 0;
        _readReady = _readPreparing = false;
      }
      return true;
    }
    if (_operation == Nv5ConfigOperations.Status && _pendingToken == _readToken && _readToken != 0) {
      _readReady = wire.Result == Nv5ConfigResults.Ok;
      _readPreparing = wire.Result == Nv5ConfigResults.Accepted;
      if (!_readReady && !_readPreparing) {
        _readToken = 0;
      }
      return true;
    }
    if (_operation == Nv5ConfigOperations.Read) {
      return true;
    }
    if (_operation == Nv5ConfigOperations.Begin && wire.Result == Nv5ConfigResults.Ok) {
      _token = wire.Token;
      _baseRevision = wire.TransactionRevision;
    } else if (_operation == Nv5ConfigOperations.Begin) {
      _phase = wire.CommittedKnown != 0 ? ClientPhase.Ready : ClientPhase.Uncertain;
      return true;
    }
    if (_token != 0) {
      _nextSequence = wire.NextSequence;
    }
    if (_operation == Nv5ConfigOperations.Set && wire.Result == Nv5ConfigResults.Ok) {
      _stageOffset = (ushort)(_stageOffset + Math.Min(48, _stagedBytes.Length - _stageOffset));
      _moreStage = _stageOffset < _stagedBytes.Length;
    }
    if (!_moreStage) {
      _stagedName = [];
      Wipe(ref _stagedBytes);
    }
    if (_operation == Nv5ConfigOperations.Status && _expectedDigest.Length != 0
        && wire.Result == Nv5ConfigResults.NotFound && wire.ReceiptBootNonce == 0) {
      _recovering = false;
      _phase = _durableConfirmed && ConfigurationMatchesExpected()
              && wire.CommittedRevision == _baseRevision + 1
              && wire.ActiveRevision == _baseRevision + 1
          ? ClientPhase.Active
          : ClientPhase.Uncertain;
      return true;
    }
    if (_recovering) {
      if (wire.Result <= Nv5ConfigResults.Accepted && wire.ReceiptBootNonce == 0
          && wire.State == Nv5ConfigStates.Committing && wire.BootNonce == _commitBoot
          && wire.CommittedKnown != 0) {
        _phase = ClientPhase.Committing;
        return true;
      }
      _recovering = false;
      if (wire.Result != Nv5ConfigResults.Ok || wire.ReceiptBootNonce == 0
          || wire.ReceiptBootNonce != _commitBoot
          || wire.TransactionRevision != _baseRevision + 1
          || !Same(wire.TransactionDigest, _expectedDigest)) {
        _phase = ClientPhase.Uncertain;
        return true;
      }
    }
    if (_operation == Nv5ConfigOperations.Commit && wire.Result == Nv5ConfigResults.Ok
        && wire.ReceiptBootNonce == 0) {
      _phase = ClientPhase.Uncertain;
      return true;
    }
    if ((_operation == Nv5ConfigOperations.Commit
            || (_operation == Nv5ConfigOperations.Status && _expectedDigest.Length != 0))
        && wire.ReceiptBootNonce != 0
        && (wire.TransactionRevision != _baseRevision + 1 || wire.ReceiptBootNonce != _commitBoot
            || !Same(wire.TransactionDigest, _expectedDigest))) {
      _phase = ClientPhase.Uncertain;
      return true;
    }
    if (wire.State == Nv5ConfigStates.Active
        && (wire.ActiveRevision != _baseRevision + 1
            || !Same(wire.ActiveDigest, _expectedDigest))) {
      _phase = ClientPhase.Uncertain;
      return true;
    }
    if ((wire.State is Nv5ConfigStates.CommittedRestartRequired or Nv5ConfigStates.Active
            or Nv5ConfigStates.CommittedNotActive)
        && _expectedDigest.Length != 0 && wire.Result == Nv5ConfigResults.Ok
        && wire.CommittedKnown != 0 && wire.CommittedRevision == _baseRevision + 1
        && Same(wire.CommittedDigest, _expectedDigest) && wire.ReceiptBootNonce == _commitBoot
        && wire.TransactionRevision == _baseRevision + 1
        && Same(wire.TransactionDigest, _expectedDigest)) {
      _durableConfirmed = true;
    }
    UpdatePhase(wire);
    return true;
  }

  private bool HandleValue(Nv5ConfigValueMessage wire) {
    byte[] rawName = wire.Name ?? new byte[16];
    byte[] data = wire.Data ?? new byte[48];
    int end = Array.IndexOf(rawName, (byte)0);
    if (end < 0) {
      end = rawName.Length;
    }
    string name = Encoding.ASCII.GetString(rawName, 0, end);
    if (!CanonicalName(name) || rawName.Take(end).Any(value => value > 0x7e) || wire.SettingId == 0
        || rawName.Skip(end).Any(value => value != 0) || wire.TotalLength > 95
        || (wire.Offset != 0 && wire.Offset != 48)
        || (wire.Offset == 48 && wire.TotalLength <= 48) || wire.Offset > wire.TotalLength
        || wire.Length != Math.Min(48, wire.TotalLength - wire.Offset)
        || data.Skip(wire.Length).Any(value => value != 0)
        || (wire.ValueType != Nv5ConfigValueTypes.Text
            && (wire.TotalLength != 4 || wire.Offset != 0))) {
      return false;
    }
    if (_headerReady
        && (wire.SettingId != _readHeader.SettingId
            || wire.TotalLength != _readHeader.TotalLength
            || wire.ValueType != _readHeader.ValueType || wire.Flags != _readHeader.Flags
            || !Same(rawName, _readHeader.Name))) {
      return false;
    }
    if (!_headerReady) {
      _readHeader = wire;
      _readBytes = new byte[wire.TotalLength];
      _headerReady = true;
    }
    byte mask = wire.Offset == 0 ? (byte)1 : (byte)2;
    var chunk = data.AsSpan(0, wire.Length);
    if ((_readChunks & mask) != 0
        && !_readBytes.AsSpan(wire.Offset, wire.Length).SequenceEqual(chunk)) {
      _phase = ClientPhase.Error;
      _pending = _outboundReady = false;
      return false;
    }
    chunk.CopyTo(_readBytes.AsSpan(wire.Offset));
    _readChunks |= mask;
    FinishRead();
    return true;
  }

  private bool FinishRead() {
    if (!_readResult || !_headerReady
        || _readChunks != (_readHeader.TotalLength > 48 ? 3 : 1)) {
      return false;
    }
    if (!ValidBytes(_readHeader.ValueType, _readBytes)) {
      _phase = ClientPhase.Error;
      _pending = _outboundReady = _sent = false;
      return false;
    }
    byte[] name = _readHeader.Name ?? [];
    int end = Array.IndexOf(name, (byte)0);
    _completedValue = new Value(
        Encoding.ASCII.GetString(name, 0, end < 0 ? name.Length : end), _readBytes,
        _readHeader.SettingId, _readHeader.Index, _readHeader.ValueType, _readHeader.Flags);
    _valueReady = true;
    _pending = _outboundReady = _sent = false;
    _outbound = null;
    // The completed value now owns these bytes.
    _readBytes = [];
    return true;
  }

  private static bool Same(byte[]? left, byte[]? right) =>
      (left ?? []).AsSpan().SequenceEqual(right ?? []);

  private static byte[] Copy(byte[]? bytes) => bytes == null ? [] : (byte[])bytes.Clone();

  /// <summary>Clears bytes that may hold a secret before dropping them.</summary>
  private static void Wipe(ref byte[] bytes) {
    Array.Clear(bytes);
    bytes = [];
  }

  // The calibration messages carry the same fields as the settings messages, except that a
  // calibration RESULT has no link fingerprints. Each field is copied by name, so a layout change
  // in either dialect cannot silently misplace one.
  private static Nv5CalControlMessage Calibration(Nv5ConfigControlMessage wire) => new() {
    BootNonce = wire.BootNonce,
    ClientNonce = wire.ClientNonce,
    Token = wire.Token,
    ExpectedRevision = wire.ExpectedRevision,
    Sequence = wire.Sequence,
    Index = wire.Index,
    Digest = Copy(wire.Digest),
    Operation = wire.Operation,
    Scope = wire.Scope,
    TargetSystem = wire.TargetSystem,
    TargetComponent = wire.TargetComponent,
    SchemaVersion = wire.SchemaVersion,
  };

  private static Nv5CalItemMessage Calibration(Nv5ConfigItemMessage wire) => new() {
    BootNonce = wire.BootNonce,
    ClientNonce = wire.ClientNonce,
    Token = wire.Token,
    Sequence = wire.Sequence,
    Offset = wire.Offset,
    TotalLength = wire.TotalLength,
    Name = Copy(wire.Name),
    Data = Copy(wire.Data),
    ValueType = wire.ValueType,
    Length = wire.Length,
    TargetSystem = wire.TargetSystem,
    TargetComponent = wire.TargetComponent,
    SchemaVersion = wire.SchemaVersion,
  };

  private static Nv5ConfigResultMessage Settings(Nv5CalResultMessage wire) => new() {
    BootNonce = wire.BootNonce,
    ClientNonce = wire.ClientNonce,
    Token = wire.Token,
    ReceiptBootNonce = wire.ReceiptBootNonce,
    TransactionRevision = wire.TransactionRevision,
    CommittedRevision = wire.CommittedRevision,
    ActiveRevision = wire.ActiveRevision,
    SnapshotRevision = wire.SnapshotRevision,
    Sequence = wire.Sequence,
    NextSequence = wire.NextSequence,
    CatalogId = wire.CatalogId,
    Capabilities = wire.Capabilities,
    LeaseMs = wire.LeaseMs,
    SettingCount = wire.SettingCount,
    ErrorSetting = wire.ErrorSetting,
    CommittedDigest = Copy(wire.CommittedDigest),
    ActiveDigest = Copy(wire.ActiveDigest),
    TransactionDigest = Copy(wire.TransactionDigest),
    SnapshotDigest = Copy(wire.SnapshotDigest),
    ErrorName = Copy(wire.ErrorName),
    Operation = wire.Operation,
    Scope = wire.Scope,
    State = wire.State,
    Result = wire.Result,
    RestartRequired = wire.RestartRequired,
    IncompleteValue = wire.IncompleteValue,
    CommittedKnown = wire.CommittedKnown,
    CandidateRestartRequired = wire.CandidateRestartRequired,
    TargetSystem = wire.TargetSystem,
    TargetComponent = wire.TargetComponent,
    SchemaVersion = wire.SchemaVersion,
  };

  private static Nv5ConfigValueMessage Settings(Nv5CalValueMessage wire) => new() {
    BootNonce = wire.BootNonce,
    ClientNonce = wire.ClientNonce,
    Token = wire.Token,
    Revision = wire.Revision,
    Sequence = wire.Sequence,
    CatalogId = wire.CatalogId,
    Index = wire.Index,
    SettingCount = wire.SettingCount,
    SettingId = wire.SettingId,
    Offset = wire.Offset,
    TotalLength = wire.TotalLength,
    Name = Copy(wire.Name),
    Data = Copy(wire.Data),
    ValueType = wire.ValueType,
    Length = wire.Length,
    Flags = wire.Flags,
    Scope = wire.Scope,
    TargetSystem = wire.TargetSystem,
    TargetComponent = wire.TargetComponent,
    SchemaVersion = wire.SchemaVersion,
  };
}
