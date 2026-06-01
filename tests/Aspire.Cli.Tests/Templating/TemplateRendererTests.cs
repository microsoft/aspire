// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Smoke tests for the <see cref="TemplateRenderer"/> + <see cref="ITemplateSource"/> seam.
/// These exist to lock in the contract that any <see cref="ITemplateSource"/>
/// implementation (today embedded resources and on-disk directories; tomorrow
/// git checkouts / external trees) renders identically through the same renderer.
/// </summary>
public class TemplateRendererTests
{
    [Fact]
    public async Task RenderAsync_DirectorySource_CopiesFilesAndAppliesTransformer()
    {
        using var sourceDir = new TempDirectory();
        // Build a small template tree on disk so we can exercise the seam without
        // depending on the embedded templates the CLI ships.
        File.WriteAllText(Path.Combine(sourceDir.Path, "README.md"), "Hello {{projectName}}!");
        Directory.CreateDirectory(Path.Combine(sourceDir.Path, "src"));
        File.WriteAllText(Path.Combine(sourceDir.Path, "src", "main.txt"), "version={{aspireVersion}}");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        await renderer.RenderAsync(
            source,
            outputDir.Path,
            content => content
                .Replace("{{projectName}}", "MyApp")
                .Replace("{{aspireVersion}}", "13.4.0"),
            CancellationToken.None);

        Assert.Equal("Hello MyApp!", File.ReadAllText(Path.Combine(outputDir.Path, "README.md")));
        Assert.Equal("version=13.4.0", File.ReadAllText(Path.Combine(outputDir.Path, "src", "main.txt")));
    }

    [Fact]
    public async Task RenderAsync_DirectorySource_SkipsExcludedDirectories()
    {
        using var sourceDir = new TempDirectory();
        File.WriteAllText(Path.Combine(sourceDir.Path, "keep.txt"), "kept");
        Directory.CreateDirectory(Path.Combine(sourceDir.Path, "bin"));
        File.WriteAllText(Path.Combine(sourceDir.Path, "bin", "build.log"), "skipped");
        Directory.CreateDirectory(Path.Combine(sourceDir.Path, "node_modules"));
        File.WriteAllText(Path.Combine(sourceDir.Path, "node_modules", "huge.bin"), "skipped");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        await renderer.RenderAsync(source, outputDir.Path, content => content, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(outputDir.Path, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(outputDir.Path, "bin")));
        Assert.False(Directory.Exists(Path.Combine(outputDir.Path, "node_modules")));
    }

    [Fact]
    public async Task RenderAsync_DirectorySource_TreatsKnownBinaryExtensionsAsBinary()
    {
        using var sourceDir = new TempDirectory();
        // 16 bytes that include sequences which would be mangled by a text round-trip
        // (BOM-like prefix, embedded null, high-bit bytes, CR+LF). If the renderer
        // mistakenly went through StreamReader for this extension, those bytes would
        // not survive unchanged.
        var binaryBytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x00, 0x01, 0xFF, 0xFE, 0xFD, 0x0D, 0x0A, 0x7F, 0x80, 0x90, 0xA0, 0xB0, 0xC0 };
        File.WriteAllBytes(Path.Combine(sourceDir.Path, "image.png"), binaryBytes);

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        // Use a content transformer that would corrupt text — if the binary path is
        // wired correctly, the transformer is never invoked for the .png file and
        // the bytes round-trip unchanged.
        await renderer.RenderAsync(source, outputDir.Path, _ => "CORRUPTED", CancellationToken.None);

