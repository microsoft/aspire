// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Proves the central thesis of the embedded-templating work: the real shipping
/// templates render through the generic <see cref="ManifestTemplateRenderer"/>
/// pipeline from an ordinary folder on disk (<see cref="DirectoryTemplateSource"/>)
/// byte-for-byte identically to how they render from the assembly's embedded
/// resources (<see cref="EmbeddedResourceTemplateSource"/>). That equivalence is
/// what makes the future "treat this git repo / folder as a template" experience
/// viable without any <c>dotnet new</c> or <c>nupkg</c> involvement — the renderer
/// and per-template manifests are already source-agnostic.
/// </summary>
/// <remarks>
/// The on-disk side renders the actual authored template folder under
/// <c>src/Aspire.Cli/Templating/Templates/&lt;root&gt;</c>, NOT a copy reconstructed
/// from the embedded resources. Comparing the authored folder against the embedded
/// resources therefore also guards embedding fidelity: if the build ever drops a
/// file, mangles a path, or transforms content on its way into the assembly, the
/// parity assertion fails.
///
/// Unlike <see cref="ManifestTemplateRendererTests"/>, which exercises the engine
/// against small synthetic manifests, these tests run the real <c>template.json</c>
/// manifests and file trees the CLI ships, so they also catch a manifest drifting
/// out of sync with its content (an undeclared symbol or condition surfaces as a
/// render failure here).
///
/// Symbol and condition values are derived from each manifest rather than hard
/// coded, so a newly added manifest template is covered automatically once its
/// root is added to the theory data. The values are deliberately non-semantic
/// (e.g. ports are not real numbers): this test verifies source-interchangeability,
/// embedding fidelity, and the structural transforms (substitution + renames), not
/// that the output compiles — that is covered by the per-template embedded apply
/// tests and CLI e2e tests.
/// </remarks>
public class EmbeddedManifestTemplateRenderTests
{
    // Only manifest-driven templates (those carrying a template.json) flow through
    // ManifestTemplateRenderer. The token/conditional-block starters (ts-starter,
    // py-starter, go-starter, empty-apphost) use the older CliTemplateFactory path
    // and are intentionally out of scope here.
    public static TheoryData<string> ManifestTemplateRoots() =>
    [
        "csharp-apphost",
        "csharp-starter",
        "ts-cs-starter",
    ];

