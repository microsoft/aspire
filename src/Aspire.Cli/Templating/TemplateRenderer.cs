// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Writes a template tree from any <see cref="ITemplateSource"/> to an output
/// directory, applying a content transform to text files and copying binary
/// files verbatim. This is the single execution surface shared by every
/// template the CLI scaffolds — embedded today, folder/git-backed later.
/// </summary>
/// <remarks>
/// The content transform is intentionally a single <see cref="Func{T, TResult}"/>
/// so callers can compose <c>ApplyTokens</c> and <c>ConditionalBlockProcessor.Process</c>
/// (or any future helper) without the renderer needing to know what each
/// helper does. The renderer only owns the I/O and text-vs-binary policy.
/// </remarks>
internal sealed class TemplateRenderer
{
    /// <summary>
    /// File extensions whose contents are copied byte-for-byte instead of
    /// being routed through the content transformer. Anything not in this
    /// set is treated as text and decoded as UTF-8.
    /// </summary>
    private static readonly HashSet<string> s_binaryExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".ico",
        ".bmp",
        ".webp",
        ".svg",
        ".woff",
        ".woff2",
        ".ttf",
        ".otf"
    ];

    private readonly ILogger _logger;

    public TemplateRenderer(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Renders <paramref name="source"/> into <paramref name="outputPath"/>,
    /// applying <paramref name="contentTransformer"/> to every text file and
    /// (optionally) <paramref name="pathTransformer"/> to every relative file path
    /// before it is written. Creates parent directories as needed and overwrites
    /// existing files.
    /// </summary>
    /// <param name="source">The template source providing the file tree to render.</param>
    /// <param name="outputPath">Absolute or relative path to the output directory.</param>
    /// <param name="contentTransformer">Function applied to the text content of every non-binary file.</param>
    /// <param name="cancellationToken">Cancellation token observed between files and during async I/O.</param>
    /// <param name="pathTransformer">
    /// Optional. Applied to each <see cref="TemplateFile.RelativePath"/> before the
    /// on-disk path is computed so callers can template file names and directory
    /// names (e.g. <c>{{projectName}}.csproj</c> → <c>MyApp.csproj</c>). Defaults
    /// to identity when not supplied — file and directory names pass through
    /// unchanged. Pass an explicit transformer (typically a plain token
    /// replacer) when the template tree has tokens in its paths.
    /// </param>
    public async Task RenderAsync(
        ITemplateSource source,
        string outputPath,
        Func<string, string> contentTransformer,
        CancellationToken cancellationToken,
        Func<string, string>? pathTransformer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(contentTransformer);

        pathTransformer ??= static p => p;

        var files = source.EnumerateFiles();
        _logger.LogDebug("Rendering {FileCount} template files to '{OutputPath}'.", files.Count, outputPath);

        var fullOutputRoot = Path.GetFullPath(outputPath);

        // Resolve every transformed path up front so a misbehaving path transform
        // (two sources collapsing to one target, a traversal sequence, or a
        // separator injected into a segment) fails before any byte is written
        // rather than silently overwriting or escaping the output directory.
        var plannedFiles = new List<(TemplateFile File, string FilePath)>(files.Count);
        var claimedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            // Apply the path transformer to every '/'-separated segment so file
            // names AND directory names can be templated (e.g. `{{projectName}}.csproj`
            // or `{{projectName}}.AppHost/AppHost.cs`).
            var transformedRelative = TransformRelativePath(file.RelativePath, pathTransformer);

            // Source-side paths are always '/'-separated by contract on TemplateFile.
            var nativeRelative = transformedRelative.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(outputPath, nativeRelative);
            var fullFilePath = Path.GetFullPath(filePath);

            // Defense in depth: a transformed path must stay inside the output root.
            if (!fullFilePath.StartsWith(fullOutputRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(fullFilePath, fullOutputRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Template file '{file.RelativePath}' resolved to '{fullFilePath}', which is outside the output directory '{fullOutputRoot}'.");
            }

            // Use a case-insensitive comparer so a collision that would only
            // manifest on a case-insensitive file system (Windows/macOS) is still
            // caught when rendering on Linux.
            if (claimedPaths.TryGetValue(fullFilePath, out var existingSource))
            {
                throw new InvalidOperationException(
                    $"Template files '{existingSource}' and '{file.RelativePath}' both render to '{fullFilePath}'. Adjust the template path replacements so each source maps to a distinct output path.");
            }

            claimedPaths.Add(fullFilePath, file.RelativePath);
            plannedFiles.Add((file, filePath));
        }

        foreach (var (file, filePath) in plannedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            using var stream = file.OpenRead();
            _logger.LogDebug("Writing template file '{RelativePath}' to '{FilePath}'.", file.RelativePath, filePath);

            if (s_binaryExtensions.Contains(Path.GetExtension(filePath)))
            {
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream, cancellationToken);
            }
            else
            {
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken);
                var transformed = contentTransformer(content);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                // Match the previous behavior: UTF-8 with no BOM so generated files
                // diff cleanly against any hand-edited source the user keeps in git.
                await using var writer = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(transformed.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private static string TransformRelativePath(string relativePath, Func<string, string> pathTransformer)
    {
        // Apply the transformer per-segment rather than over the whole path so a
        // transformer that incidentally produces a '/' (e.g. token expansion) can't
        // accidentally split a single segment into two directories.
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var transformed = pathTransformer(segments[i]);

            // A transformed segment must remain a single, well-formed path segment.
            // Reject separators (a token expansion that injected one), empty
            // segments, and the relative specifiers '.'/'..' so a template can
            // never assemble a traversal or collapse two segments into one.
            if (transformed.Length == 0
                || transformed is "." or ".."
                || transformed.Contains('/')
                || transformed.Contains('\\'))
            {
                throw new InvalidOperationException(
                    $"Template path segment '{segments[i]}' transformed to '{transformed}', which is not a valid single path segment.");
            }

            segments[i] = transformed;
        }

        return string.Join('/', segments);
    }
}
