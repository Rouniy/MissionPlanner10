using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Tests;

/// <summary>The surveyed LPS field as native text, and its field document.</summary>
public class Nv5LpsFieldTests {
  [Fact]
  public void A_field_holds_the_origin_the_shared_radio_settings_and_four_survey_rows() {
    Assert.Equal(Nv5LpsTestData.Keys, Nv5LpsField.Keys);
    Assert.Equal(15 + 4 * 12, Nv5LpsField.Keys.Count);
    Assert.False(Nv5LpsField.SurveyKey("LPS_FIELD_GEN"));
    Assert.False(Nv5LpsField.SurveyKey("LPS_RNG_SF"));
    Assert.False(Nv5LpsField.SurveyKey("LPS_RV_HZ"));
    Assert.False(Nv5LpsField.SurveyKey("LPS_RSP_SYM"));
    Assert.True(Nv5LpsField.SurveyKey("LPS_DATUM_LO"));
    Assert.True(Nv5LpsField.SurveyKey("LPS_B4_BVAR"));
    Assert.Equal(Nv5ConfigValueTypes.Float32, Nv5LpsField.TypeOf("LPS_B1_CEN"));
    Assert.Equal(Nv5ConfigValueTypes.Int32, Nv5LpsField.TypeOf("LPS_LAT_E7"));
    Assert.Equal(Nv5ConfigValueTypes.UInt32, Nv5LpsField.TypeOf("LPS_B1_ADDR"));
  }

  [Fact]
  public void A_field_document_round_trips_exactly_and_a_bad_one_gives_nothing() {
    SortedDictionary<string, string> source = Nv5LpsTestData.Field();
    Assert.Equal("", Nv5LpsField.Validate(source));
    Assert.True(Nv5LpsField.FromJson(Nv5LpsField.ToJson(source),
        out SortedDictionary<string, string> restored, out string error), error);
    Assert.Equal(source, restored);

    SortedDictionary<string, string> invalid = Nv5LpsTestData.Copy(source);
    invalid["LPS_B2_ADDR"] = source["LPS_B1_ADDR"];
    Assert.False(Nv5LpsField.FromJson(Nv5LpsField.ToJson(invalid), out restored, out error));
    Assert.Empty(restored);
    Assert.Equal("Beacon IDs and nonzero addresses must be unique.", error);
    invalid = Nv5LpsTestData.Copy(source);
    invalid["LPS_DATUM_LO"] = "4294967296";
    Assert.Equal("Invalid LPS_DATUM_LO.", Nv5LpsField.Validate(invalid));
    invalid = Nv5LpsTestData.Copy(source);
    invalid["LPS_B1_E_MM"] = "-2147483649";
    Assert.Equal("Invalid LPS_B1_E_MM.", Nv5LpsField.Validate(invalid));
    invalid = Nv5LpsTestData.Copy(source);
    invalid["LPS_B1_CNN"] = "-1";
    Assert.Equal("Invalid LPS_B1_CNN.", Nv5LpsField.Validate(invalid));
    invalid["LPS_B1_CNN"] = "0";
    invalid["LPS_B1_CEN"] = "-1";
    Assert.Equal("Beacon 1 survey covariance is not positive semidefinite.",
        Nv5LpsField.Validate(invalid));
    invalid = Nv5LpsTestData.Copy(source);
    invalid["LPS_BCOUNT"] = "2";
    Assert.Equal("A field needs three or four surveyed beacons.", Nv5LpsField.Validate(invalid));
  }

  [Fact]
  public void Only_a_complete_field_document_of_native_text_is_read() {
    string text = System.Text.Encoding.UTF8.GetString(
        Nv5LpsField.ToJson(Nv5LpsTestData.Field()));
    Assert.StartsWith("{\n  \"format\": \"nv5-lps-field\",\n  \"version\": 1,", text.Replace(
        "\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    foreach ((string from, string to, string reason) in new[] {
      ("\"version\": 1", "\"version\": 2", "Not an NV5 LPS field document (version 1)."),
      ("\"nv5-lps-field\"", "\"nv5-settings\"", "Not an NV5 LPS field document (version 1)."),
      ("\"LPS_B4_BVAR\": \"0\"", "\"LPS_B4_BVAR\": 0", "Field values must be exact native text."),
      ("\"LPS_B4_BVAR\": \"0\"", "\"LPS_B4_XXX\": \"0\"", "Field document has missing or unknown values."),
      ("\"LPS_B4_BVAR\": \"0\"", "\"LPS_B4_CUU\": \"0\"", "Field document has missing or unknown values."),
      ("{", "[", "Not an NV5 LPS field document (version 1)."),
    }) {
      Assert.Contains(from, text, StringComparison.Ordinal);
      byte[] changed = System.Text.Encoding.UTF8.GetBytes(text.Replace(from, to,
          StringComparison.Ordinal));
      Assert.False(Nv5LpsField.FromJson(changed, out _, out string error), to);
      Assert.Equal(reason, error);
    }
  }

