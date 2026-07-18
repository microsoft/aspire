// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
namespace Aspire.Cli.Tests.Projects;

public class AppHostWorkloadIdTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Create_NormalizesCasingLikeHostingAppHostPathSha()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        Assert.Equal(
            AppHostWorkloadId.Create(target.FullName),
            AppHostWorkloadId.Create(target.FullName.ToUpperInvariant()));
    }

    [Fact]
    public void Create_UsesFullPathLikeHostingAppHostPathSha()
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
    public void Create_AddsAppHostPrefix()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        Assert.StartsWith("apphost-", AppHostWorkloadId.Create(target));
    }
}
