// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal interface IIntegrationIndexSource
{
    IntegrationIndexDescriptor Index { get; }

    Task<IEnumerable<IntegrationPackageCandidate>> GetPackageCandidatesAsync(
        IntegrationIndexSourceContext context,
        CancellationToken cancellationToken);
}
