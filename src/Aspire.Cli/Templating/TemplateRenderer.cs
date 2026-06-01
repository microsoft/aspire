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
    /// applying <paramref name="contentTransformer"/> to every text file.
    /// Creates parent directories as needed. Overwrites existing files.
    /// </summary>
    public async Task RenderAsync(
        ITemplateSource source,
        string outputPath,
        Func<string, string> contentTransformer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(contentTransformer);

        var files = source.EnumerateFiles();
        _logger.LogDebug("Rendering {FileCount} template files to '{OutputPath}'.", files.Count, outputPath);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Source-side paths are always '/'-separated by contract on TemplateFile.
            var nativeRelative = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(outputPath, nativeRelative);
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
}
