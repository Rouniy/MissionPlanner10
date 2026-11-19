using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MissionPlanner;

namespace MissionPlanner.Services;

internal static class NvModemMessageIds {
  internal const uint NvRxStat = 53002;
  internal const uint Nv5LinkStatus = 53010;
  internal const uint Nv5RtspConfig = 53014;
  internal const uint Nv5RtspConfigAck = 53015;
  internal const uint NvModemInfo = 53016;
  internal const uint NvEncryptionKeysSet = 53017;
  internal const uint NvEncryptionKeysAck = 53018;
  internal const uint Nv5Distance = 53020;
  internal const uint Nv5ConfigControl = 53021;
  internal const uint Nv5ConfigItem = 53022;
  internal const uint Nv5ConfigResult = 53023;
  internal const uint Nv5ConfigValue = 53024;
  internal const uint Nv5CalControl = 53025;
  internal const uint Nv5CalItem = 53026;
  internal const uint Nv5CalResult = 53027;
  internal const uint Nv5CalValue = 53028;
  internal const uint Nv5LpsStatus = 53030;
  internal const uint Nv5LpsBeacon = 53031;
  internal const uint Nv5RangingCapture = 53032;
  internal const uint Nv5RangingCaptureData = 53033;
}

internal static class NvModemInfoFlags {
  internal const byte Channel1Active = 1 << 0;
  internal const byte Channel2Active = 1 << 1;
}

internal static class NvModemCapabilities {
  internal const ulong MavlinkParameters = 1UL << 0;
  internal const ulong Rtsp = 1UL << 8;
  internal const ulong AtomicEncryptionKeys = 1UL << 12;
  internal const ulong ConfigurationTransactions = 1UL << 14;
  internal const ulong CalibrationTransactions = 1UL << 15;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 28)]
internal struct NvRxStatMessage {
  internal int BootTime;
  internal float Snr;
  internal float Quality;
  internal uint BytesReceived;
  internal uint MavParsed;
  internal uint Frequency;
  internal uint Bandwidth;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 78)]
internal struct Nv5LinkStatusMessage {
  internal uint TimeBootMs;
  internal uint SampleMs;
  internal uint FrequencyHz;
  internal uint BandwidthHz;
  internal uint TxApplicationBytes;
  internal uint RxApplicationBytes;
  internal uint TxRadioBytes;
  internal uint RxRadioBytes;
  internal uint TxFrames;
  internal uint RxFrames;
  internal uint Errors;
  internal uint DroppedBytes;
  internal uint FecRecovered;
  internal uint HopMissed;
  internal uint SyncLost;
  internal ushort TxQueueBytes;
  internal ushort RxQueueBytes;
  internal short PacketRssiDbmX10;
  internal short PacketSnrDbX10;
  internal short ChannelRssiDbmX10;
  internal byte Channel;
  internal byte RadioChip;
  internal byte Role;
  internal byte Modulation;
  internal byte Flags;
  internal byte Txbuf;
  internal byte LinkQuality;
  internal byte TxState;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 103)]
internal struct Nv5RtspConfigMessage {
  internal uint TransactionId;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte Operation;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)]
  internal byte[] Path;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 9)]
internal struct Nv5RtspConfigAckMessage {
  internal uint TransactionId;
  internal ushort Detail;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte Result;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 53)]
internal struct NvModemInfoMessage {
  internal ulong Capabilities;
  internal uint TimeBootMs;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
  internal byte[] BuildHash;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
  internal byte[] Uid2;

  internal byte SchemaVersion;
  internal byte ModemGeneration;
  internal byte HardwareVersionMajor;
  internal byte HardwareVersionMinor;
  internal byte FirmwareVersionMajor;
  internal byte FirmwareVersionMinor;
  internal byte FirmwareVersionPatch;
  internal byte ProtocolVersion;
  internal byte ProductProfile;
  internal byte RadioCount;
  internal byte Flags;
  internal byte Channel1Role;
  internal byte Channel2Role;
  internal byte Channel1RadioChip;
  internal byte Channel2RadioChip;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 40)]
