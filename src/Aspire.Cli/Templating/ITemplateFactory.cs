// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

internal interface ITemplateFactory
{
    /// <summary>
    /// Gets template definitions synchronously for command registration.
    /// This must not perform any I/O or async work.
    /// </summary>
    IEnumerable<ITemplate> GetTemplates();

    /// <summary>
    /// Gets templates that are available for use, performing any necessary
    /// runtime availability checks (e.g. SDK availability). A null source selects
    /// built-in templates; other sources are handled by the appropriate factory.
    /// </summary>
    Task<IEnumerable<ITemplate>> GetTemplatesAsync(TemplateSource? source, CancellationToken cancellationToken = default);
    Task<IEnumerable<ITemplate>> GetInitTemplatesAsync(CancellationToken cancellationToken = default);
}
