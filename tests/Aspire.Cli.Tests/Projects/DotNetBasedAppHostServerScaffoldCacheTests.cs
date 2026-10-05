// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

/// <summary>
/// Guards the scaffold fingerprint cache on <see cref="DotNetBasedAppHostServerProject"/>.
/// Scaffolding used to delete <c>obj/</c> on every run, which discarded NuGet's restore output and
/// forced a full restore plus a full project-reference graph walk on every in-repo
/// <c>aspire run</c>. The scaffold is now rewritten only when its generated content actually
/// changes. These tests pin that contract in both directions.
/// </summary>
public class DotNetBasedAppHostServerScaffoldCacheTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CreateProjectFiles_PreservesRestoreArtifacts_WhenScaffoldContentUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var project = CreateProject(appPath);

        IntegrationReference[] integrations = [IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")];

        await project.CreateProjectFilesAsync(integrations);
        var assetsPath = SeedRestoreArtifacts(project);

        await project.CreateProjectFilesAsync(integrations);

        Assert.True(File.Exists(assetsPath),
            "obj/project.assets.json was deleted even though the scaffold content did not change.");
    }

    [Fact]
    public async Task CreateProjectFiles_ClearsRestoreArtifacts_WhenIntegrationSetChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var project = CreateProject(appPath);

        await project.CreateProjectFilesAsync([IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")]);
        var assetsPath = SeedRestoreArtifacts(project);

        // A different integration set produces a different csproj, so the previous restore no
        // longer describes this project and must not be reused. Aspire.Hosting.* names are
        // resolved as in-repo project references and dropped when missing, so use a package that
        // actually lands in the csproj as a PackageReference.
        await project.CreateProjectFilesAsync(
        [
            IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0"),
            IntegrationReference.FromPackage("Contoso.Widgets", "1.0.0"),
        ]);

        Assert.False(File.Exists(assetsPath),
            "obj/project.assets.json survived even though the integration set changed.");

        var csproj = await File.ReadAllTextAsync(
            Path.Combine(project.ProjectModelPath, DotNetBasedAppHostServerProject.ProjectFileName));
        Assert.Contains("Contoso.Widgets", csproj);
    }

    [Fact]
    public async Task CreateProjectFiles_RewritesScaffold_WhenAScaffoldFileWasDeleted()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var project = CreateProject(appPath);

        IntegrationReference[] integrations = [IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")];

        await project.CreateProjectFilesAsync(integrations);
        SeedRestoreArtifacts(project);

        // Simulate a hand-deleted or half-written scaffold. The fingerprint still matches, so only
        // the existence guard can catch this.
        var programPath = Path.Combine(project.ProjectModelPath, "Program.cs");
        File.Delete(programPath);

        await project.CreateProjectFilesAsync(integrations);

        Assert.True(File.Exists(programPath), "Program.cs was not restored after being deleted.");
    }

    [Fact]
    public async Task CreateProjectFiles_RewritesScaffold_WhenRestoreArtifactsAreAbsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var project = CreateProject(appPath);

        IntegrationReference[] integrations = [IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")];

        await project.CreateProjectFilesAsync(integrations);

        // A half-restored obj/ with no project.assets.json is what an interrupted restore leaves
        // behind. The fingerprint matches, so only the assets check can force the clean rebuild.
        var objPath = Path.Combine(project.ProjectModelPath, "obj");
        Directory.CreateDirectory(objPath);
        var stalePath = Path.Combine(objPath, "stale.cache");
        File.WriteAllText(stalePath, "partial restore");

        await project.CreateProjectFilesAsync(integrations);

        Assert.False(File.Exists(stalePath),
            "obj/ was reused even though no project.assets.json was present to restore from.");
    }

    [Fact]
    public async Task CreateProjectFiles_ClearsRestoreArtifacts_WhenProjectReferencePathChanges()
    {
        // Pointing an integration at a local checkout is the in-repo dev-speed path: edits to that
        // project flow straight into the next build without a repack. The cache must not pin an
        // earlier target, so retargeting the reference has to rewrite the scaffold.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var project = CreateProject(appPath);

        var firstProject = Path.Combine(appPath, "v1", "Contoso.Widgets.csproj");
        var secondProject = Path.Combine(appPath, "v2", "Contoso.Widgets.csproj");

        await project.CreateProjectFilesAsync(
            [IntegrationReference.FromProject("Contoso.Widgets", firstProject)]);
        var assetsPath = SeedRestoreArtifacts(project);

        var csprojPath = Path.Combine(project.ProjectModelPath, DotNetBasedAppHostServerProject.ProjectFileName);
        Assert.Contains(firstProject, await File.ReadAllTextAsync(csprojPath));

        await project.CreateProjectFilesAsync(
            [IntegrationReference.FromProject("Contoso.Widgets", secondProject)]);

        Assert.False(File.Exists(assetsPath),
            "obj/project.assets.json survived even though the project reference was retargeted.");
        Assert.Contains(secondProject, await File.ReadAllTextAsync(csprojPath));
    }

    /// <summary>
    /// Writes the marker file the cache uses to decide whether a usable restore already exists.
    /// Real restores create far more under obj/, but project.assets.json is the file the skip path
    /// checks and the one whose loss forces a full restore.
    /// </summary>
    private static string SeedRestoreArtifacts(DotNetBasedAppHostServerProject project)
    {
        var objPath = Path.Combine(project.ProjectModelPath, "obj");
        Directory.CreateDirectory(objPath);
        var assetsPath = Path.Combine(objPath, "project.assets.json");
        File.WriteAllText(assetsPath, "{}");
        return assetsPath;
    }

    private static DotNetBasedAppHostServerProject CreateProject(string appPath)
    {
        // Pin ProjectModelPath inside the workspace so test artifacts don't bleed into the user's
        // ~/.aspire/hosts directory.
        var projectModelPath = Path.Combine(appPath, ".aspire_server");

        return new DotNetBasedAppHostServerProject(
            appPath,
            socketPath: "test.sock",
            repoRoot: appPath,
            new TestDotNetCliRunner(),
            MockPackagingServiceFactory.Create(),
            new TestProcessExecutionFactory(),
            new TestEnvironment(),
            NullLogger<DotNetBasedAppHostServerProject>.Instance,
            projectModelPath);
    }
}
