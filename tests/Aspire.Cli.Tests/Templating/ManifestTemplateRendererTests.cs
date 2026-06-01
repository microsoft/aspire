// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Tests for <see cref="ManifestTemplateRenderer"/> driven by an on-disk
/// <c>template.json</c>. These cover the manifest-specific behaviors that the
/// embedded-template integration tests don't isolate: single-pass replacement
/// (no re-matching of a replacement's output), longest-match priority, suffix
/// renames tied to the authored segment, manifest exclusion from output, and
/// the leftover-conditional-marker safety check.
/// </summary>
public class ManifestTemplateRendererTests
{
    private static async Task RenderAsync(
        string templateJson,
        Action<string> populateSource,
        IReadOnlyDictionary<string, string> symbols,
        IReadOnlyDictionary<string, bool> conditions,
        string outputPath)
    {
        using var sourceDir = new TempDir();
        File.WriteAllText(Path.Combine(sourceDir.Path, TemplateManifest.FileName), templateJson);
        populateSource(sourceDir.Path);

        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new ManifestTemplateRenderer(NullLogger.Instance);
        await renderer.RenderAsync(source, outputPath, symbols, conditions, CancellationToken.None);
    }

    [Fact]
    public async Task RenderAsync_ReplacementOutput_IsNotRescannedByLaterRule()
    {
        // Rule order: raw -> projectName, then safe -> classPrefix. If the project
        // name contains the safe literal, a naive chained Replace would rewrite it a
        // second time. The single-pass renderer must leave the injected value intact.
        using var output = new TempDir();
        await RenderAsync(
            """
            { "replacements": [
                { "from": "RAW_NAME", "to": "projectName" },
                { "from": "SAFE_NAME", "to": "classPrefix" } ] }
            """,
            dir => File.WriteAllText(Path.Combine(dir, "file.txt"), "ns=SAFE_NAME proj=RAW_NAME"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // projectName intentionally contains the OTHER rule's `from` literal.
                ["projectName"] = "SAFE_NAME_suffix",
                ["classPrefix"] = "PREFIX"
            },
            new Dictionary<string, bool>(StringComparer.Ordinal),
            output.Path);

        // The SAFE_NAME injected as part of projectName must NOT have been rewritten
        // to PREFIX; only the original SAFE_NAME token becomes PREFIX.
        Assert.Equal("ns=PREFIX proj=SAFE_NAME_suffix", File.ReadAllText(Path.Combine(output.Path, "file.txt")));
    }

    [Fact]
    public async Task RenderAsync_LongestMatchWins()
    {
        using var output = new TempDir();
        await RenderAsync(
            """
            { "replacements": [
                { "from": "ab", "to": "short" },
                { "from": "abc", "to": "long" } ] }
            """,
            dir => File.WriteAllText(Path.Combine(dir, "file.txt"), "abc"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["short"] = "SHORT",
                ["long"] = "LONG"
            },
            new Dictionary<string, bool>(StringComparer.Ordinal),
            output.Path);

        Assert.Equal("LONG", File.ReadAllText(Path.Combine(output.Path, "file.txt")));
    }

    [Fact]
    public async Task RenderAsync_FileRename_AppliesToContentAndPath()
    {
        using var output = new TempDir();
        await RenderAsync(
            """
            {
              "replacements": [ { "from": "SRC", "to": "projectName", "target": "both" } ],
              "fileRenames": [ { "fromSuffix": "._csproj", "toSuffix": ".csproj" } ]
            }
            """,
            dir =>
            {
                Directory.CreateDirectory(Path.Combine(dir, "SRC.AppHost"));
                File.WriteAllText(Path.Combine(dir, "SRC.AppHost", "SRC.AppHost._csproj"), "<Project>SRC</Project>");
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["projectName"] = "MyApp" },
            new Dictionary<string, bool>(StringComparer.Ordinal),
            output.Path);

        var rendered = Path.Combine(output.Path, "MyApp.AppHost", "MyApp.AppHost.csproj");
        Assert.True(File.Exists(rendered));
        Assert.Equal("<Project>MyApp</Project>", File.ReadAllText(rendered));
    }