internal struct NvEncryptionKeysSetMessage {
  internal uint TransactionId;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
  internal byte ChannelMask;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] Channel1Key;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] Channel2Key;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 19)]
internal struct NvEncryptionKeysAckMessage {
  internal uint TransactionId;
  internal uint Channel1Fingerprint;
  internal uint Channel2Fingerprint;
  internal ushort Detail;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
  internal byte ChannelMask;
  internal byte Result;
}

internal static class NvEncryptionKeysResults {
  internal const byte Applied = 0;
}

// NV5 firmware ranging, LPS, native configuration and calibration messages. Fields are in MAVLink
// wire order; fields after an "Extensions" comment are MAVLink 2 extensions that older firmware
// omits, so they decode as zero.

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 97)]
internal struct Nv5DistanceMessage {
  internal ulong TimeBootUs;
  internal ulong CycleUs;
  internal uint CenterHz;
  internal uint HopSpanHz;
  internal uint BoundarySeq;
  internal uint AgeMs;
  internal uint WindowUs;
  internal int DistanceCm;
  internal byte Channel;
  internal byte ValidCount;
  internal byte TimeoutCount;
  internal byte LateCount;
  internal byte Status;
  internal byte Calibration;
  internal byte Flags;

  // Extensions.
  internal ulong BankRevision;
  internal ulong CalibrationEpoch;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] CalibrationId;

  internal float RawMetres;
  internal float CorrectedMetres;
  internal float IndependentVariance;
  internal float PersistentVariance;
  internal byte CalibrationStatus;
  internal byte ReferenceScope;
}

/// <summary>Result validity of <see cref="Nv5DistanceMessage.Status"/>. A modem that answers
/// ranging requests reports <see cref="Waiting"/> for good.</summary>
internal static class Nv5DistanceStatuses {
  internal const byte Disabled = 0;
  internal const byte Waiting = 1;
  internal const byte Valid = 2;
  internal const byte Timeout = 3;
  internal const byte Late = 4;
  internal const byte HardwareFault = 5;
  internal const byte Stale = 6;
  internal const byte Suppressed = 7;
}

/// <summary>Summary calibration state of <see cref="Nv5DistanceMessage.Calibration"/>.</summary>
internal static class Nv5DistanceCalibrations {
  internal const byte Unknown = 0;
  internal const byte Uncalibrated = 1;
  internal const byte Calibrated = 2;
}

internal static class Nv5DistanceFlags {
  internal const byte Restored = 1;
  internal const byte RfAvailable = 2;
  internal const byte WindowActive = 4;
}

/// <summary>Live calibration applicability of
/// <see cref="Nv5DistanceMessage.CalibrationStatus"/>.</summary>
internal static class Nv5RangingCalibrationStatuses {
  internal const byte NotEvaluated = 0;
  internal const byte Applied = 1;
  internal const byte NoRecord = 2;
  internal const byte IdentityUnknown = 3;
  internal const byte PeerMismatch = 4;
  internal const byte ProfileMismatch = 5;
  internal const byte AssemblyMismatch = 6;
  internal const byte WrongReferenceScope = 7;
  internal const byte Unchecked = 8;
  internal const byte OutsideDomain = 9;
  internal const byte Incomplete = 10;
  internal const byte InvalidModel = 11;
  internal const byte InvalidResult = 12;
  internal const byte UnsupportedUncertainty = 13;
}

/// <summary>Correction reference plane of <see cref="Nv5DistanceMessage.ReferenceScope"/>.</summary>
internal static class Nv5RangingReferenceScopes {
  internal const byte RfConnectorPair = 0;
  internal const byte WholeAssembly = 1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 75)]
internal struct Nv5ConfigControlMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal ulong ExpectedRevision;
  internal uint Sequence;
  internal ushort Index;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] Digest;

  internal byte Operation;
  internal byte Scope;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 101)]
internal struct Nv5ConfigItemMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal uint Sequence;
  internal ushort Offset;
  internal ushort TotalLength;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] Name;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
  internal byte[] Data;

  internal byte ValueType;
  internal byte Length;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 251)]
