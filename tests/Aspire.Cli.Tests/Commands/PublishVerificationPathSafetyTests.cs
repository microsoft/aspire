// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Commands;

public class PublishVerificationPathSafetyTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ValidateDestinationAsync_OutsideRepository_FailsClosed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outside = Directory.CreateTempSubdirectory("aspire-verify-outside-");
        try
        {
            var git = CreateGit(workspace.WorkspaceRoot);

            await Assert.ThrowsAsync<PublishVerificationException>(
                () => PublishVerificationPathSafety.ValidateDestinationAsync(
                    outside.FullName,
                    workspace.WorkspaceRoot,
                    git,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ValidateDestinationAsync_InsideNestedRepository_FailsClosed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var nested = workspace.WorkspaceRoot.CreateSubdirectory("nested");
        var git = CreateGit(workspace.WorkspaceRoot);
        git.GetRootFromDirectoryAsyncCallback = (directory, _) =>
            Task.FromResult<DirectoryInfo?>(
                PublishVerificationPathSafety.IsWithinRoot(nested.FullName, directory.FullName)
                    ? nested
                    : workspace.WorkspaceRoot);

        await Assert.ThrowsAsync<PublishVerificationException>(
            () => PublishVerificationPathSafety.ValidateDestinationAsync(
                nested.FullName,
                workspace.WorkspaceRoot,
                git,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateDestinationAsync_TargetContainingSubmoduleMetadata_FailsClosed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = workspace.WorkspaceRoot.CreateSubdirectory("generated");
        var submodule = target.CreateSubdirectory("module");
        await File.WriteAllTextAsync(Path.Combine(submodule.FullName, ".git"), "gitdir: ../../.git/modules/module");
        var git = CreateGit(workspace.WorkspaceRoot);

        await Assert.ThrowsAsync<PublishVerificationException>(
            () => PublishVerificationPathSafety.ValidateDestinationAsync(
                target.FullName,
                workspace.WorkspaceRoot,
                git,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateDestinationAsync_SymlinkEscape_FailsClosed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = workspace.WorkspaceRoot.CreateSubdirectory("generated");
        var outside = Directory.CreateTempSubdirectory("aspire-verify-link-target-");
        var link = Path.Combine(target.FullName, "escape");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside.FullName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
            }

            var git = CreateGit(workspace.WorkspaceRoot);
            await Assert.ThrowsAsync<PublishVerificationException>(
                () => PublishVerificationPathSafety.ValidateDestinationAsync(
                    target.FullName,
                    workspace.WorkspaceRoot,
                    git,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void ValidateGeneratedTree_SymlinkEscape_FailsClosed()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var staging = workspace.WorkspaceRoot.CreateSubdirectory("staging");
        var outside = Directory.CreateTempSubdirectory("aspire-verify-staged-link-");
        var link = Path.Combine(staging.FullName, "escape");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside.FullName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
            }

            Assert.Throws<PublishVerificationException>(
                () => PublishVerificationPathSafety.ValidateGeneratedTree(staging.FullName));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            outside.Delete(recursive: true);
        }
    }

    private static TestGitRepository CreateGit(DirectoryInfo repositoryRoot)
    {
        return new TestGitRepository
        {
            GetRootFromDirectoryAsyncCallback = (_, _) =>
                Task.FromResult<DirectoryInfo?>(repositoryRoot)
        };
    }
}
