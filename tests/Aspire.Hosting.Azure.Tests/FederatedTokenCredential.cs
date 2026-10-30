// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Identity;

namespace Aspire.Hosting.Azure.Tests;

/// <summary>
/// Models an audience-specific access-token cache backed by a non-renewable assertion.
/// </summary>
internal sealed class FederatedTokenCredential : TokenCredential
{
    private readonly object _lock = new();
    private readonly Dictionary<string, AccessToken> _tokens = new(StringComparer.Ordinal);
    private readonly List<string> _requests = [];
    private readonly DateTimeOffset _assertionExpiresOn;
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public FederatedTokenCredential()
    {
        _assertionExpiresOn = _now.AddMinutes(5);
    }

    public string[] Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public string? FailingScope { get; init; }
    public Action<string, CancellationToken>? OnRequest { get; init; }

    public void Advance(TimeSpan elapsed)
    {
        lock (_lock)
        {
            _now += elapsed;
        }
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var scope = Assert.Single(requestContext.Scopes);
            _requests.Add(scope);
            OnRequest?.Invoke(scope, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (scope == FailingScope)
            {
                throw new AuthenticationFailedException("Audience authentication failed.");
            }

            if (_tokens.TryGetValue(scope, out var token) && token.ExpiresOn > _now)
            {
                return token;
            }

            if (_now >= _assertionExpiresOn)
            {
                throw new AuthenticationFailedException("AADSTS700024: Client assertion is not within its valid time range.");
            }

            token = new AccessToken(new TestTokenCredential().GetToken(requestContext, cancellationToken).Token, _now.AddHours(1));
            _tokens[scope] = token;
            return token;
        }
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
