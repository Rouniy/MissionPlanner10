using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using MissionPlanner.Views.Setup;

namespace MissionPlanner.Tests;

public partial class NvModemTests {
  [Fact]
  public void Registers_exact_nv_modem_message_layouts_and_crc_extras() {
    NvModemMavlinkDialect.Register();

    Assert.Equal(28, Marshal.SizeOf<NvRxStatMessage>());
    Assert.Equal(78, Marshal.SizeOf<Nv5LinkStatusMessage>());
    Assert.Equal(103, Marshal.SizeOf<Nv5RtspConfigMessage>());
    Assert.Equal(9, Marshal.SizeOf<Nv5RtspConfigAckMessage>());
    Assert.Equal(53, Marshal.SizeOf<NvModemInfoMessage>());
    Assert.Equal(40, Marshal.SizeOf<NvEncryptionKeysSetMessage>());
    Assert.Equal(19, Marshal.SizeOf<NvEncryptionKeysAckMessage>());
    AssertMessage(NvModemMessageIds.NvRxStat, 49, 28, 28, typeof(NvRxStatMessage));
    AssertMessage(NvModemMessageIds.Nv5LinkStatus, 165, 77, 78,
        typeof(Nv5LinkStatusMessage));
    AssertMessage(NvModemMessageIds.Nv5RtspConfig, 127, 103, 103,
        typeof(Nv5RtspConfigMessage));
    AssertMessage(NvModemMessageIds.Nv5RtspConfigAck, 193, 9, 9,
        typeof(Nv5RtspConfigAckMessage));
    AssertMessage(NvModemMessageIds.NvModemInfo, 207, 53, 53,
        typeof(NvModemInfoMessage));
    AssertMessage(NvModemMessageIds.NvEncryptionKeysSet, 129, 40, 40,
        typeof(NvEncryptionKeysSetMessage));
    AssertMessage(NvModemMessageIds.NvEncryptionKeysAck, 61, 19, 19,
        typeof(NvEncryptionKeysAckMessage));
  }

  [Fact]
  public void Registers_nv5_ranging_lps_configuration_and_calibration_messages() {
    NvModemMavlinkDialect.Register();

    // CRC extras and lengths of the NV5 firmware's message definitions.
    AssertLayout(NvModemMessageIds.Nv5Distance, 90, 47, 97, typeof(Nv5DistanceMessage));
    AssertLayout(NvModemMessageIds.Nv5ConfigControl, 193, 75, 75,
        typeof(Nv5ConfigControlMessage));
    AssertLayout(NvModemMessageIds.Nv5ConfigItem, 75, 101, 101, typeof(Nv5ConfigItemMessage));
    AssertLayout(NvModemMessageIds.Nv5ConfigResult, 0, 251, 251,
        typeof(Nv5ConfigResultMessage));
    AssertLayout(NvModemMessageIds.Nv5ConfigValue, 241, 121, 121,
        typeof(Nv5ConfigValueMessage));
    AssertLayout(NvModemMessageIds.Nv5CalControl, 233, 75, 75, typeof(Nv5CalControlMessage));
    AssertLayout(NvModemMessageIds.Nv5CalItem, 219, 101, 101, typeof(Nv5CalItemMessage));
    AssertLayout(NvModemMessageIds.Nv5CalResult, 126, 243, 243, typeof(Nv5CalResultMessage));
    AssertLayout(NvModemMessageIds.Nv5CalValue, 73, 121, 121, typeof(Nv5CalValueMessage));
    AssertLayout(NvModemMessageIds.Nv5LpsStatus, 199, 165, 167, typeof(Nv5LpsStatusMessage));
    AssertLayout(NvModemMessageIds.Nv5LpsBeacon, 108, 127, 167, typeof(Nv5LpsBeaconMessage));
    AssertLayout(NvModemMessageIds.Nv5RangingCapture, 181, 25, 25,
        typeof(Nv5RangingCaptureMessage));
    AssertLayout(NvModemMessageIds.Nv5RangingCaptureData, 53, 229, 229,
        typeof(Nv5RangingCaptureDataMessage));
    Assert.Equal(16384UL, NvModemCapabilities.ConfigurationTransactions);
    Assert.Equal(32768UL, NvModemCapabilities.CalibrationTransactions);
  }

  [Fact]
  public void Every_nv_modem_struct_reproduces_its_wire_crc_extra() {
    NvModemMavlinkDialect.Register();
    uint[] ids = [
      NvModemMessageIds.NvRxStat, NvModemMessageIds.Nv5LinkStatus,
      NvModemMessageIds.Nv5RtspConfig, NvModemMessageIds.Nv5RtspConfigAck,
      NvModemMessageIds.NvModemInfo, NvModemMessageIds.NvEncryptionKeysSet,
      NvModemMessageIds.NvEncryptionKeysAck, NvModemMessageIds.Nv5Distance,
      NvModemMessageIds.Nv5ConfigControl, NvModemMessageIds.Nv5ConfigItem,
      NvModemMessageIds.Nv5ConfigResult, NvModemMessageIds.Nv5ConfigValue,
      NvModemMessageIds.Nv5CalControl, NvModemMessageIds.Nv5CalItem,
      NvModemMessageIds.Nv5CalResult, NvModemMessageIds.Nv5CalValue,
      NvModemMessageIds.Nv5LpsStatus, NvModemMessageIds.Nv5LpsBeacon,
      NvModemMessageIds.Nv5RangingCapture, NvModemMessageIds.Nv5RangingCaptureData,
    ];

    foreach (uint id in ids) {
      MAVLink.message_info info = Assert.Single(MAVLink.MAVLINK_MESSAGE_INFOS,
          candidate => candidate.msgid == id);
      Assert.True(WireCrcExtra(info) == info.crc,
          $"{info.name}: struct fields give CRC extra {WireCrcExtra(info)}, wire uses {info.crc}");
    }
  }

  [Fact]
  public void Decodes_distance_from_firmware_without_calibration_extensions() {
    var older = new Nv5DistanceMessage {
      TimeBootUs = 12_345_678,
      CycleUs = 1_000_000,
      CenterHz = 2_440_000_000,
      AgeMs = 120,
      WindowUs = 80_000,
      DistanceCm = -42,
      Channel = 2,
      ValidCount = 1,
      Status = 2,
      Calibration = 1,
      Flags = 0x02,
      CalibrationId = new byte[16],
    };

    MAVLink.MAVLinkMessage packet = Packet(
        NvModemMessageIds.Nv5Distance, older, systemId: 10, componentId: 11);
    Nv5DistanceMessage actual = packet.ToStructure<Nv5DistanceMessage>();

    Assert.True(packet.payloadlength <= 47, $"payload {packet.payloadlength} kept extensions");
    Assert.Equal(-42, actual.DistanceCm);
    Assert.Equal(2_440_000_000u, actual.CenterHz);
    Assert.Equal(2, actual.Channel);
    Assert.Equal(2, actual.Status);
    Assert.Equal(0UL, actual.BankRevision);
    Assert.Equal(0f, actual.RawMetres);
    Assert.Equal(0, actual.CalibrationStatus);

    var current = older;
    current.CalibrationId = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
    current.BankRevision = 7;
    current.RawMetres = -0.42f;
    current.CorrectedMetres = float.NaN;
    current.CalibrationStatus = 2;
    current.ReferenceScope = 1;

    actual = Packet(NvModemMessageIds.Nv5Distance, current, systemId: 10, componentId: 11)
        .ToStructure<Nv5DistanceMessage>();

    Assert.Equal(7UL, actual.BankRevision);
    Assert.Equal(current.CalibrationId, actual.CalibrationId);
    Assert.Equal(-0.42f, actual.RawMetres);
    Assert.True(float.IsNaN(actual.CorrectedMetres));
    Assert.Equal(2, actual.CalibrationStatus);
    Assert.Equal(1, actual.ReferenceScope);
  }

  [Fact]
  public void Decodes_lps_status_pose_arrays_through_the_shared_parser() {
    var expected = new Nv5LpsStatusMessage {
      BootNonce = 0x0123_4567_89ab_cdefUL,
      RuntimeEpoch = 3,
      SnapshotSeq = 99,
      Challenge = 0xfeed_beefUL,
      FieldGeneration = 4,
      PoseValidForUs = 400_000,
      PositionEnu = [12.5f, -3.25f, 1.125f],
      VelocityEn = [0.5f, -0.25f],
      CovarianceEnu = [0.04f, 0.001f, 0.002f, 0.05f, 0.003f, 0.09f],
      Uid = Enumerable.Range(0x20, 18).Select(value => (byte)value).ToArray(),
      Role = 1,
      Channel = 2,
      Status = 13,
      OwnerFlags = 1,
      BeaconMask = 0x0f,
      CalibrationStatus = 1,
      CalibratedMask = 0x05,
    };

    Nv5LpsStatusMessage actual = Packet(
            NvModemMessageIds.Nv5LpsStatus, expected, systemId: 10, componentId: 11)
        .ToStructure<Nv5LpsStatusMessage>();

    Assert.Equal(expected.BootNonce, actual.BootNonce);
    Assert.Equal(expected.Challenge, actual.Challenge);
    Assert.Equal(expected.PositionEnu, actual.PositionEnu);
    Assert.Equal(expected.VelocityEn, actual.VelocityEn);
    Assert.Equal(expected.CovarianceEnu, actual.CovarianceEnu);
    Assert.Equal(expected.Uid, actual.Uid);
    Assert.Equal(13, actual.Status);
    Assert.Equal(0x05, actual.CalibratedMask);
  }

