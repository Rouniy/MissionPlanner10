using System.Text;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MissionPlanner.Services;
using MissionPlanner.ViewModels;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;
using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public class ResxTranslationEditorTests {
  [Fact]
  public void Pinned_upstream_tree_is_accepted_as_the_authoritative_resource_baseline() {
    string root = FindRepoRoot();
    string upstream = root;

    ResxTranslationProject project = ResxTranslationService.Load(upstream, "ru-RU");

    Assert.True(project.ResourceFiles > 100);
    Assert.True(project.Entries.Count > 1000);
    Assert.Contains(project.Entries,
        item => item.RelativePath == "GCSViews/FlightData.resx" && item.Key == "CHK_autopan.Text");
  }

  [Fact]
  public void Load_matches_upstream_keys_existing_culture_and_source_tree_rules() {
    using var source = TempDirectory.Create();
    string views = Path.Combine(source.Path, "GCSViews");
    Directory.CreateDirectory(views);
    WriteResx(Path.Combine(views, "FlightData.resx"),
        ("$this.Text", "Flight Data", null, null),
        ("BUT_save.Text", "Save & Close", null, "Button label"),
        ("plain.key", "not a form resource", null, null),
        ("icon.Text", "binary", "System.Drawing.Bitmap, System.Drawing", null));
    WriteResx(Path.Combine(views, "FlightData.RU-ru.RESX"),
        ("BUT_save.Text", "Сохранить и закрыть", null, "Переведено"));
    WriteResx(Path.Combine(source.Path, "Strings.RESX"),
        ("Welcome", "Welcome <pilot>", null, null),
        ("Image", "base64", "System.Drawing.Bitmap, System.Drawing", null));
    Directory.CreateDirectory(Path.Combine(source.Path, "obj"));
    WriteResx(Path.Combine(source.Path, "obj", "Ignored.resx"),
        ("button.Text", "duplicate build output", null, null));
    Directory.CreateDirectory(Path.Combine(source.Path, "translation"));
    WriteResx(Path.Combine(source.Path, "translation", "Ignored.resx"),
        ("button.Text", "old generated output", null, null));

    ResxTranslationProject project = ResxTranslationService.Load(source.Path, "ru-RU");

    Assert.Equal(2, project.ResourceFiles);
    Assert.Equal(3, project.Entries.Count);
    ResxTranslationEntry translated = Assert.Single(project.Entries,
        item => item.Key == "BUT_save.Text");
    Assert.Equal("GCSViews/FlightData.resx", translated.RelativePath);
    Assert.Equal("Save & Close", translated.SourceText);
    Assert.Equal("Сохранить и закрыть", translated.Translation);
    Assert.Equal("Переведено", translated.Comment);
    Assert.True(translated.HasExistingTranslation);

    ResxTranslationEntry missing = Assert.Single(project.Entries,
        item => item.Key == "$this.Text");
    Assert.Equal(missing.SourceText, missing.Translation);
    Assert.False(missing.HasExistingTranslation);
    Assert.Contains(project.Entries, item => item.Key == "Welcome");
    Assert.DoesNotContain(project.Entries, item => item.Key is "plain.key" or "Image");
    Assert.Empty(project.Warnings);
  }

  [Fact]
  public void Load_surfaces_bad_resx_without_losing_valid_resources_and_rejects_dtds() {
    using var source = TempDirectory.Create();
    WriteResx(Path.Combine(source.Path, "Good.resx"),
        ("button.Text", "Good", null, null));
    File.WriteAllText(Path.Combine(source.Path, "Bad.resx"),
        "<!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><root>"
        + "<data name=\"button.Text\"><value>&xxe;</value></data></root>");

    ResxTranslationProject project = ResxTranslationService.Load(source.Path, "de-DE");

    Assert.Single(project.Entries);
    Assert.Single(project.Warnings);
    Assert.Contains("Bad.resx", project.Warnings[0]);
    Assert.Contains("DTD", project.Warnings[0], StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Load_uses_windows_case_insensitive_resource_identity_on_linux() {
    using var source = TempDirectory.Create();
    WriteResx(Path.Combine(source.Path, "View.resx"),
        ("first.Text", "First", null, null));
    if (File.Exists(Path.Combine(source.Path, "view.RESX"))) {
      // A case-insensitive file system (Windows, default macOS) cannot hold both files.
      return;
    }
    WriteResx(Path.Combine(source.Path, "view.RESX"),
        ("second.Text", "Second", null, null));

    ResxTranslationProject project = ResxTranslationService.Load(source.Path, "de-DE");

    Assert.Equal(1, project.ResourceFiles);
    Assert.Single(project.Entries);
    Assert.Equal("first.Text", project.Entries[0].Key);
    Assert.Single(project.Warnings);
    Assert.Contains("Case-colliding", project.Warnings[0]);
  }

  [Fact]
  public void Export_writes_compatible_sparse_resx_resume_html_and_recoverable_backups() {
    using var output = TempDirectory.Create();
    ResxTranslationEntry[] first = [
      new("GCSViews/FlightData.resx", "BUT_save.Text", "Save", "Сохранить & <закрыть>",
          "Button <label>", false),
      new("GCSViews/FlightData.resx", "$this.Text", "Flight Data", "Flight Data", null, false),
      new("Strings.resx", "Welcome", "Welcome", "Добро пожаловать", null, false),
    ];

    ResxTranslationExportResult initial = ResxTranslationService.Export(
        output.Path, "ru-RU", first);

    Assert.Equal(2, initial.ResourceFiles);
    Assert.Equal(2, initial.TranslatedEntries);
    Assert.Equal(0, initial.OverwrittenFiles);
    Assert.Null(initial.BackupDirectory);
    string flightPath = Path.Combine(output.Path, "GCSViews", "FlightData.ru-RU.resx");
    string stringsPath = Path.Combine(output.Path, "Strings.ru-RU.resx");
    Assert.True(File.Exists(flightPath));
    Assert.True(File.Exists(stringsPath));
    XDocument flight = XDocument.Load(flightPath);
    XElement data = Assert.Single(flight.Root!.Elements("data"));
    Assert.Equal("BUT_save.Text", data.Attribute("name")!.Value);
    Assert.Equal("Сохранить & <закрыть>", data.Element("value")!.Value);
    Assert.Equal("Button <label>", data.Element("comment")!.Value);
    string html = File.ReadAllText(initial.ResumeHtmlPath);
    Assert.Contains("Сохранить &amp; &lt;закрыть&gt;", html);

    var second = first.Select(item => item.Key == "BUT_save.Text"
        ? item with { Translation = "Сохранить" }
        : item).ToArray();
    ResxTranslationExportResult replaced = ResxTranslationService.Export(
        output.Path, "ru-RU", second);

    Assert.Equal(3, replaced.OverwrittenFiles);
    Assert.NotNull(replaced.BackupDirectory);
    Assert.True(File.Exists(Path.Combine(
        replaced.BackupDirectory!, "GCSViews", "FlightData.ru-RU.resx")));
    XDocument backup = XDocument.Load(Path.Combine(
        replaced.BackupDirectory!, "GCSViews", "FlightData.ru-RU.resx"));
    Assert.Equal("Сохранить & <закрыть>", backup.Root!.Element("data")!.Element("value")!.Value);
    XDocument current = XDocument.Load(flightPath);
    Assert.Equal("Сохранить", current.Root!.Element("data")!.Element("value")!.Value);
  }

  [Fact]
  public void Export_replaces_stale_translation_with_valid_empty_resx() {
    using var output = TempDirectory.Create();
    var translated = new ResxTranslationEntry(
        "View.resx", "button.Text", "Save", "Speichern", null, false);
    ResxTranslationService.Export(output.Path, "de-DE", [translated]);

    ResxTranslationService.Export(output.Path, "de-DE",
        [translated with { Translation = "Save" }]);

    XDocument document = XDocument.Load(Path.Combine(output.Path, "View.de-DE.resx"));
    Assert.Empty(document.Root!.Elements("data"));
  }

  [Fact]
  public void Resume_import_accepts_escaped_and_legacy_tables_and_csv_is_rfc4180_safe() {
    using var directory = TempDirectory.Create();
    string path = Path.Combine(directory.Path, "output.html");
    File.WriteAllText(path,
        "<html><body><table>"
        + "<tr><td>GCSViews/FlightData.resx</td><td>BUT_save.Text</td>"
        + "<td>Сохранить &amp; &lt;закрыть&gt;</td></tr>"
        + "<tr><td>View.resx</td><td>literal.Text</td><td>Keep <angle> text</td></tr>"
        + "</table></body></html>", Encoding.UTF8);

    IReadOnlyDictionary<TranslationIdentity, string> values =
        ResxTranslationService.ImportResumeHtml(path);
    Assert.Equal("Сохранить & <закрыть>",
        values[new TranslationIdentity("GCSViews/FlightData.resx", "BUT_save.Text")]);
    Assert.Equal("Keep <angle> text",
        values[new TranslationIdentity("View.resx", "literal.Text")]);
    Assert.True(ResxTranslationService.ResumeFileMatches(
        "MissionPlanner.GCSViews.FlightData.resources", "GCSViews/FlightData.resx"));
    Assert.False(ResxTranslationService.ResumeFileMatches(
        "MissionPlanner.Other.FlightData.resources", "GCSViews/FlightData.resx"));

    string csv = ResxTranslationService.BuildCsv([
      new ResxTranslationEntry("View.resx", "button.Text", "Save, \"now\"", "Строка\n2", null, false),
    ]);
    Assert.StartsWith("File,Key,English,Translation\r\n", csv);
    Assert.Contains("\"Save, \"\"now\"\"\"", csv);
    Assert.Contains("\"Строка\n2\"", csv);
  }

  [Theory]
  [InlineData("../Escape.resx")]
  [InlineData("/absolute.resx")]
  [InlineData("nested//empty.resx")]
  public void Export_rejects_paths_outside_selected_tree(string relativePath) {
    using var output = TempDirectory.Create();
    var entry = new ResxTranslationEntry(
        relativePath, "button.Text", "Save", "Guardar", null, false);

    Assert.Throws<InvalidDataException>(() =>
        ResxTranslationService.Export(output.Path, "es-ES", [entry]));
  }

  [Fact]
  public void Export_does_not_follow_child_directory_symlinks_outside_selected_tree() {
    if (!OperatingSystem.IsLinux()) {
      return;
    }
    using var output = TempDirectory.Create();
    using var outside = TempDirectory.Create();
    Directory.CreateSymbolicLink(Path.Combine(output.Path, "linked"), outside.Path);
    var entry = new ResxTranslationEntry(
        "linked/View.resx", "button.Text", "Save", "Salvar", null, false);

    Assert.Throws<IOException>(() =>
        ResxTranslationService.Export(output.Path, "pt-BR", [entry]));
    Assert.Empty(Directory.GetFiles(outside.Path, "*", SearchOption.AllDirectories));
  }

  [Fact]
  public void Load_honours_pre_cancelled_scan() {
    using var source = TempDirectory.Create();
    WriteResx(Path.Combine(source.Path, "View.resx"),
        ("button.Text", "Save", null, null));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    Assert.Throws<OperationCanceledException>(() =>
        ResxTranslationService.Load(source.Path, "en-GB", cancellation.Token));
  }

  [Fact]
  public async Task View_model_tracks_missing_search_edits_and_resume_import() {
    using var source = TempDirectory.Create();
    WriteResx(Path.Combine(source.Path, "View.resx"),
        ("one.Text", "One", null, null),
        ("two.Text", "Two", null, null));
    using var viewModel = new TranslationEditorViewModel();

    await viewModel.LoadDirectoryAsync(source.Path, "fr-FR");
    Assert.Equal(2, viewModel.TotalCount);
    Assert.Equal(2, viewModel.MissingCount);
    Assert.False(viewModel.HasUnsavedChanges);

    TranslationEditorRow row = Assert.Single(viewModel.AllRows, item => item.Key == "one.Text");
    row.Translation = "Un";
    Assert.True(viewModel.HasUnsavedChanges);
    Assert.Equal(1, viewModel.TranslatedCount);
    Assert.Equal(1, viewModel.MissingCount);
    viewModel.SearchText = "two";
    Assert.Single(viewModel.Rows);
    Assert.Equal("two.Text", viewModel.Rows[0].Key);

    string html = Path.Combine(source.Path, "resume.html");
    File.WriteAllText(html,
        "<table><tr><td>MissionPlanner.View.resources</td><td>two.Text</td><td>Deux</td></tr></table>");
    Assert.Equal(1, viewModel.ImportResume(html));
    Assert.Equal("Deux", Assert.Single(viewModel.AllRows, item => item.Key == "two.Text").Translation);
  }

  [AvaloniaFact]
  public void Native_window_and_developer_tools_expose_official_resedit_workflow() {
    var window = new TranslationEditorWindow();
    using var developerTools = new ConfigDeveloperToolsViewModel();

    Assert.IsType<TranslationEditorViewModel>(window.DataContext);
    Assert.NotNull(window.FindControl<DataGrid>("TranslationGrid"));
    Assert.Contains(developerTools.Actions,
        action => action.Label == "Translation / RESX Editor");
  }

  [Fact]
  public void Culture_and_upstream_key_rules_are_explicit() {
    Assert.Contains(ResxTranslationService.Cultures, item => item.Name == "ru-RU");
    Assert.True(ResxTranslationService.IsTranslatable("FlightData.resx", "$this.Text"));
    Assert.True(ResxTranslationService.IsTranslatable("Strings.resx", "arbitrary"));
    Assert.False(ResxTranslationService.IsTranslatable("FlightData.resx", "button.Size"));
    Assert.Equal("GCSViews/FlightData.zh-Hans.resx",
        ResxTranslationService.LocalizedRelativePath("GCSViews/FlightData.resx", "zh-Hans"));
  }

  private static void WriteResx(
      string path,
      params (string Key, string Value, string? Type, string? Comment)[] entries) {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    XNamespace xml = XNamespace.Xml;
    var root = new XElement("root");
    foreach (var entry in entries) {
      var data = new XElement("data",
          new XAttribute("name", entry.Key),
          new XAttribute(xml + "space", "preserve"));
      if (entry.Type != null) {
        data.Add(new XAttribute("type", entry.Type));
      }
      data.Add(new XElement("value", entry.Value));
      if (entry.Comment != null) {
        data.Add(new XElement("comment", entry.Comment));
      }
      root.Add(data);
    }
    new XDocument(root).Save(path);
  }

  private static string FindRepoRoot() {
    string? path = AppContext.BaseDirectory;
    while (path != null && !File.Exists(Path.Combine(path, "MissionPlanner.slnx"))) {
      path = Directory.GetParent(path)?.FullName;
    }
    return path ?? throw new DirectoryNotFoundException("Repository root not found.");
  }

  private sealed class TempDirectory : IDisposable {
    private TempDirectory(string path) => Path = path;
    public string Path { get; }

    public static TempDirectory Create() {
      string path = System.IO.Path.Combine(
          System.IO.Path.GetTempPath(), "mp-resx-tests-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(path);
      return new TempDirectory(path);
    }

    public void Dispose() {
      try {
        Directory.Delete(Path, recursive: true);
      } catch {
        // A failed test should retain its primary assertion rather than fail during cleanup.
      }
    }
  }
}
