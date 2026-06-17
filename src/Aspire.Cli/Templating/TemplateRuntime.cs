// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// The runtime model used to apply a template.
/// </summary>
internal enum TemplateRuntime
{
    /// <summary>
    /// Indicates a template that runs through the .NET template engine.
    /// </summary>
    DotNet,

    /// <summary>
    /// Indicates a template that runs through the CLI scaffolding flow.
    /// </summary>
    Cli
}
