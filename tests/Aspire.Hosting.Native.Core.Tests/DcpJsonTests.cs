// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class DcpJsonTests
{
    [Fact]
    public async Task GenericContainerSupportsMultipleEndpointsEntrypointAndMounts()
    {
        var spec = new DcpWorkloadSpec
        {
            Image = "example/service:1", Command = "/bin/worker", Env = [],
            Args = ["--serve"],
            Ports = [new DcpContainerPort(8080, "127.0.0.1", WorkloadProtocol.Tcp),
                new DcpContainerPort(8443, "127.0.0.1", WorkloadProtocol.Tcp) { HostPort = 5001 }],
            VolumeMounts = [new DcpVolumeMount(WorkloadMountKind.Volume, "retained-data", "/data", false),
                new DcpVolumeMount(WorkloadMountKind.Bind, "/host/config", "/config", true)]
        };
        await Verify(JsonSerializer.Serialize(spec, DcpJsonContext.Default.DcpWorkloadSpec), "json")
            .UseDirectory("Snapshots");
    }
    [Fact]
    public async Task TypedWorkloadEnvelopePreservesTheDcpWireContract()
    {
        var spec = new DcpWorkloadSpec
        {
            Image = "example/worker:1", Env = [new DcpEnvironment("CUSTOM", "configured")],
            Args = ["--work"], Ports = [new DcpContainerPort(4567, "127.0.0.1", WorkloadProtocol.Tcp)]
        };
        using var handler = new DcpCleanupTestHandler();
        await using var client = handler.CreateClient(new());
        await client.CreateAsync(DcpResourceCollection.Containers, "test-worker",
            JsonSerializer.SerializeToElement(spec, DcpJsonContext.Default.DcpWorkloadSpec), null,
            TestContext.Current.CancellationToken);
        var content = Assert.Single(handler.RequestContents);
        Assert.Equal("application/json", content.ContentType);
        await Verify(content.Body, "json")
            .UseDirectory("Snapshots");
    }

    [Fact]
    public void TypedStatusAcceptsAdditionalKubernetesFieldsWithoutInventingExitCodesOrPorts()
    {
        var resource = JsonSerializer.Deserialize("""
            {"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"Executable",
             "metadata":{"name":"arbitrary-worker"},"status":{"state":"Exited","futureField":true}}
            """, DcpJsonContext.Default.DcpResource)!;
        Assert.Equal(DcpProcessState.Exited, resource.Status!.State);
        Assert.Null(resource.Status.ExitCode);
        Assert.Null(resource.Status.EffectivePort);
        Assert.Null(resource.Status.Pid);
    }
}