  [Fact]
  public void Parses_custom_nv5_packet_through_the_shared_mission_planner_parser() {
    NvModemMavlinkDialect.Register();
    var expected = new Nv5LinkStatusMessage {
      SampleMs = 1000,
      FrequencyHz = 868_000_000,
      TxRadioBytes = 125_000,
      PacketRssiDbmX10 = -873,
      PacketSnrDbX10 = 42,
      Channel = 2,
      RadioChip = 0,
      Role = 2,
      Modulation = 1,
      Flags = 0xc7,
      LinkQuality = 97,
      TxState = 1,
    };

    MAVLink.MAVLinkMessage packet = Packet(
        NvModemMessageIds.Nv5LinkStatus, expected, systemId: 41, componentId: 68);
    Nv5LinkStatusMessage actual = packet.ToStructure<Nv5LinkStatusMessage>();

    Assert.Equal((uint)NvModemMessageIds.Nv5LinkStatus, packet.msgid);
    Assert.Equal(41, packet.sysid);
    Assert.Equal(868_000_000u, actual.FrequencyHz);
    Assert.Equal(-873, actual.PacketRssiDbmX10);
    Assert.Equal(2, actual.Channel);
    Assert.Equal(97, actual.LinkQuality);
  }

  [Fact]
  public void Parses_shared_nv_modem_passport_through_the_startup_dialect() {
    NvModemInfoMessage expected = ModemInfo(
        generation: 5, productProfile: 7,
        flags: NvModemInfoFlags.Channel1Active | NvModemInfoFlags.Channel2Active,
        channel1Role: 0, channel2Role: 2, channel1Chip: 0, channel2Chip: 3);

    MAVLink.MAVLinkMessage packet = Packet(
        NvModemMessageIds.NvModemInfo, expected, systemId: 213, componentId: 247);
    NvModemInfoMessage actual = packet.ToStructure<NvModemInfoMessage>();

    Assert.Equal(1, actual.SchemaVersion);
    Assert.Equal(5, actual.ModemGeneration);
    Assert.Equal(7, actual.ProductProfile);
    Assert.Equal(2, actual.RadioCount);
    Assert.Equal(2, actual.Channel2Role);
    Assert.Equal(3, actual.Channel2RadioChip);
  }

  [Fact]
  public void Parses_atomic_nv5_encryption_key_wire_layout() {
    byte[] channel1 = Convert.FromHexString("00112233445566778899aabbccddeeff");
    byte[] channel2 = Convert.FromHexString("ffeeddccbbaa99887766554433221100");
    var expected = new NvEncryptionKeysSetMessage {
      TransactionId = 0x12345678,
      TargetSystem = 249,
      TargetComponent = 253,
      SchemaVersion = 1,
      ChannelMask = 3,
      Channel1Key = channel1,
      Channel2Key = channel2,
    };

    MAVLink.MAVLinkMessage packet = Packet(
        NvModemMessageIds.NvEncryptionKeysSet, expected, 1, 1);
    NvEncryptionKeysSetMessage actual = packet.ToStructure<NvEncryptionKeysSetMessage>();

    Assert.Equal(expected.TransactionId, actual.TransactionId);
    Assert.Equal(249, actual.TargetSystem);
    Assert.Equal(253, actual.TargetComponent);
    Assert.Equal(3, actual.ChannelMask);
    Assert.Equal(channel1, actual.Channel1Key);
    Assert.Equal(channel2, actual.Channel2Key);
  }

  [Theory]
  [InlineData(MAVLink.MAV_PARAM_TYPE.UINT8, 255d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.INT8, -128d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.UINT16, 65535d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.INT16, -32768d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.UINT32, 4294967295d)]
  [InlineData(MAVLink.MAV_PARAM_TYPE.INT32, -2147483648d)]
  public void Preserves_bytewise_mavlink_integer_parameter_encoding(
      MAVLink.MAV_PARAM_TYPE type, double expected) {
    float wire = NvModemParameterCodec.Encode(expected, (byte)type);

    Assert.Equal(expected, NvModemParameterCodec.Decode(wire, (byte)type));
  }

  [Fact]
  public void Compares_integer_parameters_exactly_and_locks_nv4_aes_to_128_bits() {
    Assert.False(NvModemParameterCodec.ValuesEqual(
        4_000_000_000, 4_000_000_001, (byte)MAVLink.MAV_PARAM_TYPE.UINT32));
    Assert.True(NvModemParameterCodec.ValuesEqual(
        1.0, 1.0 + 1e-9, (byte)MAVLink.MAV_PARAM_TYPE.REAL32));

    var keyBits = new NvModemParameterRow(
        "ENC_KEY_BITS", 128, (byte)MAVLink.MAV_PARAM_TYPE.INT32);
    Assert.True(keyBits.IsValid);
    keyBits.ValueText = "256";
    Assert.False(keyBits.IsValid);
    keyBits.ValueText = "128";
    Assert.True(keyBits.IsValid);

    var frame = new NvModemParameterRow(
        "CH1_FRAME", 64, (byte)MAVLink.MAV_PARAM_TYPE.UINT32);
    Assert.True(frame.IsValid);
    frame.ValueText = "32";
    Assert.False(frame.IsValid);
    frame.ValueText = "72";
    Assert.False(frame.IsValid);
    frame.ValueText = "80";
    Assert.True(frame.IsValid);
    frame.ValueText = "496";
    Assert.True(frame.IsValid);
    frame.ValueText = "512";
    Assert.False(frame.IsValid);
  }

  [Fact]
  public void Carries_reference_descriptions_and_nv4_refresh_compatibility() {
    Assert.True(NvModemCatalog.IsNv4Signature("REFRESH_SETTING"));
    Assert.True(NvModemCatalog.IsNv4Signature("REFRESH_SETTINGS"));
    Assert.True(NvModemCatalog.IsReadOnly("REFRESH_SETTING"));
    Assert.True(NvModemCatalog.IsReadOnly("REFRESH_SETTINGS"));
    Assert.False(NvModemCatalog.Applicable("REFRESH_SETTING",
        new Dictionary<string, double>()));
    Assert.False(NvModemCatalog.Applicable("REFRESH_SETTINGS",
        new Dictionary<string, double>()));
    Assert.Equal("REFRESH_SETTING", NvModemCatalog.Nv4RefreshParameterName(
        new Dictionary<string, double> {
          ["REFRESH_SETTINGS"] = 0,
          ["REFRESH_SETTING"] = 0,
        }));
    Assert.Equal("REFRESH_SETTINGS", NvModemCatalog.Nv4RefreshParameterName(
        new Dictionary<string, double> { ["REFRESH_SETTINGS"] = 0 }));
    Assert.True(NvModemCatalog.IsReadOnly("DIVERSITY"));
    Assert.False(NvModemCatalog.RequiresManualReboot(NvModemGeneration.Nv5, "RTSP_PORT"));
    Assert.True(NvModemCatalog.RequiresManualReboot(NvModemGeneration.Nv5, "APP_ROUTE"));
    Assert.False(NvModemCatalog.RequiresManualReboot(NvModemGeneration.Nv4, "APP_ROUTE"));
    Assert.Contains("advertised name automatically",
        NvModemCatalog.Description("REFRESH_SETTING"), StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Old firmware", NvModemCatalog.Description("REFRESH_SETTINGS"));
    Assert.Contains("FLRC video stream 0", NvModemCatalog.Description("UDP_RX_BASE"));
    Assert.Contains("Read-only derived topology", NvModemCatalog.Description("DIVERSITY"));
    Assert.DoesNotContain("reboots", NvModemCatalog.Description("APP_ROUTE"),
        StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Boolean setting", NvModemCatalog.Description("SWAP_TLM_STREAM"),
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("868000 = 868 MHz", NvModemCatalog.Description("CH1_FREQ_KHZ"));
    Assert.Contains("64..240", NvModemCatalog.Description("CH1_FRAME"));
    Assert.Contains("0=receiver", NvModemCatalog.Description("CH2_ROLE"));
    Assert.Equal(0, NvModemCatalog.Nv5KeyWordIndex("CH1_KEY_W0"));
    Assert.Equal(3, NvModemCatalog.Nv5KeyWordIndex("CH2_KEY_W3"));
    Assert.Equal(-1, NvModemCatalog.Nv5KeyWordIndex("CH1_KEY00"));
    Assert.Contains("big-endian", NvModemCatalog.Description("CH1_KEY_W0"),
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("MAVLink INT32", NvModemCatalog.Description("CH1_KEY_W0"),
        StringComparison.OrdinalIgnoreCase);
    byte[] key = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP");
    Assert.Equal(0x41424344u, NvModemCatalog.Nv5KeyWord(key, 0));
    Assert.Equal(0x4d4e4f50u, NvModemCatalog.Nv5KeyWord(key, 3));
    byte[] signedKey = Convert.FromHexString("FFEEDDCC000000000000000000000000");
    Assert.Equal(unchecked((int)0xFFEEDDCCu), NvModemCatalog.Nv5SignedKeyWord(signedKey, 0));
    var restored = new byte[NvModemCatalog.Nv5KeyBytes];
    for (int word = 0; word < NvModemCatalog.Nv5KeyWordCount; word++) {
      NvModemCatalog.WriteNv5KeyWord(restored, word, NvModemCatalog.Nv5KeyWord(key, word));
    }
    Assert.Equal(key, restored);
    Assert.Equal("Teensy · RFM/SX1278",
        NvModemCatalog.HardwareModel(NvModemGeneration.Nv4, 99));
  }

  [Fact]
  public void Carries_complete_nv4_sketch_parameter_catalog_and_descriptions() {
    string[] expected = [
      "HW_VERSION", "WD_TIMEOUT", "DATA_REFLECT", "DATA_RF_STAT",
      "RC_DELAY", "RX_RSSI_TYPE", "SBUS_ENABLE", "SBUS_MASK",
      "LOCAL_SYS_ID", "LOCAL_COMP_ID", "UAV_SYS_ID", "UAV_COMP_ID",
      "NET_PORT_LOCAL", "NET_PORT_REMOTE", "PROXY_UDP_RPORT", "PROXY_UDP_LPORT",
      "PROXY_RSSI", "NET_ENABLE", "TX_ON", "SERIAL_BAUDRATE", "UNITED_PKG_CNT",
      "USE_FHSS", "GUARD_INTERVAL", "CENTRAL_FREQ_MZ", "BANDWIDTH_MHZ", "PREAMBLE_LEN",
      "ENC_KEY_BYTE1", "ENC_KEY_BYTE2", "ENC_KEY_BYTE3", "ENC_KEY_BYTE4",
      "ENC_KEY_BYTE5", "ENC_KEY_BYTE6", "ENC_KEY_BYTE7", "ENC_KEY_BYTE8",
      "CHL_WIDE_KHZ", "ENC_KEY_BITS", "SPREAD_FACTOR", "POWER_TX", "LNA_GAIN",
      "CODING_RATE", "HOPS_WAITING", "SYNC_WORD", "HARDWARE_CRC",
      "NET_BYTE_1", "NET_BYTE_2", "NET_BYTE_3", "NET_MASK_1", "NET_MASK_2",
      "NET_MASK_3", "NET_MASK_4", "NET_BYTE_REMOTE", "CHECK_SYNC_WORD",
      "ACCEPT_UNKN_MAV", "UART2_STAT_ON", "DEV_MODE", "REFRESH_SETTING",
    ];

    Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal),
        NvModemCatalog.Nv4ParameterNames.OrderBy(name => name, StringComparer.Ordinal));
    Assert.Equal(expected.Length,
        NvModemCatalog.Nv4ParameterNames.Distinct(StringComparer.Ordinal).Count());
    foreach (string name in expected) {
      string description = NvModemCatalog.Description(name);
      Assert.False(string.IsNullOrWhiteSpace(description), name);
      Assert.False(description.StartsWith("Published by the modem", StringComparison.Ordinal),
          name);
    }

    Assert.Contains("-1 calculates", NvModemCatalog.Description("WD_TIMEOUT"));
    Assert.DoesNotContain("disables", NvModemCatalog.Description("WD_TIMEOUT"),
        StringComparison.OrdinalIgnoreCase);
    Assert.Contains("2=SBUS on the secondary UART",
        NvModemCatalog.Description("SBUS_ENABLE"));
    Assert.Contains("Unsynchronized receiver", NvModemCatalog.Description("HOPS_WAITING"));
    Assert.Contains("does not read it", NvModemCatalog.Description("DATA_REFLECT"));
    Assert.Contains("does not read it", NvModemCatalog.Description("PROXY_RSSI"));
    Assert.StartsWith("Fourth octet of the remote",
        NvModemCatalog.Description("NET_BYTE_REMOTE"), StringComparison.Ordinal);
  }

  [Fact]
  public void Keeps_same_system_component_devices_separate_by_existing_mavlink_link() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var udp = new NvModemLink(new MAVLinkInterface(), "udp://0.0.0.0:14550");
    var serial = new NvModemLink(new MAVLinkInterface(), "serial:/dev/ttyUSB0:115200");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 };