  [Fact]
  public void Values_are_read_as_the_settings_table_reads_them_and_written_as_it_shows_them() {
    SortedDictionary<string, string> survey = Nv5LpsTestData.Field();
    survey["LPS_B1_E_MM"] = "0x3E8";
    survey["LPS_B2_ADDR"] = "0xBEEF0001";
    survey["LPS_DATUM_LO"] = "0x10";
    survey["LPS_BCOUNT"] = "0x3";
    survey["LPS_B2_CEE"] = "1e-8";
    survey["LPS_B3_CEN"] = "-0";
    Assert.Equal("", Nv5LpsField.Validate(survey));
    SortedDictionary<string, string> canonical = Nv5LpsField.Canonical(survey);
    Assert.Equal("1000", canonical["LPS_B1_E_MM"]);
    Assert.Equal("3203334145", canonical["LPS_B2_ADDR"]);
    Assert.Equal("16", canonical["LPS_DATUM_LO"]);
    Assert.Equal("3", canonical["LPS_BCOUNT"]);
    Assert.Equal("9.99999994E-09", canonical["LPS_B2_CEE"]);
    Assert.Equal("0", canonical["LPS_B3_CEN"]);
    // A value that does not parse stays as it is, for validation to name.
    survey["LPS_B1_ID"] = "north";
    Assert.Equal("north", Nv5LpsField.Canonical(survey)["LPS_B1_ID"]);
  }

  [Fact]
  public void The_generation_follows_the_survey_and_avoids_every_listed_modem() {
    SortedDictionary<string, string> original = Nv5LpsTestData.Field();
    SortedDictionary<string, string> candidate = Nv5LpsTestData.Copy(original);
    Assert.Equal(uint.MaxValue, Nv5LpsField.Generation(candidate, [original]));
    // Radio settings are not the survey: the generation stays.
    candidate["LPS_RNG_SF"] = "9";
    Assert.Equal(uint.MaxValue, Nv5LpsField.Generation(candidate, [original]));
    candidate["LPS_B1_E_MM"] = "1";
    SortedDictionary<string, string> second = Nv5LpsTestData.Copy(original);
    second["LPS_FIELD_GEN"] = "1";
    Assert.Equal(2u, Nv5LpsField.Generation(candidate, [original, second]));
  }

  [Fact]
  public void The_generation_is_kept_when_a_modem_joins_the_field() {
    SortedDictionary<string, string> held = Nv5LpsTestData.Field();
    held["LPS_FIELD_GEN"] = "5";
    var empty = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (string key in Nv5LpsField.Keys) {
      empty[key] = "0";
    }
    // The modem without a field takes generation 5; the one holding it needs no save.
    Assert.Equal(5u, Nv5LpsField.Generation(held, [held, empty]));
    // Generation 5 with another survey elsewhere would not be a new field there.
    SortedDictionary<string, string> other = Nv5LpsTestData.Copy(held);
    other["LPS_B1_E_MM"] = "7";
    Assert.Equal(1u, Nv5LpsField.Generation(held, [held, other]));
  }

  [Fact]
  public void Rows_past_the_count_compare_as_the_modem_stores_them() {
    SortedDictionary<string, string> held = Nv5LpsTestData.Field();
    held["LPS_FIELD_GEN"] = "5";
    SortedDictionary<string, string> shown = Nv5LpsTestData.Copy(held);
    shown["LPS_B4_ID"] = "9";
    shown["LPS_B4_E_MM"] = "5000";
    Assert.Equal(held, Nv5LpsField.Normalized(shown));
    Assert.Equal(5u, Nv5LpsField.Generation(shown, [held]));
  }
}
