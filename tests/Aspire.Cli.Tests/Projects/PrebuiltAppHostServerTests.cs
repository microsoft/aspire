// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class PrebuiltAppHostServerTests(ITestOutputHelper outputHelper)
{
    private const string NuGetOrgSource = "https://api.nuget.org/v3/index.json";

    [Fact]
    public async Task WriteIfChangedAsync_LeavesAnIdenticalFileAlone()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "IntegrationRestore.csproj");
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project />", CancellationToken.None);
        var firstWrite = File.GetLastWriteTimeUtc(path);

        // MSBuild rebuilds when an input timestamp moves, so an identical rewrite must not touch the file.
        File.SetLastWriteTimeUtc(path, firstWrite.AddMinutes(-5));
        var backdated = File.GetLastWriteTimeUtc(path);
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project />", CancellationToken.None);

        Assert.Equal(backdated, File.GetLastWriteTimeUtc(path));
        Assert.Equal("<Project />", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteIfChangedAsync_WritesWhenContentDiffers()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "IntegrationRestore.csproj");
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project />", CancellationToken.None);
        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />", CancellationToken.None);

        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteIfChangedAsync_RewritesAFileItCannotRead()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Read permission is removed with POSIX file modes.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "IntegrationRestore.csproj");
        await File.WriteAllTextAsync(path, "<Project />");

        // A generated file the CLI owns but momentarily cannot read -- an antivirus scanner holding it
        // open on Windows, a mode the user changed on POSIX -- must be rewritten, not turned into a
        // launch failure. The write below is what surfaces a genuinely unrecoverable error.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserWrite);
        }

        await PrebuiltAppHostServer.WriteIfChangedAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />", CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\" />", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void CanSkipIntegrationRestore_RestoresWhenNothingHasBeenRestoredYet()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        Assert.False(PrebuiltAppHostServer.CanSkipIntegrationRestore(workspace.WorkspaceRoot.FullName, "fingerprint", NullLogger.Instance));
    }

    [Fact]
    public void CanSkipIntegrationRestore_RestoresWhenTheAssetsAreMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteRestoreState(workspace.WorkspaceRoot.FullName, fingerprint: "fingerprint", writeAssets: false);

        Assert.False(PrebuiltAppHostServer.CanSkipIntegrationRestore(workspace.WorkspaceRoot.FullName, "fingerprint", NullLogger.Instance));
    }

    [Fact]
    public void CanSkipIntegrationRestore_RestoresWhenTheFingerprintChanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteRestoreState(workspace.WorkspaceRoot.FullName, fingerprint: "old-fingerprint");

        Assert.False(PrebuiltAppHostServer.CanSkipIntegrationRestore(workspace.WorkspaceRoot.FullName, "new-fingerprint", NullLogger.Instance));
    }

    [Fact]
    public void CanSkipIntegrationRestore_SkipsWhenTheFingerprintMatches()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteRestoreState(workspace.WorkspaceRoot.FullName, fingerprint: "fingerprint");

        Assert.True(PrebuiltAppHostServer.CanSkipIntegrationRestore(workspace.WorkspaceRoot.FullName, "fingerprint", NullLogger.Instance));
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintChangesWhenAReferencedProjectChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        await File.WriteAllTextAsync(referencedProject, """<Project><ItemGroup><PackageReference Include="Aspire.Hosting" Version="13.5.0" /></ItemGroup></Project>""");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var before = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        // A referenced project bumping its own dependency changes the closure that restore resolves
        // without changing a byte of the generated project file.
        await File.WriteAllTextAsync(referencedProject, """<Project><ItemGroup><PackageReference Include="Aspire.Hosting" Version="13.6.0-dev" /></ItemGroup></Project>""");
        var after = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintChangesWhenRestoreConfigContentChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(
            configPath,
            "<configuration><packageSources><add key=\"a\" value=\"https://example.invalid/a\" /></packageSources></configuration>");
        var first = await PrebuiltAppHostServer.ComputeRestoreInputsAsync(
            "<Project />",
            [],
            [],
            [configPath],
            restoreAdditionalProjectSources: null,
            nugetPackagesPath: null,
            nugetFallbackPackagesPaths: null,
            CancellationToken.None);

        await File.WriteAllTextAsync(
            configPath,
            "<configuration><packageSources><add key=\"b\" value=\"https://example.invalid/b\" /></packageSources></configuration>");
        var second = await PrebuiltAppHostServer.ComputeRestoreInputsAsync(
            "<Project />",
            [],
            [],
            [configPath],
            restoreAdditionalProjectSources: null,
            nugetPackagesPath: null,
            nugetFallbackPackagesPaths: null,
            CancellationToken.None);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Theory]
    [InlineData("%ASPIRE_TEST_SOURCE%")]
    [InlineData("$ASPIRE_TEST_SOURCE")]
    [InlineData("${ASPIRE_TEST_SOURCE}")]
    public async Task ComputeRestoreInputsAsync_ConfigEnvironmentReferenceDisablesRestoreSkip(string source)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(
            configPath,
            $"""
            <configuration>
              <packageSources>
                <add key="environment" value="{source}" />
              </packageSources>
            </configuration>
            """);

        var inputs = await PrebuiltAppHostServer.ComputeRestoreInputsAsync(
            "<Project />",
            [],
            [],
            [configPath],
            restoreAdditionalProjectSources: null,
            nugetPackagesPath: null,
            nugetFallbackPackagesPaths: null,
            CancellationToken.None);

        Assert.False(inputs.IsEligibleForSkip);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintChangesWhenGlobalPackagesFolderChanges()
    {
        var first = await PrebuiltAppHostServer.ComputeRestoreInputsAsync(
            "<Project />",
            [],
            [],
            nugetConfigPaths: null,
            restoreAdditionalProjectSources: null,
            nugetPackagesPath: Path.GetFullPath("packages-a"),
            nugetFallbackPackagesPaths: null,
            CancellationToken.None);

        var second = await PrebuiltAppHostServer.ComputeRestoreInputsAsync(
            "<Project />",
            [],
            [],
            nugetConfigPaths: null,
            restoreAdditionalProjectSources: null,
            nugetPackagesPath: Path.GetFullPath("packages-b"),
            nugetFallbackPackagesPaths: null,
            CancellationToken.None);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintChangesWhenFallbackPackagesFoldersChange()
    {
        var first = await PrebuiltAppHostServer.ComputeRestoreInputsAsync(
            "<Project />",
            [],
            [],
            nugetConfigPaths: null,
            restoreAdditionalProjectSources: null,
            nugetPackagesPath: null,
            nugetFallbackPackagesPaths: [Path.GetFullPath("fallback-a")],
            CancellationToken.None);

        var second = await PrebuiltAppHostServer.ComputeRestoreInputsAsync(
            "<Project />",
            [],
            [],
            nugetConfigPaths: null,
            restoreAdditionalProjectSources: null,
            nugetPackagesPath: null,
            nugetFallbackPackagesPaths: [Path.GetFullPath("fallback-b")],
            CancellationToken.None);

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintChangesWhenACentrallyManagedVersionChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        // Under central package management the reference carries no version at all: the version lives
        // in Directory.Packages.props, which MSBuild imports automatically. Bumping it there changes
        // what restore resolves while every hashed file stays byte-for-byte identical.
        await File.WriteAllTextAsync(referencedProject, """<Project><ItemGroup><PackageReference Include="Aspire.Hosting" /></ItemGroup></Project>""");
        var packagesProps = Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props");
        await File.WriteAllTextAsync(packagesProps, """<Project><ItemGroup><PackageVersion Include="Aspire.Hosting" Version="13.5.0" /></ItemGroup></Project>""");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var before = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        await File.WriteAllTextAsync(packagesProps, """<Project><ItemGroup><PackageVersion Include="Aspire.Hosting" Version="13.6.0-dev" /></ItemGroup></Project>""");
        var after = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintChangesWhenATransitivelyReferencedProjectChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var leaf = Path.Combine(workspace.WorkspaceRoot.FullName, "Leaf.csproj");
        await File.WriteAllTextAsync(leaf, """<Project><ItemGroup><PackageReference Include="Aspire.Hosting" Version="13.5.0" /></ItemGroup></Project>""");
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        await File.WriteAllTextAsync(referencedProject, """<Project><ItemGroup><ProjectReference Include="Leaf.csproj" /></ItemGroup></Project>""");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var before = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        // Restore resolves the whole graph, not just its first level, so a package bump two hops out
        // changes the closure exactly as much as one hop out does.
        await File.WriteAllTextAsync(leaf, """<Project><ItemGroup><PackageReference Include="Aspire.Hosting" Version="13.6.0-dev" /></ItemGroup></Project>""");
        var after = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_IsNotEligibleForSkipWhenATransitivelyReferencedProjectFloats()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var leaf = Path.Combine(workspace.WorkspaceRoot.FullName, "Leaf.csproj");
        await File.WriteAllTextAsync(leaf, """<Project><ItemGroup><PackageReference Include="Some.Package" Version="13.4.*" /></ItemGroup></Project>""");
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        await File.WriteAllTextAsync(referencedProject, """<Project><ItemGroup><ProjectReference Include="Leaf.csproj" /></ItemGroup></Project>""");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var inputs = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.False(inputs.IsEligibleForSkip);
    }

    [Theory]
    [InlineData("""<Project><Import Project="Versions.props" /></Project>""")]
    [InlineData("""<Project><ItemGroup><ProjectReference Include="$(RepoRoot)/Leaf.csproj" /></ItemGroup></Project>""")]
    public async Task ComputeRestoreInputsAsync_IsNotEligibleForSkipWhenRestoreInputsRequireEvaluation(string projectContent)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        await File.WriteAllTextAsync(referencedProject, projectContent);
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var inputs = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.False(inputs.IsEligibleForSkip);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintIncludesNuGetConfigCanonicalCasing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("src");
        var referencedProject = Path.Combine(projectDirectory.FullName, "Aspire.Hosting.Java.csproj");
        await File.WriteAllTextAsync(referencedProject, "<Project />");
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(nugetConfigPath, "<configuration />");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var before = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);
        await File.WriteAllTextAsync(nugetConfigPath, "<configuration><packageSources><clear /></packageSources></configuration>");
        var after = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_ToleratesAProjectReferenceCycle()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var first = Path.Combine(workspace.WorkspaceRoot.FullName, "First.csproj");
        var second = Path.Combine(workspace.WorkspaceRoot.FullName, "Second.csproj");
        // MSBuild rejects a cycle, but the fingerprint is computed before anything validates the
        // graph, so walking it has to terminate on its own rather than hang the launch.
        await File.WriteAllTextAsync(first, """<Project><ItemGroup><ProjectReference Include="Second.csproj" /></ItemGroup></Project>""");
        await File.WriteAllTextAsync(second, """<Project><ItemGroup><ProjectReference Include="First.csproj" /></ItemGroup></Project>""");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("First", first) };

        var inputs = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.NotEmpty(inputs.Fingerprint);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_FingerprintIsStableForUnchangedInputs()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        await File.WriteAllTextAsync(referencedProject, "<Project />");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var first = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);
        var second = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_IsNotEligibleForSkipWhenAReferencedProjectFloats()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        // The generated project pins exact versions, so the only float is inside the referenced
        // project. Restore must still run because the feed can resolve it to a different package.
        await File.WriteAllTextAsync(referencedProject, """<Project><ItemGroup><PackageReference Include="Some.Package" Version="13.4.*" /></ItemGroup></Project>""");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };

        var inputs = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", [], projectRefs, CancellationToken.None);

        Assert.False(inputs.IsEligibleForSkip);
    }

    [Fact]
    public async Task ComputeRestoreInputsAsync_IsEligibleForSkipWhenEveryVersionIsExact()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var referencedProject = Path.Combine(workspace.WorkspaceRoot.FullName, "Aspire.Hosting.Java.csproj");
        await File.WriteAllTextAsync(referencedProject, """<Project ToolsVersion="4.0"><ItemGroup><PackageReference Include="Some.Package" Version="13.5.0" /></ItemGroup></Project>""");
        var projectRefs = new List<IntegrationReference> { IntegrationReference.FromProject("Aspire.Hosting.Java", referencedProject) };
        var packageRefs = new List<IntegrationReference> { IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.5.0") };

        var inputs = await PrebuiltAppHostServer.ComputeRestoreInputsAsync("<Project />", packageRefs, projectRefs, CancellationToken.None);

        Assert.True(inputs.IsEligibleForSkip);
    }

    [Theory]
    [InlineData("""<PackageReference Include="A" Version="13.4.*" />""")]
    [InlineData("""<PackageReference Include="A" Version="[13.4,14)" />""")]
    [InlineData("""<PackageReference Include="A" Version="(13.4,)" />""")]
    [InlineData("""<PackageReference Include="A" VersionOverride="13.4.*" />""")]
    [InlineData("""<PackageReference Include="A" Version = "13.4.*" />""")]
    [InlineData("""<PackageReference Include='A' Version='13.4.*' />""")]
    [InlineData("""<PackageReference Include="A" Version="$(PackageVersion)" />""")]
    public void HasFloatingVersionAttribute_DetectsVersionsThatCanResolveDifferently(string projectText)
    {
        Assert.True(PrebuiltAppHostServer.HasFloatingVersionAttribute(projectText));
    }

    [Theory]
    [InlineData("""<PackageReference Include="A" Version="13.5.0" />""")]
    [InlineData("""<PackageReference Include="A" Version="13.5.0-preview.1.25000.1" />""")]
    // ToolsVersion ends in "Version" but is not a package version; the word boundary must exclude it.
    [InlineData("""<Project ToolsVersion="4.0,x" />""")]
    [InlineData("<Project />")]
    public void HasFloatingVersionAttribute_IsFalseForExactVersions(string projectText)
    {
        Assert.False(PrebuiltAppHostServer.HasFloatingVersionAttribute(projectText));
    }

    [Theory]
    [InlineData("13.4.*")]
    [InlineData("[13.4,14)")]
    [InlineData("(13.4,)")]
    public void HasFloatingPackageVersion_DetectsVersionsThatCanResolveDifferently(string version)
    {
        var packageRefs = new List<IntegrationReference> { IntegrationReference.FromPackage("Aspire.Hosting.Redis", version) };

        Assert.True(PrebuiltAppHostServer.HasFloatingPackageVersion(packageRefs));
    }

    [Fact]
    public void HasFloatingPackageVersion_IsFalseForExactVersions()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.5.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Java", "13.5.0-preview.1.25000.1")
        };

        Assert.False(PrebuiltAppHostServer.HasFloatingPackageVersion(packageRefs));
    }

    [Theory]
    [InlineData("error NETSDK1004: Assets file '/tmp/obj/project.assets.json' not found. Run a NuGet package restore.")]
    [InlineData("/tmp/obj/project.assets.json' doesn't have a target for 'net10.0'.")]
    public void ShouldRetryWithRestore_RetriesWhenTheAssetsAreMissingOrStale(string line)
    {
        var output = new OutputCollector();
        output.AppendOutput(line);

        Assert.True(PrebuiltAppHostServer.ShouldRetryWithRestore(output));
    }

    [Theory]
    [InlineData("error NETSDK1064: Package Aspire.Hosting.Redis, version 13.5.0 was not found. It might have been deleted since NuGet restore. Otherwise, NuGet restore might have only partially completed, which might have been due to maximum path length restrictions.")]
    [InlineData("error NU1101: Unable to find package Aspire.Hosting.Java. No packages exist with this id in source(s): dotnet-public")]
    [InlineData("error NU1102: Unable to find package Aspire.Hosting with version (>= 13.6.0-dev)")]
    public void ShouldRetryWithRestore_RetriesWhenThePackageCacheIsMissingPackages(string line)
    {
        var output = new OutputCollector();
        output.AppendOutput(line);

        Assert.True(PrebuiltAppHostServer.ShouldRetryWithRestore(output));
    }

    [Fact]
    public void ShouldRetryWithRestore_DoesNotRetryAnOrdinaryCompileFailure()
    {
        var output = new OutputCollector();
        output.AppendOutput("Program.cs(3,1): error CS0103: The name 'Foo' does not exist in the current context");

        Assert.False(PrebuiltAppHostServer.ShouldRetryWithRestore(output));
    }

    [Fact]
    public void GetIntegrationBuildFailureMessage_ExplainsAPackageDowngrade()
    {
        var output = new OutputCollector();
        // Localized MSBuild output: the error code is the only stable part to match on.
        output.AppendOutput("IntegrationRestore.csproj : error NU1605: Advertencia como error: Degradación del paquete detectada: Aspire.Hosting de 13.6.0-dev a 13.5.0.");

        var message = PrebuiltAppHostServer.GetIntegrationBuildFailureMessage(output);

        Assert.Contains("aspire.config.json", message, StringComparison.Ordinal);
        Assert.Contains(VersionHelper.GetDefaultTemplateVersion(), message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetIntegrationBuildFailureMessage_FallsBackForOtherFailures()
    {
        var output = new OutputCollector();
        output.AppendOutput("Program.cs(3,1): error CS0103: The name 'Foo' does not exist in the current context");

        Assert.Equal(ErrorStrings.IntegrationBuildFailed, PrebuiltAppHostServer.GetIntegrationBuildFailureMessage(output));
    }

    private static void WriteRestoreState(string restoreDir, string fingerprint, bool writeAssets = true)
    {
        var objDir = Path.Combine(restoreDir, "obj");
        Directory.CreateDirectory(objDir);
        if (writeAssets)
        {
            File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}");
        }

        File.WriteAllText(Path.Combine(objDir, "aspire-restore.stamp"), fingerprint);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithPackagesOnly_ProducesPackageReferences()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")
        };
        var projectRefs = new List<IntegrationReference>();

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var packageElements = doc.Descendants("PackageReference").ToList();
        Assert.Equal(2, packageElements.Count);
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting" && e.Attribute("Version")?.Value == "13.2.0");
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "13.2.0");

        Assert.Empty(doc.Descendants("ProjectReference"));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithProjectRefsOnly_ProducesProjectReferences()
    {
        var packageRefs = new List<IntegrationReference>();
        var projectRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var projectElements = doc.Descendants("ProjectReference").ToList();
        Assert.Single(projectElements);
        Assert.Equal("/path/to/MyIntegration.csproj", projectElements[0].Attribute("Include")?.Value);
        Assert.Equal("false", projectElements[0].Element("IsAspireProjectResource")?.Value);
        Assert.Equal("true", projectElements[0].Element("ReferenceOutputAssembly")?.Value);
        Assert.Null(projectElements[0].Element("Private"));

        Assert.Empty(doc.Descendants("PackageReference"));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithMixed_ProducesBothReferenceTypes()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")
        };
        var projectRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/tmp/libs");
        var doc = XDocument.Parse(xml);

        Assert.Equal(2, doc.Descendants("PackageReference").Count());
        Assert.Single(doc.Descendants("ProjectReference"));
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DoesNotSetOutDir()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0")
        };
        var projectRefs = new List<IntegrationReference>();

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(packageRefs, projectRefs, "/custom/output/path");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Null(doc.Descendants(ns + "OutDir").FirstOrDefault());
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DoesNotSetEarlyOutputPathProperties()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/custom/output/path");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Null(doc.Descendants(ns + "BaseOutputPath").FirstOrDefault());
        Assert.Null(doc.Descendants(ns + "BaseIntermediateOutputPath").FirstOrDefault());
        Assert.Null(doc.Descendants(ns + "MSBuildProjectExtensionsPath").FirstOrDefault());
    }

    [Fact]
    public void CreateClosureDirectoryBuildProps_SetsEarlyOutputPathProperties()
    {
        var doc = IntegrationClosureBuilder.CreateClosureDirectoryBuildProps(
            "/custom/output/path",
            "/custom/intermediate/path",
            "/custom/packages/path");

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal(
            Path.Combine("/custom/output/path", "bin") + Path.DirectorySeparatorChar,
            doc.Descendants(ns + "BaseOutputPath").FirstOrDefault()?.Value);
        Assert.Equal(
            "/custom/intermediate/path" + Path.DirectorySeparatorChar,
            doc.Descendants(ns + "BaseIntermediateOutputPath").FirstOrDefault()?.Value);
        Assert.Equal("$(BaseIntermediateOutputPath)", doc.Descendants(ns + "MSBuildProjectExtensionsPath").FirstOrDefault()?.Value);
        Assert.Equal("/custom/packages/path", doc.Descendants(ns + "RestorePackagesPath").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WritesClosureManifestFiles()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/work");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ClosureMetadataFileName), doc.Descendants(ns + "AspireClosureMetadataFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ClosureSourcesFileName), doc.Descendants(ns + "AspireClosureSourcesFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ClosureTargetsFileName), doc.Descendants(ns + "AspireClosureTargetsFile").FirstOrDefault()?.Value);
        Assert.Equal(Path.Combine("/tmp/work", IntegrationClosureBuilder.ProjectRefAssemblyNamesFileName), doc.Descendants(ns + "AspireProjectRefAssemblyNamesFile").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WritesClosureManifestTarget()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/work");
        var doc = XDocument.Parse(xml);

        var target = doc.Descendants("Target")
            .FirstOrDefault(element => element.Attribute("Name")?.Value == "_WriteAspireClosureManifest");

        Assert.NotNull(target);
        Assert.Equal("Build", target.Attribute("AfterTargets")?.Value);
        Assert.Equal("ResolveLockFileCopyLocalFiles", target.Attribute("DependsOnTargets")?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_HasCopyLocalLockFileAssemblies()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var copyLocal = doc.Descendants(ns + "CopyLocalLockFileAssemblies").FirstOrDefault()?.Value;
        Assert.Equal("true", copyLocal);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_DisablesAnalyzersAndDocGen()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal("false", doc.Descendants(ns + "EnableDefaultItems").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "EnableNETAnalyzers").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "GenerateDocumentationFile").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "IsPackable").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "IsPublishable").FirstOrDefault()?.Value);
        Assert.Equal("false", doc.Descendants(ns + "ProduceReferenceAssembly").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_TargetsNet10()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs");
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal("net10.0", doc.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithAdditionalSources_SetsRestoreAdditionalProjectSources()
    {
        var sources = new[] { "/local/packages", "https://my-feed/v3/index.json" };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(
            [],
            [],
            "/tmp/libs",
            sources);
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreConfigFile = doc.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").Single().Value;
        Assert.Null(restoreConfigFile);
        Assert.Equal("/local/packages;https://my-feed/v3/index.json", restoreSources);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithEmptyAdditionalSources_OverridesEnvironmentDefault()
    {
        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile([], [], "/tmp/libs", Enumerable.Empty<string>());
        var doc = XDocument.Parse(xml);

        var ns = doc.Root!.GetDefaultNamespace();
        var restoreSources = doc.Descendants(ns + "RestoreAdditionalProjectSources").Single();
        Assert.Equal(string.Empty, restoreSources.Value);
    }

    [Fact]
    public void GenerateIntegrationProjectFile_WithExactVersions_ExactPinsOnlyAspirePackages()
    {
        var packageRefs = new List<IntegrationReference>
        {
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17166.ga49d604d"),
            IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
        };

        var xml = PrebuiltAppHostServer.GenerateIntegrationProjectFile(
            packageRefs,
            [],
            "/tmp/libs",
            useExactPackageVersions: true);
        var doc = XDocument.Parse(xml);

        var packageElements = doc.Descendants("PackageReference").ToList();
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "[13.4.0-pr.17166.ga49d604d]");
        Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "CommunityToolkit.Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "1.0.0");
    }

    [Fact]
    public void Constructor_UsesWorkspaceAspireDirectoryForWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var server = CreatePrebuiltAppHostServer(workspace, appPath: appHostDirectory.FullName);

        var workingDirectory = Assert.IsType<string>(
            typeof(PrebuiltAppHostServer)
                .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(server));

        var normalizedWorkspaceRoot = PathNormalizer.ResolveToFilesystemPath(workspace.WorkspaceRoot.FullName);
        var rootDirectory = Path.Combine(normalizedWorkspaceRoot, ".aspire", "integrations", "apphosts");
        var isUnderRoot = workingDirectory.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase);
        var parentDirectory = Path.GetDirectoryName(workingDirectory);
        var isDirectChildOfRoot = parentDirectory is not null &&
                                   string.Equals(parentDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase);
        var isSafeToDelete = isUnderRoot && isDirectChildOfRoot && !string.Equals(workingDirectory, rootDirectory, StringComparison.OrdinalIgnoreCase);

        try
        {
            Assert.True(isSafeToDelete);
        }
        finally
        {
            if (isSafeToDelete && Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Constructor_UsesDistinctWorkingDirectoriesForMultipleAppHostsInSameWorkspace()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var firstAppHost = workspace.CreateDirectory(Path.Combine("apps", "api"));
        var secondAppHost = workspace.CreateDirectory(Path.Combine("apps", "web"));

        PrebuiltAppHostServer CreateServer(string appHostDirectory) =>
            CreatePrebuiltAppHostServer(workspace, appPath: appHostDirectory);

        var firstServer = CreateServer(firstAppHost.FullName);
        var secondServer = CreateServer(secondAppHost.FullName);

        var workingDirectoryField = typeof(PrebuiltAppHostServer)
            .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var firstWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(firstServer));
        var secondWorkingDirectory = Assert.IsType<string>(workingDirectoryField.GetValue(secondServer));

        var normalizedWorkspaceRoot = PathNormalizer.ResolveToFilesystemPath(workspace.WorkspaceRoot.FullName);
        var appHostsRoot = Path.Combine(normalizedWorkspaceRoot, ".aspire", "integrations", "apphosts");

        try
        {
            Assert.StartsWith(appHostsRoot, firstWorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(appHostsRoot, secondWorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(firstWorkingDirectory, secondWorkingDirectory);
        }
        finally
        {
            foreach (var dir in new[] { firstWorkingDirectory, secondWorkingDirectory })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void GetAppHostIntegrationCacheDirectory_NormalizesSymlinkedAppHostDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix symlink canonicalization is covered by this test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real-apphost");
        var linkDirectoryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "linked-apphost");
        TestSymlinkHelper.TryCreateSymlink(linkDirectoryPath, realDirectory.FullName);

        var realCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(realDirectory);
        var linkCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(new DirectoryInfo(linkDirectoryPath));

        Assert.Equal(realCacheDirectory.FullName, linkCacheDirectory.FullName);
    }

    [Fact]
    public async Task GetAppHostIntegrationCacheDirectory_PreservesLexicalWorkspaceForExternalSymlinkTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix symlink canonicalization is covered by this test.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var lexicalWorkspace = workspace.WorkspaceRoot.CreateSubdirectory("workspace");
        await File.WriteAllTextAsync(Path.Combine(lexicalWorkspace.FullName, AspireConfigFile.FileName), "{}");
        var externalAppHost = workspace.WorkspaceRoot.CreateSubdirectory("external-apphost");
        var linkedAppHostPath = Path.Combine(lexicalWorkspace.FullName, "apphost");
        TestSymlinkHelper.TryCreateSymlink(linkedAppHostPath, externalAppHost.FullName);

        var externalCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(externalAppHost);
        var linkedCacheDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(new DirectoryInfo(linkedAppHostPath));

        Assert.StartsWith(
            PathNormalizer.ResolveToFilesystemPath(
                Path.Combine(lexicalWorkspace.FullName, ".aspire", "integrations", "apphosts")) + Path.DirectorySeparatorChar,
            linkedCacheDirectory.FullName,
            StringComparisons.FileSystemPath);
        Assert.Equal(externalCacheDirectory.Name, linkedCacheDirectory.Name);
    }

    // The local pseudo-channel has no package-source mappings, so it does not require an overlay.
    // Every other explicit channel emits its mapping policy regardless of the running CLI identity.

    [Fact]
    public async Task CreateRestoreOverlay_LocalIdentity_LocalRequested_ReturnsNull()
    {
        // A local pseudo-channel does not define a package-source mapping policy.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await CreateRestoreOverlayAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_LocalIdentity_PrRequested_EmitsOverlay()
    {
        // The project-requested channel determines the overlay independently of the CLI identity.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StableIdentity_StableRequested_EmitsOverlay()
    {
        // The project-requested stable channel supplies the overlay mappings.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("stable");
        var server = CreateServerWithExplicitChannel(workspace, "stable", executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "stable");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StableIdentity_LocalRequested_ReturnsNull()
    {
        // Overlay selection depends on the project-requested channel, not the CLI identity.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("stable");
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await CreateRestoreOverlayAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_DailyIdentity_DailyRequested_EmitsOverlay()
    {
        // An explicit daily channel supplies its package-source mapping overlay.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        var server = CreateServerWithExplicitChannel(workspace, "daily", executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "daily");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_PrIdentity_DifferentPrRequested_EmitsOverlay()
    {
        // The requested PR channel supplies the overlay independently of the running PR build.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("pr-67890");
        var server = CreateServerWithExplicitChannel(workspace, "pr-12345", executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "pr-12345");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_LocalIdentity_StagingRequested_EmitsOverlayWithGlobalPackagesFolder()
    {
        // The policy overlay is temporary, so the source-specific package cache must use a stable
        // absolute path that survives overlay cleanup and remains valid for the package manifest.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("local");
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, channelSource)
        };
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: mappings,
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var server = CreateServerWithChannel(workspace, stagingChannel, executionContext);

        using var result = await CreateRestoreOverlayAsync(server, "staging");

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        var gpf = doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gpf);
        var gpfValue = gpf.Attribute("value")?.Value;
        Assert.False(string.IsNullOrEmpty(gpfValue));
        Assert.True(Path.IsPathFullyQualified(gpfValue), $"globalPackagesFolder value must be an absolute path. Got: {gpfValue}");
        // The overlay directory is recursively deleted on dispose; the cache must live elsewhere
        // so manifest paths produced by BundleNuGetService remain valid for the AppHost's lifetime.
        var overlayDirectory = result.ConfigFile.Directory!.FullName;
        Assert.False(
            gpfValue!.StartsWith(overlayDirectory, StringComparison.Ordinal),
            $"globalPackagesFolder must not be under the policy overlay directory '{overlayDirectory}'. Got: {gpfValue}");
        // The cache subdirectory must be keyed by the resolved feed URL so two different staging
        // feeds (e.g. two darc builds or an overrideStagingFeed setting) get distinct caches.
        var expectedCacheKey = CliPathHelper.ComputeStagingCacheIdentityKey(result.CacheIdentity);
        Assert.NotNull(expectedCacheKey);
        var expectedCachePath = Path.Combine(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory),
            expectedCacheKey);
        Assert.Equal(expectedCachePath, gpfValue);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StagingRequested_FromRealPackagingService_EmitsStableGlobalPackagesFolderOutsideOverlayDirectory()
    {
        // A staging channel synthesized by the real packaging service must select an absolute
        // package cache outside the temporary overlay directory. Package manifests retain paths
        // into this cache after the overlay is disposed.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        const string overrideStagingFeed = "https://pkgs.dev.azure.com/dnceng/internal/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json";
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workspace,
            identityChannel: PackageChannelNames.Staging);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = overrideStagingFeed
            })
            .Build();
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            configuration,
            NullLogger<PackagingService>.Instance);

        var server = CreateServerWithPackagingService(workspace, packagingService, executionContext);

        using var result = await CreateRestoreOverlayAsync(server, PackageChannelNames.Staging);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        var gpf = doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gpf);
        var gpfValue = gpf.Attribute("value")?.Value;
        Assert.False(string.IsNullOrEmpty(gpfValue));
        Assert.True(Path.IsPathFullyQualified(gpfValue), $"globalPackagesFolder value must be an absolute path. Got: {gpfValue}");
        var overlayDirectory = result.ConfigFile.Directory!.FullName;
        Assert.False(
            gpfValue!.StartsWith(overlayDirectory, StringComparison.Ordinal),
            $"globalPackagesFolder must not be under the policy overlay directory '{overlayDirectory}'. Got: {gpfValue}");
        // The cache key is derived from the resolved staging feed URL so the same CLI talking to
        // a different overrideStagingFeed gets a different cache bucket.
        var expectedCacheKey = CliPathHelper.ComputeStagingCacheIdentityKey(result.CacheIdentity);
        Assert.NotNull(expectedCacheKey);
        var expectedCachePath = Path.Combine(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory),
            expectedCacheKey);
        Assert.Equal(expectedCachePath, gpfValue);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-99")]
    public async Task CreateRestoreOverlay_LocalRequested_ReturnsNull_RegardlessOfIdentity(string identity)
    {
        // The project-requested local channel never emits a mapping overlay, independently of the
        // running CLI identity.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel(identity);
        var server = CreateServerWithExplicitChannel(workspace, "local", executionContext);

        var result = await CreateRestoreOverlayAsync(server, "local");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_MapsAspireToOverrideAndAddsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: null,
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverrideWithoutRequestedChannel_DoesNotIncludeExplicitChannelAspireMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var explicitChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, explicitChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: null,
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Empty(GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_PreservesRequestedChannelMappingsAndGlobalPackagesFolder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("CommunityToolkit*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var executionContext = CreateContextWithIdentityChannel("pr-12345");
        var server = CreateServerWithChannel(workspace, stagingChannel, executionContext);

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal(["CommunityToolkit*"], GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
        var gpf = doc.Descendants("config")
            .SelectMany(c => c.Elements("add"))
            .FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(gpf);
        // The source-specific package cache must outlive the policy overlay so BundleNuGetService's
        // manifest paths remain valid after disposal.
        var gpfValue = gpf.Attribute("value")?.Value;
        Assert.False(string.IsNullOrEmpty(gpfValue));
        Assert.True(Path.IsPathFullyQualified(gpfValue), $"globalPackagesFolder value must be an absolute path. Got: {gpfValue}");
        var overlayDirectory = result.ConfigFile.Directory!.FullName;
        Assert.False(
            gpfValue!.StartsWith(overlayDirectory, StringComparison.Ordinal),
            $"globalPackagesFolder must not be under the policy overlay directory '{overlayDirectory}'. Got: {gpfValue}");
        // The cache key is derived from the --source override, not the channel's own mappings,
        // so users running multiple overrides against the same CLI get distinct cache buckets.
        var expectedCacheKey = CliPathHelper.ComputeStagingCacheIdentityKey(result.CacheIdentity);
        Assert.NotNull(expectedCacheKey);
        var expectedCachePath = Path.Combine(
            CliPathHelper.GetStagingNuGetPackagesDirectory(executionContext.AspireHomeDirectory),
            expectedCacheKey);
        Assert.Equal(expectedCachePath, gpfValue);
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_DropsRequestedChannelAspireMappings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource), new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Empty(GetPackagePatternsForSource(doc, channelSource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithUnknownRequestedChannel_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([])
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRestoreOverlayAsync(
                server,
                requestedChannel: PackageChannelNames.Staging,
                packageSourceOverride: packageSourceOverride));

        Assert.Contains(PackageChannelNames.Staging, exception.Message);
        Assert.Equal(PackageChannelNames.Staging, packagingService.LastRequestedChannelName);
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_UsesChannelAllPackagesMappingAsFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping(PackageMapping.AllPackages, channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, channelSource));
        Assert.Empty(GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithPackageSourceOverride_WhenChannelLookupFails_StillCreatesOverridePolicyWithFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channel lookup failed.")
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        using var result = await CreateRestoreOverlayAsync(
            server,
            requestedChannel: "staging",
            packageSourceOverride: packageSourceOverride);

        Assert.NotNull(result);
        var doc = XDocument.Load(result.ConfigFile.FullName);
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, packageSourceOverride));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, NuGetOrgSource));
    }

    [Fact]
    public async Task CreateRestoreOverlay_WithExplicitChannelAndNoOverride_WhenChannelLookupFails_Throws()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channel lookup failed.")
        };
        var server = CreateServerWithPackagingService(workspace, packagingService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRestoreOverlayAsync(
                server,
                requestedChannel: "staging",
                packageSourceOverride: null));

        Assert.Equal("Channel lookup failed.", exception.Message);
    }

    [Fact]
    public async Task ResolveAdditionalSources_WithPackageSourceOverrideAndMatchedChannel_OmitsChannelAspireFeedFromSources()
    {
        // Additional sources are co-eligible with mapped sources, so the channel's Aspire feed
        // must be excluded when an explicit source owns the Aspire package mapping.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await ResolveAdditionalSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.DoesNotContain(channelSource, sources);
        Assert.Contains(NuGetOrgSource, sources);
    }

    [Fact]
    public async Task ResolveAdditionalSources_WithPackageSourceOverrideAndMatchedChannelNonAspireMapping_KeepsChannelSourceAndAddsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("CommunityToolkit*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await ResolveAdditionalSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.Contains(channelSource, sources);
        // The policy adds NuGet.org as the catch-all when the channel does not provide one.
        Assert.Contains(NuGetOrgSource, sources);
    }

    [Fact]
    public async Task ResolveAdditionalSources_WithPackageSourceOverrideAndMatchedChannelAllPackagesMapping_OmitsNuGetOrgFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var stagingChannel = PackageChannel.CreateExplicitChannel(
            name: "staging",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping(PackageMapping.AllPackages, channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var server = CreateServerWithChannel(workspace, stagingChannel, CreateContextWithIdentityChannel("pr-12345"));

        var sources = await ResolveAdditionalSourcesAsync(server, requestedChannel: "staging", packageSourceOverride: packageSourceOverride);

        Assert.NotNull(sources);
        Assert.Contains(packageSourceOverride, sources);
        Assert.Contains(channelSource, sources);
        // The channel's catch-all remains authoritative, so NuGet.org is not added as another
        // co-eligible source.
        Assert.DoesNotContain(NuGetOrgSource, sources);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", "https://api.nuget.org/v3/index.json")]
    [InlineData("/tmp/aspire-packages", "/tmp/aspire-packages")]
    [InlineData(@"C:\packages", @"C:\packages")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json", "https://***@feed.example.com/v3/index.json")]
    [InlineData("https://feed.blob.core.windows.net/foo/index.json?sv=2024-01&sig=secret-sig", "https://feed.blob.core.windows.net/foo/index.json")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json?sig=secret", "https://***@feed.example.com/v3/index.json")]
    [InlineData("https://feed.example.com/v3/index.json#fragment", "https://feed.example.com/v3/index.json")]
    public void RedactSourceForDisplay_StripsCredentialsAndQueryFromHttpUrlsButPreservesPlainSources(string input, string expected)
    {
        Assert.Equal(expected, PrebuiltAppHostServer.RedactSourceForDisplay(input));
    }

    [Fact]
    public async Task CreateRestoreOverlay_StagingRequested_RefusesWhenPackagingServiceReportsUnavailable()
    {
        // An unavailable staging channel must fail with the packaging service's actionable reason
        // instead of allowing restore to use another explicit channel.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRestoreOverlayAsync(server, "staging"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task CreateRestoreOverlay_StagingRequestedWithSourceOverride_RefusesWhenPackagingServiceReportsUnavailable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateRestoreOverlayAsync(server, "staging", "/tmp/aspire-pr-hive/packages"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task ResolveAdditionalSources_StagingRequested_RefusesWhenPackagingServiceReportsUnavailable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        const string unavailableReason =
            "Staging unavailable on this daily CLI build. Set overrideStagingFeed or enable the StagingChannelEnabled feature flag to use it.";
        var server = CreateServerWithUnavailableStagingChannel(workspace, executionContext, unavailableReason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ResolveAdditionalSourcesAsync(server, "staging"));
        Assert.Equal(unavailableReason, ex.Message);
    }

    [Fact]
    public async Task ResolveAdditionalSources_NonStagingRequest_NotAffectedByStagingUnavailableReason()
    {
        // Negative control: the staging refusal must only fire for requestedChannel == "staging".
        // A request for any other channel name must continue to resolve normally even when the
        // packaging service is reporting staging-unavailable.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var executionContext = CreateContextWithIdentityChannel("daily");
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]),
            GetStagingChannelUnavailableReasonCallback = () => "Staging unavailable"
        };

        var server = CreatePrebuiltAppHostServer(workspace, packagingService: packagingService, executionContext: executionContext);

        var sources = await ResolveAdditionalSourcesAsync(server, "daily");

        Assert.NotNull(sources);
        Assert.Contains("https://pkgs.dev.azure.com/fake/v3/index.json", sources);
    }

    private static PrebuiltAppHostServer CreateServerWithUnavailableStagingChannel(
        TemporaryWorkspace workspace,
        CliExecutionContext executionContext,
        string unavailableReason)
    {
        // PackagingService omits an unavailable staging channel and reports the reason separately.
        // Returning a daily channel verifies that source resolution does not substitute it.
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel]),
            GetStagingChannelUnavailableReasonCallback = () => unavailableReason
        };

        return CreatePrebuiltAppHostServer(workspace, packagingService: packagingService, executionContext: executionContext);
    }

    private static CliExecutionContext CreateContextWithIdentityChannel(string identityChannel) =>
        new(new DirectoryInfo(Path.GetTempPath()),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hives")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "cache")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "sdks")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "logs")),
            "test.log",
            identityChannel: identityChannel);

    // Builds a PrebuiltAppHostServer with the constant test wiring (socket name, SDK installer, process
    // execution factory, logger) so individual tests only specify the parameters their scenario exercises.
    // The execution context defaults to a fresh test context and is shared with the default NuGet service.
    // Tests that need bundle-layout discovery (FixedLayoutDiscovery) or a custom process runner build their
    // own BundleNuGetService and pass it via nugetService.
    private static PrebuiltAppHostServer CreatePrebuiltAppHostServer(
        TemporaryWorkspace workspace,
        string? appPath = null,
        LayoutConfiguration? layout = null,
        TestDotNetCliRunner? dotNetCliRunner = null,
        IPackagingService? packagingService = null,
        CliExecutionContext? executionContext = null,
        BundleNuGetService? nugetService = null)
    {
        executionContext ??= TestExecutionContextFactory.CreateTestContext();

        if (nugetService is null)
        {
            var nugetLayoutRoot = workspace.CreateDirectory("nuget-layout");
            var managedDirectory = nugetLayoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
            File.WriteAllText(
                Path.Combine(
                    managedDirectory.FullName,
                    BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                string.Empty);
            var nugetExecutionFactory = new TestProcessExecutionFactory
            {
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse())
            };
            nugetService = new BundleNuGetService(
                new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = nugetLayoutRoot.FullName }),
                new LayoutProcessRunner(nugetExecutionFactory),
                new TestFeatures(),
                new TestEnvironment(),
                NullLogger<BundleNuGetService>.Instance);
        }

        return new PrebuiltAppHostServer(
            appPath ?? workspace.WorkspaceRoot.FullName,
            "test.sock",
            layout ?? new LayoutConfiguration(),
            nugetService,
            dotNetCliRunner ?? new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            packagingService ?? MockPackagingServiceFactory.Create(),
            executionContext,
            new TestProcessExecutionFactory(),
            new TestEnvironment(),
            NullLogger.Instance);
    }

    private static PrebuiltAppHostServer CreateServerWithExplicitChannel(
        TemporaryWorkspace workspace,
        string channelName,
        CliExecutionContext executionContext)
    {
        // channelName is the name of the channel registered in the TestPackagingService — i.e. the
        // channel a project's aspire.config.json would resolve to when it requests that name.
        var mappings = new[]
        {
            new PackageMapping(PackageMapping.AllPackages, "https://pkgs.dev.azure.com/fake/v3/index.json")
        };
        var channel = PackageChannel.CreateExplicitChannel(
            channelName, PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);
        return CreateServerWithChannel(workspace, channel, executionContext);
    }

    private static PrebuiltAppHostServer CreateServerWithChannel(
        TemporaryWorkspace workspace,
        PackageChannel channel,
        CliExecutionContext executionContext)
    {
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        return CreateServerWithPackagingService(workspace, packagingService, executionContext);
    }

    private static PrebuiltAppHostServer CreateServerWithPackagingService(
        TemporaryWorkspace workspace,
        IPackagingService packagingService,
        CliExecutionContext? executionContext = null)
    {
        return CreatePrebuiltAppHostServer(workspace, packagingService: packagingService, executionContext: executionContext);
    }

    private static async Task<TemporaryNuGetConfig?> CreateRestoreOverlayAsync(
        PrebuiltAppHostServer server,
        string? requestedChannel,
        string? packageSourceOverride = null)
    {
        var restoreSources = await server.ResolveIntegrationRestoreSourcesAsync(
            requestedChannel,
            packageSourceOverride,
            CancellationToken.None);
        var configSources = PrebuiltAppHostServer.ResolveNuGetConfigSources(
            restoreSources.PackageSourceMappings,
            ambientSources: []);
        return await server.CreateRestoreOverlayAsync(restoreSources, configSources);
    }

    private static async Task<IReadOnlyList<string>?> ResolveAdditionalSourcesAsync(
        PrebuiltAppHostServer server,
        string? requestedChannel,
        string? packageSourceOverride = null)
    {
        var restoreSources = await server.ResolveIntegrationRestoreSourcesAsync(
            requestedChannel,
            packageSourceOverride,
            CancellationToken.None);
        return restoreSources.AdditionalSources.Count > 0 ? restoreSources.AdditionalSources : null;
    }

    [Fact]
    public async Task ResolveRequestedChannel_UsesProjectLocalAspireConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-new"
            }
            """);

        var server = CreatePrebuiltAppHostServer(workspace);

        var channel = server.ResolveRequestedChannel();

        Assert.Equal("pr-new", channel);
    }

    [Fact]
    public async Task PrepareAsync_WithNoIntegrations_WritesDefaultAppSettings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var server = CreatePrebuiltAppHostServer(workspace);

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", []);

            Assert.True(
                result.Success,
                result.Output is null
                    ? null
                    : string.Join(Environment.NewLine, result.Output.GetLines().Select(static line => line.Line)));
            Assert.Null(server.SelectedProjectLayoutPath);

            var appSettingsPath = Path.Combine(workingDirectory, "appsettings.json");
            Assert.True(File.Exists(appSettingsPath));

            var appSettingsContent = await File.ReadAllTextAsync(appSettingsPath);
            Assert.Contains("\"Aspire.Hosting\"", appSettingsContent);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferences_SetsOnlyPackageProbeManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")]);

            Assert.True(result.Success);
            Assert.Null(server.SelectedProjectLayoutPath);
            Assert.Equal(3, executionFactory.AttemptCount);

            var manifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            Assert.StartsWith(
                CliPathHelper.StripMacOSFirmlinkPrefix(
                    Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "integrations", "package-restore")),
                CliPathHelper.StripMacOSFirmlinkPrefix(manifestPath),
                StringComparison.OrdinalIgnoreCase);

            var startInfo = server.CreateStartInfo(123);
            Assert.Equal(manifestPath, startInfo.Environment[KnownConfigNames.IntegrationProbeManifestPath]);
            Assert.False(startInfo.Environment.ContainsKey(KnownConfigNames.IntegrationLibsPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenPackageRestoreIsCanceled_PropagatesCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AsyncAttemptCallback = (_, _, cancellationToken) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<(int ExitCode, string? Stdout)>(cancellationToken);
        };
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")],
                cancellationToken: cancellation.Token));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferences_UsesPackageSourceOverride()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        List<string>? restoreArgs = null;

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([packageSourceOverride, NuGetOrgSource], GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageSourceOverride_AddsNuGetOrgFallbackSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        List<string>? restoreArgs = null;

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([packageSourceOverride, NuGetOrgSource], GetSourceArguments(restoreArgs!));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("pr-12345")]
    [InlineData("local")]
    [InlineData("worktree-feature")]
    public async Task PrepareAsync_WithHiveBackedChannel_UsesLocalAspireSourceAsOverride(string channelName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSource = workspace.CreateDirectory("hive-packages");
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, $$"""
            {
                "channel": "{{channelName}}"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: channelName,
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSource.FullName)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([packageSource.FullName, NuGetOrgSource], GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHiveBackedChannelUsingFileUri_UsesLocalAspireSourceAsOverride()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var packageSource = workspace.CreateDirectory("hive-packages");
        var packageSourceUri = new Uri(packageSource.FullName).AbsoluteUri;
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", packageSourceUri)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Contains(packageSourceUri, GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
            Assert.Contains("CommunityToolkit.Aspire.Hosting.Redis,1.0.0", restoreArgs!);
            Assert.Contains("--nuget-config", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithExplicitPackageSourceOverride_IgnoresHiveBackedAspireSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var explicitPackageSource = workspace.CreateDirectory("explicit-packages");
        var hivePackageSource = workspace.CreateDirectory("hive-packages");
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", hivePackageSource.FullName),
                new PackageMapping(PackageMapping.AllPackages, channelSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: explicitPackageSource.FullName);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([explicitPackageSource.FullName, channelSource], GetSourceArguments(restoreArgs!));
            Assert.DoesNotContain(hivePackageSource.FullName, restoreArgs!);
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHttpBackedChannel_DoesNotUseExactPackageVersions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            Assert.Equal([channelSource], GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,13.4.0-pr.17141.gf142085f", restoreArgs!);
            Assert.DoesNotContain("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenPackagingServiceThrowsForExplicitChannel_Fails()
    {
        // An explicitly requested channel must not silently fall back to ambient NuGet sources
        // when channel resolution fails, because that could restore a different package build
        // while reporting the requested channel.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelName = "pr-12345";
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, $$"""
            {
                "channel": "{{channelName}}"
            }
            """);

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromException<IEnumerable<PackageChannel>>(
                new InvalidOperationException("simulated packaging service failure"))
        };
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.False(result.Success);
            Assert.Null(restoreArgs);
            Assert.Contains(
                result.Output!.GetLines(),
                line => line.Line.Contains("simulated packaging service failure", StringComparison.Ordinal));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithHiveBackedChannelPointingAtMissingLocalDirectory_DoesNotApplyOverride()
    {
        // Negative case for ResolveLocalPackageSourceOverrideAsync: a stale aspire.config.json
        // (e.g. user pinned channel = "pr-12345" but later deleted the local hive directory)
        // must NOT cause the prebuilt restore to pin Aspire packages to a non-existent local
        // directory. GetExistingLocalAspirePackageSource skips mappings whose Source does not
        // exist on disk, so auto-discovery returns null and restore falls through to the
        // ambient + channel-source path with no exact-pin.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var missingPackageSource = Path.Combine(workspace.WorkspaceRoot.FullName, "this-hive-was-deleted");
        Assert.False(Directory.Exists(missingPackageSource));
        List<string>? restoreArgs = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var channel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", missingPackageSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                restoreArgs = [.. args];
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")]);

            Assert.True(result.Success);
            Assert.NotNull(restoreArgs);
            // The override was not applied (Directory.Exists check failed), so the source list
            // is just the channel's raw Aspire mapping with no NuGet.org fallback appended (the
            // fallback only fires on the override path), and no exact-pin is emitted. Contrast
            // with PrepareAsync_WithHiveBackedChannel_UsesLocalAspireSourceAsOverride where the
            // existing local directory promotes the channel source to an override and adds the
            // NuGet.org fallback + exact-pinning.
            Assert.Equal([missingPackageSource], GetSourceArguments(restoreArgs!));
            Assert.DoesNotContain(NuGetOrgSource, GetSourceArguments(restoreArgs!));
            Assert.Contains("Aspire.Hosting.CodeGeneration.TypeScript,13.4.0-pr.17141.gf142085f", restoreArgs!);
            Assert.DoesNotContain("Aspire.Hosting.CodeGeneration.TypeScript,[13.4.0-pr.17141.gf142085f]", restoreArgs!);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithSourceAndChannelHavingAspireMapping_PolicyOverlayDropsChannelAspireMapping()
    {
        // End-to-end check that `aspire new --source <pr> --channel <X>` does not let the channel's
        // Aspire* feed remain co-eligible with the override at restore time.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings:
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, NuGetOrgSource)
            ],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        XDocument? policyOverlay = null;
        executionFactory.AssertionCallback = (args, _, _, _) =>
        {
            if (args is ["nuget", "restore", ..])
            {
                // Read the policy overlay while it still exists; it is disposed when
                // PrepareAsync's inner `using var` exits, which races with our assertions.
                var argsList = (IReadOnlyList<string>)args;
                var nugetConfigIndex = -1;
                for (var i = 0; i < argsList.Count - 1; i++)
                {
                    if (argsList[i] == "--nuget-config")
                    {
                        nugetConfigIndex = i;
                        break;
                    }
                }

                if (nugetConfigIndex >= 0)
                {
                    policyOverlay = XDocument.Load(argsList[nugetConfigIndex + 1]);
                }
            }
        };

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.Equal("daily", result.ChannelName);
            Assert.NotNull(policyOverlay);

            // The overlay's complete mapping policy makes only the override eligible for Aspire packages.
            Assert.Equal(["Aspire*"], GetPackagePatternsForSource(policyOverlay!, packageSourceOverride));
            Assert.Empty(GetPackagePatternsForSource(policyOverlay!, channelSource));
            Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(policyOverlay!, NuGetOrgSource));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_OutputIncludesSourceAndChannelContext()
    {
        // When restore fails, the displayed output is the only debugging surface most users see.
        // Pin that --source and the requested channel are present so a failed
        // `aspire new --source <X> --channel <Y>` doesn't require re-running with diagnostic logs
        // just to recover which inputs were in play.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        // Fail the restore step itself; BundleNuGetService throws on non-zero exit which
        // propagates through PrepareAsync's outer catch.
        executionFactory.DefaultExitCode = 1;

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.17141.gf142085f")],
                packageSourceOverride: packageSourceOverride);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            Assert.Contains($"--source: {packageSourceOverride}", combined);
            Assert.Contains("channel:  daily", combined);
            Assert.Contains("packages: Aspire.Hosting.CodeGeneration.TypeScript 13.4.0-pr.17141.gf142085f", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_WithManyPackages_TruncatesPackageList()
    {
        // The package preview caps at 5 entries with a "(+N more)" suffix so the error footer
        // doesn't explode for projects with large package counts. Pin the truncation shape so
        // it can't silently regress.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.DefaultExitCode = 1;

        var packages = Enumerable.Range(0, 8)
            .Select(i => IntegrationReference.FromPackage($"Aspire.Hosting.Pkg{i}", "1.0.0"))
            .ToArray();

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                packages,
                packageSourceOverride: packageSourceOverride);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            // First five packages appear; later ones are collapsed into a count.
            Assert.Contains("Aspire.Hosting.Pkg0 1.0.0", combined);
            Assert.Contains("Aspire.Hosting.Pkg4 1.0.0", combined);
            Assert.DoesNotContain("Aspire.Hosting.Pkg5 1.0.0", combined);
            Assert.DoesNotContain("Aspire.Hosting.Pkg7 1.0.0", combined);
            Assert.Contains("(+3 more)", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferencesAndExplicitChannel_UsesDiscoveredConfigAndPersistentPolicyOverlay()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var noRestoreValues = new List<bool>();
        var processOptions = new List<ProcessInvocationOptions>();
        XDocument? generatedProject = null;
        XDocument? generatedPolicyOverlay = null;
        string? nugetConfigDiscoveryDirectory = null;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "daily"
            }
            """);
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="anonymousAlias" value="{{channelSource}}" />
                <add key="private" value="{{channelSource}}" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, noRestore, options, _) =>
            {
                noRestoreValues.Add(noRestore);
                processOptions.Add(options);
                generatedProject = XDocument.Load(projectFilePath.FullName);
                generatedPolicyOverlay = XDocument.Load(Path.Combine(projectFilePath.DirectoryName!, "NuGet.Config"));
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            logger: NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };

        var layout = CreateBundleLayout(workspace);
        var nugetExecutionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, _, _, _) =>
                nugetConfigDiscoveryDirectory = GetArgumentValue(args, "--working-dir"),
            AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse(
                [ambientConfigPath],
                [
                    ("anonymousAlias", channelSource, true),
                    ("private", channelSource, true)
                ]))
        };
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(nugetExecutionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);
            var secondResult = await server.PrepareAsync(
                "13.4.0-pr.17141.gf142085f",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17141.gf142085f"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.True(firstResult.Success);
            Assert.True(secondResult.Success);
            Assert.Equal([false, true], noRestoreValues);
            Assert.All(processOptions, options =>
            {
                Assert.False(options.SuppressLogging);
                Assert.NotNull(options.EnvironmentVariableFilter);
                Assert.True(options.EnvironmentVariableFilter(CliPathHelper.NuGetPackagesEnvironmentVariable));
                Assert.False(options.EnvironmentVariableFilter("PATH"));
                Assert.Equal(
                    channelSource,
                    options.EnvironmentVariables?["RestoreAdditionalProjectSources"]);
                Assert.Equal(
                    generatedPolicyOverlay?
                        .Descendants("config")
                        .Elements("add")
                        .Single(element => element.Attribute("key")?.Value == "globalPackagesFolder")
                        .Attribute("value")?
                        .Value,
                    options.EnvironmentVariables?[CliPathHelper.NuGetPackagesEnvironmentVariable]);
            });
            Assert.NotNull(generatedProject);
            Assert.Equal(
                Path.Combine(workingDirectory, IntegrationClosureBuilder.IntegrationRestoreFolderName),
                nugetConfigDiscoveryDirectory);

            var ns = generatedProject!.Root!.GetDefaultNamespace();
            var restoreConfigFile = generatedProject.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
            var restoreSources = generatedProject.Descendants(ns + "RestoreAdditionalProjectSources").FirstOrDefault()?.Value;
            Assert.Null(restoreConfigFile);
            Assert.Equal(string.Empty, restoreSources);
            Assert.True(File.Exists(Path.Combine(workingDirectory, "integration-restore", "NuGet.Config")));

            Assert.NotNull(generatedPolicyOverlay);
            Assert.Empty(generatedPolicyOverlay.Descendants("packageSources"));
            Assert.Equal(["Aspire*"], GetPackagePatternsForKey(generatedPolicyOverlay, "anonymousAlias"));
            Assert.Equal(["Aspire*"], GetPackagePatternsForKey(generatedPolicyOverlay, "private"));
            Assert.NotNull(XDocument.Load(ambientConfigPath).Descendants("packageSourceCredentials").ElementAtOrDefault(0));

            // Aspire package versions remain in their original (non-pinned) form when no override
            // is in play; the exact-version pinning only fires when a single source is selected.
            var packageElements = generatedProject.Descendants("PackageReference").ToList();
            Assert.Contains(packageElements, e =>
                e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" &&
                e.Attribute("Version")?.Value == "13.4.0-pr.17141.gf142085f");
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithCredentialBearingAmbientSource_SuppressesRawProcessLogging()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string credentialBearingSource = "https://user:secret@packages.example.com/v3/index.json";
        ProcessInvocationOptions? buildOptions = null;
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, options, _) =>
            {
                buildOptions = options;
                WriteClosureInputs(
                    projectFilePath.Directory!,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MyIntegration.dll"] = "integration-v1"
                    },
                    ["MyIntegration"]);
                return 0;
            }
        };

        var layout = CreateBundleLayout(workspace);
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory
            {
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse(
                    sources: [("private", credentialBearingSource, true)]))
            }),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.True(result.Success);
            Assert.NotNull(buildOptions);
            Assert.True(buildOptions.SuppressLogging);
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferencesAndNoMappings_InvalidatesCacheWhenAmbientNuGetConfigChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, """
            <configuration>
              <config>
                <add key="signatureValidationMode" value="accept" />
              </config>
            </configuration>
            """);

        var (server, executionFactory) = CreatePackageReferenceServer(workspace);
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
        {
            var args = executionFactory.LastArguments!;
            return Task.FromResult(args is ["nuget", "settings", ..]
                ? (0, (string?)CreateNuGetSettingsResponse([ambientConfigPath]))
                : (0, (string?)null));
        };

        var workingDirectory = GetWorkingDirectory(server);
        try
        {
            var firstResult = await server.PrepareAsync(
                "13.4.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0")]);
            var firstManifestPath = server.IntegrationProbeManifestPath;

            await File.WriteAllTextAsync(ambientConfigPath, """
                <configuration>
                  <config>
                    <add key="signatureValidationMode" value="require" />
                  </config>
                </configuration>
                """);

            var secondResult = await server.PrepareAsync(
                "13.4.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0")]);
            var secondManifestPath = server.IntegrationProbeManifestPath;

            Assert.True(firstResult.Success);
            Assert.True(secondResult.Success);
            Assert.NotNull(firstManifestPath);
            Assert.NotNull(secondManifestPath);
            Assert.NotEqual(firstManifestPath, secondManifestPath);
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithPackageReferencesAndExplicitChannel_LoadsAmbientConfigAfterPolicyOverlay()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://packages.example.com/v3/index.json";
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="private" value="{{channelSource}}" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
              </packageSourceCredentials>
              <config>
                <add key="signatureValidationMode" value="require" />
              </config>
            </configuration>
            """);

        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };
        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        XDocument? restoreOverlay = null;
        string[]? restoreConfigPaths = null;
        string? policyOverlayPath = null;
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
        {
            var args = executionFactory.LastArguments!;
            if (args is ["nuget", "settings", ..])
            {
                return Task.FromResult((
                    0,
                    (string?)CreateNuGetSettingsResponse(
                        [ambientConfigPath],
                        [("private", channelSource, true)])));
            }

            if (args is ["nuget", "restore", ..])
            {
                restoreConfigPaths = GetArgumentValues(args, "--nuget-config");
                policyOverlayPath = restoreConfigPaths[0];
                restoreOverlay = XDocument.Load(policyOverlayPath);
            }

            return Task.FromResult((0, (string?)null));
        };

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0")],
                requestedChannel: "daily");

            Assert.True(result.Success);
            Assert.NotNull(policyOverlayPath);
            Assert.Equal([policyOverlayPath, ambientConfigPath], Assert.IsType<string[]>(restoreConfigPaths));
            Assert.NotNull(restoreOverlay);
            Assert.Empty(restoreOverlay.Descendants("packageSources"));
            Assert.Equal(["Aspire*"], GetPackagePatternsForKey(restoreOverlay, "private"));
            var ambientConfig = XDocument.Load(ambientConfigPath);
            Assert.NotNull(ambientConfig.Descendants("packageSourceCredentials").Single().Element("private"));
            Assert.Equal(
                "require",
                ambientConfig.Descendants("config").Elements("add").Single().Attribute("value")?.Value);
            Assert.False(File.Exists(policyOverlayPath));
            Assert.NotNull(server.IntegrationProbeManifestPath);
            Assert.True(Directory.Exists(Path.GetDirectoryName(server.IntegrationProbeManifestPath)));
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(GetWorkingDirectory(server));
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenNuGetConfigChanges_InvalidatesRestoreStamp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://pkgs.dev.azure.com/fake/v3/index.json";
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, """
            <configuration>
              <packageSources>
                <add key="private" value="https://packages.example.com/v1/index.json" />
              </packageSources>
            </configuration>
            """);

        var noRestoreValues = new List<bool>();
        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, noRestore, _, _) =>
            {
                noRestoreValues.Add(noRestore);
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };
        var layout = CreateBundleLayout(workspace);
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory
            {
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse([ambientConfigPath]))
            }),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);
        var integrations = new[]
        {
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        try
        {
            var firstResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");
            var secondResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");
            await File.WriteAllTextAsync(ambientConfigPath, """
                <configuration>
                  <packageSources>
                    <add key="private" value="https://packages.example.com/v2/index.json" />
                  </packageSources>
                </configuration>
                """);
            var thirdResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");

            Assert.True(firstResult.Success);
            Assert.True(secondResult.Success);
            Assert.True(thirdResult.Success);
            Assert.Equal([false, true, false], noRestoreValues);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_AmbientOnlyRestoreInvalidatesStalePolicyOverlayStamp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var ambientConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(ambientConfigPath, """
            <configuration>
              <packageSources>
                <add key="private" value="https://packages.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        var noRestoreValues = new List<bool>();
        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, noRestore, _, _) =>
            {
                noRestoreValues.Add(noRestore);
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };
        var dailyChannel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", "https://pkgs.dev.azure.com/fake/v3/index.json")],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([dailyChannel])
        };
        var layout = CreateBundleLayout(workspace);
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory
            {
                AttemptCallback = (_, _) => (0, CreateNuGetSettingsResponse([ambientConfigPath]))
            }),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);
        var integrations = new[]
        {
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        };

        try
        {
            var firstResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");
            var ambientResult = await server.PrepareAsync("13.4.0", integrations);
            var finalResult = await server.PrepareAsync("13.4.0", integrations, requestedChannel: "daily");

            Assert.True(firstResult.Success);
            Assert.True(ambientResult.Success);
            Assert.True(finalResult.Success);
            Assert.Equal([false, false, false], noRestoreValues);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithCredentialBearingSource_RejectsProjectRestoreWithoutPersistingCredentials()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string channelSource = "https://feed.blob.core.windows.net/packages/index.json?sig=secret-sig";
        var buildCalled = false;
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) =>
            {
                buildCalled = true;
                return 0;
            }
        };
        var channel = PackageChannel.CreateExplicitChannel(
            name: "daily",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", channelSource)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(),
            NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
        };
        var server = CreatePrebuiltAppHostServer(
            workspace,
            dotNetCliRunner: dotNetCliRunner,
            packagingService: packagingService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ]);

            Assert.False(result.Success);
            Assert.False(buildCalled);
            Assert.NotNull(result.Output);
            var output = string.Join(Environment.NewLine, result.Output.GetLines().Select(static line => line.Line));
            Assert.Contains("Configure credentials through NuGet instead.", output);
            Assert.DoesNotContain("secret-sig", output);
            Assert.False(File.Exists(Path.Combine(
                workingDirectory,
                IntegrationClosureBuilder.IntegrationRestoreFolderName,
                "NuGet.Config")));
        }
        finally
        {
            server.Dispose();
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_RestoreFailure_WithAutoDiscoveredLocalSource_FooterShowsEffectiveSource()
    {
        // When the caller passes no --source but a local hive is auto-discovered, the failure
        // footer must identify the effective source that participated in restore.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var localHive = workspace.CreateDirectory("local-aspire-hive").FullName;

        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(aspireConfigPath, """
            {
                "channel": "pr-12345"
            }
            """);

        var prChannel = PackageChannel.CreateExplicitChannel(
            name: "pr-12345",
            quality: PackageChannelQuality.Both,
            mappings: [new PackageMapping("Aspire*", localHive)],
            nuGetPackageCache: new FakeNuGetPackageCache(),
            features: new TestFeatures(), NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([prChannel])
        };

        var (server, executionFactory) = CreatePackageReferenceServer(workspace, packagingService);
        executionFactory.DefaultExitCode = 1;

        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.12345.gabcdef00",
                [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.4.0-pr.12345.gabcdef00")]);

            Assert.False(result.Success);
            Assert.NotNull(result.Output);

            var combined = string.Join('\n', result.Output!.GetLines().Select(static line => line.Line));
            Assert.Contains($"--source: {localHive}", combined);
            Assert.Contains("channel:  pr-12345", combined);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Theory]
    [InlineData("https://user:p@ss@host/path", "<unparseable http source>")]
    [InlineData("https://user:p#word@host/", "<unparseable http source>")]
    [InlineData("http://foo bar/path", "<unparseable http source>")]
    [InlineData("HTTPS://user:p@ss@host/path", "<unparseable http source>")]
    [InlineData("/tmp/aspire/some path with [brackets]", "/tmp/aspire/some path with [brackets]")]
    public void RedactSourceForDisplay_FailsClosedForMalformedHttpButPassesThroughLocalPaths(string input, string expected)
    {
        // HTTP-looking inputs that Uri.TryCreate cannot parse (e.g. unescaped @ or # in user-info
        // or embedded whitespace) must return the sentinel rather than risk exposing credentials.
        // Plain non-HTTP inputs pass through because they represent local source paths.
        Assert.Equal(expected, PrebuiltAppHostServer.RedactSourceForDisplay(input));
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferencesAndPackageSourceOverride_UsesPolicyOverlayAndEnvironmentDefault()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        XDocument? generatedProject = null;
        XDocument? restoreOverlay = null;
        ProcessInvocationOptions? buildOptions = null;

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, options, _) =>
            {
                generatedProject = XDocument.Load(projectFilePath.FullName);
                restoreOverlay = XDocument.Load(Path.Combine(projectFilePath.DirectoryName!, "NuGet.Config"));
                buildOptions = options;
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, ["MyIntegration"]);
                return 0;
            }
        };
        var server = CreatePrebuiltAppHostServer(workspace, dotNetCliRunner: dotNetCliRunner);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.4.0-pr.17166.ga49d604d",
                [
                    IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.4.0-pr.17166.ga49d604d"),
                    IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Redis", "1.0.0"),
                    IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
                ],
                packageSourceOverride: packageSourceOverride);

            Assert.True(result.Success);
            Assert.NotNull(generatedProject);

            var ns = generatedProject.Root!.GetDefaultNamespace();
            var restoreConfigFile = generatedProject.Descendants(ns + "RestoreConfigFile").FirstOrDefault()?.Value;
            Assert.Null(restoreConfigFile);
            Assert.Contains(
                packageSourceOverride,
                generatedProject.Descendants(ns + "RestoreAdditionalProjectSources").Single().Value);
            Assert.NotNull(restoreOverlay);
            Assert.Equal(2, restoreOverlay.Descendants("packageSources").Elements("add").Count());
            Assert.Equal(["Aspire*"], GetPackagePatternsForSource(restoreOverlay, packageSourceOverride));
            Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(restoreOverlay, NuGetOrgSource));
            var restoreEnvironmentSources = Assert.IsType<string>(
                buildOptions?.EnvironmentVariables?["RestoreAdditionalProjectSources"]);
            Assert.Equal([packageSourceOverride, NuGetOrgSource], restoreEnvironmentSources.Split(';'));

            var packageElements = generatedProject.Descendants("PackageReference").ToList();
            Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "[13.4.0-pr.17166.ga49d604d]");
            Assert.Contains(packageElements, e => e.Attribute("Include")?.Value == "CommunityToolkit.Aspire.Hosting.Redis" && e.Attribute("Version")?.Value == "1.0.0");
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithStagingPinnedProjectOutsideLaunchDirectory_UsesStagingSourcesAndPolicyOverlay()
    {
        const string stagingFeed = "https://example.com/staging/v3/index.json";

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("elsewhere");
        var config = AspireConfigFile.LoadOrCreate(projectDirectory.FullName);
        config.Channel = PackageChannelNames.Staging;
        config.Save(projectDirectory.FullName);

        var layout = CreateBundleLayout(workspace);
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            workspace.WorkspaceRoot,
            identityChannel: PackageChannelNames.Stable);

        string[]? restoreInvocation = null;
        IDictionary<string, string>? restoreEnvironment = null;
        string? policyOverlayContent = null;
        var inheritedPackagesFolder = Path.Combine(workspace.WorkspaceRoot.FullName, "inherited-packages");
        var executionFactory = new TestProcessExecutionFactory
        {
            AssertionCallback = (args, environment, _, _) =>
            {
                if (args.Length > 1 &&
                    args[0] == "nuget" &&
                    args[1] == "restore")
                {
                    restoreInvocation = args.ToArray();
                    restoreEnvironment = environment;
                    policyOverlayContent = File.ReadAllText(GetArgumentValue(args, "--nuget-config"));
                }
            }
        };
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
            Task.FromResult(executionFactory.LastArguments is ["nuget", "settings", ..]
                ? (0, (string?)CreateNuGetSettingsResponse())
                : (executionFactory.DefaultExitCode, (string?)null));

        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(new Dictionary<string, string?>
            {
                [CliPathHelper.NuGetPackagesEnvironmentVariable] = inheritedPackagesFolder
            }),
            NullLogger<BundleNuGetService>.Instance);

        var stagingChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Staging,
            PackageChannelQuality.Both,
            [
                new PackageMapping("Aspire*", stagingFeed),
                new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")
            ],
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            NullLogger.Instance,
            configureGlobalPackagesFolder: true);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([stagingChannel])
        };

        var server = CreatePrebuiltAppHostServer(
            workspace,
            appPath: projectDirectory.FullName,
            layout: layout,
            packagingService: packagingService,
            executionContext: executionContext,
            nugetService: nugetService);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0")]);

            Assert.True(result.Success);
            Assert.Equal(PackageChannelNames.Staging, result.ChannelName);

            Assert.NotNull(restoreInvocation);
            Assert.Contains(stagingFeed, restoreInvocation!);
            Assert.Contains(
                restoreInvocation!,
                argument => string.Equals(
                    CliPathHelper.StripMacOSFirmlinkPrefix(argument),
                    CliPathHelper.StripMacOSFirmlinkPrefix(workingDirectory),
                    StringComparison.Ordinal));
            Assert.NotNull(restoreEnvironment);
            Assert.NotNull(policyOverlayContent);
            Assert.Contains(stagingFeed, policyOverlayContent!);
            Assert.Contains("Aspire*", policyOverlayContent!);
            var globalPackagesFolder = XDocument.Parse(policyOverlayContent!)
                .Descendants("config")
                .Elements("add")
                .Single(element => element.Attribute("key")?.Value == "globalPackagesFolder")
                .Attribute("value")?.Value;
            Assert.NotEqual(inheritedPackagesFolder, globalPackagesFolder);
            Assert.Equal(globalPackagesFolder, restoreEnvironment![CliPathHelper.NuGetPackagesEnvironmentVariable]);
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithOnlyProjectReferences_SetsOnlyProjectLayout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MyIntegration.dll"] = "integration-v1"
        };

        var layout = CreateBundleLayout(workspace);
        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], layout: layout);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync(
                "13.2.0",
                [IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")]);

            Assert.True(
                result.Success,
                result.Output is null
                    ? null
                    : string.Join(Environment.NewLine, result.Output.GetLines().Select(static line => line.Line)));
            Assert.Null(server.IntegrationProbeManifestPath);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            Assert.True(File.Exists(Path.Combine(layoutPath, "libs", "MyIntegration.dll")));

            var startInfo = server.CreateStartInfo(123);
            Assert.Equal(Path.Combine(layoutPath, "libs"), startInfo.Environment[KnownConfigNames.IntegrationLibsPath]);
            Assert.False(startInfo.Environment.ContainsKey(KnownConfigNames.IntegrationProbeManifestPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_ReusesProjectLayoutWhenClosureIsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstLayoutPath, server.SelectedProjectLayoutPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_WritesPackageProbeManifestAndCopiesOnlyProjectOutputs()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(result.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedLibs = Directory.GetFiles(Path.Combine(layoutPath, "libs"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(layoutPath, "libs"), path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["MyIntegration.dll"], copiedLibs);

            var probeManifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            await using var probeManifestStream = File.OpenRead(probeManifestPath);
            using var probeManifest = await JsonDocument.ParseAsync(probeManifestStream);

            var managedAssemblies = probeManifest.RootElement.GetProperty("managedAssemblies").EnumerateArray().ToList();
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Aspire.Hosting.Redis" &&
                    assembly.GetProperty("packageId").GetString() == "Aspire.Hosting.Redis" &&
                    assembly.GetProperty("packageVersion").GetString() == "13.2.0" &&
                    assembly.GetProperty("path").GetString() == Path.Combine(workingDirectory, "integration-restore", "closure-sources", "Aspire.Hosting.Redis.dll"));
            Assert.Equal(0, probeManifest.RootElement.GetProperty("nativeLibraries").GetArrayLength());
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_WritesPackageResourcesAndNativeAssetsToProbeManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["fr/Aspire.Hosting.Redis.resources.dll"] = "redis-fr",
            ["runtimes/test-rid/native/testnative.so"] = "native",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = new Dictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/Aspire.Hosting.Redis.dll", "runtime"),
            ["fr/Aspire.Hosting.Redis.resources.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/fr/Aspire.Hosting.Redis.resources.dll", "resources"),
            ["runtimes/test-rid/native/testnative.so"] = ("Aspire.Hosting.Redis", "13.2.0", "runtimes/test-rid/native/testnative.so", "native")
        };

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var result = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(result.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedLibs = Directory.GetFiles(Path.Combine(layoutPath, "libs"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(layoutPath, "libs"), path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["MyIntegration.dll"], copiedLibs);

            var probeManifestPath = Assert.IsType<string>(server.IntegrationProbeManifestPath);
            await using var probeManifestStream = File.OpenRead(probeManifestPath);
            using var probeManifest = await JsonDocument.ParseAsync(probeManifestStream);

            var managedAssemblies = probeManifest.RootElement.GetProperty("managedAssemblies").EnumerateArray().ToList();
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Aspire.Hosting.Redis" &&
                    !assembly.TryGetProperty("culture", out _));
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Aspire.Hosting.Redis.resources" &&
                    assembly.GetProperty("culture").GetString() == "fr");

            var nativeLibraries = probeManifest.RootElement.GetProperty("nativeLibraries").EnumerateArray().ToList();
            Assert.Contains(
                nativeLibraries,
                nativeLibrary => nativeLibrary.GetProperty("fileName").GetString() == "testnative.so");
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_CreatesNewProjectLayoutWhenClosureChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);

            closureFiles["MyIntegration.dll"] = "integration-v2";

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);

            var secondLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            Assert.NotEqual(firstLayoutPath, secondLayoutPath);
            Assert.True(Directory.Exists(firstLayoutPath));
            Assert.True(Directory.Exists(secondLayoutPath));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_RecreatesProjectLayoutWhenCachedLayoutIsCorrupt()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var layoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var copiedFilePath = Path.Combine(layoutPath, "libs", "MyIntegration.dll");
            await File.WriteAllTextAsync(copiedFilePath, "corrupt");

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);

            Assert.Equal(layoutPath, server.SelectedProjectLayoutPath);
            Assert.Equal("integration-v1", await File.ReadAllTextAsync(copiedFilePath));
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_DoesNotTouchLockedPreviousProjectLayout()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var lockedFilePath = Path.Combine(firstLayoutPath, "libs", "MyIntegration.dll");

            using (var lockedFile = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                closureFiles["MyIntegration.dll"] = "integration-v2";

                var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
                Assert.True(secondResult.Success);

                var secondLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
                Assert.NotEqual(firstLayoutPath, secondLayoutPath);
                Assert.True(File.Exists(lockedFilePath));
                Assert.True(File.Exists(Path.Combine(secondLayoutPath, "libs", "MyIntegration.dll")));
            }
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    [Fact]
    public void ClosureManifest_WithPackageBackedEntries_ChangesFingerprintWhenPackageSourcePathChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var firstPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-a");
        var secondPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-b");
        var firstSourcePath = Path.Combine(firstPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var secondSourcePath = Path.Combine(secondPackageRoot.FullName, "Aspire.Hosting.Redis.dll");

        File.WriteAllText(firstSourcePath, "redis");
        File.WriteAllText(secondSourcePath, "redis");

        var firstManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                firstSourcePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime")
        ],
        "{}",
        CancellationToken.None);

        var secondManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                secondSourcePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime")
        ],
        "{}",
        CancellationToken.None);

        Assert.NotEqual(firstManifest.ManifestFingerprint, secondManifest.ManifestFingerprint);
    }

    [Fact]
    public void ClosureManifest_ProjectLayoutManifestIgnoresPackageBackedEntries()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var firstPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-a");
        var secondPackageRoot = workspace.WorkspaceRoot.CreateSubdirectory("packages-b");
        var projectRoot = workspace.WorkspaceRoot.CreateSubdirectory("project");
        var firstPackagePath = Path.Combine(firstPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var secondPackagePath = Path.Combine(secondPackageRoot.FullName, "Aspire.Hosting.Redis.dll");
        var projectPath = Path.Combine(projectRoot.FullName, "MyIntegration.dll");

        File.WriteAllText(firstPackagePath, "redis");
        File.WriteAllText(secondPackagePath, "redis");
        File.WriteAllText(projectPath, "integration");

        var firstManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                firstPackagePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime"),
            new AppHostServerClosureSource(projectPath, "MyIntegration.dll")
        ],
        "{}",
        CancellationToken.None);

        var secondManifest = AppHostServerClosureManifest.Create(
        [
            new AppHostServerClosureSource(
                secondPackagePath,
                "Aspire.Hosting.Redis.dll",
                "Aspire.Hosting.Redis",
                "13.2.0",
                "lib/net10.0/Aspire.Hosting.Redis.dll",
                "sha512-redis",
                "runtime"),
            new AppHostServerClosureSource(projectPath, "MyIntegration.dll")
        ],
        "{}",
        CancellationToken.None);

        Assert.NotEqual(firstManifest.ManifestFingerprint, secondManifest.ManifestFingerprint);
        Assert.Equal(firstManifest.ProjectLayoutFingerprint, secondManifest.ProjectLayoutFingerprint);
        Assert.Equal(firstManifest.GetProjectLayoutManifestLines(), secondManifest.GetProjectLayoutManifestLines());
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public async Task ReadClosureManifestAsync_PreservesTrailingWhitespaceInPaths()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var restoreDirectory = workspace.CreateDirectory("restore");
        var sourcePath = Path.Combine(restoreDirectory.FullName, "MyIntegration.dll ");
        var relativePath = "MyIntegration.dll ";
        File.WriteAllText(sourcePath, "integration");
        File.WriteAllLines(
            Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureSourcesFileName),
            [sourcePath]);
        File.WriteAllLines(
            Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureMetadataFileName),
            ["|||"]);
        File.WriteAllLines(
            Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureTargetsFileName),
            [relativePath]);
        var intermediateOutputPath = Path.Combine(restoreDirectory.FullName, "obj");
        WriteProjectAssetsFile(intermediateOutputPath, packageMetadata: null);

        var manifest = await IntegrationClosureBuilder.ReadClosureManifestAsync(
            restoreDirectory.FullName,
            Path.Combine(intermediateOutputPath, IntegrationClosureBuilder.ProjectAssetsFileName),
            "{}",
            ClosureFileMissingBehavior.Throw,
            logger: null,
            CancellationToken.None);

        var entry = Assert.Single(Assert.IsType<AppHostServerClosureManifest>(manifest).Entries);
        Assert.Equal(sourcePath, entry.SourcePath);
        Assert.Equal(relativePath, entry.RelativePath);
    }

    [Fact]
    public async Task PrepareAsync_WithProjectReferences_ReusesProjectLayoutWhenOnlyPackageTimestampChanges()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var closureFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = "redis-v1",
            ["MyIntegration.dll"] = "integration-v1"
        };
        var packageMetadata = CreatePackageMetadata();

        var server = CreateProjectReferenceServer(workspace, closureFiles, ["MyIntegration"], packageMetadata);
        var workingDirectory = GetWorkingDirectory(server);

        try
        {
            var firstResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(firstResult.Success);

            var firstLayoutPath = Assert.IsType<string>(server.SelectedProjectLayoutPath);
            var packageSourcePath = Path.Combine(workingDirectory, "integration-restore", "closure-sources", "Aspire.Hosting.Redis.dll");
            File.SetLastWriteTimeUtc(packageSourcePath, File.GetLastWriteTimeUtc(packageSourcePath).AddMinutes(5));

            var secondResult = await server.PrepareAsync("13.2.0", CreateProjectReferenceIntegrations());
            Assert.True(secondResult.Success);
            Assert.Equal(firstLayoutPath, server.SelectedProjectLayoutPath);
            Assert.Single(Directory.GetDirectories(Path.Combine(workingDirectory, "project-layouts", "items")));
        }
        finally
        {
            DeleteWorkingDirectory(workingDirectory);
        }
    }

    private static IReadOnlyList<IntegrationReference> CreateProjectReferenceIntegrations()
    {
        return
        [
            IntegrationReference.FromPackage("Aspire.Hosting", "13.2.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.2.0"),
            IntegrationReference.FromProject("MyIntegration", "/path/to/MyIntegration.csproj")
        ];
    }

    private static PrebuiltAppHostServer CreateProjectReferenceServer(
        TemporaryWorkspace workspace,
        IReadOnlyDictionary<string, string> closureFiles,
        IReadOnlyList<string> projectReferenceAssemblyNames,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata = null,
        LayoutConfiguration? layout = null)
    {
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (projectFilePath, _, _, _) =>
            {
                WriteClosureInputs(projectFilePath.Directory!, closureFiles, projectReferenceAssemblyNames, packageMetadata);
                return 0;
            }
        };

        return CreatePrebuiltAppHostServer(workspace, layout: layout, dotNetCliRunner: dotNetCliRunner);
    }

    private static (PrebuiltAppHostServer Server, TestProcessExecutionFactory ExecutionFactory) CreatePackageReferenceServer(TemporaryWorkspace workspace)
    {
        return CreatePackageReferenceServer(workspace, MockPackagingServiceFactory.Create());
    }

    private static (PrebuiltAppHostServer Server, TestProcessExecutionFactory ExecutionFactory) CreatePackageReferenceServer(
        TemporaryWorkspace workspace,
        IPackagingService packagingService)
    {
        var layout = CreateBundleLayout(workspace);
        var executionFactory = new TestProcessExecutionFactory();
        executionFactory.AsyncAttemptCallback = (_, _, _) =>
            Task.FromResult(executionFactory.LastArguments is ["nuget", "settings", ..]
                ? (0, (string?)CreateNuGetSettingsResponse())
                : (executionFactory.DefaultExitCode, (string?)null));
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(executionFactory),
            new TestFeatures(),
            new TestEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleNuGetService>.Instance);

        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            packagingService: packagingService,
            nugetService: nugetService);

        return (server, executionFactory);
    }

    private static LayoutConfiguration CreateBundleLayout(TemporaryWorkspace workspace)
    {
        var layoutRoot = workspace.CreateDirectory("layout");
        var managedDirectory = layoutRoot.CreateSubdirectory(BundleDiscovery.ManagedDirectoryName);
        File.WriteAllText(
            Path.Combine(
                managedDirectory.FullName,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            string.Empty);

        return new LayoutConfiguration { LayoutPath = layoutRoot.FullName };
    }

    private static void WriteClosureInputs(
        DirectoryInfo restoreDirectory,
        IReadOnlyDictionary<string, string> closureFiles,
        IReadOnlyList<string> projectReferenceAssemblyNames,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata = null)
    {
        var sourceRoot = restoreDirectory.CreateSubdirectory("closure-sources");
        var metadataLines = new List<string>();
        var sourcePaths = new List<string>();
        var targetPaths = new List<string>();

        foreach (var (relativePath, content) in closureFiles.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            var sourcePath = Path.Combine(sourceRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var sourcePathDirectory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(sourcePathDirectory))
            {
                Directory.CreateDirectory(sourcePathDirectory);
            }

            if (!File.Exists(sourcePath) || File.ReadAllText(sourcePath) != content)
            {
                File.WriteAllText(sourcePath, content);
            }

            sourcePaths.Add(sourcePath);
            targetPaths.Add(relativePath.Replace('/', Path.DirectorySeparatorChar));
            metadataLines.Add(packageMetadata is not null && packageMetadata.TryGetValue(relativePath, out var package)
                ? $"{package.NuGetPackageId}|{package.NuGetPackageVersion}|{package.PathInPackage}|{package.AssetType}"
                : "|||");
        }

        WriteProjectAssetsFile(GetIntermediateOutputPath(restoreDirectory), packageMetadata);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureMetadataFileName), metadataLines);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureSourcesFileName), sourcePaths);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ClosureTargetsFileName), targetPaths);
        File.WriteAllLines(Path.Combine(restoreDirectory.FullName, IntegrationClosureBuilder.ProjectRefAssemblyNamesFileName), projectReferenceAssemblyNames);
    }

    private static IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)> CreatePackageMetadata()
    {
        return new Dictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>(StringComparer.Ordinal)
        {
            ["Aspire.Hosting.Redis.dll"] = ("Aspire.Hosting.Redis", "13.2.0", "lib/net10.0/Aspire.Hosting.Redis.dll", "runtime")
        };
    }

    private static string GetIntermediateOutputPath(DirectoryInfo restoreDirectory)
    {
        var document = XDocument.Load(Path.Combine(restoreDirectory.FullName, "Directory.Build.props"));
        return document.Descendants("BaseIntermediateOutputPath").Single().Value;
    }

    private static void WriteProjectAssetsFile(
        string intermediateOutputPath,
        IReadOnlyDictionary<string, (string NuGetPackageId, string NuGetPackageVersion, string PathInPackage, string AssetType)>? packageMetadata)
    {
        var objDirectory = Directory.CreateDirectory(intermediateOutputPath);
        var libraries = packageMetadata is null
            ? string.Empty
            : string.Join(
                ",\n",
                packageMetadata.Values
                    .GroupBy(static package => (package.NuGetPackageId, package.NuGetPackageVersion))
                    .Select(static group => group.First())
                    .OrderBy(static package => package.NuGetPackageId, StringComparer.Ordinal)
                    .ThenBy(static package => package.NuGetPackageVersion, StringComparer.Ordinal)
                    .Select(static package => $$"""
                        "{{package.NuGetPackageId}}/{{package.NuGetPackageVersion}}": {
                          "sha512": "sha512-{{package.NuGetPackageId}}-{{package.NuGetPackageVersion}}",
                          "type": "package",
                          "path": "{{package.NuGetPackageId.ToLowerInvariant()}}/{{package.NuGetPackageVersion}}",
                          "files": [
                            "{{package.PathInPackage}}"
                          ]
                        }
                        """));

        var projectAssetsContent = $$"""
            {
              "libraries": {
            {{libraries}}
              }
            }
            """;
        File.WriteAllText(Path.Combine(objDirectory.FullName, "project.assets.json"), projectAssetsContent);
    }

    private static string GetWorkingDirectory(PrebuiltAppHostServer server)
    {
        return Assert.IsType<string>(
            typeof(PrebuiltAppHostServer)
                .GetField("_workingDirectory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(server));
    }

    private static string GetArgumentValue(IReadOnlyList<string> arguments, string optionName)
    {
        var optionIndex = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], optionName, StringComparison.Ordinal))
            {
                optionIndex = i;
                break;
            }
        }

        Assert.True(optionIndex >= 0 && optionIndex < arguments.Count - 1, $"Option '{optionName}' was not found.");
        return arguments[optionIndex + 1];
    }

    [Fact]
    public void CreateStartInfo_SetsCliLogFilePathEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var layout = CreateBundleLayout(workspace);
        var executionContext = TestExecutionContextFactory.CreateTestContext();
        var nugetService = new BundleNuGetService(
            new FixedLayoutDiscovery(layout),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);

        var server = CreatePrebuiltAppHostServer(
            workspace,
            layout: layout,
            executionContext: executionContext,
            nugetService: nugetService);

        var startInfo = server.CreateStartInfo(123);

        Assert.Equal(executionContext.LogFilePath, startInfo.Environment[KnownConfigNames.CliLogFilePath]);
    }

    private static string[] GetSourceArguments(IReadOnlyList<string> args)
    {
        var sources = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--source")
            {
                sources.Add(args[i + 1]);
            }
        }

        return [.. sources];
    }

    private static string CreateNuGetSettingsResponse(
        IEnumerable<string>? configPaths = null,
        IEnumerable<(string Name, string Source, bool IsEnabled)>? sources = null)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            ConfigPaths = configPaths?.ToArray() ?? [],
            Sources = sources?
                .Select(static source => new
                {
                    source.Name,
                    source.Source,
                    source.IsEnabled
                })
                .ToArray() ?? []
        });

    private static string[] GetArgumentValues(IReadOnlyList<string> args, string argumentName)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == argumentName)
            {
                values.Add(args[i + 1]);
            }
        }

        return [.. values];
    }

    private static string[] GetPackagePatternsForSource(XDocument doc, string source)
    {
        var sourceKey = doc.Descendants("packageSources")
            .Elements("add")
            .SingleOrDefault(element => PackageSourceIdentity.Comparer.Equals(
                element.Attribute("value")?.Value,
                source))
            ?.Attribute("key")
            ?.Value;
        sourceKey ??= doc.Descendants("packageSource")
            .Select(static element => element.Attribute("key")?.Value)
            .FirstOrDefault(key => key is not null && PackageSourceIdentity.Comparer.Equals(key, source));
        if (sourceKey is null)
        {
            return [];
        }

        return GetPackagePatternsForKey(doc, sourceKey);
    }

    private static string[] GetPackagePatternsForKey(XDocument doc, string sourceKey)
    {
        return [.. doc.Descendants("packageSource")
            .Where(element => string.Equals(
                element.Attribute("key")?.Value,
                sourceKey,
                StringComparison.OrdinalIgnoreCase))
            .Elements("package")
            .Select(static element => element.Attribute("pattern")?.Value)
            .OfType<string>()];
    }

    private static void DeleteWorkingDirectory(string workingDirectory)
    {
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class FixedLayoutDiscovery(LayoutConfiguration layout) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => layout;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) => layout.GetComponentPath(component);

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }

}
