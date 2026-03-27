// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

public class NodeFunctionalTests : IClassFixture<NodeAppFixture>
{
    private readonly NodeAppFixture _nodeJsFixture;

    public NodeFunctionalTests(NodeAppFixture nodeJsFixture)
    {
        _nodeJsFixture = nodeJsFixture;
    }

    [Fact]
    [RequiresTools(["node"])]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/4508", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task VerifyNodeAppWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var nodeClient = _nodeJsFixture.App.CreateHttpClient(_nodeJsFixture.NodeAppBuilder!.Resource.Name, "http");
        var response = await nodeClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from node!", response);
    }

    [Fact]
    [RequiresTools(["npm"])]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/4508", typeof(PlatformDetection), nameof(PlatformDetection.IsRunningFromAzdo))]
    public async Task VerifyNpmAppWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var npmClient = _nodeJsFixture.App.CreateHttpClient(_nodeJsFixture.NpmAppBuilder!.Resource.Name, "http");
        var response = await npmClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from npm!", response);
    }
}
