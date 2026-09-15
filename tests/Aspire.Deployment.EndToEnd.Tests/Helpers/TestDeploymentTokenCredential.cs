// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Azure.Core;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

internal sealed class TestDeploymentTokenCredential(AccessToken token) : TokenCredential
{
    internal ConcurrentQueue<string[]> RequestedScopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedScopes.Enqueue([.. requestContext.Scopes]);
        return token;
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