    [Fact]
    public async Task RenderAsync_FileRename_DoesNotFireForSymbolValueEndingInSuffix()
    {
        // A project name ending in "._csproj" must not cause a non-project file to be
        // rewritten: the suffix rename is decided from the authored segment, which
        // here ends in ".cs", not "._csproj".
        using var output = new TempDir();
        await RenderAsync(
            """
            {
              "replacements": [ { "from": "SRC", "to": "projectName", "target": "both" } ],
              "fileRenames": [ { "fromSuffix": "._csproj", "toSuffix": ".csproj" } ]
            }
            """,
            dir => File.WriteAllText(Path.Combine(dir, "SRC.cs"), "x"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["projectName"] = "Weird._csproj" },
            new Dictionary<string, bool>(StringComparer.Ordinal),
            output.Path);

        // Segment was "SRC.cs" -> "Weird._csproj.cs"; the trailing suffix is ".cs",
        // so no rename happens and the embedded "._csproj" is left untouched.
        Assert.True(File.Exists(Path.Combine(output.Path, "Weird._csproj.cs")));
    }

    [Fact]
    public async Task RenderAsync_ExcludesManifestFromOutput()
    {
        using var output = new TempDir();
        await RenderAsync(
            """{ "replacements": [] }""",
            dir => File.WriteAllText(Path.Combine(dir, "keep.txt"), "kept"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            output.Path);

        Assert.True(File.Exists(Path.Combine(output.Path, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(output.Path, TemplateManifest.FileName)));
    }

    [Fact]
    public async Task RenderAsync_ProcessesDeclaredConditions()
    {
        using var output = new TempDir();
        await RenderAsync(
            """{ "conditions": [ "useRedis" ] }""",
            dir => File.WriteAllText(
                Path.Combine(dir, "Program.cs"),
                "before\n{{#useRedis}}\nredis-line\n{{/useRedis}}\nafter\n"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["useRedis"] = false },
            output.Path);

        var rendered = File.ReadAllText(Path.Combine(output.Path, "Program.cs"));
        Assert.DoesNotContain("redis-line", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    [Fact]
    public async Task RenderAsync_LeftoverConditionalMarker_Throws()
    {
        using var output = new TempDir();
        // The file uses a conditional marker the manifest does not declare, so the
        // marker survives processing and EnsureNoConditionalMarkers must throw.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RenderAsync(
            """{ "conditions": [] }""",
            dir => File.WriteAllText(Path.Combine(dir, "Program.cs"), "{{#undeclared}}\nx\n{{/undeclared}}\n"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            output.Path));

        Assert.Contains("unprocessed conditional marker", ex.Message);
    }

    [Fact]
    public async Task RenderAsync_MissingManifest_Throws()
    {
        using var sourceDir = new TempDir();
        File.WriteAllText(Path.Combine(sourceDir.Path, "file.txt"), "x");
        using var output = new TempDir();

        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new ManifestTemplateRenderer(NullLogger.Instance);

        await Assert.ThrowsAsync<TemplateManifestException>(() => renderer.RenderAsync(
            source,
            output.Path,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            CancellationToken.None));
    }

    [Fact]
    public async Task RenderAsync_UndeclaredSymbol_ThrowsBeforeWriting()
    {
        using var output = new TempDir();
        await Assert.ThrowsAsync<TemplateManifestException>(() => RenderAsync(
            """{ "replacements": [ { "from": "X", "to": "missingSymbol" } ] }""",
            dir => File.WriteAllText(Path.Combine(dir, "file.txt"), "X"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            output.Path));

        Assert.Empty(Directory.GetFiles(output.Path));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("aspire-cli-manifest-tests-");
        }

        public DirectoryInfo Directory { get; }

        public string Path => Directory.FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