    viewModel.HandlePacket(udp, Packet(NvModemMessageIds.Nv5LinkStatus, status, 7, 68));
    viewModel.HandlePacket(serial, Packet(NvModemMessageIds.Nv5LinkStatus, status, 7, 68));

    Assert.Equal(2, viewModel.Devices.Count);
    Assert.Contains(viewModel.Devices, item => item.Label.Contains("udp://", StringComparison.Ordinal));
    Assert.Contains(viewModel.Devices, item => item.Label.Contains("serial:", StringComparison.Ordinal));
  }

  [Fact]
  public void Keeps_hub_attached_modems_separate_and_targets_them_on_the_shared_link() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var hubLink = new NvModemLink(new MAVLinkInterface(), "NV5 HUB management");
    viewModel.HandlePacket(hubLink, Packet(NvModemMessageIds.NvModemInfo,
        ModemInfo(5, 7, 0, uidSeed: 0x20), 10, 21));
    viewModel.HandlePacket(hubLink, Packet(NvModemMessageIds.NvModemInfo,
        ModemInfo(5, 7, 0, uidSeed: 0x30), 10, 22));
    Assert.Equal(2, viewModel.Devices.Count);

    viewModel.SelectedDevice = viewModel.Devices.Single(
        device => device.State.Key.ComponentId == 22);
    transport.Sent.Clear();
    viewModel.RefreshSelectedCommand.Execute(null);

    FakeTransport.SentPacket request = Assert.Single(transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_request_list_t);
    Assert.Same(hubLink, request.Link);
    Assert.Equal((byte)10, request.SystemId);
    Assert.Equal((byte)22, request.ComponentId);
  }

  [Fact]
  public void Discovery_replays_shared_cache_and_probes_every_observed_mavlink_id() {
    var transport = new FakeTransport();
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    transport.Links.Add(source);
    transport.Endpoints[source] = [new NvModemEndpoint(37, 203)];
    transport.Cached[source] = [Packet(NvModemMessageIds.NvModemInfo,
        ModemInfo(5, 7, NvModemInfoFlags.Channel1Active, channel1Role: 2), 149, 241)];

    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);

    NvModemDeviceChoice modem = Assert.Single(viewModel.Devices);
    Assert.Contains("149:241", modem.Label, StringComparison.Ordinal);
    Assert.Contains(transport.Sent, sent => sent.SystemId == 37 && sent.ComponentId == 203
        && sent.Packet is MAVLink.mavlink_command_long_t command
        && command.command == (ushort)MAVLink.MAV_CMD.REQUEST_MESSAGE
        && command.param1 == NvModemMessageIds.Nv5LinkStatus);
    Assert.Contains(transport.Sent, sent => sent.SystemId == 37 && sent.ComponentId == 203
        && sent.Packet is MAVLink.mavlink_command_long_t command
        && command.command == (ushort)MAVLink.MAV_CMD.REQUEST_MESSAGE
        && command.param1 == NvModemMessageIds.NvModemInfo);
    Assert.Contains(transport.Sent, sent => sent.SystemId == 37 && sent.ComponentId == 203
        && sent.Packet is MAVLink.mavlink_command_long_t command
        && command.command == (ushort)MAVLink.MAV_CMD.UAVCAN_GET_NODE_INFO);
  }

  [Fact]
  public void Discovery_replays_the_complete_shared_parameter_cache_not_only_the_last_packet() {
    var transport = new FakeTransport();
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    transport.Links.Add(source);
    transport.Cached[source] = [Packet(NvModemMessageIds.NvModemInfo,
        ModemInfo(5, 7, NvModemInfoFlags.Channel1Active), 255, 11)];
    transport.Parameters[source] = [
      CachedParameter("MAV_SYS_ID", 255, 5, 3, 255, 11),
      CachedParameter("MODEM_PROFILE", 7, 5, 3, 255, 11),
      CachedParameter("ETH_ENABLE", 1, 5, 3, 255, 11),
    ];

    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);

    Assert.Single(viewModel.Devices);
    Assert.Equal(3, viewModel.Parameters.Count);
    Assert.Equal(new[] { "ETH_ENABLE", "MAV_SYS_ID", "MODEM_PROFILE" },
        viewModel.Parameters.Select(parameter => parameter.Name).OrderBy(name => name));
    Assert.Equal("Parameters: 3 / 3", viewModel.ParameterProgress);
  }

  [Fact]
  public void Parameter_names_alone_never_add_autopilots_to_the_modem_list() {
    var transport = new FakeTransport();
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    transport.Links.Add(source);
    transport.Parameters[source] = [
      CachedParameter("CH1_OPT", 4, 6, 4, 1, 1),
      CachedParameter("CH2_REV", 1, 6, 4, 1, 1),
      CachedParameter("MODEM_PROFILE", 7, 6, 4, 42, 1),
      CachedParameter("RTSP_PORT", 554, 6, 4, 42, 1),
    ];

    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    viewModel.HandlePacket(source, ParameterPacket("CH1_OPT", 4, 6, 1, 1, 1));
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 6, 1, 42, 1));

    Assert.Empty(viewModel.Devices);
    Assert.Empty(viewModel.Parameters);
  }

  [Theory]
  [InlineData((byte)0)]
  [InlineData((byte)1)]
  public void Reflected_gcs_rtsp_requests_do_not_create_a_255_190_modem(byte operation) {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5RtspConfig,
        new Nv5RtspConfigMessage {
          TransactionId = 9,
          TargetSystem = 6,
          TargetComponent = 6,
          Operation = operation,
          Path = new byte[96],
        }, MAVLinkInterface.gcssysid,
        (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_MISSIONPLANNER));

    Assert.Empty(viewModel.Devices);
  }

  [Fact]
  public void Modem_rtsp_report_can_still_identify_a_real_255_190_endpoint() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5RtspConfig,
        new Nv5RtspConfigMessage {
          Operation = 2,
          Path = new byte[96],
        }, 255, 190));

    NvModemDeviceChoice modem = Assert.Single(viewModel.Devices);
    Assert.Contains("NV5 255:190", modem.Label, StringComparison.Ordinal);
  }

  [Fact]
  public void Nv4_detection_uses_the_strict_can_node_signature_at_any_id() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared TCP");

    viewModel.HandlePacket(source, NodeInfoPacket("camera.node", 41, 212));
    Assert.Empty(viewModel.Devices);

    viewModel.HandlePacket(source, NodeInfoPacket("RX_433/70", 42, 213, hardwareMajor: 2));
    Assert.Empty(viewModel.Devices);

    viewModel.HandlePacket(source, NodeInfoPacket("TX_433/70", 199, 254));

    NvModemDeviceChoice modem = Assert.Single(viewModel.Devices);
    Assert.Contains("NV4 199:254", modem.Label, StringComparison.Ordinal);
    Assert.Contains(transport.Sent, sent => sent.SystemId == 199 && sent.ComponentId == 254
        && sent.Packet is MAVLink.mavlink_param_request_list_t);
  }

  [Theory]
  [InlineData("NV_TX", "TX")]
  [InlineData("nv_rx_433", "RX")]
  public void Nv4_rfm_detection_uses_the_legacy_node_signature(
      string nodeName, string expectedRole) {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");

    // Legacy RFM builds did not reliably report version major 4. The legacy status
    // check therefore identifies these nodes by the case-insensitive NV_TX/NV_RX prefix.
    viewModel.HandlePacket(source, NodeInfoPacket(
        nodeName, 231, 249, hardwareMajor: 0, softwareMajor: 0));

    NvModemDeviceChoice modem = Assert.Single(viewModel.Devices);
    Assert.Contains("NV4 231:249", modem.Label, StringComparison.Ordinal);
    Assert.Contains(expectedRole, modem.Label, StringComparison.Ordinal);
    Assert.Contains(transport.Sent, sent => sent.SystemId == 231 && sent.ComponentId == 249
        && sent.Packet is MAVLink.mavlink_param_request_list_t);
  }

  [Fact]
  public void Supports_vendor_identity_modes_at_any_id_and_rejects_param_only_endpoints() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.NvModemInfo,
        ModemInfo(5, 7, NvModemInfoFlags.Channel1Active, channel1Role: 0), 213, 247));
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.NvModemInfo,
        ModemInfo(4, 4, NvModemInfoFlags.Channel1Active, channel1Role: 1,
            channel1Chip: 4), 121, 203));
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 2 }, 149, 241));
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.NvRxStat,
        new NvRxStatMessage { Frequency = 433_000_000 }, 90, 91));
    viewModel.HandlePacket(source, NodeInfoPacket("RX_433/70", 199, 254));
    viewModel.HandlePacket(source,
        NodeInfoPacket("NV_TX", 77, 250, hardwareMajor: 0, softwareMajor: 0));
    viewModel.HandlePacket(source, ParameterPacket(
        "MODEM_PROFILE", 8, 5, 1, 88, 222));

    Assert.Equal(6, viewModel.Devices.Count);
    Assert.Contains(viewModel.Devices, item =>
        item.Label.Contains("NV5 213:247", StringComparison.Ordinal)
        && item.Label.Contains("RX", StringComparison.Ordinal));
    Assert.Contains(viewModel.Devices, item =>
        item.Label.Contains("NV4 121:203", StringComparison.Ordinal)
        && item.Label.Contains("TX", StringComparison.Ordinal));
    Assert.Contains(viewModel.Devices, item =>
        item.Label.Contains("NV5 149:241", StringComparison.Ordinal));
    Assert.Contains(viewModel.Devices, item =>
        item.Label.Contains("NV4 90:91", StringComparison.Ordinal));
    Assert.Contains(viewModel.Devices, item =>
        item.Label.Contains("NV4 199:254", StringComparison.Ordinal)
        && item.Label.Contains("RX", StringComparison.Ordinal));
    Assert.Contains(viewModel.Devices, item =>
        item.Label.Contains("NV4 77:250", StringComparison.Ordinal)
        && item.Label.Contains("TX", StringComparison.Ordinal));
    Assert.DoesNotContain(viewModel.Devices, item =>
        item.Label.Contains("88:222", StringComparison.Ordinal));
  }

  [Theory]
  [InlineData((byte)0, (byte)4)]
  [InlineData((byte)1, (byte)1)]
  public void Nv5_unlocked_receiver_displays_current_channel_signal_only(
      byte modulation, byte radioChip) {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage {
          SampleMs = 1000,
          Channel = 1,
          RadioChip = radioChip,
          Role = 0,
          Modulation = modulation,
          Flags = 0xfb,
          PacketRssiDbmX10 = -395,
          PacketSnrDbX10 = 112,
          ChannelRssiDbmX10 = -970,
        }, 5, 68));

    Assert.Contains("L no", viewModel.RadioStatuses[0].Link, StringComparison.Ordinal);
    Assert.Contains("R -97.0", viewModel.RadioStatuses[0].Link, StringComparison.Ordinal);
    Assert.Contains("S —", viewModel.RadioStatuses[0].Link, StringComparison.Ordinal);
    Assert.DoesNotContain("-39.5", viewModel.RadioStatuses[0].Link, StringComparison.Ordinal);
  }

  [Fact]
  public void Nv5_locked_receiver_prefers_packet_signal_and_allows_channel_fallback() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    var status = new Nv5LinkStatusMessage {
      SampleMs = 1000,
      Channel = 1,
      RadioChip = 0,
      Role = 0,
      Flags = 1 << 2,
      PacketRssiDbmX10 = -395,
      PacketSnrDbX10 = 112,
      ChannelRssiDbmX10 = -970,
    };

    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.Nv5LinkStatus, status, 5, 68));
    Assert.Contains("R -39.5", viewModel.RadioStatuses[0].Link, StringComparison.Ordinal);
    Assert.Contains("S 11.2", viewModel.RadioStatuses[0].Link, StringComparison.Ordinal);

    status.PacketRssiDbmX10 = short.MinValue;
    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.Nv5LinkStatus, status, 5, 68));
    Assert.Contains("R -97.0", viewModel.RadioStatuses[0].Link, StringComparison.Ordinal);
  }

  [Fact]
  public void Invalid_passport_and_unscoped_nv4_parameter_do_not_create_devices() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");

    NvModemInfoMessage invalid = ModemInfo(5, 7, 0);
    invalid.SchemaVersion = 0;
    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, invalid, 10, 20));
    viewModel.HandlePacket(source,
        ParameterPacket("HW_VERSION", 4, 5, 1, 30, 40));

    Assert.Empty(viewModel.Devices);
  }

  [Theory]
  [InlineData(2, 5, 1)]
  [InlineData(1, 3, 1)]
  [InlineData(1, 5, 0)]
  public void Rejects_unsupported_nv_passport_schema_generation_or_capability(
      byte schema, byte generation, ulong capabilities) {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    NvModemInfoMessage passport = ModemInfo(generation, 7, 0);
    passport.SchemaVersion = schema;
    passport.Capabilities = capabilities;

    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, passport, 10, 20));

    Assert.Empty(viewModel.Devices);
  }

  [Fact]
  public void Autopilot_version_alone_does_not_classify_component_68_as_nv5() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared serial");

    viewModel.HandlePacket(source, Packet((uint)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION,
        new MAVLink.mavlink_autopilot_version_t { product_id = 7 }, 11, 68));

    Assert.Empty(viewModel.Devices);
  }

  [Fact]
  public void Clears_parameter_rows_immediately_when_switching_devices_and_reuses_target_link() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var first = new NvModemLink(new MAVLinkInterface(), "UDP first");
    var second = new NvModemLink(new MAVLinkInterface(), "TCP second");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(first, Packet(NvModemMessageIds.Nv5LinkStatus, status, 1, 68));
    viewModel.HandlePacket(first, ParameterPacket("MODEM_PROFILE", 7, 1, 2, 1, 68));
    Assert.Single(viewModel.Parameters);
    viewModel.HandlePacket(second, Packet(NvModemMessageIds.Nv5LinkStatus, status, 2, 68));

    transport.Sent.Clear();
    viewModel.SelectedDevice = viewModel.Devices.Single(item =>
        item.Label.Contains("TCP second", StringComparison.Ordinal));

    Assert.Empty(viewModel.Parameters);
    Assert.Contains(transport.Sent, sent => ReferenceEquals(sent.Link, second)
        && sent.Packet is MAVLink.mavlink_param_request_list_t request
        && request.target_system == 2 && request.target_component == 68);
    Assert.DoesNotContain(transport.Sent, sent => ReferenceEquals(sent.Link, first));
  }

  [Fact]
  public void Nv5_key_write_is_one_atomic_transaction_and_stays_on_discovery_link() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 9, 68));
    const int count = 6;
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 5, count, 9, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 1, 5, count, 9, 68));
    DeliverNv5KeyWords(viewModel, source, 9, 68, 1,
        Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP"), count);
    transport.Sent.Clear();
    viewModel.KeyText = "4142434445464748494A4B4C4D4E4F50";

    viewModel.SetKeyCommand.Execute(null);
    FakeTransport.SentPacket sent = Assert.Single(transport.Sent);
    var write = Assert.IsType<NvEncryptionKeysSetMessage>(sent.Packet);
    Assert.Same(source, sent.Link);
    Assert.Equal(9, write.TargetSystem);
    Assert.Equal(68, write.TargetComponent);
    Assert.Equal(1, write.SchemaVersion);
    Assert.Equal(0x01, write.ChannelMask);
    Assert.Equal(Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP"), write.Channel1Key);
    Assert.Equal(new byte[16], write.Channel2Key);
    Assert.DoesNotContain(transport.Sent,
        packet => packet.Packet is MAVLink.mavlink_param_set_t);
    foreach (NvModemParameterRow row in viewModel.Parameters.Where(row =>
                 row.Name.StartsWith("CH1_KEY", StringComparison.Ordinal)
                 && !row.Name.EndsWith("_HASH", StringComparison.Ordinal))) {
      Assert.Equal((byte)MAVLink.MAV_PARAM_TYPE.INT32, row.Type);
    }

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.NvEncryptionKeysAck,
        new NvEncryptionKeysAckMessage {
          TransactionId = write.TransactionId,
          Channel1Fingerprint = 654321,
          TargetSystem = 1,
          TargetComponent = 1,
          SchemaVersion = 1,
          ChannelMask = 0x01,
          Result = NvEncryptionKeysResults.Applied,
        }, 9, 68));

    Assert.False(viewModel.IsBusy);
    Assert.Equal("4142434445464748494A4B4C4D4E4F50", viewModel.KeyText);
    Assert.Contains("stored atomically", viewModel.Status, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Nv5_diversity_keeps_keys_independent_and_targets_only_the_selected_radio() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5 diversity");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 }, 15, 68));
    viewModel.HandlePacket(source, ParameterPacket("DIVERSITY", 1,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 9, 15, 68));
    byte[] radio1Key = Enumerable.Repeat((byte)0x11, 16).ToArray();
    byte[] radio2Key = Enumerable.Repeat((byte)0x22, 16).ToArray();
    DeliverNv5KeyWords(viewModel, source, 15, 68, 1, radio1Key, 9);
    DeliverNv5KeyWords(viewModel, source, 15, 68, 2, radio2Key, 9);
    viewModel.SelectedKeyRadio = viewModel.KeyRadios.Single(radio => radio.Channel == 2);
    transport.Sent.Clear();
    byte[] replacement = Convert.FromHexString("80FFEEDDCCBBAA998877665544332211");
    viewModel.KeyText = Convert.ToHexString(replacement);

    viewModel.SetKeyCommand.Execute(null);

    var write = Assert.IsType<NvEncryptionKeysSetMessage>(Assert.Single(transport.Sent).Packet);
    Assert.Equal(0x02, write.ChannelMask);
    Assert.Equal(new byte[16], write.Channel1Key);
    Assert.Equal(replacement, write.Channel2Key);
    NvModemParameterRow channel1 = viewModel.Parameters.Single(
        row => row.Name == "CH1_KEY_W0");
    NvModemParameterRow channel2 = viewModel.Parameters.Single(
        row => row.Name == "CH2_KEY_W0");
    Assert.True(channel1.TryValue(out double channel1Word));
    Assert.True(channel2.TryValue(out double channel2Word));
    Assert.Equal(NvModemCatalog.Nv5SignedKeyWord(radio1Key, 0), channel1Word);
    Assert.Equal(NvModemCatalog.Nv5SignedKeyWord(replacement, 0), channel2Word);
  }

  [Fact]
  public void Nv5_save_writes_edited_key_words_as_exact_int32_parameters() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5 typed key");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 }, 16, 68));
    DeliverNv5KeyWords(viewModel, source, 16, 68, 1, new byte[16], 4);
    transport.Sent.Clear();
    NvModemParameterRow row = viewModel.Parameters.Single(item => item.Name == "CH1_KEY_W0");
    row.ValueText = "-1";
    NvModemDeviceState device = viewModel.SelectedDevice!.State;

    Assert.True(viewModel.QueueParameterWrites(device, [row]));
    viewModel.BeginQueuedWrites(device, keyOnly: false, keyChannel: 0);

    var write = Assert.IsType<MAVLink.mavlink_param_set_t>(Assert.Single(transport.Sent).Packet);
    Assert.Equal("CH1_KEY_W0", NvModemParameterCodec.Name(write.param_id));
    Assert.Equal((byte)MAVLink.MAV_PARAM_TYPE.INT32, write.param_type);
    Assert.DoesNotContain(transport.Sent,
        sent => sent.Packet is NvEncryptionKeysSetMessage);

    // A stale list value with the same raw bits but the old UINT32 type is not the write echo.
    viewModel.HandlePacket(source, ParameterPacket("CH1_KEY_W0", uint.MaxValue,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 4, 16, 68));
    Assert.True(viewModel.IsBusy);
    Assert.True(row.IsChanged);

    viewModel.HandlePacket(source, ParameterPacket("CH1_KEY_W0", -1,
        (byte)MAVLink.MAV_PARAM_TYPE.INT32, 4, 16, 68));
    Assert.False(viewModel.IsBusy);
    Assert.False(row.IsChanged);
    Assert.DoesNotContain(transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_request_list_t);
  }

  [Fact]
  public void Nv5_binary_key_uses_hex_text_and_atomic_bytes() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "TCP NV5");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 }, 7, 68));
    byte[] stored = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    DeliverNv5KeyWords(viewModel, source, 7, 68, 1, stored, 4);

    Assert.Equal("000102030405060708090A0B0C0D0E0F", viewModel.KeyText);
    transport.Sent.Clear();
    viewModel.KeyText = "ABCDEFGHIJKLMNOP";
    viewModel.SetKeyCommand.Execute(null);
    Assert.Empty(transport.Sent);
    Assert.Contains("exactly 32 hexadecimal digits", viewModel.Status,
        StringComparison.OrdinalIgnoreCase);

    // Lower- or uppercase hex input is accepted, and the editor normalizes it to uppercase.
    viewModel.KeyText = "ffeeddccbbaa99887766554433221100";
    viewModel.SetKeyCommand.Execute(null);

    var write = Assert.IsType<NvEncryptionKeysSetMessage>(Assert.Single(transport.Sent).Packet);
    Assert.Equal(Convert.FromHexString("ffeeddccbbaa99887766554433221100"),
        write.Channel1Key);
    Assert.Equal("FFEEDDCCBBAA99887766554433221100", viewModel.KeyText);
  }

  [Fact]
  public void Nv5_key_generator_uses_cryptographic_bytes_and_uppercase_hex() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 }, 12, 68));
    DeliverNv5KeyWords(viewModel, source, 12, 68, 1, new byte[16], 4);
    transport.Sent.Clear();

    viewModel.GenerateKeyCommand.Execute(null);

    Assert.Matches("^[0-9A-F]{32}$", viewModel.KeyText);
    Assert.Contains("Generated and staged", viewModel.Status, StringComparison.Ordinal);
    Assert.Empty(transport.Sent);
  }

  [Fact]
  public void Nv4_key_generator_uses_32_random_bytes_and_uppercase_hex() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV4");
    viewModel.HandlePacket(source, NodeInfoPacket("RX_433/70", 17, 16));
    for (int index = 1; index <= 8; index++) {
      viewModel.HandlePacket(source, ParameterPacket($"ENC_KEY_BYTE{index}", 0,
          (byte)MAVLink.MAV_PARAM_TYPE.INT32, 8, 17, 16));
    }
    transport.Sent.Clear();

    viewModel.GenerateKeyCommand.Execute(null);

    Assert.Matches("^[0-9A-F]{64}$", viewModel.KeyText);
    Assert.Contains("Generated and staged", viewModel.Status, StringComparison.Ordinal);
    Assert.Empty(transport.Sent);
  }

  [Fact]
  public void Nv4_hex_key_preserves_the_full_signed_int32_word_range() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV4 signed words");
    viewModel.HandlePacket(source, NodeInfoPacket("RX_433/70", 19, 16));
    for (int index = 1; index <= 8; index++) {
      viewModel.HandlePacket(source, ParameterPacket($"ENC_KEY_BYTE{index}", 0,
          (byte)MAVLink.MAV_PARAM_TYPE.INT32, 9, 19, 16));
    }
    viewModel.HandlePacket(source, ParameterPacket("REFRESH_SETTING", 0,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 9, 19, 16));
    transport.Sent.Clear();
    viewModel.KeyText = "00000080FFFFFF7F000000000000000000000000000000000000000000000000";

    viewModel.SetKeyCommand.Execute(null);

    var first = Assert.IsType<MAVLink.mavlink_param_set_t>(transport.Sent[^1].Packet);
    Assert.Equal("ENC_KEY_BYTE1", NvModemParameterCodec.Name(first.param_id));
    Assert.Equal((byte)MAVLink.MAV_PARAM_TYPE.INT32, first.param_type);
    Assert.Equal(int.MinValue, NvModemParameterCodec.Decode(first.param_value, first.param_type));
    viewModel.HandlePacket(source, ParameterPacket("ENC_KEY_BYTE1", int.MinValue,
        (byte)MAVLink.MAV_PARAM_TYPE.INT32, 9, 19, 16));

    var second = Assert.IsType<MAVLink.mavlink_param_set_t>(transport.Sent[^1].Packet);
    Assert.Equal("ENC_KEY_BYTE2", NvModemParameterCodec.Name(second.param_id));
    Assert.Equal(int.MaxValue, NvModemParameterCodec.Decode(second.param_value, second.param_type));
  }

  [Fact]
  public void Nv5_atomic_key_retry_reuses_the_idempotency_transaction_id() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 1 }, 8, 68));
    DeliverNv5KeyWords(viewModel, source, 8, 68, 1,
        Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP"), 4);
    transport.Sent.Clear();
    viewModel.KeyText = "4142434445464748494A4B4C4D4E4F50";
    viewModel.SetKeyCommand.Execute(null);
    var first = Assert.IsType<NvEncryptionKeysSetMessage>(Assert.Single(transport.Sent).Packet);

    now += TimeSpan.FromMilliseconds(1300);
    viewModel.ServiceTransactions();

    var retry = Assert.IsType<NvEncryptionKeysSetMessage>(transport.Sent[^1].Packet);
    Assert.Equal(2, transport.Sent.Count);
    Assert.Equal(first.TransactionId, retry.TransactionId);
    Assert.Equal(first.Channel1Key, retry.Channel1Key);
  }

  [Theory]
  [InlineData("REFRESH_SETTING")]
  [InlineData("REFRESH_SETTINGS")]
  public void Nv4_key_transaction_uses_advertised_refresh_parameter_on_same_link(
      string refreshParameter) {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV4");
    const int count = 10;
    viewModel.HandlePacket(source, NodeInfoPacket("RX_433/70", 1, 16));
    viewModel.HandlePacket(source, ParameterPacket("HW_VERSION", 4, 5, count, 1, 16));
    for (int index = 1; index <= 8; index++) {
      viewModel.HandlePacket(source, ParameterPacket($"ENC_KEY_BYTE{index}", index, 6,
          count, 1, 16));
    }
    viewModel.HandlePacket(source, ParameterPacket(refreshParameter, 0, 5,
        count, 1, 16));
    transport.Sent.Clear();
    viewModel.KeyText = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";

    viewModel.SetKeyCommand.Execute(null);
    for (int writeIndex = 0; writeIndex < 9; writeIndex++) {
      var write = Assert.IsType<MAVLink.mavlink_param_set_t>(transport.Sent[^1].Packet);
      string name = NvModemParameterCodec.Name(write.param_id);
      Assert.Same(source, transport.Sent[^1].Link);
      viewModel.HandlePacket(source, ParameterPacket(name,
          NvModemParameterCodec.Decode(write.param_value, write.param_type), write.param_type,
          count, 1, 16));
    }

    string[] writtenNames = [.. transport.Sent
        .Where(sent => sent.Packet is MAVLink.mavlink_param_set_t)
        .Select(sent => NvModemParameterCodec.Name(
            ((MAVLink.mavlink_param_set_t)sent.Packet).param_id))];
    Assert.Equal(refreshParameter, writtenNames[^1]);
    Assert.DoesNotContain(refreshParameter == "REFRESH_SETTING"
        ? "REFRESH_SETTINGS" : "REFRESH_SETTING", writtenNames);
    var refresh = Assert.IsType<MAVLink.mavlink_param_set_t>(transport.Sent[^1].Packet);
    Assert.Equal((byte)MAVLink.MAV_PARAM_TYPE.UINT32, refresh.param_type);
    Assert.False(viewModel.IsBusy);
  }

  [Fact]
  public void Parameter_file_roundtrip_includes_described_values_and_rtsp_path() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "TCP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 3, 68));
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 5, 2, 3, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FREQ_KHZ", 868000, 5, 2, 3, 68));

    NvModemParameterComparison comparison = Assert.IsType<NvModemParameterComparison>(
        viewModel.BuildParameterFileComparison(
            "CH1_FREQ_KHZ,915000\n#NV5_RTSP_PATH,/cam/main\n", "test.param"));
    Assert.Equal(2, comparison.Rows.Count);
    Assert.DoesNotContain("CH1_FREQ_KHZ,915000", viewModel.ExportParameterFile());
    Assert.Equal(2, viewModel.ApplyParameterComparison(comparison));
    string exported = viewModel.ExportParameterFile();

    Assert.Contains("CH1_FREQ_KHZ,915000", exported);
    Assert.Contains("#NV5_RTSP_PATH,/cam/main", exported);
    Assert.Contains("key-word values", exported, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("\r\r\n", exported);
    Assert.True(viewModel.HasPendingChanges);
  }

  [Fact]
  public void Parameter_file_preview_contains_only_real_differences_and_honours_selection() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "TCP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 31, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FREQ_KHZ", 868000, 5, 2, 31, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FRAME", 64, 5, 2, 31, 68));

    NvModemParameterComparison comparison = Assert.IsType<NvModemParameterComparison>(
        viewModel.BuildParameterFileComparison(
            "CH1_FREQ_KHZ,868000\nCH1_FRAME,240\nUNKNOWN,1\nCH1_FRAME,bad\n", "radio.param"));

    NvModemParameterComparisonRow change = Assert.Single(comparison.Rows);
    Assert.Equal("CH1_FRAME", change.Name);
    Assert.Equal("64", change.CurrentText);
    Assert.Equal("240", change.ProposedText);
    Assert.Equal(1, comparison.Unknown);
    Assert.Equal(1, comparison.Invalid);
    Assert.DoesNotContain("CH1_FRAME,240", viewModel.ExportParameterFile());

    change.Use = false;
    Assert.Equal(0, viewModel.ApplyParameterComparison(comparison));
    Assert.DoesNotContain("CH1_FRAME,240", viewModel.ExportParameterFile());
  }

  [Fact]
  public void Revert_selected_restores_only_one_changed_parameter_without_writing() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 }, 32, 68));
    viewModel.HandlePacket(source, ParameterPacket("ETH_ENABLE", 1,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 2, 32, 68));
    viewModel.HandlePacket(source, ParameterPacket("MAV_ENABLE", 1,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 2, 32, 68));
    NvModemParameterRow ethernet = viewModel.Parameters.Single(row => row.Name == "ETH_ENABLE");
    NvModemParameterRow mavlink = viewModel.Parameters.Single(row => row.Name == "MAV_ENABLE");
    ethernet.ValueText = "0";
    mavlink.ValueText = "0";
    viewModel.SelectedParameter = ethernet;
    transport.Sent.Clear();

    Assert.True(viewModel.CanRevertSelectedParameter);
    viewModel.RevertSelectedParameterCommand.Execute(null);

    Assert.Equal("1", ethernet.ValueText);
    Assert.Equal("0", mavlink.ValueText);
    Assert.False(ethernet.IsChanged);
    Assert.True(mavlink.IsChanged);
    Assert.False(viewModel.CanRevertSelectedParameter);
    Assert.True(viewModel.HasPendingChanges);
    Assert.Empty(transport.Sent);
  }

  [Fact]
  public void Copy_from_another_nv5_previews_differences_before_staging() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var link = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(link, Packet(NvModemMessageIds.Nv5LinkStatus, status, 40, 68));
    viewModel.HandlePacket(link, ParameterPacket("MODEM_PROFILE", 7, 5, 4, 40, 68));
    viewModel.HandlePacket(link, ParameterPacket("CH1_CHIP", 0, 5, 4, 40, 68));
    viewModel.HandlePacket(link, ParameterPacket("CH1_MOD", 0, 5, 4, 40, 68));
    viewModel.HandlePacket(link, ParameterPacket("CH1_FRAME", 64, 5, 4, 40, 68));
    viewModel.HandlePacket(link, Packet(NvModemMessageIds.Nv5LinkStatus, status, 41, 68));
    viewModel.HandlePacket(link, ParameterPacket("MODEM_PROFILE", 7, 5, 4, 41, 68));
    viewModel.HandlePacket(link, ParameterPacket("CH1_CHIP", 0, 5, 4, 41, 68));
    viewModel.HandlePacket(link, ParameterPacket("CH1_MOD", 0, 5, 4, 41, 68));
    viewModel.HandlePacket(link, ParameterPacket("CH1_FRAME", 240, 5, 4, 41, 68));

    NvModemParameterComparison comparison = Assert.IsType<NvModemParameterComparison>(
        viewModel.BuildCopyParameterComparison());

    NvModemParameterComparisonRow change = Assert.Single(comparison.Rows);
    Assert.Equal("CH1_FRAME", change.Name);
    Assert.Equal("64", change.CurrentText);
    Assert.Equal("240", change.ProposedText);
    Assert.DoesNotContain("CH1_FRAME,240", viewModel.ExportParameterFile());

    Assert.Equal(1, viewModel.ApplyParameterComparison(comparison));
    Assert.Contains("CH1_FRAME,240", viewModel.ExportParameterFile());
    Assert.DoesNotContain(transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_set_t);
  }

  [Fact]
  public void Late_rtsp_read_does_not_overwrite_a_locally_staged_path() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 4, 68));
    viewModel.RtspPath = "/operator/staged";

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5RtspConfig,
        RtspPacket("/device/current"), 4, 68));

    Assert.Equal("/operator/staged", viewModel.RtspPath);
    Assert.True(viewModel.HasPendingChanges);
  }

  [Fact]
  public void Silent_parameter_read_retries_then_stops_without_blocking_the_view_model() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP silent modem");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 5, 68));
    transport.Sent.Clear();

    for (int retry = 0; retry < 6; retry++) {
      now += TimeSpan.FromMilliseconds(2100);
      viewModel.ServiceTransactions();
    }
    now += TimeSpan.FromMilliseconds(3100);
    viewModel.ServiceTransactions();

    Assert.Equal(6, transport.Sent.Count(sent =>
        sent.Packet is MAVLink.mavlink_param_request_list_t));
    Assert.False(viewModel.IsBusy);
    Assert.StartsWith("Error:", viewModel.Status, StringComparison.Ordinal);
    Assert.Contains("Press Refresh selected", viewModel.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void Parameter_list_retry_preserves_values_already_received() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP slow NV5");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 }, 18, 68));
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 3, 18, 68));
    transport.Sent.Clear();

    now += TimeSpan.FromMilliseconds(2100);
    viewModel.ServiceTransactions();
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 0,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 3, 18, 68));

    Assert.Single(transport.Sent,
        sent => sent.Packet is MAVLink.mavlink_param_request_list_t);
    Assert.Contains(viewModel.Parameters, row => row.Name == "MODEM_PROFILE");
    Assert.Contains(viewModel.Parameters, row => row.Name == "CH1_MOD");
  }

  [Fact]
  public void Factory_preset_is_staged_locally_with_the_reference_lr2021_defaults() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "TCP NV5");
    var status = new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 2 };
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus, status, 6, 68));
    const ushort count = 7;
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 5, count, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_CHIP", 0, 5, count, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 0, 5, count, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FRAME", 64, 5, count, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FHSS", 1, 5, count, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FHSS_KHZ", 2000, 5, count, 6, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_SCAN_DW", 9, 5, count, 6, 68));
    transport.Sent.Clear();

    viewModel.StageRadioPresetCommand.Execute("factory");
    string staged = viewModel.ExportParameterFile();

    Assert.Contains("CH1_MOD,1", staged);
    Assert.Contains("CH1_FRAME,240", staged);
    Assert.Contains("CH1_FHSS,0", staged);
    Assert.Contains("CH1_FHSS_KHZ,40000", staged);
    Assert.Contains("CH1_SCAN_DW,2", staged);
    Assert.Contains("CH1_FLRC_RATE,1300000", staged);
    Assert.True(viewModel.HasPendingChanges);
    Assert.Empty(transport.Sent);
  }

  [Fact]
  public void Factory_lora_preset_uses_the_current_reference_acquisition_values() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "TCP NV5 LoRa");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 1, Role = 2 }, 7, 68));
    const ushort count = 7;
    viewModel.HandlePacket(source, ParameterPacket("MODEM_PROFILE", 7, 5, count, 7, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_CHIP", 1, 5, count, 7, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 1, 5, count, 7, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FRAME", 240, 5, count, 7, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FHSS", 0, 5, count, 7, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_FHSS_KHZ", 14000, 5, count, 7, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_SCAN_DW", 2, 5, count, 7, 68));

    viewModel.StageRadioPresetCommand.Execute("factory");
    string staged = viewModel.ExportParameterFile();

    Assert.Contains("CH1_MOD,0", staged);
    Assert.Contains("CH1_FRAME,64", staged);
    Assert.Contains("CH1_FHSS,1", staged);
    Assert.Contains("CH1_FHSS_KHZ,40000", staged);
    Assert.Contains("CH1_SCAN_DW,5", staged);
  }

  [Fact]
  public void Reports_the_value_retained_by_the_modem_when_parameter_write_is_rejected() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP rejecting NV5");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 }, 8, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_SCAN_DW", 9,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 1, 8, 68));
    NvModemParameterRow row = Assert.Single(viewModel.Parameters);
    row.ValueText = "5";
    NvModemDeviceState device = viewModel.SelectedDevice!.State;
    transport.Sent.Clear();

    Assert.True(viewModel.QueueParameterWrites(device, [row], addLegacyRefresh: false));
    viewModel.BeginQueuedWrites(device, keyOnly: false, keyChannel: 0);
    viewModel.HandlePacket(source, ParameterPacket("CH1_SCAN_DW", 9,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 1, 8, 68));
    for (int attempt = 0; attempt < 3; attempt++) {
      now += TimeSpan.FromMilliseconds(1300);
      viewModel.ServiceTransactions();
    }

    Assert.False(viewModel.IsBusy);
    Assert.Equal(3, transport.Sent.Count(sent => sent.Packet is MAVLink.mavlink_param_set_t));
    Assert.Contains("CH1_SCAN_DW", viewModel.Status, StringComparison.Ordinal);
    Assert.Contains("requested 5", viewModel.Status, StringComparison.Ordinal);
    Assert.Contains("reports 9", viewModel.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void Replaces_an_offline_mavlink_address_for_the_same_modem_uid() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    NvModemInfoMessage identity = ModemInfo(5, 7, 0, uidSeed: 0x40);
    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, identity, 10, 12));
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 1,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 1, 10, 12));
    NvModemDeviceState original = viewModel.SelectedDevice!.State;

    now += TimeSpan.FromSeconds(6);
    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, identity, 10, 13));

    NvModemDeviceChoice migrated = Assert.Single(viewModel.Devices);
    Assert.Same(original, migrated.State);
    Assert.Equal((byte)13, migrated.State.Key.ComponentId);
    Assert.Equal(1, migrated.State.Parameters["CH1_MOD"]);
    Assert.Contains("10:13", migrated.Label, StringComparison.Ordinal);

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1 }, 10, 12));
    Assert.Single(viewModel.Devices);
    Assert.Equal((byte)13, viewModel.Devices[0].State.Key.ComponentId);
  }

  [Fact]
  public void Rejects_a_second_live_endpoint_that_reports_the_same_modem_uid() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    NvModemInfoMessage identity = ModemInfo(5, 7, 0, uidSeed: 0x50);

    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, identity, 10, 12));
    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, identity, 10, 13));

    NvModemDeviceChoice modem = Assert.Single(viewModel.Devices);
    Assert.Equal((byte)12, modem.State.Key.ComponentId);
    Assert.Contains("Two live modem endpoints", viewModel.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void Accepts_the_requested_nv5_identity_after_flash_persistence_deadline() {
    var transport = new FakeTransport();
    DateTime now = DateTime.UtcNow;
    using var viewModel = new NvModemViewModel(transport, () => now, startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "shared UDP");
    NvModemInfoMessage identity = ModemInfo(5, 7, 0, uidSeed: 0x60);
    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, identity, 10, 13));
    viewModel.HandlePacket(source, ParameterPacket("MAV_SAVE_MS", 100,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 2, 10, 13));
    viewModel.HandlePacket(source, ParameterPacket("LOCAL_IP_4", 13,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 2, 10, 13));
    NvModemParameterRow localIp = viewModel.Parameters.Single(
        row => row.Name == "LOCAL_IP_4");
    localIp.ValueText = "11";
    NvModemDeviceState device = viewModel.SelectedDevice!.State;
    transport.Sent.Clear();
    Assert.True(viewModel.QueueParameterWrites(device, [localIp], addLegacyRefresh: false));
    viewModel.BeginQueuedWrites(device, keyOnly: false, keyChannel: 0);
    viewModel.HandlePacket(source, ParameterPacket("LOCAL_IP_4", 11,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 2, 10, 13));

    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, identity, 10, 11));
    Assert.Equal((byte)13, Assert.Single(viewModel.Devices).State.Key.ComponentId);

    now += TimeSpan.FromMilliseconds(400);
    viewModel.HandlePacket(source,
        Packet(NvModemMessageIds.NvModemInfo, identity, 10, 11));

    NvModemDeviceChoice migrated = Assert.Single(viewModel.Devices);
    Assert.Equal((byte)11, migrated.State.Key.ComponentId);
    Assert.Contains("requested identity", viewModel.Status, StringComparison.Ordinal);
  }

  [Fact]
  public void Role_presets_follow_the_current_staged_nv5_role() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");
    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0 }, 31, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_MOD", 0,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 2, 31, 68));
    viewModel.HandlePacket(source, ParameterPacket("CH1_ROLE", 0,
        (byte)MAVLink.MAV_PARAM_TYPE.UINT32, 2, 31, 68));

    Assert.False(viewModel.CanStageRxRolePreset);
    Assert.True(viewModel.CanStageTxRolePreset);

    viewModel.StageRadioPresetCommand.Execute("tx");

    Assert.True(viewModel.CanStageRxRolePreset);
    Assert.False(viewModel.CanStageTxRolePreset);

    NvModemParameterRow role = Assert.Single(
        viewModel.Parameters, row => row.Name == "CH1_ROLE");
    role.ValueText = "2";

    Assert.True(viewModel.CanStageRxRolePreset);
    Assert.True(viewModel.CanStageTxRolePreset);
  }

  [Fact]
  public void Transmitter_controls_follow_the_live_nv5_tx_state() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var source = new NvModemLink(new MAVLinkInterface(), "UDP NV5");

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 2, TxState = 1 },
        32, 68));

    Assert.True(viewModel.CanControlTransmitter);
    Assert.False(viewModel.CanEnableTransmitter);
    Assert.True(viewModel.CanSuppressTransmitter);

    transport.Sent.Clear();
    viewModel.SetTransmitterEnabledCommand.Execute("true");
    Assert.Empty(transport.Sent);

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 2, TxState = 2 },
        32, 68));

    Assert.True(viewModel.CanEnableTransmitter);
    Assert.False(viewModel.CanSuppressTransmitter);

    viewModel.HandlePacket(source, Packet(NvModemMessageIds.Nv5LinkStatus,
        new Nv5LinkStatusMessage { Channel = 1, RadioChip = 0, Role = 0, TxState = 0 },
        32, 68));

    Assert.False(viewModel.CanEnableTransmitter);
    Assert.False(viewModel.CanSuppressTransmitter);
  }

  [AvaloniaFact]
  public void Nv_modem_view_and_navigation_entry_are_available() {
    var transport = new FakeTransport();
    using var viewModel = new NvModemViewModel(transport, () => DateTime.UtcNow,
        startTimer: false);
    var view = new NvModemView { DataContext = viewModel };

    Assert.NotNull(view.FindControl<DataGrid>("ParametersGrid"));
    Assert.NotNull(view.FindControl<Button>("LoadParametersButton"));
    Assert.NotNull(view.FindControl<Button>("SaveParametersButton"));
    Assert.NotNull(view.FindControl<Button>("CopyRadioSettingsButton"));
    using var setup = new MissionPlanner.ViewModels.SetupViewModel();
    int sik = setup.Pages.ToList().FindIndex(page => page.Header == "Sik Radio");
    int nv = setup.Pages.ToList().FindIndex(page => page.Header == "NV Modem");
    Assert.Equal(sik + 1, nv);
  }

  private static void AssertMessage(uint id, byte crc, uint minimum, uint length, Type type) {
    MAVLink.message_info info = Assert.Single(MAVLink.MAVLINK_MESSAGE_INFOS,
        candidate => candidate.msgid == id);
    Assert.Equal(crc, info.crc);
    Assert.Equal(minimum, info.minlength);
    Assert.Equal(length, info.length);
    Assert.Equal(type, info.type);
  }

  private static void AssertLayout(uint id, byte crc, uint minimum, uint length, Type type) {
    Assert.Equal((int)length, Marshal.SizeOf(type));
    AssertMessage(id, crc, minimum, length, type);
  }

  // MAVLink's CRC extra hashes the message name and every non-extension field's type, name and
  // array length in wire order, so it pins a hand-written layout to the dialect XML.
  private static byte WireCrcExtra(MAVLink.message_info info) {
    ushort crc = 0xffff;
    void Accumulate(IEnumerable<byte> bytes) {
      foreach (byte value in bytes) {
        byte tmp = (byte)(value ^ (byte)(crc & 0xff));
        tmp ^= (byte)(tmp << 4);
        crc = (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
      }
    }

    Accumulate(Encoding.ASCII.GetBytes(info.name + " "));
    var fields = info.type
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .OrderBy(field => Marshal.OffsetOf(info.type, field.Name).ToInt32());
    foreach (var field in fields) {
      if (Marshal.OffsetOf(info.type, field.Name).ToInt32() >= info.minlength) {
        break;
      }
      string name = string.Concat(field.Name.Select((letter, index) =>
          index > 0 && char.IsUpper(letter) && !char.IsUpper(field.Name[index - 1])
              ? "_" + char.ToLowerInvariant(letter)
              : char.ToLowerInvariant(letter).ToString()));
      Type element = field.FieldType.IsArray ? field.FieldType.GetElementType()! : field.FieldType;
      bool text = name is "path" or "name" or "error_name";
      string wireType = text ? "char" : Type.GetTypeCode(element) switch {
        TypeCode.Byte => "uint8_t",
        TypeCode.SByte => "int8_t",
        TypeCode.UInt16 => "uint16_t",
        TypeCode.Int16 => "int16_t",
        TypeCode.UInt32 => "uint32_t",
        TypeCode.Int32 => "int32_t",
        TypeCode.UInt64 => "uint64_t",
        TypeCode.Int64 => "int64_t",
        TypeCode.Single => "float",
        TypeCode.Double => "double",
        _ => throw new InvalidOperationException($"{info.name}.{field.Name}: {element}"),
      };
      Accumulate(Encoding.ASCII.GetBytes(wireType + " " + name + " "));
      if (field.FieldType.IsArray) {
        var marshal = (MarshalAsAttribute)Attribute.GetCustomAttribute(
            field, typeof(MarshalAsAttribute))!;
        Accumulate([(byte)marshal.SizeConst]);
      }
    }
    return (byte)((crc & 0xff) ^ (crc >> 8));
  }

  private static void DeliverNv5KeyWords(
      NvModemViewModel viewModel,
      NvModemLink source,
      byte systemId,
      byte componentId,
      int channel,
      byte[] key,
      ushort count) {
    Assert.Equal(NvModemCatalog.Nv5KeyBytes, key.Length);
    for (int word = 0; word < NvModemCatalog.Nv5KeyWordCount; word++) {
      viewModel.HandlePacket(source, ParameterPacket(
          NvModemCatalog.Nv5KeyWordName(channel, word),
          NvModemCatalog.Nv5SignedKeyWord(key, word),
          (byte)MAVLink.MAV_PARAM_TYPE.INT32,
          count,
          systemId,
          componentId));
    }
  }

  private static MAVLink.MAVLinkMessage ParameterPacket(
      string name, double value, byte type, ushort count, byte systemId, byte componentId) =>
      Packet((uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE,
          new MAVLink.mavlink_param_value_t {
            param_id = NvModemParameterCodec.NameBytes(name),
            param_value = NvModemParameterCodec.Encode(value, type),
            param_type = type,
            param_count = count,
            param_index = 0,
          }, systemId, componentId);

  private static Nv5RtspConfigMessage RtspPacket(string path) {
    byte[] bytes = new byte[96];
    Encoding.Latin1.GetBytes(path).CopyTo(bytes, 0);
    return new Nv5RtspConfigMessage {
      Operation = 2,
      Path = bytes,
    };
  }

  private static NvModemCachedParameter CachedParameter(
      string name, double value, byte type, int count, byte systemId, byte componentId) {
    float wire = NvModemParameterCodec.Encode(value, type);
    return new NvModemCachedParameter(
        systemId, componentId, name, value, type, count,
        unchecked((uint)BitConverter.SingleToInt32Bits(wire)));
  }

  private static NvModemInfoMessage ModemInfo(
      byte generation, byte productProfile, byte flags,
      byte channel1Role = 0, byte channel2Role = 0,
      byte channel1Chip = 0, byte channel2Chip = 0,
      byte uidSeed = 0) => new() {
        Capabilities = 1,
        TimeBootMs = 1000,
        BuildHash = new byte[8],
        Uid2 = uidSeed == 0
            ? new byte[18]
            : Enumerable.Range(0, 18).Select(index => (byte)(uidSeed + index)).ToArray(),
        SchemaVersion = 1,
        ModemGeneration = generation,
        HardwareVersionMajor = generation,
        HardwareVersionMinor = 1,
        FirmwareVersionMajor = generation,
        ProtocolVersion = 4,
        ProductProfile = productProfile,
        RadioCount = (byte)(((flags & NvModemInfoFlags.Channel1Active) != 0 ? 1 : 0)
        + ((flags & NvModemInfoFlags.Channel2Active) != 0 ? 1 : 0)),
        Flags = flags,
        Channel1Role = channel1Role,
        Channel2Role = channel2Role,
        Channel1RadioChip = channel1Chip,
        Channel2RadioChip = channel2Chip,
      };

  private static MAVLink.MAVLinkMessage NodeInfoPacket(
      string name, byte systemId, byte componentId,
      byte hardwareMajor = 4, byte softwareMajor = 4) {
    byte[] nameBytes = new byte[80];
    Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
    return Packet((uint)MAVLink.MAVLINK_MSG_ID.UAVCAN_NODE_INFO,
        new MAVLink.mavlink_uavcan_node_info_t {
          name = nameBytes,
          hw_unique_id = new byte[16],
          hw_version_major = hardwareMajor,
          sw_version_major = softwareMajor,
        }, systemId, componentId);
  }

  private static MAVLink.MAVLinkMessage Packet(
      uint id, object payload, byte systemId, byte componentId) {
    NvModemMavlinkDialect.Register();
    var generator = new MAVLink.MavlinkParse();
    byte[] bytes = generator.GenerateMAVLinkPacket20(
        (MAVLink.MAVLINK_MSG_ID)id, payload, false, systemId, componentId);
    return new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes))!;
  }

  private sealed class FakeTransport : INvModemMavlinkTransport {
    internal sealed record SentPacket(
        NvModemLink Link, object Packet, byte SystemId, byte ComponentId);

    internal List<NvModemLink> Links { get; } = [];
    internal List<SentPacket> Sent { get; } = [];
    internal Dictionary<NvModemLink, List<NvModemEndpoint>> Endpoints { get; } = [];
    internal Dictionary<NvModemLink, List<MAVLink.MAVLinkMessage>> Cached { get; } = [];
    internal Dictionary<NvModemLink, List<NvModemCachedParameter>> Parameters { get; } = [];

    public event Action<NvModemLink, MAVLink.MAVLinkMessage>? PacketReceived {
      add { }
      remove { }
    }

    public event Action? LinksChanged {
      add { }
      remove { }
    }

    public IReadOnlyList<NvModemLink> Snapshot() => Links;

    public IReadOnlyList<NvModemEndpoint> KnownEndpoints(NvModemLink source) =>
        Endpoints.GetValueOrDefault(source) ?? [];

    public IReadOnlyList<MAVLink.MAVLinkMessage> CachedDiscoveryPackets(NvModemLink source) =>
        Cached.GetValueOrDefault(source) ?? [];

    public IReadOnlyList<NvModemCachedParameter> CachedParameters(NvModemLink source) =>
        Parameters.GetValueOrDefault(source) ?? [];

    public bool Send(NvModemLink source, object packet, byte systemId, byte componentId) {
      Sent.Add(new SentPacket(source, packet, systemId, componentId));
      return true;
    }

    public void Dispose() { }
  }
}
