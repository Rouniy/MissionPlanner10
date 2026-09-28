using System.Text;
using MissionPlanner.ArduPilot;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public class LegacyFirmwareUploaderTests {
  [Fact]
  public void IntelHexParserFillsUnprogrammedGapsWithErasedFlash() {
    string hex = Record(0, 0, 0x10, 0x20) +
        Record(4, 0, 0x50) +
        Record(0, 1);

    byte[] image = LegacyFirmwareUploader.ParseIntelHex(new StringReader(hex));

    Assert.Equal(new byte[] { 0x10, 0x20, 0xff, 0xff, 0x50 }, image);
  }

  [Fact]
  public void IntelHexParserSupportsSegmentAndLinearAddressRecords() {
    string segmented = Record(0, 2, 0x00, 0x01) +
        Record(2, 0, 0xaa) +
        Record(0, 1);
    string linear = Record(0, 4, 0x00, 0x01) +
        Record(0, 0, 0xbb) +
        Record(0, 1);

    byte[] segmentImage = LegacyFirmwareUploader.ParseIntelHex(new StringReader(segmented));
    byte[] linearImage = LegacyFirmwareUploader.ParseIntelHex(new StringReader(linear));

    Assert.Equal(0xaa, segmentImage[0x12]);
    Assert.All(segmentImage.Take(0x12), value => Assert.Equal(0xff, value));
    Assert.Equal(0xbb, linearImage[0x10000]);
  }

  [Fact]
  public void IntelHexParserRejectsChecksumMismatch() {
    string valid = Record(0, 0, 1, 2, 3);
    string corrupt = valid[..^3] + "00\n" + Record(0, 1);

    InvalidDataException error = Assert.Throws<InvalidDataException>(
        () => LegacyFirmwareUploader.ParseIntelHex(new StringReader(corrupt)));

    Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void IntelHexParserRequiresEndOfFileRecord() {
    InvalidDataException error = Assert.Throws<InvalidDataException>(
        () => LegacyFirmwareUploader.ParseIntelHex(new StringReader(Record(0, 0, 1))));

    Assert.Contains("end-of-file", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void IntelHexParserRejectsConflictingOverlappingData() {
    string hex = Record(0, 0, 1) + Record(0, 0, 2) + Record(0, 1);

    InvalidDataException error = Assert.Throws<InvalidDataException>(
        () => LegacyFirmwareUploader.ParseIntelHex(new StringReader(hex)));

    Assert.Contains("conflicts", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("apj", "CubeOrange", "Px4Bootloader")]
  [InlineData("px4", "px4-v2", "Px4Bootloader")]
  [InlineData("vrx", "vrbrain-v51", "Px4Bootloader")]
  [InlineData("hex", "apm1-1280", "Apm1280")]
  [InlineData("hex", "apm1-quad", "Apm2560")]
  [InlineData("hex", "apm2", "Apm2560V2")]
  [InlineData("dfu", "stm32", "Stm32Dfu")]
  public void ManifestTargetMatchesOfficialLegacyBoardFamilies(
      string format, string platform, string expected) {
    Assert.Equal(expected,
        LegacyFirmwareUploader.InferManifestTarget(format, platform)?.ToString());
  }

  [Theory]
  [InlineData("bin", "bebop2")]
  [InlineData("hex", "unknown-board")]
  [InlineData("ELF", "navio2")]
  public void AmbiguousManifestImagesAreNotAutomaticallyFlashed(string format, string platform) {
    Assert.Null(LegacyFirmwareUploader.InferManifestTarget(format, platform));
  }

  [Fact]
  public void FirmwareSelectionAppliesTheFormatFilterThatOfficialUiIgnored() {
    APFirmware.FirmwareInfo[] source = [
      Firmware("Copter", "OFFICIAL", "apm2", "hex", new Version(3, 2)),
      Firmware("Copter", "OFFICIAL", "CubeOrange", "apj", new Version(4, 6)),
      Firmware("FIXED_WING", "OFFICIAL", "apm2", "hex", new Version(3, 4)),
      Firmware("Copter", "BETA", "apm2", "hex", new Version(3, 3)),
    ];

    List<APFirmware.FirmwareInfo> result =
        ConfigFirmwareLegacyViewModel.FilterFirmwareOptions(
            source, "Copter", "OFFICIAL", "apm2", "hex");

    APFirmware.FirmwareInfo selected = Assert.Single(result);
    Assert.Equal(new Version(3, 2), selected.MavFirmwareVersion);
  }

  [Fact]
  public void TargetValidationPreventsWrongTransportByExtension() {
    InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        LegacyFirmwareUploader.ValidateFileTarget(
            "/tmp/copter.apj", LegacyFirmwareTarget.Apm2560V2));

    Assert.Contains("not a valid file type", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  private static APFirmware.FirmwareInfo Firmware(
      string mavType, string release, string platform, string format, Version version) => new() {
    MavType = mavType,
    MavFirmwareVersionType = release,
    Platform = platform,
    Format = format,
    MavFirmwareVersion = version,
  };

  private static string Record(int address, byte type, params byte[] data) {
    var bytes = new List<byte> {
      checked((byte)data.Length),
      checked((byte)(address >> 8)),
      checked((byte)address),
      type,
    };
    bytes.AddRange(data);
    bytes.Add(unchecked((byte)(-bytes.Sum(value => value))));
    var line = new StringBuilder(1 + bytes.Count * 2).Append(':');
    foreach (var value in bytes) {
      line.Append(value.ToString("X2"));
    }
    // A fixed terminator keeps records the same on every platform; the checksum test trims it.
    return line.Append('\n').ToString();
  }
}
