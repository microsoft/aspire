// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal sealed record IntegrationIndexDescriptor(
    string Id,
    string DisplayName,
    string Provenance,
    IntegrationIndexSourceKind SourceKind);

internal enum IntegrationIndexSourceKind
{
    StaticGeneratedArtifact,
    DynamicNuGetSearch
}
