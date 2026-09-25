// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001, ASPIREPROJECTS001, ASPIREDOTNETTOOL, ASPIREEXTENSION001

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFBuildCustomizationWarningTests
{
    [Theory]
    [InlineData("startup-provider", true)]
    [InlineData("target-provider", true)]
    [InlineData("target-path-provider", true)]
    [InlineData("startup-metadata", true)]
    [InlineData("target-metadata", true)]
    [InlineData("runtime-only", false)]
    [InlineData("unrelated-provider", false)]
    [InlineData("ordinary", false)]
    public async Task WarnsOnlyForParticipatingBuildCustomizationsAndContinues(string customization, bool expectWarning)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        // This launcher owns builds, so the test can focus on EF without materializing a run-mode coordinator.
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        var startupPath = new Projects.ServiceA().ProjectPath;
        var startup = customization == "startup-metadata"
            ? builder.AddResource(new DotnetProjectResource("api", Path.GetDirectoryName(startupPath)!))
                .WithAnnotation(new CustomBuildProjectMetadata { ProjectPath = startupPath })
                .WithProjectDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true })
            : builder.AddDotnetProject("api", startupPath, options => options.ExcludeLaunchProfile = true);
        var target = builder.AddDotnetProject("target", new Projects.ServiceB().ProjectPath,
            options => options.ExcludeLaunchProfile = true);
        var migrations = startup.AddEFMigrations("migrations");
        var callbackCalls = 0;
        switch (customization)
        {
            case "startup-provider":
                startup.WithBuildEnvironment(_ => { callbackCalls++; });
                break;
            case "target-provider":
            case "target-path-provider":
                target.WithBuildEnvironment(_ => { callbackCalls++; });
                if (customization == "target-provider")
                {
                    migrations.WithMigrationsProjectForPolyglot(target);
                }
                else
                {
                    migrations.WithMigrationsProject(new Projects.ServiceB().ProjectPath);
                }
                break;
            case "target-metadata":
                migrations.WithMigrationsProject<CustomBuildProjectMetadata>();
                break;
            case "runtime-only":
                startup.WithEnvironment("BUILD_FLAVOR", "runtime");
                startup.WithEnvironment("ConnectionStrings__database", "not-a-build-input");
                break;
            case "unrelated-provider":
                target.WithBuildEnvironment("BUILD_FLAVOR", "unrelated");
                break;
        }
        using var app = builder.Build();
        var sink = new TestSink();
        var logger = new TestLogger("EF", sink, level => level >= LogLevel.Information);
        var tool = new TestEfTool();
        using var executor = new EFCoreOperationExecutor(
            migrations.Resource, logger, TestContext.Current.CancellationToken, app.Services, tool.Resource);

        var result = await executor.UpdateDatabaseAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, callbackCalls);
        var warnings = sink.Writes.Where(write => write.LogLevel == LogLevel.Warning).ToArray();
        if (expectWarning)
        {
            var affected = customization switch
            {
                "target-provider" or "target-path-provider" => "'target'",
                "target-metadata" => "the migrations project metadata",
                _ => "'api'"
            };
            Assert.Equal(ExpectedWarning("database", "update", affected), Assert.Single(warnings).Message);
        }
        else
        {
            Assert.Empty(warnings);
        }
        Assert.Equal(
            ExpectedArguments(executor, migrations.Resource, "database", "update", noBuild: true),
            Assert.Single(tool.Invocations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningPreservesToolFailureOrCancellation(bool canceled)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var startup = builder.AddDotnetProject("api", new Projects.ServiceA().ProjectPath,
            options => options.ExcludeLaunchProfile = true).WithBuildEnvironment("BUILD_FLAVOR", "custom");
        var migrations = startup.AddEFMigrations("migrations");
        using var app = builder.Build();
        var sink = new TestSink();
        var tool = new TestEfTool
        {
            Result = canceled ? CommandResults.Canceled() : CommandResults.Failure("EF output is missing")
        };
        using var executor = new EFCoreOperationExecutor(
            migrations.Resource, new TestLogger("EF", sink, level => level >= LogLevel.Information),
            TestContext.Current.CancellationToken, app.Services, tool.Resource);

        var result = await executor.UpdateDatabaseAsync();

        Assert.False(result.Success);
        Assert.Equal(canceled ? "dotnet-ef command was canceled." : "EF output is missing", result.ErrorMessage);
        Assert.Single(tool.Invocations);
        Assert.Single(sink.Writes, write => write.LogLevel == LogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingWarnsBeforeToolStartAndBuildsParticipatingProjects(bool bundle)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var callbackCalls = 0;
        var startup = builder.AddDotnetProject("api", new Projects.ServiceA().ProjectPath,
            options => options.ExcludeLaunchProfile = true).WithBuildEnvironment(_ => { callbackCalls++; });
        var migrations = startup.AddEFMigrations("migrations");
        Assert.Empty(startup.Resource.GetProjectMetadata().BuildEnvironment);
        using var app = builder.Build();
        var sink = new TestSink();
        var tool = new TestEfTool();
        sink.MessageLogged += write =>
        {
            if (write.LogLevel == LogLevel.Warning)
            {
                Assert.Empty(tool.Invocations);
            }
        };
        using var executor = new EFCoreOperationExecutor(
            migrations.Resource, new TestLogger("EF", sink, level => level >= LogLevel.Information),
            TestContext.Current.CancellationToken, app.Services, tool.Resource);

        var result = bundle
            ? await executor.GenerateMigrationBundleAsync(targetRuntime: "linux-x64", selfContained: true)
            : await executor.GenerateMigrationScriptAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, callbackCalls);
        Assert.Equal(ExpectedWarning("migrations", bundle ? "bundle" : "script", "'api'"),
            Assert.Single(sink.Writes, write => write.LogLevel == LogLevel.Warning).Message);
        var expected = ExpectedArguments(executor, migrations.Resource, "migrations", bundle ? "bundle" : "script", noBuild: false);
        expected.AddRange(bundle ? ["--target-runtime", "linux-x64", "--self-contained", "--force"] : ["--idempotent"]);
        Assert.Equal(expected, Assert.Single(tool.Invocations));
    }

    [Fact]
    public async Task NestedCommandsWarnOnceAndNewRequestedOperationsWarnAgain()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var startup = builder.AddDotnetProject("api", new Projects.ServiceA().ProjectPath,
            options => options.ExcludeLaunchProfile = true).WithBuildEnvironment("BUILD_FLAVOR", "custom");
        var migrations = startup.AddEFMigrations("migrations");
        using var app = builder.Build();
        var sink = new TestSink();
        var logger = new TestLogger("EF", sink, level => level >= LogLevel.Information);
        var tool = new TestEfTool();

        for (var operation = 0; operation < 2; operation++)
        {
            using var executor = new EFCoreOperationExecutor(
                migrations.Resource, logger, TestContext.Current.CancellationToken, app.Services, tool.Resource);
            Assert.True((await executor.ResetDatabaseAsync()).Success);
        }

        Assert.Equal(4, tool.Invocations.Count);
        Assert.Equal(
            new[] { ExpectedWarning("database", "drop", "'api'"), ExpectedWarning("database", "drop", "'api'") },
            sink.Writes.Where(write => write.LogLevel == LogLevel.Warning).Select(write => write.Message));
    }

    private static List<string> ExpectedArguments(
        EFCoreOperationExecutor executor, EFMigrationResource migrations, string command, string subCommand, bool noBuild)
    {
        var args = new List<string> { command, subCommand };
        if (noBuild)
        {
            args.Add("--no-build");
        }
        args.AddRange(["--no-color", "--prefix-output", "--project",
            migrations.MigrationsProjectPath ?? migrations.StartupProjectResource.GetProjectMetadata().ProjectPath]);
        if (migrations.MigrationsProjectPath is not null)
        {
            args.AddRange(["--startup-project", migrations.StartupProjectResource.GetProjectMetadata().ProjectPath]);
        }
        var configuration = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (!string.IsNullOrEmpty(configuration))
        {
            args.AddRange(["--configuration", configuration]);
        }
        if (executor.ResolvedFramework is not null)
        {
            args.AddRange(["--framework", executor.ResolvedFramework]);
        }

        return args;
    }

    private static string ExpectedWarning(string command, string subCommand, string affected) =>
        $"EF command '{command} {subCommand}' is continuing with Aspire-specific build customizations configured on {affected} " +
        "(WithBuildEnvironment, WithDotnetProgramBuildEnvironment, or custom build-property metadata). " +
        "These customizations are not forwarded as MSBuild global properties to dotnet-ef. " +
        "EF may use suitable output, select different or stale output, or fail if the expected output is missing. " +
        "Where equivalent, define the required settings in shared .csproj or Directory.Build.props configuration " +
        "so the coordinated build and EF evaluate the same values.";
}
