using System.Globalization;
using MissionPlanner.Services;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

/// <summary>A surveyed field, LPS reports and LPS modems for the tests.</summary>
internal static class Nv5LpsTestData {
  internal static Target Target() => new(Enumerable.Repeat((byte)'U', 18).ToArray(), "route", 1,
      NvModemCapabilities.ConfigurationTransactions, 10, 11, true);

  /// <summary>Every setting of a field, written out here rather than taken from the code under
  /// test.</summary>
  internal static readonly string[] Keys = [.. new[] { "LPS_FIELD_GEN", "LPS_DATUM_LO",
      "LPS_DATUM_HI", "LPS_LAT_E7", "LPS_LON_E7", "LPS_HAE_MM", "LPS_MSL_MM", "LPS_BCOUNT",
      "LPS_RNG_BW_HZ", "LPS_RNG_SF", "LPS_RNG_EXT", "LPS_RSP_SYM", "LPS_RV_HZ", "LPS_RV_BW_HZ",
      "LPS_RV_SF" }, .. Enumerable.Range(1, 4).SelectMany(row => new[] { "ID", "ADDR", "E_MM",
      "N_MM", "U_MM", "CEE", "CEN", "CEU", "CNN", "CNU", "CUU", "BVAR" }
          .Select(suffix => $"LPS_B{row}_{suffix}"))];

  /// <summary>A three-beacon survey at the edges of the native ranges.</summary>
  internal static SortedDictionary<string, string> Field() {
    var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (string key in Keys) {
      values[key] = "0";
    }
    values["LPS_FIELD_GEN"] = "4294967295";
    values["LPS_BCOUNT"] = "3";
    values["LPS_DATUM_LO"] = "4294967295";
    values["LPS_DATUM_HI"] = "4294967294";
    values["LPS_RNG_BW_HZ"] = values["LPS_RV_BW_HZ"] = "1000000";
    values["LPS_RNG_SF"] = values["LPS_RV_SF"] = "8";
    values["LPS_RSP_SYM"] = "15";
    values["LPS_B1_ID"] = "4294967295";
    values["LPS_B2_ID"] = "4294967294";
    values["LPS_B3_ID"] = "4294967293";
    values["LPS_B1_ADDR"] = "4294967295";
    values["LPS_B2_ADDR"] = "4294967294";
    values["LPS_B3_ADDR"] = "4294967293";
    values["LPS_B1_E_MM"] = "-2147483648";
    values["LPS_B2_N_MM"] = "2147483647";
    values["LPS_B1_CEE"] = "9.99999994E-09";
    return values;
  }

  internal static SortedDictionary<string, string> Copy(IReadOnlyDictionary<string, string> values) =>
      new(values.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);

  /// <summary>A rover's valid fix from raw ranges to one beacon, captured at
  /// <paramref name="captured"/> on the modem's clock.</summary>
  internal static Nv5LpsStatusMessage Status(ulong challenge = 0, ulong sequence = 1,
      ulong captured = 1000000, char uid = 'U') => new() {
        Uid = Enumerable.Repeat((byte)uid, 18).ToArray(),
        BootNonce = 123,
        RuntimeEpoch = 7,
        SnapshotSeq = sequence,
        SnapshotTimeUs = captured,
        FieldGeneration = 4,
        DatumId = ulong.MaxValue,
        Challenge = challenge,
        Role = Nv5LpsRoles.Rover,
        Channel = 2,
        Status = Nv5LpsStatuses.Valid,
        CalibrationRevision = 7,
        CalibrationStatus = Nv5RangingCalibrationStatuses.Applied,
        PoseTimeUs = captured,
        PoseValidForUs = 200000,
        // Raw ranges by default: a calibrated row carries its correction, which these rows do not.
        OwnerFlags = Nv5LpsOwnerFlags.Bound,
        BeaconMask = 1,
        CalibratedMask = 0,
        PositionEnu = new float[3],
        VelocityEn = new float[2],
        CovarianceEnu = [1, 0, 0, 1, 0, 1],
      };

