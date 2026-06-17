// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Templating;

internal sealed class TemplateProvider : ITemplateProvider
{
    private readonly IEnumerable<ITemplateFactory> _factories;

    public TemplateProvider(IEnumerable<ITemplateFactory> factories)
    {
        if (factories == null || !factories.Any())
        {
            throw new ArgumentException(TemplatingStrings.AtLeastOneTemplateFactoryMustBeProvided, nameof(factories));
        }

        _factories = factories;

    }

    public IEnumerable<ITemplate> GetTemplates()
    {
        return _factories.SelectMany(static f => f.GetTemplates());
    }

    public async Task<IEnumerable<ITemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = await Task.WhenAll(_factories.Select(f => f.GetTemplatesAsync(cancellationToken)));
        return templates.SelectMany(static t => t);
    }

    public async Task<IEnumerable<ITemplate>> GetInitTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = await Task.WhenAll(_factories.Select(f => f.GetInitTemplatesAsync(cancellationToken)));
        return templates.SelectMany(static t => t);
    }
}
