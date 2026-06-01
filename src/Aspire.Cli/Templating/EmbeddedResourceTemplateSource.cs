// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Cli.Templating;

/// <summary>
/// A template source backed by assembly manifest resources. Used for templates
/// that ship inside the CLI binary under <c>src/Aspire.Cli/Templating/Templates/&lt;name&gt;/</c>.
/// </summary>
/// <remarks>
/// Embedded resource names use <c>.</c> as the path separator at the manifest
/// level (e.g. <c>ts-starter.src.App.tsx</c>). This source restores the
/// original <c>/</c>-separated tree path by stripping the
/// <see cref="TemplateRoot"/> prefix and replacing remaining <c>.</c>s with
/// <c>/</c>… but only for the segments preceding the file extension. The
/// project files are bundled by the SDK with <c>LogicalName</c> set to the
/// physical relative path, so the inverse transformation is exact for our
/// trees (see <c>Aspire.Cli.csproj</c>).
/// </remarks>
internal sealed class EmbeddedResourceTemplateSource : ITemplateSource
{
    private readonly Assembly _assembly;
    private readonly string _templateRoot;
    private readonly string _resourcePrefix;

    public EmbeddedResourceTemplateSource(Assembly assembly, string templateRoot)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateRoot);

        _assembly = assembly;
        _templateRoot = templateRoot;
        _resourcePrefix = $"{templateRoot}.";
    }

    public string TemplateRoot => _templateRoot;

    public IReadOnlyList<TemplateFile> EnumerateFiles()
    {
        var allResourceNames = _assembly.GetManifestResourceNames();
        var matches = allResourceNames
            .Where(name => name.StartsWith(_resourcePrefix, StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"No embedded template resources found for '{_templateRoot}'. Available manifest resources: {string.Join(", ", allResourceNames)}");
        }

        var files = new List<TemplateFile>(matches.Length);
        foreach (var resourceName in matches)
        {
            // Resource names preserve the physical relative path because the project
            // bundles each file with LogicalName="<root>.<recursive-dir><filename>" via
            // %(RecursiveDir) (see Aspire.Cli.csproj). %(RecursiveDir) uses the build
            // host's platform separator — '\' on Windows, '/' on Linux/macOS — so
            // normalize to '/' regardless of where the assembly was built. The
            // renderer maps back to Path.DirectorySeparatorChar when writing to disk.
            var relative = resourceName[_resourcePrefix.Length..].Replace('\\', '/');
            var capturedResourceName = resourceName;
            files.Add(new TemplateFile(
                RelativePath: relative,
                OpenRead: () => _assembly.GetManifestResourceStream(capturedResourceName)
                    ?? throw new InvalidOperationException($"Embedded template resource not found: {capturedResourceName}")));
        }

        return files;
    }
}
