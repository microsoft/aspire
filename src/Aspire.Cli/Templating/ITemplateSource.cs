// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Abstracts the location and enumeration of a template tree so that the
/// rendering pipeline does not need to know whether the files come from
/// embedded resources, a folder on disk, a git checkout, or any future
/// transport.
/// </summary>
/// <remarks>
/// The CLI today ships only embedded-resource templates (see
/// <see cref="EmbeddedResourceTemplateSource"/>). Future external-template
/// work (a folder argument, a git URL, an OCI artifact) plugs in by
/// providing additional implementations of this interface without touching
/// <see cref="TemplateRenderer"/> or any per-template apply method.
/// </remarks>
internal interface ITemplateSource
{
    /// <summary>
    /// Returns every file in the template tree in a stable order. The
    /// order is implementation-defined but must be deterministic so
    /// rendering is reproducible.
    /// </summary>
    IReadOnlyList<TemplateFile> EnumerateFiles();
}