internal struct Nv5ConfigResultMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal ulong ReceiptBootNonce;
  internal ulong TransactionRevision;
  internal ulong CommittedRevision;
  internal ulong ActiveRevision;
  internal ulong SnapshotRevision;
  internal uint Sequence;
  internal uint NextSequence;
  internal uint CatalogId;
  internal uint Capabilities;
  internal uint LeaseMs;
  internal uint Channel1LinkHash;
  internal uint Channel2LinkHash;
  internal ushort SettingCount;
  internal ushort ErrorSetting;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] CommittedDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] ActiveDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] TransactionDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] SnapshotDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] ErrorName;

  internal byte Operation;
  internal byte Scope;
  internal byte State;
  internal byte Result;
  internal byte RestartRequired;
  internal byte IncompleteValue;
  internal byte CommittedKnown;
  internal byte CandidateRestartRequired;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 121)]
internal struct Nv5ConfigValueMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal ulong Revision;
  internal uint Sequence;
  internal uint CatalogId;
  internal ushort Index;
  internal ushort SettingCount;
  internal ushort SettingId;
  internal ushort Offset;
  internal ushort TotalLength;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] Name;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
  internal byte[] Data;

  internal byte ValueType;
  internal byte Length;
  internal byte Flags;
  internal byte Scope;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 75)]
internal struct Nv5CalControlMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal ulong ExpectedRevision;
  internal uint Sequence;
  internal ushort Index;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] Digest;

  internal byte Operation;
  internal byte Scope;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 101)]
internal struct Nv5CalItemMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal uint Sequence;
  internal ushort Offset;
  internal ushort TotalLength;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] Name;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
  internal byte[] Data;

  internal byte ValueType;
  internal byte Length;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 243)]
internal struct Nv5CalResultMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal ulong ReceiptBootNonce;
  internal ulong TransactionRevision;
  internal ulong CommittedRevision;
  internal ulong ActiveRevision;
  internal ulong SnapshotRevision;
  internal uint Sequence;
  internal uint NextSequence;
  internal uint CatalogId;
  internal uint Capabilities;
  internal uint LeaseMs;
  internal ushort SettingCount;
  internal ushort ErrorSetting;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] CommittedDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] ActiveDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] TransactionDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
  internal byte[] SnapshotDigest;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] ErrorName;

  internal byte Operation;
  internal byte Scope;
  internal byte State;
  internal byte Result;
  internal byte RestartRequired;
  internal byte IncompleteValue;
  internal byte CommittedKnown;
  internal byte CandidateRestartRequired;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 121)]
internal struct Nv5CalValueMessage {
  internal ulong BootNonce;
  internal ulong ClientNonce;
  internal ulong Token;
  internal ulong Revision;
  internal uint Sequence;
  internal uint CatalogId;
  internal ushort Index;
  internal ushort SettingCount;
  internal ushort SettingId;
  internal ushort Offset;
  internal ushort TotalLength;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] Name;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
  internal byte[] Data;

  internal byte ValueType;
  internal byte Length;
  internal byte Flags;
  internal byte Scope;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 167)]
internal struct Nv5LpsStatusMessage {
  internal ulong BootNonce;
  internal ulong RuntimeEpoch;
  internal ulong SnapshotSeq;
  internal ulong SnapshotTimeUs;
  internal ulong DatumId;
  internal ulong Challenge;
  internal ulong CalibrationRevision;
  internal ulong PoseTimeUs;
  internal ulong LastAttemptBurst;
  internal uint FieldGeneration;
  internal uint PoseValidForUs;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
  internal float[] PositionEnu;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
  internal float[] VelocityEn;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
  internal float[] CovarianceEnu;

  internal uint AcceptedBursts;
  internal uint RejectedBursts;
  internal uint OutputDrops;
  internal uint DiscardedResults;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
  internal byte[] Uid;

  internal byte LastAttemptStatus;
  internal byte Role;
  internal byte Channel;
  internal byte Status;
  internal byte AltitudeStatus;
  internal byte OwnerFlags;
  internal byte BeaconMask;

  // Extensions.
  internal byte CalibrationStatus;
  internal byte CalibratedMask;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 167)]
