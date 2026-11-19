using System.Text.RegularExpressions;

namespace MissionPlanner.Tests;

public sealed class TempHandlerAuditTests {
  [Fact]
  public void Every_pinned_temp_click_handler_has_one_closed_classification() {
    string auditPath = FindRepositoryFile("docs/migration/TEMP_HANDLER_AUDIT.md");
    string audit = File.ReadAllText(auditPath);

    MatchCollection rows = Regex.Matches(
        audit,
        @"^\|\s*`(?<handler>\w+_Click(?:_\d+)?)`\s*\|\s*`(?<status>[\w-]+)`\s*\|",
        RegexOptions.Multiline);
    string[] documented = rows.Select(match => match.Groups["handler"].Value).ToArray();
    var acceptedStatuses = new HashSet<string> {
      "ported", "replaced", "obsolete", "unsafe", "platform-specific",
    };

    // The original WinForms source is deliberately no longer kept in the active tree. The
    // live corrected audit is its reviewable migration record. The byte-identical 67-row source
    // snapshot remains separately preserved below docs/migration/Reference for provenance.
    Assert.Equal(68, documented.Length);
    Assert.Equal(documented.Length, documented.Distinct(StringComparer.Ordinal).Count());
    Assert.All(rows.Cast<Match>(), row =>
        Assert.Contains(row.Groups["status"].Value, acceptedStatuses));
    Assert.Contains("The pinned source contains 68 click handlers", audit);
    Assert.Contains("67a3c4f22bd1b38ac499f9756902e04fa4ed8444", audit);
  }

  private static string FindRepositoryFile(string relativePath) {
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
         directory != null;
         directory = directory.Parent) {
      string candidate = Path.Combine(directory.FullName, relativePath);
      if (File.Exists(candidate)) {
        return candidate;
      }
    }
    throw new FileNotFoundException(relativePath);
  }
}
