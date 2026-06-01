// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Wraps another <see cref="ITemplateSource"/> and hides files that do not match
/// a predicate. Used by <see cref="ManifestTemplateRenderer"/> to keep the
/// <c>template.json</c> manifest out of the rendered output while still reading
/// it from the same source.
/// </summary>
internal sealed class FilteringTemplateSource : ITemplateSource
{
    private readonly ITemplateSource _inner;
    private readonly Func<TemplateFile, bool> _include;

    public FilteringTemplateSource(ITemplateSource inner, Func<TemplateFile, bool> include)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(include);

        _inner = inner;
        _include = include;
    }

    public IReadOnlyList<TemplateFile> EnumerateFiles()
        => _inner.EnumerateFiles().Where(_include).ToArray();
}