internal struct Nv5LpsBeaconMessage {
  internal ulong BootNonce;
  internal ulong RuntimeEpoch;
  internal ulong SnapshotSeq;
  internal ulong SnapshotTimeUs;
  internal ulong DatumId;
  internal ulong Burst;
  internal ulong Observation;
  internal ulong LastAttemptBurst;
  internal ulong AcquiredAtUs;
  internal ulong CompletedAtUs;
  internal uint FieldGeneration;
  internal uint BeaconId;
  internal uint Address;
  internal float RawMetres;
  internal float Metres;
  internal float Variance;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 18)]
  internal byte[] Uid;

  internal byte Index;
  internal byte Flags;
  internal byte Exchanges;
  internal byte AttemptStatus;
  internal byte SolverResult;

  // Extensions.
  internal ulong BankRevision;
  internal ulong CalibrationBinding;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
  internal byte[] CalibrationId;

  internal float PersistentVariance;
  internal byte Eligible;
  internal byte Exclusion;
  internal byte CalibrationStatus;
  internal byte ReferenceScope;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 25)]
internal struct Nv5RangingCaptureMessage {
  internal ulong Token;
  internal uint Sequence;
  internal uint PeerId;
  internal ushort Offset;
  internal byte Operation;
  internal byte Channel;
  internal byte Kind;
  internal byte Scope;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 229)]
internal struct Nv5RangingCaptureDataMessage {
  internal ulong BootNonce;
  internal ulong Token;
  internal uint Sequence;
  internal ushort Offset;
  internal ushort TotalLength;

  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 200)]
  internal byte[] Data;

  internal byte Length;
  internal byte Status;
  internal byte TargetSystem;
  internal byte TargetComponent;
  internal byte SchemaVersion;
}

/// <summary>
/// Registers the NV modem messages used by the NV Modem page with Mission Planner's generated
/// MAVLink parser. The upstream MAVLink table is public and mutable, which lets the port add a private
/// dialect without editing the pinned Mission Planner submodule.
/// </summary>
internal static class NvModemMavlinkDialect {
  private static readonly object Sync = new();

  internal static void Register() {
    lock (Sync) {
      var existing = MAVLink.MAVLINK_MESSAGE_INFOS;
      var additions = MessageInfos()
          .Where(candidate => existing.All(info => info.msgid != candidate.msgid))
          .ToArray();
      if (additions.Length != 0) {
        MAVLink.MAVLINK_MESSAGE_INFOS = [.. existing, .. additions];
      }
    }
  }

