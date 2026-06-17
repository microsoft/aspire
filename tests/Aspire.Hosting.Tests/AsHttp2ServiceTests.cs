// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class AsHttp2ServiceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void Http2TransportIsNotSetWhenHttp2ServiceAnnotationIsNotApplied()
    {
        using var testProgram = CreateTestProgram(["--publisher", "manifest"]);

        testProgram.Build();
        testProgram.Run();

        var endpointsForAllServices = testProgram.AppBuilder.Resources.SelectMany(
            r => r.Annotations.OfType<EndpointAnnotation>()
                              .Where(sb => sb.Transport == "http2")
            );

        // There should be no endpoints which are set to transport http2.
        Assert.False(endpointsForAllServices.Any());
    }

    [Fact]
    public void Http2TransportIsSetWhenHttp2ServiceAnnotationIsApplied()
    {
        using var testProgram = CreateTestProgram(["--publisher", "manifest"]);
        testProgram.ServiceABuilder.AsHttp2Service();

        testProgram.Build();
        testProgram.Run();

        var httpEndpoints = testProgram.ServiceABuilder.Resource.Annotations.OfType<EndpointAnnotation>().Where(sb => sb.UriScheme == "http" || sb.UriScheme == "https");
        Assert.Equal(2, httpEndpoints.Count());
        Assert.True(httpEndpoints.All(sb => sb.Transport == "http2"));
    }

    [Fact]
    public void Http2TransportIsNotAppliedToNonHttpEndpoints()
    {
        using var testProgram = CreateTestProgram(["--publisher", "manifest"]);
        testProgram.ServiceABuilder.WithEndpoint(9999, scheme: "tcp");
        testProgram.ServiceABuilder.AsHttp2Service();

        testProgram.Build();
        testProgram.Run();

        var endpoints = testProgram.ServiceABuilder.Resource.Annotations.OfType<EndpointAnnotation>();
        var tcpBinding = endpoints.Single(sb => sb.UriScheme == "tcp");
        Assert.Equal("tcp", tcpBinding.Transport);

        var httpsBinding = endpoints.Single(sb => sb.UriScheme == "https");
        Assert.Equal("http2", httpsBinding.Transport);

        var httpBinding = endpoints.Single(sb => sb.UriScheme == "http");
        Assert.Equal("http2", httpsBinding.Transport);
    }

    private TestProgram CreateTestProgram(string[] args)
    {
        var program = TestProgram.Create<AsHttp2ServiceTests>(args, disableDashboard: true);
        program.AppBuilder.WithTestAndResourceLogging(testOutputHelper);
        return program;
    }
}
