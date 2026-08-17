// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // DockerfileBuilder is experimental
#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchArgsOverrideAnnotation is experimental

namespace Aspire.Hosting.Blazor.Tests;

public class AddBlazorGatewayTests(ITestOutputHelper testOutputHelper)
{
    private const string GatewayPackageId = "Microsoft.AspNetCore.Components.Gateway.Cli";
    private const string GatewayPackageVersion = "11.0.0-preview.7.26381.103";

    [Fact]
    public void AddBlazorGateway_PreservesProjectResourceApiAndUsesToolForRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        IResourceBuilder<ProjectResource> gateway = builder.AddBlazorGateway("gateway");

        Assert.EndsWith(
            Path.Combine("Scripts", "Gateway.cs"),
            gateway.Resource.GetProjectMetadata().ProjectPath);

        var executable = Assert.Single(gateway.Resource.Annotations.OfType<ExecutableAnnotation>());
        Assert.Equal("dotnet", executable.Command);
        Assert.Equal(builder.AppHostDirectory, executable.WorkingDirectory);
        Assert.Single(gateway.Resource.Annotations.OfType<ProjectLaunchArgsOverrideAnnotation>());

        var initialSnapshot = Assert.Single(gateway.Resource.Annotations.OfType<ResourceSnapshotAnnotation>()).InitialSnapshot;
        var source = Assert.Single(
            initialSnapshot.Properties,
            property => property.Name == CustomResourceKnownProperties.Source);
        Assert.Equal(string.Empty, source.Value);

        Assert.Collection(
            gateway.Resource.Annotations.OfType<EndpointAnnotation>().OrderBy(endpoint => endpoint.Name),
            endpoint => Assert.Equal("http", endpoint.Name),
            endpoint => Assert.Equal("https", endpoint.Name));
    }

    [Fact]
    public async Task AddBlazorGateway_ConfiguresGatewayToolArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var gateway = builder.AddBlazorGateway("gateway");
        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(gateway.Resource);

        Assert.Collection(
            args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal(GatewayPackageId, arg),
            arg => Assert.Equal("--version", arg),
            arg => Assert.Equal(GatewayPackageVersion, arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg),
            arg => Assert.Equal("--environment", arg),
            arg => Assert.Equal(builder.Environment.EnvironmentName, arg),
            arg => Assert.Equal("--Logging:LogLevel:Microsoft=Warning", arg),
            arg => Assert.Equal("--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information", arg),
            arg => Assert.Equal("--Logging:LogLevel:System.Net.Http.HttpClient.OtlpExporter=Warning", arg));
    }

    [Theory]
    [InlineData("10.0.201", false)]
    [InlineData("11.0.100-preview.6.26359.118", false)]
    [InlineData("11.0.100-preview.7.26381.103", true)]
    [InlineData("11.0.100-preview.8.26400.1", true)]
    [InlineData("11.0.100-rc.1.26400.1", true)]
    [InlineData("11.0.100", true)]
    [InlineData("12.0.100-preview.1.27000.1", true)]
    [InlineData("invalid", false)]
    public void IsCompatibleDotnetSdkVersion_RequiresNet11Preview7OrLater(string version, bool expected)
    {
        Assert.Equal(expected, BlazorGatewayExtensions.IsCompatibleDotnetSdkVersion(version));
    }

    [Fact]
    public async Task AddBlazorGateway_InPublishMode_UsesFileBasedGateway()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        IResourceBuilder<ProjectResource> gateway = builder.AddBlazorGateway("gateway");

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        Assert.Equal("gateway", container.Name);

        var build = Assert.Single(container.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(build.DockerfileFactory);

        var context = new DockerfileFactoryContext
        {
            Services = builder.Services.BuildServiceProvider(),
            Resource = container,
            CancellationToken = CancellationToken.None
        };

        var dockerfile = await build.DockerfileFactory(context);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", dockerfile);
        Assert.Contains("COPY Gateway.cs .", dockerfile);
        Assert.Contains("RUN dotnet publish Gateway.cs -c Release -o /app/publish", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0", dockerfile);
        Assert.Contains("COPY --from=build /app/publish .", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\",\"Gateway.dll\"]", dockerfile);
        Assert.DoesNotContain(GatewayPackageId, dockerfile);

        Assert.Empty(gateway.Resource.Annotations.OfType<ProjectLaunchArgsOverrideAnnotation>());
        Assert.Empty(gateway.Resource.Annotations.OfType<ExecutableAnnotation>());
    }

    [Fact]
    public void WithBlazorClientApp_RunModeGateway_ForwardsServiceReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var weatherApi = builder.AddProject<TestProjectMetadata>("weatherapi")
            .WithHttpEndpoint();
        var wasmApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj")
            .WithReference(weatherApi);

        var gateway = builder.AddBlazorGateway("gateway")
            .WithBlazorClientApp(wasmApp);

        var endpointReference = Assert.Single(
            gateway.Resource.Annotations.OfType<EndpointReferenceAnnotation>(),
            annotation => annotation.Resource.Name == "weatherapi");

        Assert.True(endpointReference.UseAllEndpoints);
        Assert.Same(gateway.Resource, wasmApp.Resource.Parent);
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "TestProject/TestProject.csproj";

        public LaunchSettings LaunchSettings { get; } = new();
    }
}
