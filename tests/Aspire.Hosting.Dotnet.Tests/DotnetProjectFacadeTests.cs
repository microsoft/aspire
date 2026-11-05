// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectFacadeTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ConcreteFacadesAreStableAndRetainTheSameBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProject("service", "Service.fsproj", options => options.ExcludeLaunchProfile = true);

        Assert.Same(resource, resource.WithReplicas(3));
        Assert.Same(resource, resource.DisableForwardedHeaders());
        Assert.Same(resource, resource.DisableForwardedHeaders());
        Assert.Same(resource, resource.WithEndpointsInEnvironment(endpoint => endpoint.Name == "http"));
        Assert.Equal(3, Assert.Single(resource.Resource.Annotations.OfType<ReplicaAnnotation>()).Replicas);
        Assert.Single(resource.Resource.Annotations.OfType<DisableForwardedHeadersAnnotation>());
        var filter = Assert.Single(resource.Resource.Annotations.OfType<EndpointEnvironmentInjectionFilterAnnotation>()).Filter;
        Assert.True(filter(new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http")));
        Assert.False(filter(new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "admin")));
        Assert.Same(
            Assert.Single(resource.Resource.Annotations.OfType<IProjectMetadata>()),
            resource.Resource.GetProjectMetadata());

        var type = typeof(DotnetProjectHostingExtensions);
        Assert.Null(type.GetCustomAttribute<ExperimentalAttribute>());
        foreach (var name in new[]
        {
            nameof(DotnetProjectHostingExtensions.WithReplicas),
            nameof(DotnetProjectHostingExtensions.DisableForwardedHeaders),
            nameof(DotnetProjectHostingExtensions.WithEndpointsInEnvironment),
            nameof(DotnetProjectHostingExtensions.GetProjectMetadata)
        })
        {
            var method = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static), method => method.Name == name);
            Assert.Null(method.GetCustomAttribute<ExperimentalAttribute>());
            Assert.False(method.IsGenericMethod);
        }
    }

    [Fact]
    public void FacadesPreserveTheExistingPolyglotCapabilityIdsWithoutDuplicates()
    {
        var exports = typeof(DotnetProjectHostingExtensions).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetCustomAttributes<AspireExportAttribute>().Select(export => (method, export)))
            .Where(item => item.export.Id is
                "withDotnetProjectReplicas" or
                "disableDotnetProjectForwardedHeaders" or
                "withDotnetProjectEndpointsInEnvironment")
            .OrderBy(item => item.export.Id, StringComparer.Ordinal)
            .ToArray();

        Assert.Collection(exports,
            item =>
            {
                Assert.Equal("disableDotnetProjectForwardedHeaders", item.export.Id);
                Assert.Equal("disableForwardedHeaders", item.export.MethodName);
                Assert.Equal(nameof(DotnetProjectHostingExtensions.DisableForwardedHeaders), item.method.Name);
                Assert.True(item.method.IsPublic);
            },
            item =>
            {
                Assert.Equal("withDotnetProjectEndpointsInEnvironment", item.export.Id);
                Assert.Equal("withEndpointsInEnvironment", item.export.MethodName);
                Assert.Equal(typeof(string[]), item.method.GetParameters()[1].ParameterType);
            },
            item =>
            {
                Assert.Equal("withDotnetProjectReplicas", item.export.Id);
                Assert.Equal("withReplicas", item.export.MethodName);
                Assert.Equal(nameof(DotnetProjectHostingExtensions.WithReplicas), item.method.Name);
                Assert.True(item.method.IsPublic);
            });
        var predicateFacade = typeof(DotnetProjectHostingExtensions).GetMethod(nameof(DotnetProjectHostingExtensions.WithEndpointsInEnvironment))!;
        Assert.Empty(predicateFacade.GetCustomAttributes<AspireExportAttribute>());
        Assert.NotNull(predicateFacade.GetCustomAttribute<AspireExportIgnoreAttribute>());
    }

    [Fact]
    public void ConcreteFacadesValidateNullArguments()
    {
        IResourceBuilder<DotnetProjectResource> missingBuilder = null!;
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => missingBuilder.WithReplicas(2)).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(missingBuilder.DisableForwardedHeaders).ParamName);
        Assert.Equal("builder", Assert.Throws<ArgumentNullException>(() => missingBuilder.WithEndpointsInEnvironment(_ => true)).ParamName);
        DotnetProjectResource missingResource = null!;
        Assert.Equal("projectResource", Assert.Throws<ArgumentNullException>(missingResource.GetProjectMetadata).ParamName);

        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProject("service", "Service.vbproj", options => options.ExcludeLaunchProfile = true);
        Assert.Equal("filter", Assert.Throws<ArgumentNullException>(() => resource.WithEndpointsInEnvironment(null!)).ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointFiltersComposeAndPolyglotNamesRemainCaseInsensitive(bool usePolyglotNames)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProject("service", "Service.vbproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint(port: 5111, name: "http")
            .WithHttpEndpoint(port: 5222, name: "admin")
            .WithHttpEndpoint(port: 5333, name: "metrics");
        if (usePolyglotNames)
        {
            Assert.Same(resource, resource.WithEndpointsInEnvironmentForPolyglot(["HTTP", "METRICS"]));
        }
        else
        {
            Assert.Same(resource, resource.WithEndpointsInEnvironment(endpoint => endpoint.Name != "admin"));
        }
        resource.WithEndpointsInEnvironment(endpoint => endpoint.Name != "metrics");
        var index = 0;
        foreach (var endpoint in resource.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            endpoint.AllocatedEndpoint = new(endpoint, "localhost", endpoint.Port!.Value, targetPortExpression: $"p{index++}");
        }
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            resource.Resource, serviceProvider: TestServiceProvider.Instance);

        Assert.Equal("http://localhost:p0", environment["ASPNETCORE_URLS"]);
    }

    [Fact]
    public void ConcreteMetadataFacadeRetainsMissingDuplicateAndReplacedMetadataValidation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var raw = new DotnetProjectResource("raw", builder.AppHostDirectory);
        Assert.Equal(
            "Resource 'raw' does not carry an IProjectMetadata annotation.",
            Assert.Throws<InvalidOperationException>(raw.GetProjectMetadata).Message);

        var resource = builder.AddDotnetProject("service", "Service.vbproj", options => options.ExcludeLaunchProfile = true);
        var original = resource.Resource.GetProjectMetadata();
        var replacement = new DotnetProjectMetadata(original.ProjectPath, buildConfiguration: null, "service");
        resource.Resource.Annotations.Add(replacement);
        Assert.Equal(
            "Resource 'service' carries more than one IProjectMetadata annotation. Project resources must carry exactly one stable project metadata annotation.",
            Assert.Throws<InvalidOperationException>(resource.Resource.GetProjectMetadata).Message);

        resource.Resource.Annotations.Remove(original);
        Assert.Equal(
            "Resource 'service' project metadata was replaced after project defaults were applied. Project metadata must remain unchanged after WithProjectDefaults configures the resource.",
            Assert.Throws<InvalidOperationException>(resource.Resource.GetProjectMetadata).Message);
    }

    [Fact]
    public async Task ForwardedHeadersFacadeChangesPublishEnvironmentWithoutChangingLegacyOverloadResolution()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "Service.vbproj");
        File.WriteAllText(path, "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var disabled = builder.AddDotnetProject("disabled", path, options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint()
            .DisableForwardedHeaders();
        var enabled = builder.AddDotnetProject("enabled", path, options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint();
#pragma warning disable CS0618 // Legacy and V2 concrete helper overloads remain independently callable.
        IResourceBuilder<ProjectResource> legacy = builder.AddProject("legacy", path, options => options.ExcludeLaunchProfile = true)
            .WithReplicas(2)
            .DisableForwardedHeaders()
            .WithEndpointsInEnvironment(_ => true);
#pragma warning restore CS0618
        IResourceBuilder<DotnetProjectResource> modern = disabled.WithReplicas(2).WithEndpointsInEnvironment(_ => true);

        var disabledEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            modern.Resource, DistributedApplicationOperation.Publish);
        var enabledEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            enabled.Resource, DistributedApplicationOperation.Publish);
        Assert.False(disabledEnvironment.ContainsKey("ASPNETCORE_FORWARDEDHEADERS_ENABLED"));
        Assert.Equal("true", enabledEnvironment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"]);
        Assert.Equal(2, Assert.Single(legacy.Resource.Annotations.OfType<ReplicaAnnotation>()).Replicas);
        Assert.Same(disabled, modern);
    }
}
