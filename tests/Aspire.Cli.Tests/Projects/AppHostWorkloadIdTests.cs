// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Projects;

public class AppHostWorkloadIdTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Create_UsesSameIdForFinalFileSymlink()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        var linkPath = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.Link.csproj");
        TryCreateSymlink(linkPath, target.FullName, isDirectory: false);

        Assert.Equal(
            AppHostWorkloadId.Create(target),
            AppHostWorkloadId.Create(linkPath));
    }

    [Fact]
    public void Create_UsesSameIdForIntermediateDirectorySymlink()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var nestedDirectory = realDirectory.CreateSubdirectory("nested");
        var target = new FileInfo(Path.Combine(nestedDirectory.FullName, "AppHost.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        var linkDirectoryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        TryCreateSymlink(linkDirectoryPath, realDirectory.FullName, isDirectory: true);
        var pathThroughLink = Path.Combine(linkDirectoryPath, "nested", "AppHost.csproj");

        Assert.Equal(
            AppHostWorkloadId.Create(target),
            AppHostWorkloadId.Create(pathThroughLink));
    }

    [Fact]
    public void Create_NormalizesRelativeSegmentsBeforeHashing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("apphost");
        var nestedDirectory = appHostDirectory.CreateSubdirectory("nested");
        var target = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        Assert.Equal(
            AppHostWorkloadId.Create(target.FullName),
            AppHostWorkloadId.Create(Path.Combine(nestedDirectory.FullName, "..", "AppHost.csproj")));
    }

    [Fact]
    public void Create_NormalizesCasingOnlyOnWindows()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        var actual = AppHostWorkloadId.Create(target.FullName);
        var differentCasing = AppHostWorkloadId.Create(target.FullName.ToUpperInvariant());

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(actual, differentCasing);
        }
        else
        {
            Assert.NotEqual(actual, differentCasing);
        }
    }

    [Fact]
    public void Create_AddsAppHostPrefix()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        Assert.StartsWith("apphost-", AppHostWorkloadId.Create(target));
    }

    private static void TryCreateSymlink(string linkPath, string targetPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }
        catch (IOException ex)
        {
            Assert.Skip($"Symbolic link creation failed in this environment: {ex.Message}");
        }
    }
}
