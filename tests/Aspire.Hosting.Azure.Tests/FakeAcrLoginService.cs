// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Publishing;
using Azure.Core;

namespace Aspire.Hosting.Azure.Tests;

internal sealed class FakeAcrLoginService : IAcrLoginService
{
    private const string AcrUsername = "00000000-0000-0000-0000-000000000000";
    
    private readonly IContainerRuntimeResolver _containerRuntimeResolver;

    public bool WasLoginCalled { get; private set; }
    public string? LastRegistryEndpoint { get; private set; }
    public string? LastTenantId { get; private set; }

    public FakeAcrLoginService(IContainerRuntimeResolver containerRuntimeResolver)
    {
        _containerRuntimeResolver = containerRuntimeResolver ?? throw new ArgumentNullException(nameof(containerRuntimeResolver));
    }

    public async Task LoginAsync(
        string registryEndpoint,
        string tenantId,
        TokenCredential credential,
        CancellationToken cancellationToken = default)
    {
        WasLoginCalled = true;
        LastRegistryEndpoint = registryEndpoint;
        LastTenantId = tenantId;
        
        var containerRuntime = await _containerRuntimeResolver.ResolveAsync(cancellationToken);
        await containerRuntime.LoginToRegistryAsync(registryEndpoint, AcrUsername, "fake-refresh-token", cancellationToken);
    }
}
