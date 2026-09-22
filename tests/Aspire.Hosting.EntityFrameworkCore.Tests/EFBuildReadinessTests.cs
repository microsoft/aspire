// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001, ASPIREPROJECTS001, ASPIREDOTNETTOOL

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFBuildReadinessTests
{
    [Theory]
    [InlineData("ef-database-update")]
    [InlineData("ef-migrations-remove")]
    [InlineData("ef-database-status")]
    public async Task CommandsWaitForBuildWithoutWaitingForStartupApplication(string command)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithAnnotation(new DotnetProgramBuildCompletionAnnotation((_, cancellationToken) =>
            {
                entered.TrySetResult();
                return completion.Task.WaitAsync(cancellationToken);
            }));
        var migrations = project.AddEFMigrations("migrations");
        project.WaitFor(migrations);
        var tool = new TestEfTool();
        migrations.Resource.ToolResource = tool.Resource;
        using var app = builder.Build();

        var execution = app.Services.GetRequiredService<ResourceCommandService>().ExecuteCommandAsync(
            migrations.Resource, command, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(tool.Invocations);
        Assert.False(execution.IsCompleted);

        completion.SetResult();
        var result = await execution.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.NotEmpty(tool.Invocations);
        Assert.True(app.ResourceNotifications.TryGetCurrentState(migrations.Resource.Name, out var state));
        Assert.Equal(KnownResourceStates.Finished, state.Snapshot.State?.Text);
    }

    [Fact]
    public async Task AutomaticMigrationDoesNotBlockBeforeStartAndPreservesBuildFailure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithAnnotation(new DotnetProgramBuildCompletionAnnotation((_, cancellationToken) =>
            {
                entered.TrySetResult();
                return completion.Task.WaitAsync(cancellationToken);
            }));
        var migrations = project.AddEFMigrations("migrations").RunDatabaseUpdateOnStart();
        project.WaitFor(migrations);
        var tool = new TestEfTool();
        migrations.Resource.ToolResource = tool.Resource;
        using var app = builder.Build();
        var token = TestContext.Current.CancellationToken;

        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()), token);
        await entered.Task.WaitAsync(token);
        Assert.Empty(tool.Invocations);

        completion.SetException(new DistributedApplicationException("coordinated build failed"));
        await app.ResourceNotifications.WaitForResourceAsync(
            migrations.Resource.Name, KnownResourceStates.FailedToStart, token);
        Assert.Empty(tool.Invocations);
        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ResourceNotifications.WaitForDependenciesAsync(project.Resource, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomBuildWarningDoesNotBypassAutomaticMigrationFailure(bool succeed)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        var project = builder.AddDotnetProject("api", new Projects.ServiceA().ProjectPath,
            options => options.ExcludeLaunchProfile = true).WithBuildEnvironment("BUILD_FLAVOR", "custom");
        var migrations = project.AddEFMigrations("migrations").RunDatabaseUpdateOnStart();
        project.WaitFor(migrations);
        var tool = new TestEfTool
        {
            Result = succeed ? CommandResults.Success() : CommandResults.Failure("EF output is missing")
        };
        migrations.Resource.ToolResource = tool.Resource;
        using var app = builder.Build();
        var token = TestContext.Current.CancellationToken;

        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()), token);
        await app.ResourceNotifications.WaitForResourceAsync(
            migrations.Resource.Name, succeed ? KnownResourceStates.Finished : KnownResourceStates.FailedToStart, token);

        Assert.Single(tool.Invocations);
        Assert.False(migrations.Resource.IsExecutingCommand);
        if (!succeed)
        {
            await Assert.ThrowsAsync<DistributedApplicationException>(
                () => app.ResourceNotifications.WaitForDependenciesAsync(project.Resource, token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationCanRetryAfterBuildFailureOrCancellation(bool cancel)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = true;
        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithAnnotation(new DotnetProgramBuildCompletionAnnotation((_, _) =>
            {
                if (!first)
                {
                    return Task.CompletedTask;
                }

                first = false;
                return cancel
                    ? Task.FromCanceled(new CancellationToken(canceled: true))
                    : Task.FromException(new DistributedApplicationException("build failed"));
            }));
        var migrations = project.AddEFMigrations("migrations");
        var tool = new TestEfTool();
        migrations.Resource.ToolResource = tool.Resource;
        using var app = builder.Build();
        var commands = app.Services.GetRequiredService<ResourceCommandService>();

        var failed = await commands.ExecuteCommandAsync(
            migrations.Resource, "ef-database-update", TestContext.Current.CancellationToken);
        Assert.False(failed.Success);
        Assert.Equal(cancel, failed.Canceled);
        Assert.Empty(tool.Invocations);
        Assert.False(migrations.Resource.IsExecutingCommand);

        var retried = await commands.ExecuteCommandAsync(
            migrations.Resource, "ef-database-update", TestContext.Current.CancellationToken);
        Assert.True(retried.Success, retried.Message);
        Assert.Single(tool.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationWaitsForSeparateMigrationsProject(bool useResource)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("api");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = builder.AddProject<Projects.ServiceB>("migration-project")
            .WithAnnotation(new DotnetProgramBuildCompletionAnnotation((_, token) => completion.Task.WaitAsync(token)));
        var migrations = project.AddEFMigrations("migrations");
        if (useResource)
        {
            migrations.WithMigrationsProjectForPolyglot(target);
            Assert.Same(target.Resource, migrations.Resource.MigrationsProjectResource);
        }
        else
        {
            migrations.WithMigrationsProject<Projects.ServiceB>();
        }
        var tool = new TestEfTool();
        using var app = builder.Build();
        using var executor = new EFCoreOperationExecutor(
            migrations.Resource, NullLogger.Instance, TestContext.Current.CancellationToken, app.Services, tool.Resource);

        var operation = executor.UpdateDatabaseAsync();
        Assert.False(operation.IsCompleted);
        Assert.Empty(tool.Invocations);
        completion.SetResult();

        Assert.True((await operation).Success);
        var arguments = Assert.Single(tool.Invocations);
        Assert.Equal(new Projects.ServiceB().ProjectPath, arguments[Array.IndexOf(arguments, "--project") + 1]);
        Assert.Equal(new Projects.ServiceA().ProjectPath, arguments[Array.IndexOf(arguments, "--startup-project") + 1]);
    }

    [Fact]
    public async Task NewOperationWaitsForNewBuildGeneration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var completion = Task.CompletedTask;
        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithAnnotation(new DotnetProgramBuildCompletionAnnotation((_, token) => completion.WaitAsync(token)));
        var migrations = project.AddEFMigrations("migrations");
        var tool = new TestEfTool();
        using var app = builder.Build();
        using var executor = new EFCoreOperationExecutor(
            migrations.Resource, NullLogger.Instance, TestContext.Current.CancellationToken, app.Services, tool.Resource);

        Assert.True((await executor.UpdateDatabaseAsync()).Success);
        var nextBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = nextBuild.Task;
        var nextOperation = executor.UpdateDatabaseAsync();
        Assert.False(nextOperation.IsCompleted);
        Assert.Single(tool.Invocations);
        nextBuild.SetResult();

        Assert.True((await nextOperation).Success);
        Assert.Equal(2, tool.Invocations.Count);
    }
}
