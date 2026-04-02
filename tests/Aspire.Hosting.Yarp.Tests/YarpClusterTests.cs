// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Aspire.Hosting.Yarp.Tests;

public class YarpClusterTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void Create_YarpCluster_From_Raw_Strings()
    {
        var cluster = new YarpCluster("raw_cluster", "http://localhost:5000", "https://localhost:5001");
        Assert.Equal("http://localhost:5000", cluster.Targets[0]);
        Assert.Equal("https://localhost:5001", cluster.Targets[1]);
    }

    [Fact]
    public void Create_YarpCluster_From_Endpoints_With_Names()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddResource(new TestResource("ServiceA"))
                              .WithHttpEndpoint(name: "testendpoint")
                              .WithHttpsEndpoint(name: "anotherendpoint");

        var httpEndpoint = resource.GetEndpoint("testendpoint");
        var httpsEndpoint = resource.GetEndpoint("anotherendpoint");

        var httpCluster = new YarpCluster(httpEndpoint);
        Assert.Equal("http://_testendpoint.ServiceA", httpCluster.Targets[0]);

        var httpsCluster = new YarpCluster(httpsEndpoint);
        Assert.Equal("https://_anotherendpoint.ServiceA", httpsCluster.Targets[0]);
    }

    [Fact]
    public void Create_YarpCluster_From_Endpoints_Without_Names()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddResource(new TestResource("ServiceC"))
                              .WithHttpEndpoint()
                              .WithHttpsEndpoint();

        var httpEndpoint = resource.GetEndpoint("http");
        var httpsEndpoint = resource.GetEndpoint("https");

        var httpCluster = new YarpCluster(httpEndpoint);
        Assert.Equal("http://_http.ServiceC", httpCluster.Targets[0]);

        var httpsCluster = new YarpCluster(httpsEndpoint);
        Assert.Equal("https://_https.ServiceC", httpsCluster.Targets[0]);
    }

    [Fact]
    public void Create_YarpCluster_From_Resource_With_One_Endpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var httpService = builder.AddResource(new TestResource("ServiceC"))
                                 .WithHttpEndpoint()
                                 .WithHttpEndpoint(name: "grpc");

        var httpCluster = new YarpCluster(httpService.Resource);
        Assert.Equal($"http://ServiceC", httpCluster.Targets[0]);

        var httpsService = builder.AddResource(new TestResource("ServiceD"))
                                  .WithHttpsEndpoint()
                                  .WithHttpsEndpoint(name: "grpc");

        var httpsCluster = new YarpCluster(httpsService.Resource);
        Assert.Equal($"https://ServiceD", httpsCluster.Targets[0]);
    }

    [Fact]
    public void Create_YarpCluster_From_Resource_With_Both_Endpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var serviceA = builder.AddResource(new TestResource("ServiceA"))
                              .WithHttpEndpoint()
                              .WithHttpsEndpoint();

        var clusterA = new YarpCluster(serviceA.Resource);
        Assert.Equal($"https+http://ServiceA", clusterA.Targets[0]);
    }

    [Fact]
    public void AddCluster_WithStringDestination_CreatesCluster()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            var cluster = config.AddCluster("test-cluster", (object)"http://localhost:5000");
            Assert.NotNull(cluster);
            Assert.Single(cluster.Targets);
            Assert.Equal("http://localhost:5000", cluster.Targets[0]);
        });
    }

    [Fact]
    public void AddCluster_WithUriDestination_CreatesCluster()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            var uri = new Uri("https://example.com:8080");
            var cluster = config.AddCluster("test-cluster", (object)uri);
            Assert.NotNull(cluster);
            Assert.Single(cluster.Targets);
            Assert.Equal(uri, cluster.Targets[0]);
        });
    }

    [Fact]
    public void AddCluster_WithObjectArrayDestinations_CreatesCluster()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            var destinations = new object[] { "http://localhost:5000", new Uri("http://localhost:5001") };
            var cluster = config.AddCluster("test-cluster", destinations);
            Assert.NotNull(cluster);
            Assert.Equal(2, cluster.Targets.Length);
            Assert.Equal("http://localhost:5000", cluster.Targets[0]);
        });
    }

    [Fact]
    public void AddCluster_WithNullObjectDestination_ThrowsArgumentException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            var ex = Assert.Throws<ArgumentException>(() => config.AddCluster("test-cluster", (object)null!));
            Assert.Contains("IValueProvider, string, or Uri", ex.Message);
        });
    }

    [Fact]
    public void AddCluster_WithNullObjectArrayDestinations_ThrowsArgumentNullException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            Assert.Throws<ArgumentNullException>(() => config.AddCluster("test-cluster", (object[])null!));
        });
    }

    [Fact]
    public void AddCluster_WithEmptyDestinationsArray_ThrowsArgumentException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            Assert.Throws<ArgumentException>(() => config.AddCluster("test-cluster", Array.Empty<object>()));
        });
    }

    [Fact]
    public void AddCluster_WithInvalidDestinationType_ThrowsArgumentException()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            var ex = Assert.Throws<ArgumentException>(() => config.AddCluster("test-cluster", new object[] { 123 }));
            Assert.Contains("IValueProvider, string, or Uri", ex.Message);
        });
    }

    [Fact]
    public void AddCluster_WithMixedValidTypes_CreatesCluster()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            var uri = new Uri("https://example.com");
            var refExpr = ReferenceExpression.Create($"http://localhost:5000");
            var cluster = config.AddCluster("test-cluster", new object[] { "http://localhost:5000", uri, refExpr });
            Assert.NotNull(cluster);
            Assert.Equal(3, cluster.Targets.Length);
        });
    }

    [Fact]
    public void AddCluster_WithObjectOverload_ValidatesTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");
        
        yarp.WithConfiguration(config =>
        {
            // Valid object type (string)
            var cluster = config.AddCluster("test-cluster", (object)"http://localhost:5000");
            Assert.NotNull(cluster);
            Assert.Single(cluster.Targets);
            
            // Invalid object type
            var ex = Assert.Throws<ArgumentException>(() => config.AddCluster("test-cluster2", (object)123));
            Assert.Contains("IValueProvider, string, or Uri", ex.Message);
        });
    }

    [Fact]
    public void AddRoute_WithStringTarget_CreatesClusterAndRoute()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");

        yarp.WithConfiguration(config =>
        {
            var route = config.AddRoute("/string/{**catchall}", (object)"https://example.net");

            var cluster = Assert.Single(yarp.Resource.Clusters);
            var storedRoute = Assert.Single(yarp.Resource.Routes);

            Assert.Same(route, storedRoute);
            Assert.Equal("https://example.net", Assert.Single(cluster.Targets));
            Assert.Equal(cluster.ClusterConfig.ClusterId, route.RouteConfig.ClusterId);
            Assert.Equal($"cluster_{YarpConfigurationBuilderHelpers.CreateSyntheticClusterName("/string/{**catchall}", "https://example.net")}", cluster.ClusterConfig.ClusterId);
            Assert.Equal("/string/{**catchall}", route.RouteConfig.Match?.Path);
        });
    }

    [Fact]
    public void AddCatchAllRoute_WithStringTarget_CreatesClusterAndRoute()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var yarp = builder.AddYarp("gateway");

        yarp.WithConfiguration(config =>
        {
            var route = config.AddCatchAllRoute((object)"https://example.org");

            var cluster = Assert.Single(yarp.Resource.Clusters);
            var storedRoute = Assert.Single(yarp.Resource.Routes);

            Assert.Same(route, storedRoute);
            Assert.Equal("https://example.org", Assert.Single(cluster.Targets));
            Assert.Equal(cluster.ClusterConfig.ClusterId, route.RouteConfig.ClusterId);
            Assert.Equal($"cluster_{YarpConfigurationBuilderHelpers.CreateSyntheticClusterName("/{**catchall}", "https://example.org")}", cluster.ClusterConfig.ClusterId);
            Assert.Equal("/{**catchall}", route.RouteConfig.Match?.Path);
        });
    }

    [Fact]
    public void ClusterConfigDtos_StayInSyncWithYarpConfigurationTypes()
    {
        AssertDtoShapeMatchesYarpConfig<YarpForwarderRequestConfig, ForwarderRequestConfig>();
        AssertDtoShapeMatchesYarpConfig<YarpHttpClientConfig, HttpClientConfig>();
        AssertDtoShapeMatchesYarpConfig<YarpWebProxyConfig, WebProxyConfig>();
        AssertDtoShapeMatchesYarpConfig<YarpSessionAffinityConfig, SessionAffinityConfig>();
        AssertDtoShapeMatchesYarpConfig<YarpSessionAffinityCookieConfig, SessionAffinityCookieConfig>();
        AssertDtoShapeMatchesYarpConfig<YarpHealthCheckConfig, HealthCheckConfig>();
        AssertDtoShapeMatchesYarpConfig<YarpActiveHealthCheckConfig, ActiveHealthCheckConfig>();
        AssertDtoShapeMatchesYarpConfig<YarpPassiveHealthCheckConfig, PassiveHealthCheckConfig>();
    }

    private static void AssertDtoShapeMatchesYarpConfig<TDto, TYarp>()
    {
        var dtoProperties = typeof(TDto)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        var yarpProperties = typeof(TYarp)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(yarpProperties, dtoProperties);
    }

    private sealed class TestResource(string name) : IResourceWithServiceDiscovery
    {
        public string Name => name;

        public ResourceAnnotationCollection Annotations { get; } = new ResourceAnnotationCollection();
    }
}
