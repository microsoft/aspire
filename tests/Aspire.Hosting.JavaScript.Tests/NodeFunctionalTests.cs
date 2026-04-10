// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

[RequiresTools(["node", "npm"])]
public class NodeFunctionalTests : IClassFixture<NodeAppFixture>
{
    private readonly NodeAppFixture _nodeJsFixture;

    public NodeFunctionalTests(NodeAppFixture nodeJsFixture)
    {
        _nodeJsFixture = nodeJsFixture;
    }

    [Fact]
    public async Task VerifyNodeAppWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var nodeClient = _nodeJsFixture.App.CreateHttpClient(_nodeJsFixture.NodeAppBuilder!.Resource.Name, "http");
        var response = await nodeClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from node!", response);
    }

    [Fact]
    public async Task VerifyNpmAppWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var npmClient = _nodeJsFixture.App.CreateHttpClient(_nodeJsFixture.NpmAppBuilder!.Resource.Name, "http");
        var response = await npmClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from npm!", response);
    }
}
