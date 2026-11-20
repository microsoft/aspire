// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Holds captured entries and the directory used for relative reporting paths.
/// </summary>
internal sealed record PipelineOutputSnapshot(
    string OutputDirectory, IReadOnlyDictionary<string, PipelineOutputFileSnapshot> Files);
