// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Docker.Tests;

public class DockerComposeImportTests
{
    [Fact]
    public void ImportsNativeContainersAndTargetPorts()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var compose = DockerComposeImportExtensions.ImportNormalized(builder, "legacy", """
            {"name":"old","services":{
              "redis":{"image":"redis:7.4","entrypoint":null,"expose":["6379/tcp"],"networks":{"default":null}},
              "api":{"image":"example/api:v1","ports":[{"target":8080,"published":"18080","protocol":"tcp","mode":"ingress"}],
                "depends_on":{"redis":{"condition":"service_started","required":true}},
                "environment":{"REDIS_URL":"redis://redis:6379/0"}}
            },"networks":{"default":{"name":"old_default","ipam":{}}}}
            """, new());

        Assert.Equal(2, builder.Resources.Count);
        var api = Assert.IsType<ContainerResource>(compose.GetService("api").Resource);
        var endpoint = Assert.Single(api.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("port8080", endpoint.Name);
        Assert.Equal(8080, endpoint.TargetPort);
        Assert.Equal(18080, endpoint.Port);
        Assert.Contains(api.Annotations, a => a is EnvironmentCallbackAnnotation);
        Assert.Contains(api.Annotations, a => a is WaitAnnotation);
        Assert.IsType<ContainerResource>(compose.GetService("redis").Resource);
    }

    [Fact]
    public void ReplacementIsTheDependencyAndEndpointIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var local = builder.AddExecutable("local-api", "dotnet", ".").WithHttpEndpoint(name: "port8080");
        var options = new DockerComposeImportOptions();
        options.ReplaceService("api", local);
        var compose = DockerComposeImportExtensions.ImportNormalized(builder, "legacy", """
            {"services":{
              "api":{"image":"example/api","expose":["8080"]},
              "gateway":{"image":"example/gateway","environment":{"API_URL":"http://api:8080/path?x=1"},
                "depends_on":{"api":{"condition":"service_started","required":true}}}
            }}
            """, options);

