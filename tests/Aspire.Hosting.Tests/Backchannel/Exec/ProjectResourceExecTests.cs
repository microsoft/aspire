// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing;

namespace Aspire.Hosting.Tests.Backchannel.Exec;

[Trait("Partition", "4")]
public class ProjectResourceExecTests : ExecTestsBase
{
    public ProjectResourceExecTests(ITestOutputHelper outputHelper)
        : base(outputHelper)
    {
    }

    [Fact]
    public async Task Exec_NotFoundTargetResource_ShouldProduceLogs()
    {
        string[] args = [
            "--operation", "run",
            "--resource", "randomnonexistingresource",
            "--command", "\"dotnet --info\"",
        ];

        using var builder = PrepareBuilder(args);
        WithTestProjectResource(builder);

        using var app = builder.Build();

        var logs = await ExecWithLogCollectionAsync(app);
        AssertLogsContain(logs, "Target resource randomnonexistingresource not found in the model resources");

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11158")]
    public async Task Exec_DotnetBuildFail_ProducesLogs_Fail()
    {
        string[] args = [
            "--operation", "run",
            "--resource", "test",
                         // not existing csproj, but we dont care if that succeeds or not - we are expecting
                         // whatever log output from the command
            "--command", "\"dotnet build \"MyRandom.csproj\"\"",
        ];

        using var builder = PrepareBuilder(args);
        WithTestProjectResource(builder);

        using var app = builder.Build();

        var logs = await ExecWithLogCollectionAsync(app);
        AssertLogsContain(logs, "Project file does not exist", "Aspire exec exit code: 1");

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Exec_NonExistingCommand_ProducesLogs_Fail()
    {
        string[] args = [
            "--operation", "run",
            "--resource", "test",
                         // not existing command. Executable should fail without start basically
            "--command", "\"randombuildcommand doit\"",
        ];

        using var builder = PrepareBuilder(args);
        WithTestProjectResource(builder);

        using var app = builder.Build();

        var logs = await ExecWithLogCollectionAsync(app);
        AssertLogsContain(logs, "Aspire exec failed to start");

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/11143", TestPlatforms.Windows)]
    public async Task Exec_DotnetInfo_ProducesLogs_Success()
    {
        string[] args = [
            "--operation", "run",
            "--resource", "test",
            "--command", "\"dotnet --info\"",
        ];

        using var builder = PrepareBuilder(args);
        WithTestProjectResource(builder);

        using var app = builder.Build();

        var logs = await ExecWithLogCollectionAsync(app);
        AssertLogsContain(logs,
            ".NET SDKs installed", // command logs
            "Aspire exec exit code: 0" // exit code is submitted separately from the command logs
        );

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Exec_DotnetHelp_ProducesLogs_Success()
    {
        string[] args = [
            "--operation", "run",
            "--resource", "test",
            "--command", "\"dotnet --help\"",
        ];

        using var builder = PrepareBuilder(args);
        WithTestProjectResource(builder);

        using var app = builder.Build();

        var logs = await ExecWithLogCollectionAsync(app);
        AssertLogsContain(logs,
            "Usage: dotnet [sdk-options] [command] [command-options] [arguments]", // command logs
            "Aspire exec exit code: 0" // exit code is submitted separately from the command logs
        );

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void WithTestProjectResource(IDistributedApplicationTestingBuilder builder, string name = "test")
    {
        builder.AddResource(new TestProjectResource(name))
               .WithInitialState(new()
               {
                   ResourceType = "TestProjectResource",
                   State = new("Running", null),
                   Properties = [new("A", "B"), new("c", "d")],
                   EnvironmentVariables = [new("e", "f", true), new("g", "h", false)]
               })
               .WithAnnotation<IProjectMetadata>(new ProjectMetadata(Directory.GetCurrentDirectory()));
    }
}

file sealed class TestProjectResource : ProjectResource
{
    public TestProjectResource(string name) : base(name)
    {
    }
}