  private static MAVLink.message_info[] MessageInfos() => [
    new(NvModemMessageIds.NvRxStat, "NV_RX_STAT", 49, 28, 28,
        typeof(NvRxStatMessage)),
    new(NvModemMessageIds.Nv5LinkStatus, "NV5_LINK_STATUS", 165, 77, 78,
        typeof(Nv5LinkStatusMessage)),
    new(NvModemMessageIds.Nv5RtspConfig, "NV5_RTSP_CONFIG", 127, 103, 103,
        typeof(Nv5RtspConfigMessage)),
    new(NvModemMessageIds.Nv5RtspConfigAck, "NV5_RTSP_CONFIG_ACK", 193, 9, 9,
        typeof(Nv5RtspConfigAckMessage)),
    new(NvModemMessageIds.NvModemInfo, "NV_MODEM_INFO", 207, 53, 53,
        typeof(NvModemInfoMessage)),
    new(NvModemMessageIds.NvEncryptionKeysSet, "NV_ENCRYPTION_KEYS_SET", 129, 40, 40,
        typeof(NvEncryptionKeysSetMessage)),
    new(NvModemMessageIds.NvEncryptionKeysAck, "NV_ENCRYPTION_KEYS_ACK", 61, 19, 19,
        typeof(NvEncryptionKeysAckMessage)),
    new(NvModemMessageIds.Nv5Distance, "NV5_DISTANCE", 90, 47, 97,
        typeof(Nv5DistanceMessage)),
    new(NvModemMessageIds.Nv5ConfigControl, "NV5_CONFIG_CONTROL", 193, 75, 75,
        typeof(Nv5ConfigControlMessage)),
    new(NvModemMessageIds.Nv5ConfigItem, "NV5_CONFIG_ITEM", 75, 101, 101,
        typeof(Nv5ConfigItemMessage)),
    new(NvModemMessageIds.Nv5ConfigResult, "NV5_CONFIG_RESULT", 0, 251, 251,
        typeof(Nv5ConfigResultMessage)),
    new(NvModemMessageIds.Nv5ConfigValue, "NV5_CONFIG_VALUE", 241, 121, 121,
        typeof(Nv5ConfigValueMessage)),
    new(NvModemMessageIds.Nv5CalControl, "NV5_CAL_CONTROL", 233, 75, 75,
        typeof(Nv5CalControlMessage)),
    new(NvModemMessageIds.Nv5CalItem, "NV5_CAL_ITEM", 219, 101, 101,
        typeof(Nv5CalItemMessage)),
    new(NvModemMessageIds.Nv5CalResult, "NV5_CAL_RESULT", 126, 243, 243,
        typeof(Nv5CalResultMessage)),
    new(NvModemMessageIds.Nv5CalValue, "NV5_CAL_VALUE", 73, 121, 121,
        typeof(Nv5CalValueMessage)),
    new(NvModemMessageIds.Nv5LpsStatus, "NV5_LPS_STATUS", 199, 165, 167,
        typeof(Nv5LpsStatusMessage)),
    new(NvModemMessageIds.Nv5LpsBeacon, "NV5_LPS_BEACON", 108, 127, 167,
        typeof(Nv5LpsBeaconMessage)),
    new(NvModemMessageIds.Nv5RangingCapture, "NV5_RANGING_CAPTURE", 181, 25, 25,
        typeof(Nv5RangingCaptureMessage)),
    new(NvModemMessageIds.Nv5RangingCaptureData, "NV5_RANGING_CAPTURE_DATA", 53, 229, 229,
        typeof(Nv5RangingCaptureDataMessage)),
  ];
}

internal static class NvModemParameterCodec {
  internal static string Name(byte[]? bytes) {
    if (bytes == null) {
      return "";
    }
    int length = Array.IndexOf(bytes, (byte)0);
    if (length < 0) {
      length = bytes.Length;
    }
    return Encoding.ASCII.GetString(bytes, 0, length);
  }

  internal static byte[] NameBytes(string name) {
    byte[] target = new byte[16];
    byte[] source = Encoding.ASCII.GetBytes(name);
    Array.Copy(source, target, Math.Min(source.Length, target.Length));
    return target;
  }

  internal static double Decode(float wireValue, byte type) {
    int bits = BitConverter.SingleToInt32Bits(wireValue);
    return (MAVLink.MAV_PARAM_TYPE)type switch {
      MAVLink.MAV_PARAM_TYPE.UINT8 => (double)(byte)bits,
      MAVLink.MAV_PARAM_TYPE.INT8 => (double)(sbyte)bits,
      MAVLink.MAV_PARAM_TYPE.UINT16 => (double)(ushort)bits,
      MAVLink.MAV_PARAM_TYPE.INT16 => (double)(short)bits,
      MAVLink.MAV_PARAM_TYPE.UINT32 => unchecked((double)(uint)bits),
      MAVLink.MAV_PARAM_TYPE.INT32 => (double)bits,
      _ => (double)wireValue,
    };
  }

