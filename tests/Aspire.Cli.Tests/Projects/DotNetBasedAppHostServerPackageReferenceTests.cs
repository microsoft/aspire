// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

/// <summary>
/// The generated capability scanner writes its own <c>Directory.Packages.props</c> that turns central
/// package management on so transitive dependencies pick up the repo's pinned versions. Central package
/// management rejects an inline <c>Version</c> attribute on a <c>PackageReference</c> with NU1008, which
/// made the scanner fail to build for any integration that lives outside the repo — the exact case
/// <c>aspire sdk export</c> hits when it is pointed at a third-party package such as a Community Toolkit
/// integration.
/// </summary>
public class DotNetBasedAppHostServerPackageReferenceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CreateProjectFiles_PinsOutOfRepoIntegrationsWithVersionOverride()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var projectModelPath = Path.Combine(appPath, ".aspire_server");

        var project = CreateProject(appPath, projectModelPath);

        // There is no src/CommunityToolkit.Aspire.Hosting.ActiveMQ under the fake repo root, so this
        // integration takes the package path rather than the project-reference path.
        await project.CreateProjectFilesAsync(
            [IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.ActiveMQ", "13.4.0")]);

        var packagesProps = XDocument.Load(Path.Combine(projectModelPath, "Directory.Packages.props"));
        Assert.Equal(
            "true",
            packagesProps.Descendants("ManagePackageVersionsCentrally").Single().Value);

        var reference = XDocument.Load(Path.Combine(projectModelPath, "AppHostServer.csproj"))
            .Descendants("PackageReference")
            .Single(element => element.Attribute("Include")?.Value == "CommunityToolkit.Aspire.Hosting.ActiveMQ");

        Assert.Equal("13.4.0", reference.Attribute("VersionOverride")?.Value);
        Assert.Null(reference.Attribute("Version"));
    }

    /// <summary>
    /// Asserting on the generated XML alone cannot catch a change in how NuGet treats
    /// <c>VersionOverride</c> under central package management. This restores the generated project
    /// for real against an offline folder feed, with a central package list that deliberately has no
    /// entry for the out-of-repo integration, so a regression surfaces as NU1008 or NU1010.
    /// </summary>
    [Fact]
    public async Task CreateProjectFiles_ProducesAProjectThatRestores()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var projectModelPath = Path.Combine(appPath, ".aspire_server");
        var feedPath = Path.Combine(appPath, "feed");
        Directory.CreateDirectory(feedPath);

        const string IntegrationPackage = "CommunityToolkit.Aspire.Hosting.ActiveMQ";

        // The template always references these two without a version, so they have to resolve
        // through the central list the way they do in the real repo.
        OfflineNuGetFeed.CreateStubPackage(feedPath, "StreamJsonRpc", "1.0.0");
        OfflineNuGetFeed.CreateStubPackage(feedPath, "Google.Protobuf", "1.0.0");
        OfflineNuGetFeed.CreateStubPackage(feedPath, IntegrationPackage, "13.4.0");

        // Mirrors the real repo: a central list that pins first-party dependencies but knows nothing
        // about a Community Toolkit integration.
        await File.WriteAllTextAsync(Path.Combine(appPath, "Directory.Packages.props"), """
            <Project>
              <ItemGroup>
                <PackageVersion Include="StreamJsonRpc" Version="1.0.0" />
                <PackageVersion Include="Google.Protobuf" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var project = CreateProject(appPath, projectModelPath);

        await project.CreateProjectFilesAsync(
            [IntegrationReference.FromPackage(IntegrationPackage, "13.4.0")]);

        var (exitCode, output) = await OfflineNuGetFeed.RestoreAsync(
            Path.Combine(projectModelPath, "AppHostServer.csproj"),
            feedPath);

        outputHelper.WriteLine(output);

        // NU1008 is the inline Version attribute this fix replaced; NU1010 is the failure mode that
        // would appear if VersionOverride stopped satisfying the central list requirement.
        Assert.DoesNotContain("NU1008", output, StringComparison.Ordinal);
        Assert.DoesNotContain("NU1010", output, StringComparison.Ordinal);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task PrepareWritesPackageProbeManifestForOutOfRepoIntegrations()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var projectModelPath = Path.Combine(appPath, ".aspire_server");
        var packageDirectory = Path.Combine(appPath, "packages");
        Directory.CreateDirectory(packageDirectory);

        var primaryAssemblyPath = Path.Combine(packageDirectory, "Contoso.Hosting.dll");
        var secondaryAssemblyPath = Path.Combine(packageDirectory, "Contoso.Hosting.Extras.dll");
        var satelliteAssemblyPath = Path.Combine(packageDirectory, "fr", "Contoso.Hosting.resources.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(satelliteAssemblyPath)!);
        await File.WriteAllTextAsync(primaryAssemblyPath, string.Empty);
        await File.WriteAllTextAsync(secondaryAssemblyPath, string.Empty);
        await File.WriteAllTextAsync(satelliteAssemblyPath, string.Empty);

        var runner = new TestDotNetCliRunner
        {
            BuildAsyncCallback = (_, _, _, _) =>
            {
                File.WriteAllLines(
                    Path.Combine(projectModelPath, "package-probe-sources.txt"),
                    [primaryAssemblyPath, secondaryAssemblyPath, satelliteAssemblyPath]);
                File.WriteAllLines(
                    Path.Combine(projectModelPath, "package-probe-metadata.txt"),
                    [
                        "Contoso.Aspire.MetaPackage|1.2.3|runtime",
                        "Contoso.Aspire.MetaPackage|1.2.3|runtime",
                        "Contoso.Aspire.MetaPackage|1.2.3|resources"
                    ]);
                File.WriteAllLines(
                    Path.Combine(projectModelPath, "package-probe-targets.txt"),
                    [
                        "Contoso.Hosting.dll",
                        "Contoso.Hosting.Extras.dll",
                        "fr/Contoso.Hosting.resources.dll"
                    ]);

                return 0;
            }
        };
        var processExecutionFactory = new TestProcessExecutionFactory();
        var project = CreateProject(appPath, projectModelPath, runner, processExecutionFactory);

        var result = await project.PrepareAsync(
            "13.5.0",
            [IntegrationReference.FromExactPackage("Contoso.Aspire.MetaPackage", "1.2.3")]);

        Assert.True(result.Success);

        var manifestPath = Path.Combine(projectModelPath, IntegrationPackageProbeManifest.FileName);
        Assert.True(File.Exists(manifestPath));

        var manifest = IntegrationPackageProbeManifest.Load(manifestPath);
        Assert.True(manifest.TryGetRuntimeAssemblyNamesForPackage("contoso.aspire.metapackage", out var canonicalPackageId, out var assemblyNames));
        Assert.Equal("Contoso.Aspire.MetaPackage", canonicalPackageId);
        Assert.Equal(["Contoso.Hosting", "Contoso.Hosting.Extras"], assemblyNames);
        Assert.Contains(
            manifest.ManagedAssemblies,
            assembly => assembly.Name == "Contoso.Hosting.resources" &&
                assembly.Culture == "fr" &&
                assembly.PackageId == "Contoso.Aspire.MetaPackage" &&
                assembly.PackageVersion == "1.2.3");

        var runResult = await project.RunAsync(
            Environment.ProcessId,
            environmentVariables: null,
            additionalArgs: null,
            debug: false,
            runControl: null);
        await using var execution = runResult.Execution;

        Assert.Equal(manifestPath, processExecutionFactory.LastEnvironmentVariables?[KnownConfigNames.IntegrationProbeManifestPath]);
    }

    /// <summary>
    /// <c>aspire sdk export</c> publishes documentation keyed on the requested version, so the
    /// restore has to fail when that version is unavailable rather than resolve to a later one.
    /// </summary>
    [Fact]
    public async Task CreateProjectFiles_PinsExactIntegrationsToASingleVersionRange()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var projectModelPath = Path.Combine(appPath, ".aspire_server");

        var project = CreateProject(appPath, projectModelPath);

        await project.CreateProjectFilesAsync(
        [
            IntegrationReference.FromExactPackage("CommunityToolkit.Aspire.Hosting.ActiveMQ", "13.4.0"),
            IntegrationReference.FromPackage("CommunityToolkit.Aspire.Hosting.Dapr", "13.4.0")
        ]);

        var references = XDocument.Load(Path.Combine(projectModelPath, "AppHostServer.csproj"))
            .Descendants("PackageReference")
            .ToDictionary(element => element.Attribute("Include")!.Value, element => element.Attribute("VersionOverride")?.Value);

        Assert.Equal("[13.4.0]", references["CommunityToolkit.Aspire.Hosting.ActiveMQ"]);

        // Everything else keeps the minimum-version form the run and dump paths have always used.
        Assert.Equal("13.4.0", references["CommunityToolkit.Aspire.Hosting.Dapr"]);
    }

    /// <summary>
    /// The generated scanner replaces a first-party <c>Aspire.Hosting.*</c> package reference with the
    /// matching repository project and drops the requested version, so a caller that publishes
    /// artifacts keyed on that version has to be able to see the substitution coming.
    /// </summary>
    [Fact]
    public void GetLocalProjectSubstitution_ReportsOnlyFirstPartyProjectsThatExist()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;

        var redisProjectPath = Path.Combine(appPath, "src", "Aspire.Hosting.Redis", "Aspire.Hosting.Redis.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(redisProjectPath)!);
        File.WriteAllText(redisProjectPath, "<Project />");

        var project = CreateProject(appPath, Path.Combine(appPath, ".aspire_server"));

        Assert.Equal(redisProjectPath, project.GetLocalProjectSubstitution("Aspire.Hosting.Redis")?.ProjectPath);

        // No src/Aspire.Hosting.Qdrant in this checkout, so the package really is restored.
        Assert.Null(project.GetLocalProjectSubstitution("Aspire.Hosting.Qdrant"));

        // Third-party integrations are never substituted, even when a same-named folder exists.
        Assert.Null(project.GetLocalProjectSubstitution("CommunityToolkit.Aspire.Hosting.ActiveMQ"));
    }

    /// <summary>
    /// A NuGet package id is case-insensitive, but this resolves one through the filesystem, which
    /// is not on Linux. Probing the caller's spelling let it decide whether the checkout was
    /// substituted at all: <c>aspire.hosting.redis</c> found nothing there while macOS and Windows
    /// found <c>src/Aspire.Hosting.Redis</c>, so a caller that publishes version-keyed artifacts saw
    /// no substitution to guard against on the one platform the docs pipeline runs on.
    /// </summary>
    [Fact]
    public void GetLocalProjectSubstitution_ResolvesFirstPartyProjectsUnderAnyCasing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;

        var redisProjectPath = Path.Combine(appPath, "src", "Aspire.Hosting.Redis", "Aspire.Hosting.Redis.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(redisProjectPath)!);
        File.WriteAllText(redisProjectPath, "<Project />");

        var project = CreateProject(appPath, Path.Combine(appPath, ".aspire_server"));

        // The on-disk spelling, not the caller's. Asserting the canonical path is what makes this
        // meaningful on a case-insensitive filesystem too, where probing the caller's spelling
        // succeeds but hands back a path spelled the way the request was.
        Assert.Equal(redisProjectPath, project.GetLocalProjectSubstitution("aspire.hosting.redis")?.ProjectPath);
        Assert.Equal(redisProjectPath, project.GetLocalProjectSubstitution("ASPIRE.HOSTING.REDIS")?.ProjectPath);

        // Case-insensitive matching still only reports what the checkout actually contains.
        Assert.Null(project.GetLocalProjectSubstitution("aspire.hosting.qdrant"));
    }

    /// <summary>
    /// The substitution check and the generated project have to make the same decision, or a caller
    /// that was told nothing would be substituted still gets a scanner built from the checkout.
    /// </summary>
    [Fact]
    public async Task CreateProjectFiles_SubstitutesTheCheckoutProjectUnderAnyCasing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var projectModelPath = Path.Combine(appPath, ".aspire_server");

        var redisProjectPath = Path.Combine(appPath, "src", "Aspire.Hosting.Redis", "Aspire.Hosting.Redis.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(redisProjectPath)!);
        File.WriteAllText(redisProjectPath, "<Project />");

        var project = CreateProject(appPath, projectModelPath);

        await project.CreateProjectFilesAsync(
            [IntegrationReference.FromExactPackage("aspire.hosting.redis", "13.4.0")]);

        var document = XDocument.Load(Path.Combine(projectModelPath, "AppHostServer.csproj"));

        Assert.Equal(
            [redisProjectPath],
            document.Descendants("ProjectReference").Select(element => element.Attribute("Include")!.Value));

        // No package reference for the integration: the checkout supplies it, which is exactly what
        // GetLocalProjectSubstitution reports to callers that publish version-keyed artifacts. The
        // two the template always carries are all that is left.
        Assert.Equal(
            ["StreamJsonRpc", "Google.Protobuf"],
            document.Descendants("PackageReference").Select(element => element.Attribute("Include")!.Value));
    }

    /// <summary>
    /// The version a checkout builds has to come from the checkout, because the version this CLI
    /// reports is overrideable. <c>eng/Versions.props</c> is where the repository states it.
    /// </summary>
    [Fact]
    public void GetLocalProjectSubstitution_ReportsTheVersionTheCheckoutBuilds()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;

        var redisProjectPath = Path.Combine(appPath, "src", "Aspire.Hosting.Redis", "Aspire.Hosting.Redis.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(redisProjectPath)!);
        File.WriteAllText(redisProjectPath, "<Project />");

        var project = CreateProject(appPath, Path.Combine(appPath, ".aspire_server"));

        // No eng/Versions.props yet, so the checkout cannot say what it builds and callers that
        // publish version-keyed artifacts have to treat the substitution as unverifiable.
        Assert.Null(project.GetLocalProjectSubstitution("Aspire.Hosting.Redis")?.CheckoutVersionPrefix);

        Directory.CreateDirectory(Path.Combine(appPath, "eng"));
        File.WriteAllText(Path.Combine(appPath, "eng", "Versions.props"), """
            <Project>
              <PropertyGroup>
                <MajorVersion>13</MajorVersion>
                <MinorVersion>5</MinorVersion>
                <PatchVersion>0</PatchVersion>
              </PropertyGroup>
            </Project>
            """);

        var withVersions = CreateProject(appPath, Path.Combine(appPath, ".aspire_server_versioned"));

        Assert.Equal("13.5.0", withVersions.GetLocalProjectSubstitution("Aspire.Hosting.Redis")?.CheckoutVersionPrefix);
    }

    /// <summary>
    /// A first-party package name does not mean the checkout can supply it. Without a matching
    /// project under <c>src/</c> the reference is dropped from the generated project, so a
    /// nonexistent package scanned clean and exported an empty module. A reference that demands an
    /// exact version now restores as a real package instead, so the failure surfaces.
    /// </summary>
    /// <remarks>
    /// Only <c>sdk export</c> demands exactness. Everything else keeps dropping the reference,
    /// because <c>aspire run</c> and the other scanner callers take their integrations from
    /// aspire.config.json, where a version-less entry resolves to this CLI's identity — a version
    /// that could never restore from a feed, so failing would break the whole AppHost rather than
    /// one integration.
    /// </remarks>
    [Fact]
    public async Task CreateProjectFiles_FallsBackToAPackageReferenceOnlyForAnExactReference()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var projectModelPath = Path.Combine(appPath, ".aspire_server");

        var project = CreateProject(appPath, projectModelPath);

        await project.CreateProjectFilesAsync(
        [
            IntegrationReference.FromExactPackage("Aspire.Hosting.NotInThisCheckout", "13.4.0"),
            IntegrationReference.FromPackage("Aspire.Hosting.AlsoMissing", "13.4.0")
        ]);

        var document = XDocument.Load(Path.Combine(projectModelPath, "AppHostServer.csproj"));
        var references = document
            .Descendants("PackageReference")
            .ToDictionary(element => element.Attribute("Include")!.Value, element => element.Attribute("VersionOverride")?.Value);

        Assert.Equal("[13.4.0]", references["Aspire.Hosting.NotInThisCheckout"]);
        Assert.False(references.ContainsKey("Aspire.Hosting.AlsoMissing"));
        Assert.Empty(document.Descendants("ProjectReference"));
    }

    /// <summary>
    /// The generated XML cannot show what NuGet does with it. This restores twice against an offline
    /// feed that holds 13.4.1 but not the requested 13.4.0: the plain reference silently resolves
    /// upward (which is what mislabels an export), and the exact reference fails instead.
    /// </summary>
    [Fact]
    public async Task CreateProjectFiles_ExactIntegrationDoesNotFloatToALaterPackage()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;
        var feedPath = Path.Combine(appPath, "feed");
        Directory.CreateDirectory(feedPath);

        const string IntegrationPackage = "Contoso.Aspire.Hosting.ExactVersionProbe";

        OfflineNuGetFeed.CreateStubPackage(feedPath, "StreamJsonRpc", "1.0.0");
        OfflineNuGetFeed.CreateStubPackage(feedPath, "Google.Protobuf", "1.0.0");
        OfflineNuGetFeed.CreateStubPackage(feedPath, IntegrationPackage, "13.4.1");

        await File.WriteAllTextAsync(Path.Combine(appPath, "Directory.Packages.props"), """
            <Project>
              <ItemGroup>
                <PackageVersion Include="StreamJsonRpc" Version="1.0.0" />
                <PackageVersion Include="Google.Protobuf" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var floatingModelPath = Path.Combine(appPath, ".aspire_server_floating");
        await CreateProject(appPath, floatingModelPath)
            .CreateProjectFilesAsync([IntegrationReference.FromPackage(IntegrationPackage, "13.4.0")]);

        var (floatingExitCode, floatingOutput) = await OfflineNuGetFeed.RestoreAsync(
            Path.Combine(floatingModelPath, "AppHostServer.csproj"),
            feedPath);
        outputHelper.WriteLine(floatingOutput);

        // 13.4.0 is a minimum, so NuGet happily hands back 13.4.1 and warns rather than fails. The
        // assets file records what was actually resolved, which the console output does not always
        // spell out.
        Assert.Equal(0, floatingExitCode);
        Assert.Contains("NU1603", floatingOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"{IntegrationPackage}/13.4.1",
            await File.ReadAllTextAsync(Path.Combine(floatingModelPath, "obj", "project.assets.json")),
            StringComparison.Ordinal);

        var exactModelPath = Path.Combine(appPath, ".aspire_server_exact");
        await CreateProject(appPath, exactModelPath)
            .CreateProjectFilesAsync([IntegrationReference.FromExactPackage(IntegrationPackage, "13.4.0")]);

        var (exactExitCode, exactOutput) = await OfflineNuGetFeed.RestoreAsync(
            Path.Combine(exactModelPath, "AppHostServer.csproj"),
            feedPath);
        outputHelper.WriteLine(exactOutput);

        // NU1102 is "package found but not at the requested version", which is the failure a caller
        // needs instead of a document labelled 13.4.0 that describes 13.4.1.
        Assert.NotEqual(0, exactExitCode);
        Assert.Contains("NU1102", exactOutput, StringComparison.Ordinal);
    }

    private static DotNetBasedAppHostServerProject CreateProject(
        string appPath,
        string projectModelPath,
        TestDotNetCliRunner? runner = null,
        TestProcessExecutionFactory? processExecutionFactory = null)
        => new(
            appPath,
            socketPath: "test.sock",
            repoRoot: appPath,
            runner ?? new TestDotNetCliRunner(),
            MockPackagingServiceFactory.Create(),
            processExecutionFactory ?? new TestProcessExecutionFactory(),
            new TestEnvironment(),
            NullLogger<DotNetBasedAppHostServerProject>.Instance,
            projectModelPath);
}