    [Theory]
    [MemberData(nameof(ManifestTemplateRoots))]
    public async Task EmbeddedTemplate_RendersIdenticallyFromAuthoredDiskFolder(string templateRoot)
    {
        var embedded = new EmbeddedResourceTemplateSource(typeof(EmbeddedResourceTemplateSource).Assembly, templateRoot);
        var embeddedFiles = embedded.EnumerateFiles();

        var templateFolder = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", templateRoot);
        Assert.True(Directory.Exists(templateFolder), $"Expected authored template folder at '{templateFolder}'.");
        var directory = new DirectoryTemplateSource(new DirectoryInfo(templateFolder));

        var manifest = LoadManifest(embeddedFiles);
        var symbols = DeriveSymbols(manifest);

        var sourceNameReplacement = manifest.Replacements.First(r => r.AppliesToPath && !r.From.StartsWith("{{", StringComparison.Ordinal));
        var sourceNameLiteralBytes = Encoding.UTF8.GetBytes(sourceNameReplacement.From);

        var sourceRenameCounts = manifest.FileRenames
            .ToDictionary(
                r => r.ToSuffix,
                r => embeddedFiles.Count(f => f.RelativePath.EndsWith(r.FromSuffix, StringComparison.Ordinal)),
                StringComparer.Ordinal);

        // Exercise every combination of the declared conditions (not just all-on /
        // all-off) so mixed branches — which affect JSON commas, package refs, and
        // launch settings in the real templates — are covered. The relevant
        // templates declare at most two conditions, so this is at most four cases.
        foreach (var conditions in EnumerateConditionCombinations(manifest.Conditions))
        {
            using var embeddedOutput = new TempDir();
            using var directoryOutput = new TempDir();

            var renderer = new ManifestTemplateRenderer(NullLogger.Instance);
            await renderer.RenderAsync(embedded, embeddedOutput.Path, symbols, conditions, CancellationToken.None);
            await renderer.RenderAsync(directory, directoryOutput.Path, symbols, conditions, CancellationToken.None);

            var embeddedTree = ReadTree(embeddedOutput.Path);
            var directoryTree = ReadTree(directoryOutput.Path);

            var conditionDescription = DescribeConditions(conditions);

            // Core thesis: the embedded resources and the authored folder produce an
            // identical tree, file for file and byte for byte.
            Assert.Equal(
                embeddedTree.Keys.OrderBy(k => k, StringComparer.Ordinal),
                directoryTree.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (relativePath, bytes) in embeddedTree)
            {
                Assert.True(
                    directoryTree.TryGetValue(relativePath, out var directoryBytes) && bytes.AsSpan().SequenceEqual(directoryBytes),
                    $"Content mismatch between embedded and authored-folder rendering for '{relativePath}' (template '{templateRoot}', {conditionDescription}).");
            }

            // Sanity: a real template renders more than a trivial number of files.
            Assert.True(embeddedTree.Count > 1, $"Expected '{templateRoot}' to render multiple files.");

            // The manifest itself must never leak into the rendered output.
            Assert.DoesNotContain(embeddedTree.Keys, k => k.Split('/').Contains(TemplateManifest.FileName));

            // The authored source name must be fully substituted out of every path
            // AND every file's content (the replacement targets both).
            foreach (var (relativePath, bytes) in embeddedTree)
            {
                Assert.False(
                    relativePath.Contains(sourceNameReplacement.From, StringComparison.Ordinal),
                    $"Source name '{sourceNameReplacement.From}' leaked into output path '{relativePath}' (template '{templateRoot}', {conditionDescription}).");
                Assert.True(
                    bytes.AsSpan().IndexOf(sourceNameLiteralBytes) < 0,
                    $"Source name '{sourceNameReplacement.From}' leaked into the content of '{relativePath}' (template '{templateRoot}', {conditionDescription}).");
            }

            // Suffix renames (e.g. ._csproj -> .csproj) must have fired for every
            // authored file carrying the suffix — verified by count, since the
            // templates never ship pre-renamed files.
            foreach (var rename in manifest.FileRenames)
            {
                Assert.DoesNotContain(embeddedTree.Keys, k => k.EndsWith(rename.FromSuffix, StringComparison.Ordinal));

                var renamedCount = embeddedTree.Keys.Count(k => k.EndsWith(rename.ToSuffix, StringComparison.Ordinal));
                Assert.True(
                    renamedCount == sourceRenameCounts[rename.ToSuffix] && renamedCount > 0,
                    $"Expected {sourceRenameCounts[rename.ToSuffix]} '*{rename.ToSuffix}' file(s) after renaming '*{rename.FromSuffix}', found {renamedCount} (template '{templateRoot}', {conditionDescription}).");
            }
        }
    }

    private static TemplateManifest LoadManifest(IReadOnlyList<TemplateFile> files)
    {
        var manifestFile = files.Single(f => string.Equals(f.RelativePath, TemplateManifest.FileName, StringComparison.Ordinal));
        using var stream = manifestFile.OpenRead();
        return TemplateManifest.Parse(stream);
    }

    private static Dictionary<string, string> DeriveSymbols(TemplateManifest manifest)
    {
        // Every distinct replacement target needs a value. The values only have to
        // be deterministic, brace-free (so the renderer's leftover-marker safety
        // checks stay meaningful) and path-safe (projectName is substituted into
        // file paths). Wrapping the symbol name in underscores satisfies all three.
        return manifest.Replacements
            .Select(r => r.To)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => $"_{name}_", StringComparer.Ordinal);
    }

    private static IEnumerable<IReadOnlyDictionary<string, bool>> EnumerateConditionCombinations(IReadOnlyList<string> conditions)
    {
        var combinations = 1 << conditions.Count;
        for (var mask = 0; mask < combinations; mask++)
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var bit = 0; bit < conditions.Count; bit++)
            {
                map[conditions[bit]] = (mask & (1 << bit)) != 0;
            }

            yield return map;
        }
    }

    private static string DescribeConditions(IReadOnlyDictionary<string, bool> conditions)
        => conditions.Count == 0
            ? "no conditions"
            : string.Join(", ", conditions.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"));

    private static Dictionary<string, byte[]> ReadTree(string root)
    {
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            tree[relative] = File.ReadAllBytes(path);
        }

        return tree;
    }

    // Tests run from artifacts/bin/Aspire.Cli.Tests/Debug/net10.0, so the repo root
    // is five directories up. Mirrors the helper in TemplateGitIgnoreTests.
    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("aspire-cli-embedded-render-tests-");
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
