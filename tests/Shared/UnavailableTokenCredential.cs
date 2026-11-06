// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Identity;

namespace Aspire.Components.ConformanceTests;

/// <summary>
/// A <see cref="TokenCredential"/> that fails immediately without making network calls or launching processes.
/// </summary>
/// <remarks>
/// Conformance tests use it when the test server isn't reachable, instead of letting the component fall back to
/// <see cref="DefaultAzureCredential"/>, whose credential chain can take longer than the RemoteExecutor timeout.
/// The client operation still runs (and emits its activities) up to the point where it needs a token.
/// </remarks>
internal sealed class UnavailableTokenCredential : TokenCredential
{
    public static UnavailableTokenCredential Instance { get; } = new();

    private UnavailableTokenCredential()
    {
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => throw new CredentialUnavailableException("The conformance test server is not reachable.");

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => ValueTask.FromException<AccessToken>(new CredentialUnavailableException("The conformance test server is not reachable."));
}
