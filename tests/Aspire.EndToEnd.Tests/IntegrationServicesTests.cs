// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using Aspire.TestProject;
using Aspire.Templates.Tests;
using Aspire.TestUtilities;

namespace Aspire.EndToEnd.Tests;

public class IntegrationServicesTests : IClassFixture<IntegrationServicesFixture>
{
    private readonly IntegrationServicesFixture _integrationServicesFixture;
    private readonly TestOutputWrapper _testOutput;

    public IntegrationServicesTests(ITestOutputHelper testOutput, IntegrationServicesFixture integrationServicesFixture)
    {
        _integrationServicesFixture = integrationServicesFixture;
        _testOutput = new TestOutputWrapper(testOutput);
    }

    [Theory]
    [OuterloopTest("EndToEnd tests require Docker and are slow")]
    [RequiresFeature(TestFeature.Docker)]
    [Trait("scenario", "basicservices")]
    [InlineData(TestResourceNames.postgres)]
    [InlineData(TestResourceNames.efnpgsql)]
    [InlineData(TestResourceNames.redis)]
    public Task VerifyComponentWorks(TestResourceNames resourceName)
        => RunTestAsync(async (cancellationToken) =>
        {
            _integrationServicesFixture.EnsureAppHasResources(resourceName);
            try
            {
                var response = await _integrationServicesFixture.IntegrationServiceA.HttpGetAsync("http", $"/{resourceName}/verify", cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                Assert.True(response.IsSuccessStatusCode, responseContent);
            }
            catch
            {
                await _integrationServicesFixture.DumpComponentLogsAsync(resourceName, _testOutput);
                throw;
            }
        });

    [Fact]
    [OuterloopTest("EndToEnd tests require Docker and are slow")]
    [RequiresFeature(TestFeature.Docker)]
    [Trait("scenario", "basicservices")]
    public Task VerifyHealthyOnIntegrationServiceA()
        => RunTestAsync(async (cancellationToken) =>
        {
            // We wait until timeout for the /health endpoint to return successfully. We assume
            // that components wired up into this project have health checks enabled.
            await _integrationServicesFixture.IntegrationServiceA.WaitForHealthyStatusAsync("http", _testOutput, cancellationToken);
        });

    private async Task RunTestAsync(Func<CancellationToken, Task> test)
    {
        _integrationServicesFixture.Project.EnsureAppHostRunning();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            await test(cancellationToken);
        }
        catch
        {
            await _integrationServicesFixture.Project.DumpDockerInfoAsync(cancellationToken: cancellationToken);
            throw;
        }
    }
}
