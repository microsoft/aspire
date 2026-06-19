// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

[RequiresTools(["bun"])]
public class BunFunctionalTests : IClassFixture<BunAppFixture>
{
    private readonly BunAppFixture _bunFixture;

    public BunFunctionalTests(BunAppFixture bunFixture)
    {
        _bunFixture = bunFixture;
    }

    [Fact]
    public async Task VerifyBunAppDirectExecutionWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var bunClient = _bunFixture.App.CreateHttpClient(_bunFixture.BunAppBuilder!.Resource.Name, "http");
        var response = await bunClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from bun!", response);
    }

    [Fact]
    public async Task VerifyBunAppPackageScriptWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var bunClient = _bunFixture.App.CreateHttpClient(_bunFixture.BunScriptBuilder!.Resource.Name, "http");
        var response = await bunClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from bun script!", response);
    }
}
