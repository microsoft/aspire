// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Runtime;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class WorkloadDefinitionTests
{
    [Fact]
    public async Task GenericContainerRpcPreservesAllEndpointsEntrypointAndMounts()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var context = new RuntimeRpcTestContext(executor);
        var allocation = Result(await context.Integration.InvokeAsync("startContainerWorkload", Arguments(
            ("context", context.Writer), ("launch", Launch()))));
        var plan = Assert.IsType<ContainerWorkload>(Assert.Single(executor.Plans));

        Assert.Equal("/bin/worker", plan.Command);
        Assert.Equal([new WorkloadMount(WorkloadMountKind.Volume, "retained-data", "/data", false)], plan.Mounts);
        Assert.Equal([new WorkloadPort("http", WorkloadProtocol.Tcp, "http", 8080, 0, ""),
            new WorkloadPort("management", WorkloadProtocol.Tcp, "https", 8443, 0, "")], plan.Endpoints);
        await Verify(allocation.ToJsonString(), "json").UseDirectory("Snapshots");
        Assert.Equal(["http://127.0.0.1:5050", "https://127.0.0.1:5051"],
            context.Read()["resources"]![0]!["urls"]!.AsArray().Select(url => url!.GetValue<string>()));
        await context.Author.InvokeAsync("retireGeneration", Arguments(
            ("context", context.Session), ("composition", context.Composition)));
        Assert.Single(executor.Removed);
    }

    [Theory]
    [InlineData("duplicate-name")]
    [InlineData("duplicate-port")]
    [InlineData("bad-port")]
    [InlineData("bad-protocol")]
    [InlineData("bad-scheme")]
    [InlineData("container-port-variable")]
    [InlineData("duplicate-mount")]
    [InlineData("relative-bind")]
    [InlineData("argument-null")]
    public async Task InvalidLaunchFailsBeforeExecutingOrChangingResourceState(string invalid)
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var context = new RuntimeRpcTestContext(executor);
        var launch = Launch();
        var endpoints = launch["endpoints"]!.AsArray();
        switch (invalid)
        {
            case "duplicate-name": endpoints[1]!["name"] = "HTTP"; break;
            case "duplicate-port": endpoints[1]!["targetPort"] = 8080; break;
            case "bad-port": endpoints[0]!["targetPort"] = 65536; break;
            case "bad-protocol": endpoints[0]!["protocol"] = "SCTP"; break;
            case "bad-scheme": endpoints[0]!["uriScheme"] = "invalid://"; break;
            case "container-port-variable": endpoints[0]!["environmentVariable"] = "PORT"; break;
            case "duplicate-mount": launch["mounts"]!.AsArray().Add(launch["mounts"]![0]!.DeepClone()); break;
            case "relative-bind":
                launch["mounts"]![0]!["type"] = "bind";
                launch["mounts"]![0]!["source"] = "relative-path";
                break;
            case "argument-null": launch["arguments"]!.AsArray().Add(null); break;
        }
        var response = await context.Integration.InvokeAsync("startContainerWorkload", Arguments(
            ("context", context.Writer), ("launch", launch)));

        Assert.Equal("INVALID_ARGUMENT", response["result"]!["$error"]!["code"]!.GetValue<string>());
        Assert.Empty(executor.Started);
        Assert.Equal("Unknown", context.Read()["resources"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void ExecutablePortVariablesAreDistinctFromEnvironmentAndEachOther()
    {
        var executable = Environment.ProcessPath!;
        var directory = Path.GetDirectoryName(executable)!;
        var plan = new ExecutableWorkload(executable, directory, [], [],
            [new("http", WorkloadProtocol.Tcp, "http", 0, 0, "HTTP_PORT"), new("metrics", WorkloadProtocol.Tcp, "http", 0, 0, "METRICS_PORT")]);
        WorkloadDefinition.Validate(plan);

        Assert.Throws<ArgumentException>(() => WorkloadDefinition.Validate(plan with
        {
            Ports = [new("http", WorkloadProtocol.Tcp, "http", 0, 0, "PORT"), new("metrics", WorkloadProtocol.Tcp, "http", 0, 0, "PORT")]
        }));
        Assert.Throws<ArgumentException>(() => WorkloadDefinition.Validate(new ExecutableWorkload(executable, directory,
            [new("HTTP_PORT", "configured")], [], plan.Endpoints)));
    }

    [Fact]
    public void EndpointCapacityAndPortLimitsAreExact()
    {
        var endpoints = Enumerable.Range(0, 32)
            .Select(index => new WorkloadPort($"port-{index}", WorkloadProtocol.Tcp, "tcp", index + 1, 0, "")).ToImmutableArray();
        WorkloadDefinition.Validate(new ContainerWorkload("example:1", null, [], [], [], endpoints));
        WorkloadDefinition.Validate(new ContainerWorkload("example:1", null, [], [], [], [new("tcp", WorkloadProtocol.Tcp, "tcp", 65535, 65535, "")]));

        Assert.Throws<ArgumentException>(() => WorkloadDefinition.Validate(
            new ContainerWorkload("example:1", null, [], [], [], endpoints.Add(new("overflow", WorkloadProtocol.Tcp, "tcp", 33, 0, "")))));
        Assert.Throws<ArgumentException>(() => WorkloadDefinition.Validate(
            new ContainerWorkload("example:1", null, [], [], [], [new("tcp", WorkloadProtocol.Tcp, "tcp", 65535, 65536, "")])));
    }

    private static JsonObject Launch() => new()
    {
        ["image"] = "example/service:1", ["entrypoint"] = "/bin/worker",
        ["environment"] = new JsonArray(), ["arguments"] = new JsonArray("--serve"),
        ["mounts"] = new JsonArray(new JsonObject
        {
            ["type"] = "volume", ["source"] = "retained-data", ["target"] = "/data", ["readOnly"] = false
        }),
        ["endpoints"] = new JsonArray(
            Endpoint("http", "http", 8080), Endpoint("management", "https", 8443))
    };

    private static JsonObject Endpoint(string name, string scheme, int port) => new()
    {
        ["name"] = name, ["protocol"] = "TCP", ["uriScheme"] = scheme, ["targetPort"] = port,
        ["hostPort"] = 0, ["environmentVariable"] = ""
    };
}