  internal static float Encode(double value, byte type) {
    int bits = (MAVLink.MAV_PARAM_TYPE)type switch {
      MAVLink.MAV_PARAM_TYPE.UINT8 => checked((byte)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.INT8 => checked((sbyte)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.UINT16 => checked((ushort)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.INT16 => checked((short)Math.Round(value)),
      MAVLink.MAV_PARAM_TYPE.UINT32 => unchecked((int)checked((uint)Math.Round(value))),
      MAVLink.MAV_PARAM_TYPE.INT32 => checked((int)Math.Round(value)),
      _ => 0,
    };
    return IsInteger(type) ? BitConverter.Int32BitsToSingle(bits) : (float)value;
  }

  internal static bool IsInteger(byte type) =>
      type >= (byte)MAVLink.MAV_PARAM_TYPE.UINT8
      && type <= (byte)MAVLink.MAV_PARAM_TYPE.INT32;

  internal static string Display(double value, byte type) => IsInteger(type)
      ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
      : value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);

  internal static bool TryParse(string text, byte type, out double value) {
    bool parsed = double.TryParse(text,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out value);
    return parsed && IsValid(value, type);
  }

  internal static bool IsValid(double value, byte type) {
    if (!double.IsFinite(value)) {
      return false;
    }
    if (!IsInteger(type)) {
      return true;
    }
    if (value != Math.Floor(value)) {
      return false;
    }
    return (MAVLink.MAV_PARAM_TYPE)type switch {
      MAVLink.MAV_PARAM_TYPE.UINT8 => value is >= byte.MinValue and <= byte.MaxValue,
      MAVLink.MAV_PARAM_TYPE.INT8 => value is >= sbyte.MinValue and <= sbyte.MaxValue,
      MAVLink.MAV_PARAM_TYPE.UINT16 => value is >= ushort.MinValue and <= ushort.MaxValue,
      MAVLink.MAV_PARAM_TYPE.INT16 => value is >= short.MinValue and <= short.MaxValue,
      MAVLink.MAV_PARAM_TYPE.UINT32 => value is >= uint.MinValue and <= uint.MaxValue,
      MAVLink.MAV_PARAM_TYPE.INT32 => value is >= int.MinValue and <= int.MaxValue,
      _ => true,
    };
  }

  internal static bool NearlyEqual(double left, double right) {
    double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
    return Math.Abs(left - right) <= scale * 1e-7;
  }

  internal static bool ValuesEqual(double left, double right, byte type) =>
      IsInteger(type) ? left == right : NearlyEqual(left, right);
}

internal sealed record NvModemLink(MAVLinkInterface Link, string Name);

internal readonly record struct NvModemEndpoint(byte SystemId, byte ComponentId);

internal readonly record struct NvModemCachedParameter(
    byte SystemId,
    byte ComponentId,
    string Name,
    double Value,
    byte Type,
    int ReportedCount,
    uint RawValueBits);

internal interface INvModemMavlinkTransport : IDisposable {
  event Action<NvModemLink, MAVLink.MAVLinkMessage>? PacketReceived;
  event Action? LinksChanged;

  IReadOnlyList<NvModemLink> Snapshot();
  IReadOnlyList<NvModemEndpoint> KnownEndpoints(NvModemLink source);
  IReadOnlyList<MAVLink.MAVLinkMessage> CachedDiscoveryPackets(NvModemLink source);
  IReadOnlyList<NvModemCachedParameter> CachedParameters(NvModemLink source);
  bool Send(NvModemLink source, object packet, byte systemId, byte componentId);
}

internal sealed class NvModemMavlinkTransport : INvModemMavlinkTransport {
  private readonly object _sync = new();
  private readonly Dictionary<MAVLinkInterface, NvModemLink> _links =
      new(ReferenceEqualityComparer.Instance);
  private bool _disposed;

  internal NvModemMavlinkTransport() {
    NvModemMavlinkDialect.Register();
    AppState.Connections.Changed += OnConnectionsChanged;
    AppState.ConnectionChanged += OnConnectionsChanged;
    RefreshLinks();
  }

  public event Action<NvModemLink, MAVLink.MAVLinkMessage>? PacketReceived;
  public event Action? LinksChanged;

  public IReadOnlyList<NvModemLink> Snapshot() {
    // Opening the already-active primary connection does not necessarily change the connection
    // manager's selection. Refresh here so the Discover button cannot miss that shared link.
    RefreshLinks();
    lock (_sync) {
      return _links.Values.ToArray();
    }
  }

  public IReadOnlyList<NvModemEndpoint> KnownEndpoints(NvModemLink source) {
    try {
      return source.Link.MAVlist.ToArray()
          .Where(mav => mav.lastvalidpacket > DateTime.MinValue
              && (mav.sysid != 0 || mav.compid != 0))
          .Select(mav => new NvModemEndpoint(mav.sysid, mav.compid))
          .Distinct()
          .ToArray();
    } catch {
      return [];
    }
  }

  public IReadOnlyList<MAVLink.MAVLinkMessage> CachedDiscoveryPackets(NvModemLink source) {
    try {
      uint[] messageIds = [
        NvModemMessageIds.NvModemInfo,
        NvModemMessageIds.Nv5LinkStatus,
        NvModemMessageIds.Nv5RtspConfig,
        NvModemMessageIds.Nv5RtspConfigAck,
        NvModemMessageIds.NvEncryptionKeysAck,
        NvModemMessageIds.NvRxStat,
        (uint)MAVLink.MAVLINK_MSG_ID.UAVCAN_NODE_INFO,
      ];
      var packets = new List<MAVLink.MAVLinkMessage>();
      foreach (MAVState mav in source.Link.MAVlist.ToArray()) {
        foreach (uint messageId in messageIds) {
          MAVLink.MAVLinkMessage? packet = mav.getPacketLast(messageId);
          if (packet != null) {
            packets.Add(packet);
          }
        }
      }
      return packets;
    } catch {
      return [];
    }
  }

  public IReadOnlyList<NvModemCachedParameter> CachedParameters(NvModemLink source) {
    try {
      var parameters = new List<NvModemCachedParameter>();
      foreach (MAVState mav in source.Link.MAVlist.ToArray()) {
        MAVLink.MAVLinkParam[] snapshot = mav.param.ToArray();
        int reported = Math.Max(mav.param.TotalReported, snapshot.Length);
        foreach (MAVLink.MAVLinkParam parameter in snapshot) {
          if (string.IsNullOrWhiteSpace(parameter.Name)) {
            continue;
          }
          parameters.Add(new NvModemCachedParameter(
              mav.sysid,
              mav.compid,
              parameter.Name,
              parameter.Value,
              (byte)parameter.Type,
              reported,
              parameter.data is { Length: >= sizeof(uint) }
                  ? BitConverter.ToUInt32(parameter.data, 0)
                  : unchecked((uint)BitConverter.SingleToInt32Bits(
                      NvModemParameterCodec.Encode(parameter.Value, (byte)parameter.Type)))));
        }
      }
      return parameters;
    } catch {
      return [];
    }
  }

  public bool Send(NvModemLink source, object packet, byte systemId, byte componentId) {
    try {
      if (source.Link.BaseStream?.IsOpen != true) {
        return false;
      }
      source.Link.sendPacket(packet, systemId, componentId);
      return true;
    } catch {
      return false;
    }
  }

  private void OnConnectionsChanged() {
    RefreshLinks();
    LinksChanged?.Invoke();
  }

  private void RefreshLinks() {
    MissionPlanner.Services.MavLinkConnection[] connections =
        [.. AppState.Connections.Snapshot().Where(connection => connection.IsOpen)];
    lock (_sync) {
      if (_disposed) {
        return;
      }
      foreach (MAVLinkInterface removed in _links.Keys
                   .Where(link => connections.All(item => !ReferenceEquals(item.Link, link)))
                   .ToArray()) {
        removed.OnPacketReceived -= OnPacketReceived;
        _links.Remove(removed);
      }
      foreach (var connection in connections) {
        if (_links.ContainsKey(connection.Link)) {
          continue;
        }
        var source = new NvModemLink(connection.Link, connection.Endpoint);
        _links.Add(connection.Link, source);
        connection.Link.OnPacketReceived += OnPacketReceived;
      }
    }
  }

  private void OnPacketReceived(object? sender, MAVLink.MAVLinkMessage packet) {
    NvModemLink? source;
    lock (_sync) {
      source = sender is MAVLinkInterface link && _links.TryGetValue(link, out var found)
          ? found : null;
    }
    if (source != null) {
      PacketReceived?.Invoke(source, packet);
    }
  }

  public void Dispose() {
    lock (_sync) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      AppState.Connections.Changed -= OnConnectionsChanged;
      AppState.ConnectionChanged -= OnConnectionsChanged;
      foreach (MAVLinkInterface link in _links.Keys) {
        link.OnPacketReceived -= OnPacketReceived;
      }
      _links.Clear();
    }
  }
}
