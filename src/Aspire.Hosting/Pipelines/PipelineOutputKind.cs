// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Identifies the kind of artifact produced by a pipeline output.
/// </summary>
[Experimental("ASPIREPIPELINES004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum PipelineOutputKind
{
    /// <summary>
    /// The output is a directory that can contain one or more artifacts.
    /// </summary>
    Directory = 0,

    /// <summary>
    /// The output is a single file.
    /// </summary>
    File = 1,
}
