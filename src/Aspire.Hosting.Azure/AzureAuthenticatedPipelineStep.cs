// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Declares the token scopes used by an Azure deployment step.
/// </summary>
internal sealed class AzureAuthenticatedPipelineStep : PipelineStep
{
    public required IReadOnlyList<string> TokenScopes { get; init; }
}