  /// <summary>An empty row 1 of exactly this capture.</summary>
  internal static Nv5LpsBeaconMessage Row(Nv5LpsStatusMessage status) => new() {
    Uid = (byte[])status.Uid.Clone(),
    BootNonce = status.BootNonce,
    RuntimeEpoch = status.RuntimeEpoch,
    SnapshotSeq = status.SnapshotSeq,
    SnapshotTimeUs = status.SnapshotTimeUs,
    FieldGeneration = status.FieldGeneration,
    DatumId = status.DatumId,
    CalibrationId = new byte[16],
  };

  internal static MAVLink.MAVLinkMessage Packet(Nv5LpsStatusMessage value, byte system = 10,
      byte component = 11) => Packet(NvModemMessageIds.Nv5LpsStatus, value, system, component);

  internal static MAVLink.MAVLinkMessage Packet(Nv5LpsBeaconMessage value, byte system = 10,
      byte component = 11) => Packet(NvModemMessageIds.Nv5LpsBeacon, value, system, component);

  /// <summary>The 64-bit nonce of a freshness challenge.</summary>
  internal static ulong Nonce(MAVLink.mavlink_command_long_t command) =>
      (ulong)command.param2 | (ulong)command.param3 << 24 | (ulong)command.param4 << 48;

  /// <summary>A modem holding <see cref="Field"/> with an LPS role on radio 2.</summary>
  internal static Nv5FakeModem Modem(int index, byte role, uint beaconId,
      bool reservation = false) {
    var modem = new Nv5FakeModem {
      Peer = new Target(Enumerable.Repeat((byte)('U' + index), 18).ToArray(), $"route-{index}", 1,
          NvModemCapabilities.ConfigurationTransactions, 10, (byte)(11 + index), true),
      Settings = Nv5FakeModem.Catalogue(transmitsOnRadio1: index == 0),
    };
    AddLps(modem, role, beaconId, reservation);
    return modem;
  }

  /// <summary>Gives a modem the LPS settings of firmware with LPS: <see cref="Field"/>, and a
  /// role on radio 2, which keeps its data link only with a reservation.</summary>
  internal static void AddLps(Nv5FakeModem modem, byte role, uint beaconId,
      bool reservation = false) {
    foreach ((string key, string text) in Field()) {
      byte type = key.StartsWith("LPS_B", StringComparison.Ordinal)
          && (key.EndsWith("_BVAR", StringComparison.Ordinal) || key[^4] == '_' && key[^3] == 'C')
          ? Nv5ConfigValueTypes.Float32
          : key.EndsWith("_MM", StringComparison.Ordinal) || key.EndsWith("_E7", StringComparison.Ordinal)
              ? Nv5ConfigValueTypes.Int32
              : Nv5ConfigValueTypes.UInt32;
      uint raw = type switch {
        Nv5ConfigValueTypes.Float32 => BitConverter.SingleToUInt32Bits(
            float.Parse(text, CultureInfo.InvariantCulture)),
        Nv5ConfigValueTypes.Int32 => unchecked((uint)int.Parse(text, CultureInfo.InvariantCulture)),
        _ => uint.Parse(text, CultureInfo.InvariantCulture),
      };
      modem.Settings.Add(new Nv5FakeModem.Setting(key, raw, type));
    }
    modem.Settings.Add(new("LPS_ROLE", role));
    modem.Settings.Add(new("LPS_RADIO", 2));
    modem.Settings.Add(new("LPS_FC_SYS", 1));
    modem.Settings.Add(new("LPS_FC_COMP", 1));
    modem.Settings.Add(new("LPS_Z_MM", 0, Nv5ConfigValueTypes.Int32));
    modem.Settings.Add(new("LPS_BEACON", beaconId));
    modem.Settings.Add(new("CH2_RSV_EN", reservation ? 1u : 0u));
  }

  private static MAVLink.MAVLinkMessage Packet(uint id, object value, byte system,
      byte component) {
    NvModemMavlinkDialect.Register();
    byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
        (MAVLink.MAVLINK_MSG_ID)id, value, false, system, component);
    return new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes))!;
  }
}
