// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing.Tests;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Collection("SlimTestProgram")]
[Trait("Partition", "2")]
public class SlimTestProgramTests
{
    private readonly SlimTestProgramFixture _slimTestProgramFixture;

    public SlimTestProgramTests(SlimTestProgramFixture slimTestProgramFixture)
    {
        _slimTestProgramFixture = slimTestProgramFixture;
    }

    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/9672")]
    public async Task TestProjectStartsAndStopsCleanly()
    {
        var testProgram = _slimTestProgramFixture.TestProgram;

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        // Make sure each service is running
        await EnsureServicesAreRunning(testProgram, cts.Token);
    }

    private static async Task EnsureServicesAreRunning(TestProgram testProgram, CancellationToken cancellationToken)
    {
        var app = testProgram.App ?? throw new ArgumentException("TestProgram.App is null");
        using var clientA = app.CreateHttpClientWithResilience(testProgram.ServiceABuilder.Resource.Name, "http");
        await clientA.GetStringAsync("/", cancellationToken);

        using var clientB = app.CreateHttpClientWithResilience(testProgram.ServiceBBuilder.Resource.Name, "http");
        await clientB.GetStringAsync("/", cancellationToken);

        using var clientC = app.CreateHttpClientWithResilience(testProgram.ServiceCBuilder.Resource.Name, "http");
        await clientC.GetStringAsync("/", cancellationToken);
    }

    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/9671")]
    public async Task TestPortOnEndpointAnnotationAndAllocatedEndpointAnnotationMatch()
    {
        var testProgram = _slimTestProgramFixture.TestProgram;

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        // Make sure each service is running
        await EnsureServicesAreRunning(testProgram, cts.Token);

        foreach (var projectBuilders in testProgram.ServiceProjectBuilders)
        {
            var endpoint = projectBuilders.Resource.Annotations.OfType<EndpointAnnotation>().Single();
            Assert.NotNull(endpoint.AllocatedEndpoint);
            Assert.Equal(endpoint.Port, endpoint.AllocatedEndpoint.Port);
        }
    }

    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/9673")]
    public async Task TestPortOnEndpointAnnotationAndAllocatedEndpointAnnotationMatchForReplicatedServices()
    {
        var testProgram = _slimTestProgramFixture.TestProgram;

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        // Make sure each service is running
        await EnsureServicesAreRunning(testProgram, cts.Token);

        foreach (var projectBuilders in testProgram.ServiceProjectBuilders)
        {
            var endpoint = projectBuilders.Resource.Annotations.OfType<EndpointAnnotation>().Single();
            Assert.NotNull(endpoint.AllocatedEndpoint);
            Assert.Equal(endpoint.Port, endpoint.AllocatedEndpoint.Port);
        }
    }
}
