// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Playground.MSTest;

[TestClass]
public sealed class ServiceTests
{
    [TestMethod]
    public async Task SharedServiceResponds()
    {
        using var client = CreateClient();
        var response = await client.GetStringAsync("/");

        Assert.AreEqual("Aspire CLI testing", response);
    }

    [TestMethod]
    public async Task SharedServiceIsHealthy()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public void ControlledFailure()
    {
        Assert.AreEqual("false", Environment.GetEnvironmentVariable("MSTEST_PLAYGROUND_FAIL"),
            "Intentional MSTest failure: run without MSTEST_PLAYGROUND_FAIL=true to pass.");
    }

    [TestMethod]
    [Ignore("Demonstrates a skipped MSTest result in the common Aspire summary.")]
    public void SkippedExample()
    {
        Assert.Fail("An ignored test should not run.");
    }

    private static HttpClient CreateClient()
    {
        var serviceUrl = Environment.GetEnvironmentVariable("MSTEST_SERVICE_URL")
            ?? throw new InvalidOperationException("MSTEST_SERVICE_URL must be supplied by the AppHost.");

        return new HttpClient
        {
            BaseAddress = new Uri(serviceUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}
