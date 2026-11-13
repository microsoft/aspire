// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Projects;

public class CSharpProjectFileTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Aspire.Hosting", false)]
    [InlineData("Aspire.Hosting", true)]
    [InlineData("Aspire.Hosting.Redis", false)]
    [InlineData("Aspire.Hosting.Redis", true)]
    public void AddIntegrationReferences_AspireHostingPackageWithoutRepositoryProject_AddsPackageReference(
        string packageName,
        bool useRepositoryRoot)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var repoRoot = useRepositoryRoot ? workspace.WorkspaceRoot.FullName : null;
        var projectFile = new CSharpProjectFile();

        projectFile.AddIntegrationReferences(
            [IntegrationReference.FromPackage(packageName, "13.4.0")],
            repoRoot);

        var packageReference = Assert.Single(projectFile.PackageReferences);
        Assert.Equal(packageName, packageReference.Name);
        Assert.Equal("13.4.0", packageReference.Version);
        Assert.Empty(projectFile.ProjectReferences);
    }
}
