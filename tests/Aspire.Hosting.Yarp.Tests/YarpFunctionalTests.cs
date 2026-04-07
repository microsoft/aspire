// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Yarp.Transforms;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Yarp.Tests;
public class YarpFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyYarpResourceExtensionsConfig()
    {
        await VerifyYarpResource((yarp, endpoint) =>
        {
            yarp.WithConfiguration(builder =>
            {
                builder.AddRoute("/aspnetapp/{**catch-all}", endpoint)
                       .WithTransformPathRemovePrefix("/aspnetapp");
            });
        });
    }

    private async Task VerifyYarpResource(Action<IResourceBuilder<YarpResource>, EndpointReference> configurator)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var backend = builder
            .AddContainer("backend", "mcr.microsoft.com/dotnet/samples:aspnetapp")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        var yarp = builder.AddYarp("yarp");

        configurator(yarp, backend.GetEndpoint("http"));

        yarp.WithHttpHealthCheck("/heath", 404); // TODO we don't have real health check path yet

        var app = builder.Build();

        await app.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(backend.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(yarp.Resource.Name, cts.Token);

        using var httpClient = app.CreateHttpClient(yarp.Resource.Name);

        using var response200 = await httpClient.GetAsync("/aspnetapp");
        Assert.Equal(System.Net.HttpStatusCode.OK, response200.StatusCode);

        using var response404 = await httpClient.GetAsync("/another");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response404.StatusCode);

        await app.StopAsync();
    }
}
