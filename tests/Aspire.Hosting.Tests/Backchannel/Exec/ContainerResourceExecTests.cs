// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Tests.Backchannel.Exec;

[Trait("Partition", "4")]
public class ContainerResourceExecTests : ExecTestsBase
{
    public ContainerResourceExecTests(ITestOutputHelper outputHelper)
        : base(outputHelper)
    {
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task Exec_NginxContainer_ListFiles_WithWorkdirSpecified_ProducesLogs_Success()
    {
        string[] args = [
            "--operation", "run",
            "--resource", "test",
            "--command", "\"ls\"",
            "--workdir", "/bin"
        ];

        using var builder = PrepareBuilder(args);
        WithContainerResource(builder);

        using var app = builder.Build();

        var logs = await ExecWithLogCollectionAsync(app);
        AssertLogsContain(logs,
            "apt-get", "base32", "base64", // typical output of `ls /bin` in a container
            "Aspire exec exit code: 0" // exit code is submitted separately from the command logs
        );

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task Exec_NginxContainer_ListFiles_ProducesLogs_Success()
    {
        string[] args = [
            "--operation", "run",
            "--resource", "test",
            "--command", "\"ls\"",
        ];

        using var builder = PrepareBuilder(args);
        WithContainerResource(builder);

        using var app = builder.Build();

        var logs = await ExecWithLogCollectionAsync(app);
        AssertLogsContain(logs,
            "bin", "boot", "dev", // typical output of `ls` in a container
            "Aspire exec exit code: 0" // exit code is submitted separately from the command logs
        );

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void WithContainerResource(IDistributedApplicationTestingBuilder builder, string name = "test")
    {
        builder.AddResource(new TestContainerResource(name))
               .WithInitialState(new()
               {
                   ResourceType = "TestProjectResource",
                   State = new("Running", null),
                   Properties = [new("A", "B"), new("c", "d")],
                   EnvironmentVariables = [new("e", "f", true), new("g", "h", false)]
               })
               .WithImage("nginx")
               .WithImageTag("1.25");
    }
}

file sealed class TestContainerResource : ContainerResource
{
    public TestContainerResource(string name) : base(name)
    {
    }
}
