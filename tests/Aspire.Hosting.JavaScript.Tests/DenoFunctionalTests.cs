// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

[RequiresTools(["deno"])]
public class DenoFunctionalTests : IClassFixture<DenoAppFixture>
{
    private readonly DenoAppFixture _denoFixture;

    public DenoFunctionalTests(DenoAppFixture denoFixture)
    {
        _denoFixture = denoFixture;
    }

    [Fact]
    public async Task VerifyDenoAppDirectExecutionWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var denoClient = _denoFixture.App.CreateHttpClient(_denoFixture.DenoAppBuilder!.Resource.Name, "http");
        var response = await denoClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from deno!", response);
    }

    [Fact]
    public async Task VerifyDenoAppTaskScriptWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var denoClient = _denoFixture.App.CreateHttpClient(_denoFixture.DenoScriptBuilder!.Resource.Name, "http");
        var response = await denoClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from deno task!", response);
    }
}
