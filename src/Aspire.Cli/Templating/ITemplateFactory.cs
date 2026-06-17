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
    /// runtime availability checks (e.g. SDK availability).
    /// </summary>
    Task<IEnumerable<ITemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<ITemplate>> GetInitTemplatesAsync(CancellationToken cancellationToken = default);
}
