using System.Xml.Linq;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public class ParameterMetadataIndexTests {
  private static readonly string[] MetaKeys = [
    "humanName", "documentation", "user",
    ParameterMetaDataConstants.DisplayName, ParameterMetaDataConstants.Description,
    ParameterMetaDataConstants.Units, ParameterMetaDataConstants.Range,
    ParameterMetaDataConstants.Values, ParameterMetaDataConstants.Increment,
    ParameterMetaDataConstants.User, ParameterMetaDataConstants.RebootRequired,
    ParameterMetaDataConstants.Bitmask, ParameterMetaDataConstants.ReadOnly,
    "NoSuchKey",
  ];

  private static readonly string[] Vehicles =
    ["ArduCopter", "ArduPlane", "Rover", "ArduSub", "SITL", "NoSuchVehicle", ""];

  // Every name, value and description below is invented. The shapes cover the rules of the
  // linear reader: scoped and unscoped names interleaved in document order, a first match that
  // lacks the key, Values answering from the first match, the field fallback, elements that are
  // skipped, and element names other than "param".
  private const string SyntheticPdef = """
    <paramfile>
      <vehicles>
        <parameters name="Alpha">
          <param name="Alpha:ONE" humanName="scoped one" documentation="scoped doc">
            <field name="Range">1 2</field>
          </param>
          <param name="TWO" humanName="unscoped two first" />
          <param name="Alpha:TWO" humanName="scoped two second" user="Advanced">
            <field name="Units">widgets</field>
          </param>
          <param name="Alpha:THREE" humanName="scoped three first" />
          <param name="THREE" humanName="unscoped three second" documentation="later doc">
            <values>
              <value code="0">Off</value>
              <value code="1">On</value>
            </values>
          </param>
          <param name="FOUR" humanName="four without values" />
          <param name="FOUR">
            <values>
              <value code="7">Never reached</value>
            </values>
          </param>
          <param humanName="no name attribute" />
          <other name="FIVE" humanName="not a param element" />
        </parameters>
        <parameters>
          <param name="SKIPPED" humanName="parent has no attributes" />
        </parameters>
      </vehicles>
      <libraries>
        <parameters name="Shared">
          <param name="ONE" humanName="library one" documentation="library doc" />
          <param name="Beta:ONE" humanName="beta one">
            <field name="Bitmask">0:First,1:Second</field>
          </param>
          <param name="SIX" humanName="six">
            <field name="Values">field named Values is never read</field>
          </param>
        </parameters>
      </libraries>
    </paramfile>
    """;

  private static readonly string[] SyntheticNames =
    ["ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SKIPPED", "Alpha:ONE", "MISSING", ""];

  private static readonly string[] SyntheticVehicles = ["Alpha", "Beta", "Gamma", ""];

  [Fact]
  public void Pdef_index_matches_the_linear_reader_on_the_packaged_overlay() {
    XDocument overlay =
      LoadPackaged(ParameterMetaDataRepositoryAPMpdef.LocalParameterMetaDataFileName);
    string[] names = overlay.Descendants("param")
      .Select(param => param.Attribute("name")!.Value)
      .SelectMany(name => new[] { name, name[(name.IndexOf(':') + 1)..] })
      .Append("NO_SUCH_PARAMETER")
      .Distinct()
      .ToArray();

    int compared = AssertPdefParity(overlay, names, Vehicles);

    Assert.True(compared > 1000, $"Only {compared} combinations were compared.");
  }

  [Fact]
  public void Pdef_index_preserves_the_document_order_rules() {
    XDocument document = XDocument.Parse(SyntheticPdef);

    AssertPdefParity(document, SyntheticNames, SyntheticVehicles);

    // Spot checks pin the rules themselves, not only agreement with the reference copy.
    Assert.Equal("unscoped two first", ReadIndexed(document, "TWO", "humanName", "Alpha"));
    Assert.Equal("Advanced", ReadIndexed(document, "TWO", "user", "Alpha"));
    Assert.Equal("scoped three first", ReadIndexed(document, "THREE", "humanName", "Alpha"));
    Assert.Equal("later doc", ReadIndexed(document, "THREE", "documentation", "Alpha"));
    Assert.Equal("", ReadIndexed(document, "FOUR", ParameterMetaDataConstants.Values, "Alpha"));
    Assert.Equal("", ReadIndexed(document, "THREE", ParameterMetaDataConstants.Values, "Alpha"));
    Assert.Equal("0:Off,1:On",
      ReadIndexed(document, "THREE", ParameterMetaDataConstants.Values, "Gamma"));
    Assert.Equal("scoped one", ReadIndexed(document, "ONE", "humanName", "Alpha"));
    Assert.Equal("library one", ReadIndexed(document, "ONE", "humanName", "Gamma"));
    Assert.Equal("not a param element", ReadIndexed(document, "FIVE", "humanName", "Alpha"));
    Assert.Equal("", ReadIndexed(document, "SKIPPED", "humanName", "Alpha"));
    Assert.Equal("", ReadIndexed(document, "SIX", ParameterMetaDataConstants.Values, "Alpha"));
  }

  [Fact]
  public void Pdef_index_treats_a_null_name_like_the_linear_reader() {
    XDocument document = XDocument.Parse(SyntheticPdef);

    Assert.Equal("no name attribute", ReadLinearPdef(document, null, "humanName", "Alpha"));
    Assert.Equal("no name attribute", ReadIndexed(document, null, "humanName", "Alpha"));
    Assert.Equal(
      ReadLinearPdef(document, null, "documentation", null),
      ReadIndexed(document, null, "documentation", null));
  }

  [Fact]
  public void Pdef_index_handles_documents_without_the_expected_root() {
    Assert.Equal("", ReadIndexed(new XDocument(), "ONE", "humanName", "Alpha"));
    XDocument otherRoot = XDocument.Parse(
      "<other><a><b name='x'><param name='ONE' humanName='n' /></b></a></other>");
    Assert.Equal("", ReadIndexed(otherRoot, "ONE", "humanName", "Alpha"));
    Assert.Equal("", ParameterMetaDataPdefReader.ResolveParameterMetaData(
      null, null, "ONE", "humanName", "Alpha"));
  }

  [Fact]
  public void Pdef_index_is_kept_per_document() {
    XDocument first = SingleParamDocument("first");
    XDocument second = SingleParamDocument("second");

    for (int round = 0; round < 3; round++) {
      Assert.Equal("first", ReadIndexed(first, "ONE", "humanName", "Alpha"));
      Assert.Equal("second", ReadIndexed(second, "ONE", "humanName", "Alpha"));
    }
  }

  [Fact]
  public void Pdef_index_follows_edits_made_after_the_first_lookup() {
    XDocument document = XDocument.Parse(SyntheticPdef);
    Assert.Equal("", ReadIndexed(document, "ADDED", "humanName", "Alpha"));
    Assert.Equal("scoped one", ReadIndexed(document, "ONE", "humanName", "Alpha"));

    XElement parameters = document.Descendants("parameters").First();
    parameters.Add(new XElement("param",
      new XAttribute("name", "ADDED"), new XAttribute("humanName", "added")));
    parameters.Elements("param").First().SetAttributeValue("name", "Alpha:RENAMED");

    Assert.Equal("added", ReadIndexed(document, "ADDED", "humanName", "Alpha"));
    Assert.Equal("scoped one", ReadIndexed(document, "RENAMED", "humanName", "Alpha"));
    Assert.Equal("library one", ReadIndexed(document, "ONE", "humanName", "Alpha"));
    AssertPdefParity(document, [.. SyntheticNames, "ADDED", "RENAMED"], SyntheticVehicles);
  }

  [Fact]
  public void Pdef_index_follows_repeated_edits_of_the_same_document() {
    XDocument document = SingleParamDocument("start");
    XElement param = document.Descendants("param").Single();

    for (int round = 0; round < 5; round++) {
      Assert.Equal("", ReadIndexed(document, $"NAME{round}", "humanName", "Alpha"));
      param.SetAttributeValue("name", $"NAME{round}");
      param.SetAttributeValue("humanName", $"round {round}");

      Assert.Equal($"round {round}", ReadIndexed(document, $"NAME{round}", "humanName", "Alpha"));
      Assert.Equal("", ReadIndexed(document, "ONE", "humanName", "Alpha"));
    }
  }

  [Fact]
  public void Overlay_still_wins_over_the_downloaded_document() {
    XDocument overlay = SingleParamDocument("overlay");
    XDocument downloaded = XDocument.Parse(SyntheticPdef);

    Assert.Equal("overlay", ParameterMetaDataPdefReader.ResolveParameterMetaData(
      overlay, downloaded, "ONE", "humanName", "Alpha"));
    Assert.Equal("scoped doc", ParameterMetaDataPdefReader.ResolveParameterMetaData(
      overlay, downloaded, "ONE", "documentation", "Alpha"));
  }

  [Fact]
  public void Legacy_index_matches_the_linear_lookup_on_the_packaged_backup() {
    XDocument backup = LoadPackaged("ParameterMetaDataBackup.xml");
    var index = new ParameterMetaDataRepositoryAPM.MetaDataIndex(backup);
    string[] legacyKeys = [.. MetaKeys, "", "has space", "1digit"];
    int compared = 0;

    string[] realVehicles = ["ArduCopter2", "ArduPlane", "ArduTracker"];
    string[] vehicles = [.. realVehicles, "NoSuchVehicle", "", "bad name"];
    foreach (string vehicle in vehicles) {
      IEnumerable<string> realNames = realVehicles.Contains(vehicle)
        ? backup.Root!.Element(vehicle)!.Elements().Select(element => element.Name.LocalName)
        : ["ACCEL_Z_D"];
      string[] names = [.. realNames, "NO_SUCH_PARAMETER", "", "has space"];
      foreach (string name in names) {
        foreach (string key in legacyKeys) {
          Assert.Equal(
            ReadLinearLegacy(backup, name, key, vehicle),
            ReadIndexedLegacy(index, name, key, vehicle));
          compared++;
        }
      }
    }

    Assert.True(compared > 50_000, $"Only {compared} combinations were compared.");
  }

  [Fact]
  public void Legacy_index_consults_only_the_first_child_of_each_vehicle_element() {
    XDocument document = XDocument.Parse("""
      <Params>
        <Alpha>
          <ONE><DisplayName>first alpha one</DisplayName></ONE>
          <ONE><DisplayName>never read</DisplayName><Units>never read</Units></ONE>
          <TWO />
          <THREE><Units>widgets</Units></THREE>
        </Alpha>
        <Beta>
          <ONE><DisplayName>beta one</DisplayName></ONE>
        </Beta>
        <Alpha>
          <ONE><Units>second alpha units</Units></ONE>
          <TWO><DisplayName>second alpha two</DisplayName></TWO>
        </Alpha>
        <Alpha />
      </Params>
      """);
    var index = new ParameterMetaDataRepositoryAPM.MetaDataIndex(document);

    foreach (string vehicle in new[] { "Alpha", "Beta", "Gamma" }) {
      foreach (string name in new[] { "ONE", "TWO", "THREE", "FOUR" }) {
        foreach (string key in new[] { "DisplayName", "Units", "Range" }) {
          Assert.Equal(
            ReadLinearLegacy(document, name, key, vehicle),
            ReadIndexedLegacy(index, name, key, vehicle));
        }
      }
    }

    Assert.Equal("first alpha one", ReadIndexedLegacy(index, "ONE", "DisplayName", "Alpha"));
    Assert.Equal("second alpha units", ReadIndexedLegacy(index, "ONE", "Units", "Alpha"));
    Assert.Equal("second alpha two", ReadIndexedLegacy(index, "TWO", "DisplayName", "Alpha"));
    Assert.Equal("", ReadIndexedLegacy(index, null, "DisplayName", "Alpha"));
    Assert.Equal("", ReadIndexedLegacy(index, "ONE", null, "Alpha"));
    Assert.Equal("", ReadIndexedLegacy(index, "ONE", "DisplayName", null));
    var emptyIndex = new ParameterMetaDataRepositoryAPM.MetaDataIndex(new XDocument());
    Assert.Equal("", ReadIndexedLegacy(emptyIndex, "ONE", "DisplayName", "Alpha"));
  }

  private static int AssertPdefParity(XDocument document, string[] names, string[] vehicles) {
    int compared = 0;
    foreach (string vehicle in vehicles) {
      foreach (string name in names) {
        foreach (string key in MetaKeys) {
          Assert.Equal(
            ReadLinearPdef(document, name, key, vehicle),
            ReadIndexed(document, name, key, vehicle));
          compared++;
        }
      }
    }
    return compared;
  }

  private static XDocument LoadPackaged(string fileName) {
    string path = Path.Combine(AppContext.BaseDirectory, fileName);
    Assert.True(File.Exists(path), $"Packaged parameter metadata is missing: {path}");
    return XDocument.Load(path);
  }

  private static XDocument SingleParamDocument(string humanName) => new(
    new XElement("paramfile",
      new XElement("vehicles",
        new XElement("parameters", new XAttribute("name", "a"),
          new XElement("param",
            new XAttribute("name", "ONE"), new XAttribute("humanName", humanName))))));

  private static string ReadIndexed(
      XDocument document, string? nodeKey, string metaKey, string? vehicleType) =>
    ParameterMetaDataPdefReader.ResolveParameterMetaData(
      document, null, nodeKey, metaKey, vehicleType);

  private static string ReadIndexedLegacy(
      ParameterMetaDataRepositoryAPM.MetaDataIndex index,
      string? nodeKey,
      string? metaKey,
      string? vehicleType) {
    try {
      return index.Find(nodeKey, metaKey, vehicleType);
    } catch {
      return string.Empty;
    }
  }

  // The pre-index body of ParameterMetaDataPdefReader.ReadParameterMetaData.
  private static string ReadLinearPdef(
      XDocument parameterMetaData, string? nodeKey, string metaKey, string? vehicleType) {
    var root = parameterMetaData?.Element("paramfile");
    if (root == null)
      return string.Empty;

    var vehicleKey = vehicleType + ":" + nodeKey;
    foreach (var param in root.Elements()
                 .SelectMany(section => section.Elements())
                 .Where(parameters => parameters.HasAttributes)
                 .SelectMany(parameters => parameters.Elements())) {
      var name = param.Attribute("name")?.Value;
      if (name != vehicleKey && name != nodeKey)
        continue;

      var attribute = param.Attribute(metaKey);
      if (attribute != null)
        return attribute.Value;

      if (metaKey == ParameterMetaDataConstants.Values) {
        return string.Join(",", param.Elements("values")
            .Elements("value")
            .Select(value => $"{value.Attribute("code")?.Value}:{value.Value}"));
      }

      var field = param.Elements("field")
          .FirstOrDefault(element => element.Attribute("name")?.Value == metaKey);
      if (field != null)
        return field.Value;
    }

    return string.Empty;
  }

  // The pre-index body of ParameterMetaDataRepositoryAPM.GetParameterMetaData.
  private static string ReadLinearLegacy(
      XDocument document, string? nodeKey, string? metaKey, string? vehicleType) {
    try {
      var elements = document.Element("Params")!.Elements(vehicleType);

      foreach (var element in elements) {
        if (element != null && element.HasElements) {
          var node = element.Element(nodeKey);
          if (node != null && node.HasElements) {
            var metaValue = node.Element(metaKey);
            if (metaValue != null) {
              return metaValue.Value;
            }
          }
        }
      }
    } catch {
    }

    return string.Empty;
  }
}