        Assert.Equal(binaryBytes, File.ReadAllBytes(Path.Combine(outputDir.Path, "image.png")));
    }

    [Fact]
    public async Task RenderAsync_EnumerationIsDeterministic()
    {
        // Order of rendering is part of the renderer contract (callers may rely on
        // it for reproducible diffs / parallel writes). Cover it explicitly.
        using var sourceDir = new TempDirectory();
        File.WriteAllText(Path.Combine(sourceDir.Path, "c.txt"), "c");
        File.WriteAllText(Path.Combine(sourceDir.Path, "a.txt"), "a");
        File.WriteAllText(Path.Combine(sourceDir.Path, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(sourceDir.Path, "sub"));
        File.WriteAllText(Path.Combine(sourceDir.Path, "sub", "a.txt"), "sub-a");

        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));

        var firstOrder = source.EnumerateFiles().Select(f => f.RelativePath).ToArray();
        var secondOrder = source.EnumerateFiles().Select(f => f.RelativePath).ToArray();

        Assert.Equal(firstOrder, secondOrder);
        // And the order itself is ordinal-sorted.
        Assert.Equal(firstOrder, firstOrder.OrderBy(static p => p, StringComparer.Ordinal).ToArray());

        await Task.CompletedTask;
    }

    [Fact]
    public async Task RenderAsync_AppliesPathTransformer_RenamesFilesAndDirectories()
    {
        using var sourceDir = new TempDirectory();
        // Simulates a multi-project template tree where both a directory name
        // (`{{projectName}}.AppHost/`) and a file inside it
        // (`{{projectName}}.csproj`) need to be templated.
        Directory.CreateDirectory(Path.Combine(sourceDir.Path, "{{projectName}}.AppHost"));
        File.WriteAllText(Path.Combine(sourceDir.Path, "{{projectName}}.AppHost", "{{projectName}}.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(sourceDir.Path, "README.md"), "Hello {{projectName}}!");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        await renderer.RenderAsync(
            source,
            outputDir.Path,
            content => content.Replace("{{projectName}}", "MyApp"),
            CancellationToken.None,
            pathTransformer: path => path.Replace("{{projectName}}", "MyApp"));

        Assert.True(Directory.Exists(Path.Combine(outputDir.Path, "MyApp.AppHost")));
        Assert.True(File.Exists(Path.Combine(outputDir.Path, "MyApp.AppHost", "MyApp.csproj")));
        Assert.True(File.Exists(Path.Combine(outputDir.Path, "README.md")));
        Assert.Equal("Hello MyApp!", File.ReadAllText(Path.Combine(outputDir.Path, "README.md")));
    }

    [Fact]
    public async Task RenderAsync_PathTransformer_DefaultsToIdentity()
    {
        // When no path transformer is supplied, file/directory names pass through
        // unchanged. This protects callers (and binary fixtures) from having their
        // paths munged by a content transformer that was only designed for text.
        using var sourceDir = new TempDirectory();
        File.WriteAllText(Path.Combine(sourceDir.Path, "{{name}}.txt"), "content");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        await renderer.RenderAsync(
            source,
            outputDir.Path,
            content => content.Replace("{{name}}", "rendered"),
            CancellationToken.None);

        // Filename is left untouched because no path transformer was supplied.
        Assert.True(File.Exists(Path.Combine(outputDir.Path, "{{name}}.txt")));
    }

    [Fact]
    public async Task RenderAsync_PathTransformer_CanDifferFromContentTransformer()
    {
        // The dual-transformer overload exists specifically so callers that compose
        // line-oriented content passes (e.g. ConditionalBlockProcessor) can use a
        // plain token replacer for paths. Cover that explicitly.
        using var sourceDir = new TempDirectory();
        File.WriteAllText(Path.Combine(sourceDir.Path, "{{name}}.txt"), "value={{value}}");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        await renderer.RenderAsync(
            source,
            outputDir.Path,
            contentTransformer: c => c.Replace("{{value}}", "42").Replace("{{name}}", "ignored-by-content"),
            CancellationToken.None,
            pathTransformer: p => p.Replace("{{name}}", "from-path-transformer"));

        Assert.True(File.Exists(Path.Combine(outputDir.Path, "from-path-transformer.txt")));
        Assert.Equal("value=42", File.ReadAllText(Path.Combine(outputDir.Path, "from-path-transformer.txt")));
    }

    [Fact]
    public async Task RenderAsync_PathTransformerCollision_ThrowsBeforeWritingAnything()
    {
        using var sourceDir = new TempDirectory();
        // Two distinct source files that a buggy path transform collapses onto the
        // same output path. The renderer must detect this up front and refuse to
        // render rather than silently letting one file clobber the other.
        File.WriteAllText(Path.Combine(sourceDir.Path, "a.txt"), "a");
        File.WriteAllText(Path.Combine(sourceDir.Path, "b.txt"), "b");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.RenderAsync(
            source,
            outputDir.Path,
            content => content,
            CancellationToken.None,
            pathTransformer: _ => "same.txt"));

        Assert.Contains("render to", ex.Message);
        // Nothing should have been written because the collision is detected before the write loop.
        Assert.Empty(Directory.GetFiles(outputDir.Path));
    }

    [Fact]
    public async Task RenderAsync_PathTransformerEscapesOutputRoot_Throws()
    {
        using var sourceDir = new TempDirectory();
        File.WriteAllText(Path.Combine(sourceDir.Path, "evil.txt"), "x");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        // A transformer that injects a traversal segment must be rejected — either by
        // the per-segment guard (separator/'..' in a segment) or the containment check.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.RenderAsync(
            source,
            outputDir.Path,
            content => content,
            CancellationToken.None,
            pathTransformer: _ => ".."));

        Assert.NotNull(ex);
        Assert.Empty(Directory.GetFiles(outputDir.Path));
    }

    [Fact]
    public async Task RenderAsync_PathTransformerInjectsSeparator_Throws()
    {
        using var sourceDir = new TempDirectory();
        File.WriteAllText(Path.Combine(sourceDir.Path, "file.txt"), "x");

        using var outputDir = new TempDirectory();
        var source = new DirectoryTemplateSource(new DirectoryInfo(sourceDir.Path));
        var renderer = new TemplateRenderer(NullLogger.Instance);

        // A single segment must stay a single segment; a transform that splits it
        // into two directories is rejected so token expansion can't restructure the tree.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.RenderAsync(
            source,
            outputDir.Path,
            content => content,
            CancellationToken.None,
            pathTransformer: _ => "nested/file.txt"));

        Assert.Contains("valid single path segment", ex.Message);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            // Use CreateTempSubdirectory per repo guidance (avoids racing on a
            // predictable Path.Combine(GetTempPath(), ...) path).
            Directory = System.IO.Directory.CreateTempSubdirectory("aspire-cli-templating-tests-");
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
                // Best-effort cleanup — leaving a stray temp dir is non-fatal.
            }
        }
    }
}
