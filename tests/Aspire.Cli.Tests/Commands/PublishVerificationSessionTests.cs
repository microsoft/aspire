// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class PublishVerificationSessionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CompleteAsync_ExactMatch_SucceedsAndCleansStagingWithoutMutatingTarget()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        Directory.CreateDirectory(context.TargetDirectory);
        await File.WriteAllTextAsync(context.TargetFile, "expected");
        context.Git.GetIncludedFilesFromPathsAsyncCallback = (_, _, _) =>
            Task.FromResult<IReadOnlySet<string>?>(PathSet(context.TargetFile));

        var session = await context.CreateSessionAsync();
        var stagingRoot = Directory.GetParent(session.OutputPath)!.FullName;
        try
        {
            var backchannel = CreateBackchannel(
                () => CreatePlan(context, session, "Prepared"),
                () => CreatePlan(context, session, "Succeeded"));
            await session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken);
            Directory.CreateDirectory(session.OutputPath);
            await File.WriteAllTextAsync(Path.Combine(session.OutputPath, "artifact.txt"), "expected");
            await session.CaptureFinalStateAsync(backchannel, TestContext.Current.CancellationToken);

            var result = await session.CompleteAsync(TestContext.Current.CancellationToken);

            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal("expected", await File.ReadAllTextAsync(context.TargetFile));
            Assert.Contains(Aspire.Cli.Resources.PublishCommandStrings.VerificationSucceeded, context.Interaction.DisplayedSuccess);
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    public async Task CompleteAsync_Drift_ReturnsDedicatedExitCodeAndDoesNotMutateTarget()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        Directory.CreateDirectory(context.TargetDirectory);
        await File.WriteAllTextAsync(context.TargetFile, "old");
        context.Git.GetIncludedFilesFromPathsAsyncCallback = (_, _, _) =>
            Task.FromResult<IReadOnlySet<string>?>(PathSet(context.TargetFile));

        await using var session = await context.CreateSessionAsync();
        var backchannel = CreateBackchannel(
            () => CreatePlan(context, session, "Prepared"),
            () => CreatePlan(context, session, "Succeeded"));
        await session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(session.OutputPath);
        await File.WriteAllTextAsync(Path.Combine(session.OutputPath, "artifact.txt"), "new");
        await session.CaptureFinalStateAsync(backchannel, TestContext.Current.CancellationToken);

        var result = await session.CompleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.PublishVerificationFailed, result.ExitCode);
        Assert.Equal("old", await File.ReadAllTextAsync(context.TargetFile));
        Assert.Contains(Aspire.Cli.Resources.PublishCommandStrings.VerificationFailed, context.Interaction.DisplayedErrors);
        Assert.DoesNotContain(context.Interaction.DisplayedPlainText, line => line.Contains("--verify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreflightAndAuthorizeAsync_MissingCapability_FailsBeforeAuthorization()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        Directory.CreateDirectory(context.TargetDirectory);
        await File.WriteAllTextAsync(context.TargetFile, "unchanged");
        await using var session = await context.CreateSessionAsync();
        var authorized = false;
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2"]),
            AuthorizePipelineExecutionAsyncCallback = _ =>
            {
                authorized = true;
                return Task.CompletedTask;
            }
        };

        await Assert.ThrowsAsync<AppHostIncompatibleException>(
            () => session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken));

        Assert.False(authorized);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(context.TargetFile));
    }

    [Fact]
    public async Task PreflightAndAuthorizeAsync_UnsupportedStep_FailsBeforeAuthorization()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        await using var session = await context.CreateSessionAsync();
        var authorized = false;
        var plan = CreatePlan(context, session, "Prepared", supportsRelocation: false);
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2", "pipeline-outputs.v1"]),
            GetPipelineOutputsAsyncCallback = _ => Task.FromResult(plan),
            AuthorizePipelineExecutionAsyncCallback = _ =>
            {
                authorized = true;
                return Task.CompletedTask;
            }
        };

        await Assert.ThrowsAsync<PublishVerificationException>(
            () => session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken));

        Assert.False(authorized);
        Assert.False(Directory.Exists(context.TargetDirectory));
    }

    [Fact]
    public async Task CaptureFinalStateAsync_UnknownState_FailsWithoutMutatingTarget()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        Directory.CreateDirectory(context.TargetDirectory);
        await File.WriteAllTextAsync(context.TargetFile, "unchanged");
        context.Git.GetIncludedFilesFromPathsAsyncCallback = (_, _, _) =>
            Task.FromResult<IReadOnlySet<string>?>(PathSet(context.TargetFile));
        await using var session = await context.CreateSessionAsync();
        var backchannel = CreateBackchannel(
            () => CreatePlan(context, session, "Prepared"),
            () => CreatePlan(context, session, "Unknown"));
        await session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PublishVerificationException>(
            () => session.CaptureFinalStateAsync(backchannel, TestContext.Current.CancellationToken));

        Assert.Equal("unchanged", await File.ReadAllTextAsync(context.TargetFile));
    }

    [Fact]
    public async Task PreflightAndAuthorizeAsync_Cancellation_CleansStaging()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        var session = await context.CreateSessionAsync();
        var stagingRoot = Directory.GetParent(session.OutputPath)!.FullName;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = token => Task.FromCanceled<string[]>(token)
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.PreflightAndAuthorizeAsync(backchannel, cancellation.Token));
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.False(Directory.Exists(stagingRoot));
        Assert.False(Directory.Exists(context.TargetDirectory));
    }

    [Fact]
    public async Task PreflightAndAuthorizeAsync_OverlappingDestinations_FailsBeforeAuthorization()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        await using var session = await context.CreateSessionAsync();
        var plan = CreatePlan(
            context,
            session,
            "Prepared",
            additionalOutputs:
            [
                new PipelineOutputInfo
                {
                    IsPrimary = false,
                    PublisherName = "publisher",
                    Name = "nested",
                    Kind = "Directory",
                    OutputPath = Path.Combine(Directory.GetParent(session.OutputPath)!.FullName, "named"),
                    LogicalTargetPath = Path.Combine(context.TargetDirectory, "nested")
                }
            ]);
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2", "pipeline-outputs.v1"]),
            GetPipelineOutputsAsyncCallback = _ => Task.FromResult(plan)
        };

        await Assert.ThrowsAsync<PublishVerificationException>(
            () => session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreflightAndAuthorizeAsync_NamedOutputMayReusePrimaryIdentifiers()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        await using var session = await context.CreateSessionAsync();
        var plan = CreatePlan(
            context,
            session,
            "Prepared",
            additionalOutputs:
            [
                new PipelineOutputInfo
                {
                    IsPrimary = false,
                    PublisherName = "aspire",
                    Name = "primary",
                    Kind = "Directory",
                    OutputPath = Path.Combine(Directory.GetParent(session.OutputPath)!.FullName, "named"),
                    LogicalTargetPath = Path.Combine(context.RepositoryRoot.FullName, "named-output")
                }
            ]);
        var authorized = false;
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2", "pipeline-outputs.v1"]),
            GetPipelineOutputsAsyncCallback = _ => Task.FromResult(plan),
            AuthorizePipelineExecutionAsyncCallback = _ =>
            {
                authorized = true;
                return Task.CompletedTask;
            }
        };

        await session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken);

        Assert.True(authorized);
    }

    [Fact]
    public async Task PreflightAndAuthorizeAsync_ManifestFilePrimary_FailsBeforeAuthorization()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        var targetFile = Path.Combine(context.RepositoryRoot.FullName, "manifest.json");
        await using var session = await PublishVerificationSession.CreateAsync(
            context.AppHostFile,
            targetFile,
            context.Git,
            context.Interaction,
            NullLogger<PublishCommand>.Instance,
            ["aspire", "publish", "--output-path", targetFile],
            TestContext.Current.CancellationToken);
        var plan = CreatePlan(
            context,
            session,
            "Prepared",
            supportsRelocation: false,
            stepName: "publish-manifest",
            primaryKind: "File",
            primaryTargetPath: targetFile);
        var authorized = false;
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2", "pipeline-outputs.v1"]),
            GetPipelineOutputsAsyncCallback = _ => Task.FromResult(plan),
            AuthorizePipelineExecutionAsyncCallback = _ =>
            {
                authorized = true;
                return Task.CompletedTask;
            }
        };

        var exception = await Assert.ThrowsAsync<PublishVerificationException>(
            () => session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken));

        Assert.Equal(Aspire.Cli.Resources.PublishCommandStrings.VerifyManifestFileNotSupported, exception.Message);
        Assert.False(authorized);
        Assert.False(File.Exists(targetFile));
    }

    [Fact]
    public async Task PreflightAndAuthorizeAsync_ManifestDirectoryPrimary_Authorizes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        await using var session = await context.CreateSessionAsync();
        var plan = CreatePlan(
            context,
            session,
            "Prepared",
            stepName: "publish-manifest");
        var authorized = false;
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2", "pipeline-outputs.v1"]),
            GetPipelineOutputsAsyncCallback = _ => Task.FromResult(plan),
            AuthorizePipelineExecutionAsyncCallback = _ =>
            {
                authorized = true;
                return Task.CompletedTask;
            }
        };

        await session.PreflightAndAuthorizeAsync(backchannel, TestContext.Current.CancellationToken);

        Assert.True(authorized);
    }

    [Fact]
    public async Task DisposeAsync_StagedSymlink_DoesNotMutateLinkTarget()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        var session = await context.CreateSessionAsync();
        var stagingRoot = Directory.GetParent(session.OutputPath)!.FullName;
        var outside = Directory.CreateTempSubdirectory("aspire-verify-cleanup-target-");
        var sentinel = Path.Combine(outside.FullName, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        Directory.CreateDirectory(session.OutputPath);
        var link = Path.Combine(session.OutputPath, "link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside.FullName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                await session.DisposeAsync();
                Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
            }

            await session.DisposeAsync();

            Assert.False(Directory.Exists(stagingRoot));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                await session.DisposeAsync();
            }

            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_ReplacedStagingRoot_DoesNotTraverseReplacement()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = await CreateContextAsync(workspace);
        var session = await context.CreateSessionAsync();
        var stagingRoot = Directory.GetParent(session.OutputPath)!.FullName;
        var outside = Directory.CreateTempSubdirectory("aspire-verify-root-replacement-");
        var sentinel = Path.Combine(outside.FullName, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        Directory.Delete(stagingRoot, recursive: true);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(stagingRoot, outside.FullName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
            }

            await session.DisposeAsync();

            Assert.False(Directory.Exists(stagingRoot));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot);
            }

            outside.Delete(recursive: true);
        }
    }

    private static TestAppHostBackchannel CreateBackchannel(
        Func<GetPipelineOutputsResponse> preparedPlan,
        Func<GetPipelineOutputsResponse> finalPlan)
    {
        var planCall = 0;
        return new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2", "pipeline-outputs.v1"]),
            GetPipelineOutputsAsyncCallback = _ => Task.FromResult(planCall++ == 0 ? preparedPlan() : finalPlan())
        };
    }

    private static GetPipelineOutputsResponse CreatePlan(
        VerificationContext context,
        PublishVerificationSession session,
        string state,
        bool supportsRelocation = true,
        PipelineOutputInfo[]? additionalOutputs = null,
        string stepName = "publish",
        string primaryKind = "Directory",
        string? primaryTargetPath = null)
    {
        return new GetPipelineOutputsResponse
        {
            AppHostDirectory = context.AppHostDirectory,
            State = state,
            Steps = [new PipelineOutputStepInfo { Name = stepName, SupportsOutputPathRelocation = supportsRelocation }],
            Outputs =
            [
                new PipelineOutputInfo
                {
                    IsPrimary = true,
                    PublisherName = "aspire",
                    Name = "primary",
                    Kind = primaryKind,
                    OutputPath = session.OutputPath,
                    LogicalTargetPath = primaryTargetPath ?? context.TargetDirectory
                },
                .. (additionalOutputs ?? [])
            ]
        };
    }

    private static async Task<VerificationContext> CreateContextAsync(TemporaryWorkspace workspace)
    {
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var targetDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "generated");
        var targetFile = Path.Combine(targetDirectory, "artifact.txt");
        var git = new TestGitRepository
        {
            GetRootFromDirectoryAsyncCallback = (_, _) =>
                Task.FromResult<DirectoryInfo?>(workspace.WorkspaceRoot),
            GetIncludedFilesFromPathsAsyncCallback = (_, _, _) =>
                Task.FromResult<IReadOnlySet<string>?>(PathSet()),
            GetIgnoredFilesAsyncCallback = (_, _, _) =>
                Task.FromResult<IReadOnlySet<string>?>(PathSet())
        };
        var interaction = new TestInteractionService();
        return new VerificationContext(
            appHostDirectory.FullName,
            appHostFile,
            targetDirectory,
            targetFile,
            git,
            interaction,
            workspace.WorkspaceRoot);
    }

    private static HashSet<string> PathSet(params string[] paths)
    {
        return paths.ToHashSet(PublishVerificationPathSafety.PathComparer);
    }

    private sealed record VerificationContext(
        string AppHostDirectory,
        FileInfo AppHostFile,
        string TargetDirectory,
        string TargetFile,
        TestGitRepository Git,
        TestInteractionService Interaction,
        DirectoryInfo RepositoryRoot)
    {
        public Task<PublishVerificationSession> CreateSessionAsync()
        {
            return PublishVerificationSession.CreateAsync(
                AppHostFile,
                TargetDirectory,
                Git,
                Interaction,
                NullLogger<PublishCommand>.Instance,
                ["aspire", "publish", "--output-path", TargetDirectory],
                TestContext.Current.CancellationToken);
        }
    }
}
