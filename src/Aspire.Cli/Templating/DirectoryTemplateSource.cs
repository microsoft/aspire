// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// A template source backed by a directory on disk. Currently used only by
/// internal tests, but it establishes the contract that any
/// <see cref="ITemplateSource"/> implementation can be rendered through
/// <see cref="TemplateRenderer"/> without per-source changes in the renderer
/// or in template apply methods.
/// </summary>
/// <remarks>
/// This type is the seed for future "external template" support
/// (e.g. <c>aspire new --from &lt;path&gt;</c> or rendering a git checkout)
/// and is not yet exposed via any CLI flag. The CLI's shipping templates
/// continue to flow through <see cref="EmbeddedResourceTemplateSource"/>.
/// </remarks>
internal sealed class DirectoryTemplateSource : ITemplateSource
{
    private readonly DirectoryInfo _root;
    private readonly IReadOnlyList<string> _excludedDirectoryNames;

    public DirectoryTemplateSource(DirectoryInfo root, IReadOnlyList<string>? excludedDirectoryNames = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"Template directory not found: {root.FullName}");
        }

        _root = root;
        // Defaults skip build/output artifacts so a template authored against a
        // working project (bin/, obj/, node_modules/, .git/) can be rendered
        // without bringing those along. Callers can override when a template
        // intentionally ships one of these names.
        _excludedDirectoryNames = excludedDirectoryNames ?? ["bin", "obj", "node_modules", ".git"];
    }

    public IReadOnlyList<TemplateFile> EnumerateFiles()
    {
        var files = new List<TemplateFile>();
        EnumerateDirectory(_root, relativePrefix: string.Empty, files);

        files.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    private void EnumerateDirectory(DirectoryInfo directory, string relativePrefix, List<TemplateFile> files)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            // TemplateFile.RelativePath uses '/' as the separator by contract; the renderer
            // converts to Path.DirectorySeparatorChar when writing to disk.
            var relative = relativePrefix.Length == 0 ? file.Name : $"{relativePrefix}/{file.Name}";
            var capturedFullName = file.FullName;
            files.Add(new TemplateFile(
                RelativePath: relative,
                OpenRead: () => new FileStream(capturedFullName, FileMode.Open, FileAccess.Read, FileShare.Read)));
        }

        foreach (var subDirectory in directory.EnumerateDirectories())
        {
            if (_excludedDirectoryNames.Any(name => string.Equals(name, subDirectory.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var subPrefix = relativePrefix.Length == 0 ? subDirectory.Name : $"{relativePrefix}/{subDirectory.Name}";
            EnumerateDirectory(subDirectory, subPrefix, files);
        }
    }
}