        Assert.Same(local.Resource, compose.GetService("api").Resource);
        Assert.Equal(2, builder.Resources.Count);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "legacy-api");
        var gateway = compose.GetService("gateway").Resource;
        var wait = Assert.Single(gateway.Annotations.OfType<WaitAnnotation>());
        Assert.Same(local.Resource, wait.Resource);
        Assert.Equal(WaitType.WaitUntilStarted, wait.WaitType);
    }

    [Theory]
    [InlineData("\"healthcheck\":{\"test\":[\"CMD\",\"true\"]}", "healthcheck")]
    [InlineData("\"build\":\".\"", "build")]
    [InlineData("\"restart\":\"always\"", "restart")]
    [InlineData("\"profiles\":[\"debug\"]", "profiles")]
    [InlineData("\"privileged\":true", "privileged")]
    [InlineData("\"network_mode\":\"host\"", "network_mode")]
    [InlineData("\"ports\":[{\"target\":80,\"published\":\"8000-8002\"}]", "ports.published")]
    [InlineData("\"ports\":[{\"target\":53,\"protocol\":\"udp\"}]", "ports")]
    [InlineData("\"environment\":{\"MISSING\":null}", "environment.MISSING")]
    [InlineData("\"depends_on\":{\"db\":{\"condition\":\"service_healthy\"}}", "depends_on.db")]
    [InlineData("\"command\":[]", "command")]
    [InlineData("\"entrypoint\":[\"sh\"]", "entrypoint")]
    public void UnsupportedSemanticsFailBeforeModelMutation(string field, string diagnosticPath)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var exception = Assert.Throws<NotSupportedException>(() =>
            DockerComposeImportExtensions.ImportNormalized(builder, "legacy",
                "{\"services\":{\"api\":{\"image\":\"example/api\"," + field + "}}}", new()));
        Assert.Contains("services.api." + diagnosticPath, exception.Message);
        Assert.Empty(builder.Resources);
    }

    [Theory]
    [InlineData("""{"services":{"a":{"image":"a","depends_on":{"b":{"condition":"service_started"}}},"b":{"image":"b","depends_on":{"a":{"condition":"service_started"}}}}}""", "cycle")]
    [InlineData("""{"services":{"a":{"image":"a","depends_on":{"missing":{"condition":"service_started"}}}}}""", "another imported service")]
    [InlineData("""{"services":{"a":{"image":"a","environment":{"URL":"http://b:8080"}},"b":{"image":"b"}}}""", "declared ports/expose")]
    [InlineData("""{"services":{"a":{"image":"a"}},"volumes":{"data":{}}}""", "$.volumes")]
    [InlineData("""{"services":{"a":{"image":"a"}},"networks":{"custom":{}}}""", "networks.custom")]
    [InlineData("""{"services":{"valid":{"image":"a"},"bad_name":{"image":"a"}}}""", "services.bad_name")]
    [InlineData("""{"services":{"API":{"image":"a"}}}""", "lowercase")]
    public void InvalidGraphsFailBeforeModelMutation(string json, string diagnostic)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var exception = Assert.Throws<NotSupportedException>(() =>
            DockerComposeImportExtensions.ImportNormalized(builder, "legacy", json, new()));
        Assert.Contains(diagnostic, exception.Message);
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void ReplacementRequiresMatchingEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var options = new DockerComposeImportOptions();
        options.ReplaceService("api", builder.AddExecutable("local", "dotnet", "."));
        var exception = Assert.Throws<NotSupportedException>(() =>
            DockerComposeImportExtensions.ImportNormalized(builder, "legacy",
                """{"services":{"api":{"image":"example/api","expose":["8080"]}}}""", options));
        Assert.Contains("port8080", exception.Message);
        Assert.Single(builder.Resources);
    }

    [Theory]
    [InlineData("http://api:8080", "http://localhost:19000")]
    [InlineData("http://api:8080/", "http://localhost:19000/")]
    [InlineData("http://api:8080/path%2Fpart?x=1#fragment", "http://localhost:19000/path%2Fpart?x=1#fragment")]
    public async Task RewritesEndpointWithoutNormalizingUrlSuffix(string original, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddExecutable("local-api", "dotnet", ".")
            .WithHttpEndpoint(name: "port8080")
            .WithEndpoint("port8080", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 19000));
        var gateway = builder.AddExecutable("local-gateway", "dotnet", ".");
        var options = new DockerComposeImportOptions();
        options.ReplaceService("api", api);
        options.ReplaceService("gateway", gateway);
        DockerComposeImportExtensions.ImportNormalized(builder, "legacy", $$$"""
            {"services":{
              "api":{"image":"example/api","expose":["8080"]},
              "gateway":{"image":"example/gateway","environment":{"API_URL":"{{{original}}}","LITERAL":"unchanged"}}
            }}
            """, options);

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            gateway.Resource, serviceProvider: TestServiceProvider.Instance);
        Assert.Equal(expected, environment["API_URL"]);
        Assert.Equal("unchanged", environment["LITERAL"]);
    }

    [Theory]
    [InlineData("redis", false)]
    [InlineData("other", true)]
    public void RejectsExistingNetworkAliases(string resourceName, bool explicitAlias)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existing = builder.AddContainer(resourceName, "redis");
        if (explicitAlias)
        {
            existing.WithContainerNetworkAlias("redis");
        }
        var exception = Assert.Throws<NotSupportedException>(() =>
            DockerComposeImportExtensions.ImportNormalized(builder, "legacy",
                """{"services":{"redis":{"image":"redis"}}}""", new()));
        Assert.Contains("network alias", exception.Message);
        Assert.Single(builder.Resources);
    }

    [Fact]
    public void RejectsReplacementSharedByMultipleServices()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var local = builder.AddExecutable("local", "dotnet", ".");
        var options = new DockerComposeImportOptions();
        options.ReplaceService("a", local);
        options.ReplaceService("b", local);
        var exception = Assert.Throws<NotSupportedException>(() =>
            DockerComposeImportExtensions.ImportNormalized(builder, "legacy",
                """{"services":{"a":{"image":"a"},"b":{"image":"b"}}}""", options));
        Assert.Contains("one resource cannot replace multiple", exception.Message);
        Assert.Single(builder.Resources);
    }

    [Fact]
    public async Task RejectsPublishBeforeInvokingDocker()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            builder.AddDockerComposeFileAsync("legacy", "does-not-exist.yaml"));
        Assert.Contains("run mode only", exception.Message);
        Assert.Empty(builder.Resources);
    }
}
