// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aspire.Hosting.Sdk.Tests;

public class AppHostSdkTargetsTests(ITestOutputHelper outputHelper)
{
    private const string AspireCliVersion = "13.5.0";
    private const string SuppressCliRunHookEnvironmentVariable = "ASPIRE_SUPPRESS_CLI_RUN_HOOK";

    private static readonly string[] s_supportedRids =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "osx-x64",
        "osx-arm64"
    ];

    [Fact]
    public async Task AddReferenceToDashboardAndDcpIsAddedWhenCliBundleIsDefaulted()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(workspace, extraProjectXml: null);

        AssertDashboardAndOrchestrationReferences(packageReferences);
    }

    [Fact]
    public async Task AddReferenceToDashboardAndDcpIsSkippedWhenCliBundleIsEnabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(workspace,
            """
              <PropertyGroup>
                <AspireUseCliBundle>true</AspireUseCliBundle>
              </PropertyGroup>
            """);

        Assert.DoesNotContain(packageReferences, static packageReference => packageReference.StartsWith("Aspire.Dashboard.Sdk.", StringComparison.Ordinal));
        Assert.DoesNotContain(packageReferences, static packageReference => packageReference.StartsWith("Aspire.Hosting.Orchestration.", StringComparison.Ordinal));
        Assert.Contains("Aspire.Hosting.AppHost=13.4.0", packageReferences);
    }

    [Fact]
    public async Task AddReferenceToDashboardAndDcpUsesSdkRidSelectionTask()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(workspace,
            """
              <PropertyGroup>
                <AspireUseCliBundle>false</AspireUseCliBundle>
              </PropertyGroup>
            """);

        Assert.Contains("UseSdkPickBestRid=true", packageReferences);
        Assert.Contains("RunRidToolFallback=false", packageReferences);
        AssertDashboardAndOrchestrationReferences(packageReferences);
    }

    [Fact]
    public async Task AddReferenceToDashboardAndDcpFallsBackToRuntimeIdentifierToolForOlderSdks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Force the pre-.NET 10 code path by disabling the in-proc PickBestRid task and pointing the
        // Exec call at the locally-built Aspire.RuntimeIdentifier.Tool assembly (which is normally
        // resolved out of the packed SDK's tools folder).
        var ridToolPath = GetAspireRuntimeIdentifierToolPath();

        var extraProjectXml = $"""
              <PropertyGroup>
                <AspireUseCliBundle>false</AspireUseCliBundle>
                <_AspireUseSdkPickBestRid>false</_AspireUseSdkPickBestRid>
                <AspireRidToolExecutable>{SecurityElement.Escape(ridToolPath)}</AspireRidToolExecutable>
              </PropertyGroup>
            """;

        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(workspace, extraProjectXml);

        Assert.Contains("UseSdkPickBestRid=false", packageReferences);
        Assert.Contains("RunRidToolFallback=true", packageReferences);
        AssertDashboardAndOrchestrationReferences(packageReferences);
    }

    [Fact]
    public async Task ProjectMetadataUsesTargetNameImportedFromDirectoryBuildProps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // The AssemblyName deliberately lives outside the project file. Reading the raw project XML
        // would fall back to the file name ("Worker") and produce a process name that does not exist.
        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                """,
            referencedDirectoryBuildPropsXml: """
                  <PropertyGroup>
                    <AssemblyName>My Attach Service</AssemblyName>
                  </PropertyGroup>
                """);

        Assert.Equal("""    public string? TargetName => @"My Attach Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesProjectFileNameWhenTargetNameIsNotSet()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // The overwhelmingly common case: no AssemblyName anywhere, so the evaluated name falls back to the project
        // file name. Consumers rely on the property being present and correct here, not just in the exotic cases.
        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                """);

        Assert.Equal("""    public string? TargetName => @"Worker";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesTargetNameInheritedFromAnAncestorDirectoryBuildProps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // The AssemblyName is several directory levels above the project, which is where a repo-wide convention
        // usually lives. Only an MSBuild evaluation can see it.
        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                """,
            referencedProjectDirectoryName: "src/services/Worker",
            ancestorDirectoryBuildPropsXml: """
                  <PropertyGroup>
                    <AssemblyName>Inherited Attach Service</AssemblyName>
                  </PropertyGroup>
                """);

        Assert.Equal("""    public string? TargetName => @"Inherited Attach Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesTargetNameWhenItDivergesFromAssemblyName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // TargetName is what the built output is actually named, and an SDK is free to set it to something other
        // than AssemblyName. The debugger attaches to the built assembly, so TargetName is the value that has to
        // win; asserting the divergence keeps a future refactor from quietly switching to AssemblyName.
        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Declared Name</AssemblyName>
                    <TargetName>Rewritten By Sdk</TargetName>
                  </PropertyGroup>
                """);

        Assert.Equal("""    public string? TargetName => @"Rewritten By Sdk";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesConfigurationConditionedTargetName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName Condition="'$(Configuration)' == 'Release'">Released Service</AssemblyName>
                    <AssemblyName Condition="'$(Configuration)' != 'Release'">Debugged Service</AssemblyName>
                  </PropertyGroup>
                """,
            extraArguments: ["-p:Configuration=Release"],
            configuration: "Release");

        Assert.Equal("""    public string? TargetName => @"Released Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesProjectReferenceConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName Condition="'$(Configuration)' == 'Release'">Released Service</AssemblyName>
                    <AssemblyName Condition="'$(Configuration)' != 'Release'">Debugged Service</AssemblyName>
                  </PropertyGroup>
                """,
            projectReferenceMetadataXml: """
                  <SetConfiguration>Configuration=Release</SetConfiguration>
                """);

        Assert.Equal("""    public string? TargetName => @"Released Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesProjectReferencePlatform()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName Condition="'$(Platform)' == 'x64'">64-bit Service</AssemblyName>
                    <AssemblyName Condition="'$(Platform)' != 'x64'">Any CPU Service</AssemblyName>
                  </PropertyGroup>
                """,
            projectReferenceMetadataXml: """
                  <SetPlatform>Platform=x64</SetPlatform>
                """);

        Assert.Equal("""    public string? TargetName => @"64-bit Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesSolutionPreparedProjectReferenceConfigurationAndPlatform()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Worker_$(Configuration)_$(Platform)_$(TargetFramework)</AssemblyName>
                  </PropertyGroup>
                """,
            solutionProjectConfiguration: "Release|x64");

        Assert.Equal("""    public string? TargetName => @"Worker_Release_x64_net8.0";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataSkipsTargetNameForReferenceDisabledInSolutionConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // A reference the solution excludes from the build carries BuildReference=false, and ResolveProjectReferences
        // skips it. The probe has to skip it too: nothing caches GetTargetPath for it, so probing costs a fresh
        // evaluation of a project the build was told not to touch.
        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Disabled Service</AssemblyName>
                  </PropertyGroup>
                """,
            solutionProjectConfiguration: "Debug|AnyCPU",
            buildProjectInSolution: false);

        // Absence is the capability signal, so an unprobed reference must omit the member rather than guess a name.
        Assert.Null(GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataRemovesProjectReferenceGlobalProperties()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName Condition="'$(Flavor)' == ''">Unflavored Service</AssemblyName>
                    <AssemblyName Condition="'$(Flavor)' != ''">Flavored Service</AssemblyName>
                  </PropertyGroup>
                """,
            projectReferenceMetadataXml: """
                  <GlobalPropertiesToRemove>Flavor</GlobalPropertiesToRemove>
                """,
            extraArguments: ["-p:Flavor=Chocolate"]);

        Assert.Equal("""    public string? TargetName => @"Unflavored Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataUsesTargetFrameworkConditionedTargetNameForMultiTargetedReference()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net8.0'">Eight Service</AssemblyName>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net9.0'">Nine Service</AssemblyName>
                  </PropertyGroup>
                """);

        Assert.Equal("""    public string? TargetName => @"Eight Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataRespectsProjectReferenceTargetFramework()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net8.0'">Eight Service</AssemblyName>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net9.0'">Nine Service</AssemblyName>
                  </PropertyGroup>
                """,
            projectReferenceMetadataXml: """
                  <SetTargetFramework>TargetFramework=net9.0</SetTargetFramework>
                """);

        Assert.Equal("""    public string? TargetName => @"Nine Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataPreservesExplicitTargetFrameworkWhenItIsAlsoRemoved()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net8.0' and '$(Flavor)' == ''">Eight Clean Service</AssemblyName>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net8.0' and '$(Flavor)' != ''">Eight Flavored Service</AssemblyName>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net9.0' and '$(Flavor)' == ''">Nine Clean Service</AssemblyName>
                    <AssemblyName Condition="'$(TargetFramework)' == 'net9.0' and '$(Flavor)' != ''">Nine Flavored Service</AssemblyName>
                  </PropertyGroup>
                """,
            projectReferenceMetadataXml: """
                  <SetTargetFramework>TargetFramework=net9.0</SetTargetFramework>
                  <GlobalPropertiesToRemove>Flavor;TargetFramework</GlobalPropertiesToRemove>
                """,
            extraArguments: ["-p:Flavor=Chocolate"]);

        Assert.Equal("""    public string? TargetName => @"Nine Clean Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataEscapesTargetNameForCSharpSource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Ünicode &quot;quoted&quot; O'Brien</AssemblyName>
                  </PropertyGroup>
                """);

        Assert.Equal("""    public string? TargetName => @"Ünicode ""quoted"" O'Brien";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataResolvesTargetNameWhenProjectDirectoryContainsAnApostrophe()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // An apostrophe in the project path reaches two different MSBuild property functions: the
        // GetFullPath over %(Identity) that normalizes ProjectPath, and the GetFullPath over the
        // $(_AspireResolvedProjectFile) property that normalizes the resolved project file before the
        // two lists are correlated. Both have to survive it, because a failure here does not degrade
        // the target name - it fails metadata generation and takes the whole AppHost build with it.
        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Apostrophe Service</AssemblyName>
                  </PropertyGroup>
                """,
            referencedProjectDirectoryName: "O'Brien");

        Assert.Equal("""    public string? TargetName => @"Apostrophe Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataOmitsTargetNameWhenResolutionIsDisabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>My Attach Service</AssemblyName>
                  </PropertyGroup>
                """,
            extraArguments: ["-p:SkipAspireProjectResourceTargetName=true"]);

        Assert.Null(GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ProjectMetadataResolvesTargetNameWhenProbeFailuresAreFatal()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // The probe passes ContinueOnError="!$(BuildingProject)", so a real build makes a failed
        // reference probe fatal while a design-time build stays tolerant. Every other test here runs
        // a bare -t: invocation, which never executes BuildOnlySettings and therefore leaves
        // BuildingProject at its default of false. Forcing it to true covers the strict path.
        var generatedSource = await GenerateProjectMetadataSourceAsync(
            workspace,
            referencedProjectXml: """
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>My Attach Service</AssemblyName>
                  </PropertyGroup>
                """,
            extraArguments: ["-p:BuildingProject=true"]);

        Assert.Equal("""    public string? TargetName => @"My Attach Service";""", GetGeneratedTargetNameMember(generatedSource));
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesAspireCliWhenCliBundleIsEnabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var aspireCliPath = await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);
        await CreateFakeDnxAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:RunArguments=--custom foo"],
            CreatePathEnvironment(fakeCliDirectory.FullName));

        Assert.Equal("Aspire", properties["_AspireResolvedCliInvocationMode"]);
        Assert.Equal("13.5.0", properties["_AspireResolvedCliVersion"]);
        Assert.Equal("true", properties["_AspireCliVersionSupportsRunHook"]);
        Assert.Equal(GetExpectedAspireRunCommand(aspireCliPath), properties["RunCommand"]);
        Assert.Equal(GetExpectedAspireRunArguments(aspireCliPath, project, "--custom foo"), properties["RunArguments"]);
        Assert.Equal(project.ProjectDirectory, properties["RunWorkingDirectory"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Dnx")]
    [InlineData("dNx")]
    public async Task ComputeRunArgumentsUsesDnxAspireCliWhenSelected(string? invocationMode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var dnxPath = await CreateFakeDnxAsync(fakeCliDirectory.FullName);
        if (invocationMode is not null)
        {
            await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);
        }

        var extraProjectXml = invocationMode is null
            ? null
            : $$"""
              <PropertyGroup>
                <AspireCliInvocationMode>{{invocationMode}}</AspireCliInvocationMode>
              </PropertyGroup>
            """;
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, extraProjectXml);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:RunArguments=--custom foo"],
            new Dictionary<string, string>
            {
                [GetPathEnvironmentVariableName()] = CreatePathWithoutAspire(fakeCliDirectory.FullName)
            });

        Assert.Equal("Dnx", properties["_AspireResolvedCliInvocationMode"]);
        Assert.Equal("13.5.0", properties["_AspireResolvedCliVersion"]);
        Assert.Equal("true", properties["_AspireCliVersionSupportsRunHook"]);
        Assert.Equal(GetExpectedDnxRunCommand(dnxPath), properties["RunCommand"]);
        Assert.Equal(GetExpectedDnxRunArguments(dnxPath, project, "--custom foo"), properties["RunArguments"]);
        Assert.Contains($"aspire.cli@{AspireCliVersion}", properties["_AspireCliVersionCommand"]);
        Assert.Equal(project.ProjectDirectory, properties["RunWorkingDirectory"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsFailsWhenDnxModeIsConfiguredAndDnxIsMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var emptyPathDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "empty-path"));
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true,
            """
              <PropertyGroup>
                <AspireCliInvocationMode>Dnx</AspireCliInvocationMode>
              </PropertyGroup>
            """);

        var result = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["msbuild", "-nologo", "-restore", "-t:ComputeRunArguments", project.ProjectFile],
            CreatePathEnvironment(emptyPathDirectory.FullName, includeCurrentPath: false));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASPIRE011", result.Output);
        Assert.Contains("AspireCliInvocationMode=Dnx", result.Output);
        Assert.Contains("dnx command could not be found on PATH", result.Output);
        Assert.Contains("Install or use the .NET SDK 10.0 or later", result.Output);
    }

    [Fact]
    public async Task ComputeRunArgumentsSkipsNonExecutableDnxOnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix execute permissions are not used on Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nonExecutableDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "non-executable"));
        var nonExecutableDnxPath = Path.Combine(nonExecutableDirectory.FullName, "dnx");
        await File.WriteAllTextAsync(nonExecutableDnxPath, "");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(nonExecutableDnxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var executableDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "executable"));
        var executableDnxPath = await CreateFakeDnxAsync(executableDirectory.FullName);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var path = $"{nonExecutableDirectory.FullName}{Path.PathSeparator}{CreatePathWithoutAspire(executableDirectory.FullName)}";

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            environment: new Dictionary<string, string>
            {
                [GetPathEnvironmentVariableName()] = path
            });

        Assert.Equal(executableDnxPath, properties["_AspireResolvedDnxPath"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsNormalizesRelativePathEntries()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        const string RelativeCliDirectory = "relative-cli";
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(project.ProjectDirectory, RelativeCliDirectory));
        var aspireCliPath = await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            environment: new Dictionary<string, string>
            {
                [GetPathEnvironmentVariableName()] = CreatePathWithoutDnx(RelativeCliDirectory)
            });

        Assert.Equal(aspireCliPath, properties["_AspireResolvedCliPath"]);
        Assert.True(Path.IsPathFullyQualified(properties["_AspireResolvedCliPath"]));
    }

    [Fact]
    public async Task ComputeRunArgumentsFailsWhenDnxVersionProbeFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        await CreateFakeDnxThatFailsVersionAsync(fakeCliDirectory.FullName);

        var result = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["msbuild", "-nologo", "-restore", "-t:ComputeRunArguments", project.ProjectFile],
            new Dictionary<string, string>
            {
                [GetPathEnvironmentVariableName()] = CreatePathWithoutAspire(fakeCliDirectory.FullName)
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"DNX could not restore or run aspire.cli@{AspireCliVersion}", result.Output);
        Assert.Contains("package sources, authentication, or connectivity", result.Output);
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesConfiguredAspireCliPathWhenCliBundleIsEnabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var aspireCliPath = await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(project, [$"-p:AspireCliPath={aspireCliPath}"]);

        AssertUsesExplicitAspireCli(properties, project, aspireCliPath);
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".BAT")]
    public async Task ComputeRunArgumentsWrapsConfiguredAspireCommandShimOnWindows(string extension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake cli !& (shim)^,;=+[]{}~@"));
        var aspireCliPath = await CreateFakeAspireCommandShimAsync(fakeCliDirectory.FullName, extension);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:OS=Windows_NT", "-p:RunArguments=--custom foo"],
            new Dictionary<string, string> { ["AspireCliPath"] = aspireCliPath });

        Assert.Equal("cmd", properties["RunCommand"]);
        Assert.Equal(GetExpectedWindowsCommandShimRunArguments(project, aspireCliPath, "--custom foo"), properties["RunArguments"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesConfiguredAspireCliPathWhenDnxModeIsConfigured()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var emptyPathDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "empty-path"));
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true,
            """
              <PropertyGroup>
                <AspireCliInvocationMode>Dnx</AspireCliInvocationMode>
              </PropertyGroup>
            """);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var aspireCliPath = await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            [$"-p:AspireCliPath={aspireCliPath}"],
            new Dictionary<string, string>
            {
                [GetPathEnvironmentVariableName()] = CreatePathWithoutDnx(emptyPathDirectory.FullName)
            });

        AssertUsesExplicitAspireCli(properties, project, aspireCliPath);
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesFileBasedAppHostPathWhenCliBundleIsEnabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = Path.Combine(workspace.Path, "FileApp");
        Directory.CreateDirectory(appHostDirectory);
        var appHostFile = Path.Combine(appHostDirectory, "apphost.cs");
        await File.WriteAllTextAsync(appHostFile, """
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var extraProjectXml = $$"""
              <PropertyGroup>
                <FileBasedProgram>true</FileBasedProgram>
              </PropertyGroup>

              <ItemGroup>
                <RuntimeHostConfigurationOption Include="EntryPointFileDirectoryPath">
                  <Value>{{SecurityElement.Escape(appHostDirectory)}}</Value>
                </RuntimeHostConfigurationOption>
                <RuntimeHostConfigurationOption Include="EntryPointFilePath">
                  <Value>{{SecurityElement.Escape(appHostFile)}}</Value>
                </RuntimeHostConfigurationOption>
              </ItemGroup>
            """;
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, extraProjectXml: extraProjectXml);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var aspireCliPath = await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:RunArguments=--custom foo"],
            CreatePathEnvironment(fakeCliDirectory.FullName));

        Assert.Equal(GetExpectedAspireRunCommand(aspireCliPath), properties["RunCommand"]);
        Assert.Equal(GetExpectedAspireRunArguments(aspireCliPath, appHostFile, "--custom foo"), properties["RunArguments"]);
        Assert.Equal(appHostDirectory, properties["RunWorkingDirectory"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenCliBundleIsDisabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: false);

        var properties = await GetComputeRunArgumentsPropertiesAsync(project);

        AssertUsesDotNetRun(properties, project);
    }

    [Theory]
    [InlineData(SuppressCliRunHookEnvironmentVariable, "true")]
    [InlineData(SuppressCliRunHookEnvironmentVariable, "1")]
    [InlineData("_AspireSuppressCliRunHook", "true")]
    [InlineData("_AspireSuppressCliRunHook", "1")]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenHookIsSuppressed(string suppressionPropertyName, string suppressionValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);

        Dictionary<string, string>? environment = suppressionPropertyName == SuppressCliRunHookEnvironmentVariable
            ? new Dictionary<string, string> { [SuppressCliRunHookEnvironmentVariable] = suppressionValue }
            : null;
        var extraArguments = suppressionPropertyName == "_AspireSuppressCliRunHook"
            ? new[] { $"-p:_AspireSuppressCliRunHook={suppressionValue}" }
            : [];

        var properties = await GetComputeRunArgumentsPropertiesAsync(project, extraArguments, environment);

        AssertUsesDotNetRun(properties, project);
    }

    [Fact]
    public async Task DotNetRunUsesAspireCliWhenCliBundleIsEnabled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "selected cli"));
        var captureFile = Path.Combine(workspace.Path, "aspire-args.txt");
        await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);
        var shadowCaptureFile = await CreateFailingWorkingDirectoryShadowAsync(project.ProjectDirectory, "aspire");

        var pathEnvironmentVariable = GetPathEnvironmentVariableName();
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_TEST_CAPTURE_PATH"] = captureFile,
            [pathEnvironmentVariable] = CreatePathWithoutAspire(fakeCliDirectory.FullName)
        };

        var result = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["run", "--project", project.ProjectFile, "--", "--custom", "foo"],
            environment);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
            [
                "run",
                "--project",
                project.ProjectFile,
                "--no-build",
                "--",
                "--custom",
                "foo"
            ],
            await File.ReadAllLinesAsync(captureFile));
        Assert.False(File.Exists(shadowCaptureFile), "The working-directory Aspire shim was invoked instead of the resolved PATH command.");
    }

    [Fact]
    public async Task DotNetRunUsesDnxAspireCliWhenAspireCliIsUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "selected cli"));
        var captureFile = Path.Combine(workspace.Path, "dnx-args.txt");
        await CreateFakeDnxAsync(fakeCliDirectory.FullName);
        var shadowCaptureFile = await CreateFailingWorkingDirectoryShadowAsync(project.ProjectDirectory, "dnx");

        var pathEnvironmentVariable = GetPathEnvironmentVariableName();
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_TEST_CAPTURE_PATH"] = captureFile,
            [pathEnvironmentVariable] = CreatePathWithoutAspire(fakeCliDirectory.FullName)
        };

        var result = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["run", "--project", project.ProjectFile, "--", "--custom", "https://host/?a=1&b=2", "%ASPIRE_TEST_LITERAL%"],
            environment);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
            [
                "--yes",
                $"aspire.cli@{AspireCliVersion}",
                "--",
                "run",
                "--project",
                project.ProjectFile,
                "--no-build",
                "--",
                "--custom",
                "https://host/?a=1&b=2",
                "%ASPIRE_TEST_LITERAL%"
            ],
            await File.ReadAllLinesAsync(captureFile));
        Assert.False(File.Exists(shadowCaptureFile), "The working-directory DNX shim was invoked instead of the resolved PATH command.");
    }

    [Fact]
    public async Task DotNetRunUsesNativeAspireCliWithoutCmdArgumentReparsingOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates native Windows executable argument forwarding.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "native aspire"));
        var captureFile = Path.Combine(workspace.Path, "aspire-native-args.txt");
        CreateFakeNativeAspireCli(fakeCliDirectory.FullName);
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_TEST_CAPTURE_PATH"] = captureFile,
            ["ASPIRE_TEST_LITERAL"] = "expanded",
            [GetPathEnvironmentVariableName()] = CreatePathWithoutDnx(fakeCliDirectory.FullName)
        };

        var result = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["run", "--project", project.ProjectFile, "--", "--custom", "https://host/?a=1&b=2", "%ASPIRE_TEST_LITERAL%"],
            environment);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
            [
                "run",
                "--project",
                project.ProjectFile,
                "--no-build",
                "--",
                "--custom",
                "https://host/?a=1&b=2",
                "%ASPIRE_TEST_LITERAL%"
            ],
            await File.ReadAllLinesAsync(captureFile));
    }

    [Fact]
    public async Task DotNetRunUsesConfiguredAspireCommandShimWhenCliBundleIsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test validates native cmd.exe command-shim execution.");
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake cli !& (shim)^,;=+[]{}~@"));
        var captureFile = Path.Combine(workspace.Path, "aspire-args.txt");
        var aspireCliPath = await CreateFakeAspireCommandShimAsync(fakeCliDirectory.FullName);

        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_TEST_CAPTURE_PATH"] = captureFile,
            ["AspireCliPath"] = aspireCliPath
        };

        var result = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["run", "--project", project.ProjectFile, "--", "--custom", "value with spaces"],
            environment);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
            [
                "run",
                "--project",
                project.ProjectFile,
                "--no-build",
                "--",
                "--custom",
                "value with spaces"
            ],
            await File.ReadAllLinesAsync(captureFile));
    }

    [Theory]
    [InlineData("13.4.0")]
    [InlineData("13.4.1")]
    [InlineData("13.4.5")]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenCliVersionIsBelowMinimum(string cliVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var cliPath = Path.Combine(fakeCliDirectory.FullName, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire");
        await CreateFakeAspireCliWithVersionAsync(fakeCliDirectory.FullName, cliVersion);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            [$"-p:AspireCliPath={cliPath}", "-p:RunArguments=--custom foo"]);

        AssertUsesDotNetRun(properties, project, "--custom foo");
    }

    [Fact]
    public async Task ComputeRunArgumentsFallsBackToDnxWhenPathAspireCliVersionIsBelowMinimum()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, includeBundlePaths: false);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        await CreateFakeAspireCliWithVersionAsync(fakeCliDirectory.FullName, "13.4.5");
        var dnxPath = await CreateFakeDnxAsync(fakeCliDirectory.FullName);
        var environment = CreatePathEnvironment(fakeCliDirectory.FullName);

        var buildResult = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["build", "-nologo", project.ProjectFile],
            environment);
        Assert.True(buildResult.ExitCode == 0, buildResult.Output);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:RunArguments=--custom foo"],
            environment);

        Assert.Equal("Dnx", properties["_AspireResolvedCliInvocationMode"]);
        Assert.Equal("13.5.0", properties["_AspireResolvedCliVersion"]);
        Assert.Equal("true", properties["_AspireCliVersionSupportsRunHook"]);
        Assert.Equal(GetExpectedDnxRunCommand(dnxPath), properties["RunCommand"]);
        Assert.Equal(GetExpectedDnxRunArguments(dnxPath, project, "--custom foo"), properties["RunArguments"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsFallsBackToDnxWhenPathAspireCliVersionCannotBeDetermined()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        await CreateFakeAspireCliThatFailsVersionAsync(fakeCliDirectory.FullName);
        var dnxPath = await CreateFakeDnxAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:RunArguments=--custom foo"],
            CreatePathEnvironment(fakeCliDirectory.FullName));

        Assert.Equal("Dnx", properties["_AspireResolvedCliInvocationMode"]);
        Assert.Equal("13.5.0", properties["_AspireResolvedCliVersion"]);
        Assert.Equal("true", properties["_AspireCliVersionSupportsRunHook"]);
        Assert.Equal(GetExpectedDnxRunCommand(dnxPath), properties["RunCommand"]);
        Assert.Equal(GetExpectedDnxRunArguments(dnxPath, project, "--custom foo"), properties["RunArguments"]);
    }

    [Theory]
    [InlineData("13.5.0-preview.1.26319.9")]
    [InlineData("13.5.0+gabcdef")]
    [InlineData("13.5.0")]
    [InlineData("13.6.0")]
    [InlineData("14.0.0")]
    public async Task ComputeRunArgumentsUsesAspireCliWhenCliVersionIsAtOrAboveMinimum(string cliVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var cliPath = Path.Combine(fakeCliDirectory.FullName, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire");
        await CreateFakeAspireCliWithVersionAsync(fakeCliDirectory.FullName, cliVersion);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            [$"-p:AspireCliPath={cliPath}", "-p:RunArguments=--custom foo"]);

        AssertUsesExplicitAspireCli(properties, project, cliPath, "--custom foo");
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesAspireCliWhenCliVersionWritesLargeStdErr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var cliPath = Path.Combine(fakeCliDirectory.FullName, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire");
        await CreateFakeAspireCliWithVersionAndLargeStdErrAsync(fakeCliDirectory.FullName, "13.5.0");

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            [$"-p:AspireCliPath={cliPath}", "-p:RunArguments=--custom foo"]);

        AssertUsesExplicitAspireCli(properties, project, cliPath, "--custom foo");
    }

    [Fact]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenCliVersionCommandTimesOut()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var cliPath = Path.Combine(fakeCliDirectory.FullName, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire");
        await CreateFakeAspireCliThatHangsOnVersionAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            [$"-p:AspireCliPath={cliPath}", "-p:RunArguments=--custom foo"]);

        AssertUsesDotNetRun(properties, project, "--custom foo");
    }

    [Fact]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenCliVersionOutputIsUnparseable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var cliPath = Path.Combine(fakeCliDirectory.FullName, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire");
        await CreateFakeAspireCliWithVersionAsync(fakeCliDirectory.FullName, "aspire version 13.5.0");

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            [$"-p:AspireCliPath={cliPath}", "-p:RunArguments=--custom foo"]);

        AssertUsesDotNetRun(properties, project, "--custom foo");
    }

    [Fact]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenCliVersionCannotBeDetermined()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var cliPath = Path.Combine(fakeCliDirectory.FullName, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire");
        await CreateFakeAspireCliThatFailsVersionAsync(fakeCliDirectory.FullName);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            [$"-p:AspireCliPath={cliPath}", "-p:RunArguments=--custom foo"]);

        AssertUsesDotNetRun(properties, project, "--custom foo");
    }

    [Fact]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenCliIsUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true);
        var emptyPathDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "empty-path"));

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:RunArguments=--custom foo"],
            CreatePathEnvironment(emptyPathDirectory.FullName, includeCurrentPath: false));

        AssertUsesDotNetRun(properties, project, "--custom foo");
    }

    private static async Task<string[]> RunAddReferenceToDashboardAndDcpAsync(TemporaryWorkspace workspace, string? extraProjectXml)
    {
        var repoRoot = GetRepoRoot();

        var projectDirectory = Path.Combine(workspace.Path, "AppHost");
        Directory.CreateDirectory(projectDirectory);

        var sdkTargetsPath = SecurityElement.Escape(Path.Combine(repoRoot, "src", "Aspire.AppHost.Sdk", "SDK", "Sdk.in.targets"));
        var packageReferencesPath = Path.Combine(projectDirectory, "obj", "package-references.txt");

        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "AppHost.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <SkipAspireWorkloadManifest>true</SkipAspireWorkloadManifest>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.AppHost" Version="13.4.0" />
              </ItemGroup>

              <Import Project="{{sdkTargetsPath}}" />

            {{extraProjectXml}}

              <Target Name="WritePackageReferences" DependsOnTargets="AddReferenceToDashboardAndDCP">
                <WriteLinesToFile File="$(BaseIntermediateOutputPath)package-references.txt"
                                  Lines="UseSdkPickBestRid=$(_AspireUseSdkPickBestRid);RunRidToolFallback=$(_AspireRunRidToolFallback);@(PackageReference->'%(Identity)=%(Version)')"
                                  Overwrite="true" />
              </Target>

            </Project>
            """);

        var result = await RunDotNetAsync(projectDirectory, "msbuild -nologo -t:WritePackageReferences");

        Assert.True(result.ExitCode == 0, result.Output);

        return await File.ReadAllLinesAsync(packageReferencesPath);
    }

    /// <summary>
    /// Builds a throwaway AppHost that ProjectReferences a single worker project, runs the Aspire
    /// codegen target, and returns the generated <c>IProjectMetadata</c> source for the worker.
    /// </summary>
    private static async Task<string> GenerateProjectMetadataSourceAsync(
        TemporaryWorkspace workspace,
        string referencedProjectXml,
        string? referencedDirectoryBuildPropsXml = null,
        string[]? extraArguments = null,
        string targetFramework = "net8.0",
        string configuration = "Debug",
        string? projectReferenceMetadataXml = null,
        string? solutionProjectConfiguration = null,
        string referencedProjectDirectoryName = "Worker",
        string? ancestorDirectoryBuildPropsXml = null,
        bool buildProjectInSolution = true)
    {
        var repoRoot = GetRepoRoot();

        // Terminate MSBuild's upward Directory.Build.props/targets probe at the workspace root so the
        // generated metadata only reflects what this test authored, not whatever happens to sit above
        // the temp directory on the machine running the test. Directory.Packages.props is discovered
        // independently of that probe, so central package management has to be switched off explicitly.
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Directory.Build.props"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Directory.Build.targets"), "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "Directory.Packages.props"),
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");

        var workerDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, referencedProjectDirectoryName)).FullName;
        var workerProjectFile = Path.Combine(workerDirectory, "Worker.csproj");
        await File.WriteAllTextAsync(workerProjectFile,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">

            {referencedProjectXml}

            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(workerDirectory, "Program.cs"), """
            System.Console.WriteLine("worker");
            """);

        if (referencedDirectoryBuildPropsXml is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(workerDirectory, "Directory.Build.props"),
                $"""
                <Project>

                {referencedDirectoryBuildPropsXml}

                </Project>
                """);
        }

        if (ancestorDirectoryBuildPropsXml is not null)
        {
            // Written to the top-most segment of the referenced project's path rather than next to the project, so
            // the value is only visible after MSBuild's upward Directory.Build.props probe has climbed several
            // levels. A consumer that reads the project XML cannot see it at all.
            var ancestorDirectory = Path.Combine(workspace.Path, referencedProjectDirectoryName.Split('/')[0]);
            await File.WriteAllTextAsync(Path.Combine(ancestorDirectory, "Directory.Build.props"),
                $"""
                <Project>

                {ancestorDirectoryBuildPropsXml}

                </Project>
                """);
        }

        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "AppHost")).FullName;
        var appHostTargetsPath = SecurityElement.Escape(Path.Combine(repoRoot, "src", "Aspire.Hosting.AppHost", "build", "Aspire.Hosting.AppHost.in.targets"));
        var appHostProjectFile = Path.Combine(appHostDirectory, "AppHost.csproj");
        var solutionConfigurationXml = solutionProjectConfiguration is null
            ? null
            : $$"""
              <PropertyGroup>
                <CurrentSolutionConfigurationContents>&lt;SolutionConfiguration&gt;&lt;ProjectConfiguration Project=&quot;{C42D47BF-C684-40EB-B438-FC98C4DC6F5D}&quot; AbsolutePath=&quot;{{SecurityElement.Escape(workerProjectFile)}}&quot; BuildProjectInSolution=&quot;{{(buildProjectInSolution ? "True" : "False")}}&quot;&gt;{{solutionProjectConfiguration}}&lt;/ProjectConfiguration&gt;&lt;/SolutionConfiguration&gt;</CurrentSolutionConfigurationContents>
              </PropertyGroup>
            """;

        // The SDK props/targets are imported explicitly so the Aspire AppHost targets land *after*
        // Sdk.targets, which is where a NuGet package's build/*.targets normally gets imported. The
        // ordering matters because the codegen writes to $(IntermediateOutputPath), which is only
        // defined once Microsoft.Common.CurrentVersion.targets has been evaluated.
        // The ProjectReference metadata mirrors what Aspire.AppHost.Sdk defaults for Aspire project
        // resources; this test imports only the AppHost targets, so the defaults are spelled out.
        await File.WriteAllTextAsync(appHostProjectFile,
            $$"""
            <Project>

              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{{targetFramework}}</TargetFramework>
                <IsAspireHost>true</IsAspireHost>
                <_AspireTasksAssembly>{{SecurityElement.Escape(GetAspireHostingTasksAssemblyPath())}}</_AspireTasksAssembly>
                <SkipAspireWorkloadManifest>true</SkipAspireWorkloadManifest>
                <SkipValidateAspireHostProjectResources>true</SkipValidateAspireHostProjectResources>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="../{{referencedProjectDirectoryName}}/Worker.csproj"
                                  IsAspireProjectResource="true"
                                  ReferenceOutputAssembly="false"
                                  SkipGetTargetFrameworkProperties="true"
                                  ExcludeAssets="all"
                                  Private="false">
                  <Project>{C42D47BF-C684-40EB-B438-FC98C4DC6F5D}</Project>
            {{projectReferenceMetadataXml}}
                </ProjectReference>
              </ItemGroup>

            {{solutionConfigurationXml}}

              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />

              <Import Project="{{appHostTargetsPath}}" />

            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(appHostDirectory, "Program.cs"), """
            System.Console.WriteLine("apphost");
            """);

        var arguments = new List<string>
        {
            "msbuild",
            "-nologo",
            "-restore",
            "-t:WriteAspireProjectMetadataSources",
            appHostProjectFile
        };

        if (extraArguments is not null)
        {
            arguments.AddRange(extraArguments);
        }

        var result = await RunDotNetWithArgumentsAsync(appHostDirectory, [.. arguments]);
        Assert.True(result.ExitCode == 0, result.Output);

        var generatedPath = Path.Combine(appHostDirectory, "obj", configuration, targetFramework, "Aspire", "references", "Worker.ProjectMetadata.g.cs");
        Assert.True(File.Exists(generatedPath), $"Generated project metadata was not found at '{generatedPath}'.{Environment.NewLine}{result.Output}");

        return await File.ReadAllTextAsync(generatedPath);
    }

    private static string? GetGeneratedTargetNameMember(string generatedSource)
    {
        return generatedSource
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .SingleOrDefault(line => line.Contains("TargetName =>", StringComparison.Ordinal));
    }

    private static async Task<RunHookProject> CreateRunHookProjectAsync(
        string workspace,
        bool aspireUseCliBundle,
        string? extraProjectXml = null,
        bool includeBundlePaths = true)
    {
        var repoRoot = GetRepoRoot();
        var projectDirectory = Path.Combine(workspace, "AppHost");
        Directory.CreateDirectory(projectDirectory);

        var appHostTargetsPath = SecurityElement.Escape(Path.Combine(repoRoot, "src", "Aspire.Hosting.AppHost", "build", "Aspire.Hosting.AppHost.in.targets"));
        var projectFile = Path.Combine(projectDirectory, "AppHost.csproj");
        var bundlePathsXml = includeBundlePaths
            ? """
                <AspireDashboardPath>$(MSBuildProjectDirectory)/Aspire.Dashboard.dll</AspireDashboardPath>
                <DcpDir>$(MSBuildProjectDirectory)</DcpDir>
              """
            : string.Empty;

        await File.WriteAllTextAsync(projectFile,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <IsAspireHost>true</IsAspireHost>
                <AspireHostingSDKVersion>{{AspireCliVersion}}</AspireHostingSDKVersion>
                <AspireUseCliBundle>{{aspireUseCliBundle.ToString().ToLowerInvariant()}}</AspireUseCliBundle>
            {{bundlePathsXml}}
                <_AspireTasksAssembly>{{SecurityElement.Escape(GetAspireHostingTasksAssemblyPath())}}</_AspireTasksAssembly>
                <SkipAspireWorkloadManifest>true</SkipAspireWorkloadManifest>
                <SkipValidateAspireHostProjectResources>true</SkipValidateAspireHostProjectResources>
              </PropertyGroup>

            {{extraProjectXml}}

              <Import Project="{{appHostTargetsPath}}" />

            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), """
            System.Console.WriteLine("AppHost should be launched by the Aspire CLI.");
            """);

        return new RunHookProject(projectDirectory, projectFile);
    }

    private static async Task<Dictionary<string, string>> GetComputeRunArgumentsPropertiesAsync(
        RunHookProject project,
        string[]? extraArguments = null,
        IDictionary<string, string>? environment = null)
    {
        var arguments = new List<string>
        {
            "msbuild",
            "-nologo",
            "-restore",
            "-t:ComputeRunArguments",
            "-getProperty:RunCommand,RunArguments,RunWorkingDirectory,_AspireResolvedCliInvocationMode,_AspireResolvedCliPath,_AspireResolvedDnxPath,_AspireResolvedDnxHostPath,_AspireResolvedDnxHostArguments,_AspireResolvedCliVersion,_AspireCliVersionSupportsRunHook,_AspireCliVersionCommand",
            project.ProjectFile
        };

        if (extraArguments is not null)
        {
            arguments.AddRange(extraArguments);
        }

        var result = await RunDotNetWithArgumentsAsync(project.ProjectDirectory, [.. arguments], environment);
        Assert.True(result.ExitCode == 0, result.Output);

        var jsonStart = result.StandardOutput.IndexOf('{');
        var jsonEnd = result.StandardOutput.LastIndexOf('}');
        Assert.True(jsonStart >= 0 && jsonEnd > jsonStart, result.Output);

        using var document = JsonDocument.Parse(result.StandardOutput[jsonStart..(jsonEnd + 1)]);
        var properties = document.RootElement.GetProperty("Properties");

        return properties.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    private static async Task<string> CreateFakeAspireCliAsync(string fakeCliDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return await CreateFakeAspireCommandShimAsync(fakeCliDirectory);
        }

        var aspirePath = Path.Combine(fakeCliDirectory, "aspire");
        await File.WriteAllTextAsync(aspirePath, """
            #!/bin/sh
            if [ "$1" = "--version" ]; then
                echo "13.5.0"
                exit 0
            fi
            printf '%s\n' "$@" > "$ASPIRE_TEST_CAPTURE_PATH"
            """);
        File.SetUnixFileMode(aspirePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return aspirePath;
    }

    private static async Task<string> CreateFakeAspireCommandShimAsync(string fakeCliDirectory, string extension = ".cmd")
    {
        var aspirePath = Path.Combine(fakeCliDirectory, $"aspire{extension}");

        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(aspirePath, """
                @echo off
                if "%~1"=="--version" (
                    echo 13.5.0
                    exit /b 0
                )
                type nul > "%ASPIRE_TEST_CAPTURE_PATH%"
                :loop
                if "%~1"=="" exit /b 0
                >> "%ASPIRE_TEST_CAPTURE_PATH%" echo %~1
                shift
                goto loop
                """);

            return aspirePath;
        }

        await File.WriteAllTextAsync(aspirePath, ("""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
                echo "13.5.0"
                exit 0
            fi
            printf '%s\n' "$@" > "$ASPIRE_TEST_CAPTURE_PATH"
            """).ReplaceLineEndings("\n"));
        File.SetUnixFileMode(aspirePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return aspirePath;
    }

    private static async Task<string> CreateFakeDnxAsync(string fakeCliDirectory)
    {
        var dnxPath = Path.Combine(fakeCliDirectory, OperatingSystem.IsWindows() ? "dnx.cmd" : "dnx");
        if (OperatingSystem.IsWindows())
        {
            CopyFakeCommandHost(fakeCliDirectory);
            var fakeSdkDirectory = Directory.CreateDirectory(Path.Combine(fakeCliDirectory, "sdk", AspireCliVersion));
            await File.WriteAllTextAsync(Path.Combine(fakeSdkDirectory.FullName, "dotnet.dll"), "");
        }

        await File.WriteAllTextAsync(dnxPath, OperatingSystem.IsWindows()
            ? """
                @echo off
                if "%~1"=="--yes" if "%~2"=="aspire.cli@13.5.0" if "%~3"=="--" if "%~4"=="--version" (
                    echo 13.5.0
                    exit /b 0
                )
                type nul > "%ASPIRE_TEST_CAPTURE_PATH%"
                :loop
                if "%~1"=="" exit /b 0
                >> "%ASPIRE_TEST_CAPTURE_PATH%" echo %~1
                shift
                goto loop
                """
            : ("""
                #!/bin/sh
                if [ "$1" = "--yes" ] && [ "$2" = "aspire.cli@13.5.0" ] && [ "$3" = "--" ] && [ "$4" = "--version" ]; then
                    echo "13.5.0"
                    exit 0
                fi
                printf '%s\n' "$@" > "$ASPIRE_TEST_CAPTURE_PATH"
                """).ReplaceLineEndings("\n"));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dnxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dnxPath;
    }

    private static async Task CreateFakeDnxThatFailsVersionAsync(string fakeCliDirectory)
    {
        var dnxPath = await CreateFakeDnxAsync(fakeCliDirectory);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(fakeCliDirectory, "fail-version"), "");
            return;
        }

        await File.WriteAllTextAsync(dnxPath, ("""
            #!/bin/sh
            exit 42
            """).ReplaceLineEndings("\n"));
        File.SetUnixFileMode(dnxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string CreateFakeNativeAspireCli(string fakeCliDirectory)
    {
        var fakeDotNetPath = CopyFakeCommandHost(fakeCliDirectory);
        var aspirePath = Path.Combine(fakeCliDirectory, "aspire.exe");
        File.Copy(fakeDotNetPath, aspirePath, overwrite: true);
        return aspirePath;
    }

    private static string CopyFakeCommandHost(string destinationDirectory)
    {
        var fakeCommandHostPath = GetAssemblyMetadataPath("FakeCommandHostPath");
        var sourceDirectory = Path.GetDirectoryName(fakeCommandHostPath)!;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "dotnet.*"))
        {
            File.Copy(sourcePath, Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)), overwrite: true);
        }

        return Path.Combine(destinationDirectory, Path.GetFileName(fakeCommandHostPath));
    }

    private static async Task<string> CreateFailingWorkingDirectoryShadowAsync(string workingDirectory, string command)
    {
        var captureFile = Path.Combine(workingDirectory, $"{command}-shadow-invoked.txt");
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(
                Path.Combine(workingDirectory, $"{command}.cmd"),
                $$"""
                @echo off
                type nul > "{{captureFile}}"
                exit /b 1
                """);
        }

        return captureFile;
    }

    private static async Task CreateFakeAspireCliWithVersionAsync(string fakeCliDirectory, string version)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(fakeCliDirectory, "aspire.cmd"), $$"""
                @echo off
                if "%~1"=="--version" (
                    echo {{version}}
                    exit /b 0
                )
                type nul > "%ASPIRE_TEST_CAPTURE_PATH%"
                :loop
                if "%~1"=="" exit /b 0
                >> "%ASPIRE_TEST_CAPTURE_PATH%" echo %~1
                shift
                goto loop
                """);

            return;
        }

        var aspirePath = Path.Combine(fakeCliDirectory, "aspire");
        await File.WriteAllTextAsync(aspirePath, ($$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
                echo "{{version}}"
                exit 0
            fi
            printf '%s\n' "$@" > "$ASPIRE_TEST_CAPTURE_PATH"
            """).ReplaceLineEndings("\n"));
        File.SetUnixFileMode(aspirePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task CreateFakeAspireCliWithVersionAndLargeStdErrAsync(string fakeCliDirectory, string version)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(fakeCliDirectory, "aspire.cmd"), $$"""
                @echo off
                if "%~1"=="--version" (
                    for /L %%i in (1,1,4096) do echo stderr-padding-abcdefghijklmnopqrstuvwxyz-0123456789 1>&2
                    echo {{version}}
                    exit /b 0
                )
                exit /b 0
                """);

            return;
        }

        var aspirePath = Path.Combine(fakeCliDirectory, "aspire");
        await File.WriteAllTextAsync(aspirePath, ($$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
                i=0
                while [ "$i" -lt 4096 ]; do
                    echo "stderr-padding-abcdefghijklmnopqrstuvwxyz-0123456789" >&2
                    i=$((i + 1))
                done
                echo "{{version}}"
                exit 0
            fi
            exit 0
            """).ReplaceLineEndings("\n"));
        File.SetUnixFileMode(aspirePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task CreateFakeAspireCliThatHangsOnVersionAsync(string fakeCliDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(fakeCliDirectory, "aspire.cmd"), """
                @echo off
                if "%~1"=="--version" (
                    :loop
                    ping -n 2 127.0.0.1 > nul
                    goto loop
                )
                exit /b 0
                """);

            return;
        }

        var aspirePath = Path.Combine(fakeCliDirectory, "aspire");
        await File.WriteAllTextAsync(aspirePath, ("""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
                while true; do
                    sleep 1
                done
            fi
            exit 0
            """).ReplaceLineEndings("\n"));
        File.SetUnixFileMode(aspirePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task CreateFakeAspireCliThatFailsVersionAsync(string fakeCliDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(fakeCliDirectory, "aspire.cmd"), """
                @echo off
                if "%~1"=="--version" (
                    exit /b 1
                )
                type nul > "%ASPIRE_TEST_CAPTURE_PATH%"
                :loop
                if "%~1"=="" exit /b 0
                >> "%ASPIRE_TEST_CAPTURE_PATH%" echo %~1
                shift
                goto loop
                """);

            return;
        }

        var aspirePath = Path.Combine(fakeCliDirectory, "aspire");
        await File.WriteAllTextAsync(aspirePath, ("""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
                exit 1
            fi
            printf '%s\n' "$@" > "$ASPIRE_TEST_CAPTURE_PATH"
            """).ReplaceLineEndings("\n"));
        File.SetUnixFileMode(aspirePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void AssertDashboardAndOrchestrationReferences(string[] packageReferences)
    {
        var dashboardReference = Assert.Single(packageReferences, static packageReference => packageReference.StartsWith("Aspire.Dashboard.Sdk.", StringComparison.Ordinal));
        var orchestrationReference = Assert.Single(packageReferences, static packageReference => packageReference.StartsWith("Aspire.Hosting.Orchestration.", StringComparison.Ordinal));

        var dashboardRid = GetPackageRid(dashboardReference, "Aspire.Dashboard.Sdk.");
        var orchestrationRid = GetPackageRid(orchestrationReference, "Aspire.Hosting.Orchestration.");

        Assert.Equal(dashboardRid, orchestrationRid);
        Assert.Contains(dashboardRid, s_supportedRids);
        Assert.Equal($"Aspire.Dashboard.Sdk.{dashboardRid}=13.4.0", dashboardReference);
        Assert.Equal($"Aspire.Hosting.Orchestration.{dashboardRid}=13.4.0", orchestrationReference);
    }

    private static string GetAspireRuntimeIdentifierToolPath()
    {
        // The path to the locally-built RID tool is baked into the test assembly via AssemblyMetadata
        // so the test can locate it regardless of the configuration the test was built with.
        var assembly = typeof(AppHostSdkTargetsTests).Assembly;
        var toolPath = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireRuntimeIdentifierToolPath", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(toolPath), "AspireRuntimeIdentifierToolPath assembly metadata is not set.");
        Assert.True(File.Exists(toolPath), $"Aspire.RuntimeIdentifier.Tool was not built at '{toolPath}'. Build the test project to produce it.");
        return toolPath!;
    }

    private static string GetAspireHostingTasksAssemblyPath()
    {
        var assembly = typeof(AppHostSdkTargetsTests).Assembly;
        var taskAssemblyPath = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireHostingTasksAssemblyPath", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(taskAssemblyPath), "AspireHostingTasksAssemblyPath assembly metadata is not set.");
        Assert.True(File.Exists(taskAssemblyPath), $"Aspire.Hosting.Tasks was not built at '{taskAssemblyPath}'. Build the test project to produce it.");
        return taskAssemblyPath!;
    }

    private static string GetAssemblyMetadataPath(string metadataName)
    {
        var path = typeof(AppHostSdkTargetsTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, metadataName, StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(path), $"{metadataName} assembly metadata is not set.");
        Assert.True(File.Exists(path), $"The file specified by {metadataName} was not built at '{path}'.");
        return path!;
    }

    private static string GetPackageRid(string packageReference, string prefix)
    {
        var equalsIndex = packageReference.IndexOf('=');
        Assert.True(equalsIndex > prefix.Length, $"Package reference '{packageReference}' did not contain a RID.");

        return packageReference[prefix.Length..equalsIndex];
    }

    private static string GetExpectedAspireRunCommand(string aspireCliPath)
        => OperatingSystem.IsWindows() && IsWindowsCommandShim(aspireCliPath) ? "cmd" : aspireCliPath;

    private static string GetExpectedDnxRunCommand(string dnxPath)
        => OperatingSystem.IsWindows() ? Path.Combine(Path.GetDirectoryName(dnxPath)!, "dotnet.exe") : dnxPath;

    private static string GetExpectedAspireRunArguments(string aspireCliPath, RunHookProject project, string? extraArguments = null)
        => GetExpectedAspireRunArguments(aspireCliPath, project.ProjectFile, extraArguments);

    private static string GetExpectedAspireRunArguments(string aspireCliPath, string projectPath, string? extraArguments = null)
    {
        var prefix = OperatingSystem.IsWindows() && IsWindowsCommandShim(aspireCliPath)
            ? $"/D /V:OFF /C {Regex.Replace(aspireCliPath, """[ \t()[\]{}!^`<>&|;,+="~@]""", "^$0")} "
            : string.Empty;
        var arguments = $"{prefix}run --project \"{projectPath}\" --no-build --";

        return string.IsNullOrEmpty(extraArguments) ? arguments : $"{arguments} {extraArguments}";
    }

    private static string GetExpectedExplicitAspireRunArguments(RunHookProject project, string? extraArguments = null)
    {
        var arguments = $"run --project \"{project.ProjectFile}\" --no-build --";

        return string.IsNullOrEmpty(extraArguments) ? arguments : $"{arguments} {extraArguments}";
    }

    private static string GetExpectedDnxRunArguments(string dnxPath, RunHookProject project, string? extraArguments = null)
    {
        var prefix = OperatingSystem.IsWindows()
            ? $"exec \"{Path.Combine(Path.GetDirectoryName(dnxPath)!, "sdk", AspireCliVersion, "dotnet.dll")}\" dnx "
            : string.Empty;
        var arguments = $"{prefix}--yes aspire.cli@{AspireCliVersion} -- run --project \"{project.ProjectFile}\" --no-build --";

        return string.IsNullOrEmpty(extraArguments) ? arguments : $"{arguments} {extraArguments}";
    }

    private static bool IsWindowsCommandShim(string path)
        => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string GetExpectedWindowsCommandShimRunArguments(RunHookProject project, string cliPath, string? extraArguments = null)
    {
        var escapedCliPath = Regex.Replace(cliPath, """[ \t()[\]{}!^`<>&|;,+="~@]""", "^$0");
        var arguments = $"/D /V:OFF /C {escapedCliPath} run --project \"{project.ProjectFile}\" --no-build --";

        return string.IsNullOrEmpty(extraArguments) ? arguments : $"{arguments} {extraArguments}";
    }

    private static void AssertUsesExplicitAspireCli(
        Dictionary<string, string> properties,
        RunHookProject project,
        string cliPath,
        string? expectedArguments = null)
    {
        if (OperatingSystem.IsWindows()
            && (cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal("cmd", properties["RunCommand"]);
            Assert.Equal(GetExpectedWindowsCommandShimRunArguments(project, cliPath, expectedArguments), properties["RunArguments"]);
            return;
        }

        Assert.Equal(cliPath, properties["RunCommand"]);
        Assert.Equal(GetExpectedExplicitAspireRunArguments(project, expectedArguments), properties["RunArguments"]);
    }

    private static void AssertUsesDotNetRun(Dictionary<string, string> properties, RunHookProject project, string expectedArguments = "")
    {
        Assert.Equal(GetExpectedDotNetRunCommand(project), properties["RunCommand"]);
        Assert.Equal(expectedArguments, properties["RunArguments"]);
        Assert.Equal(project.ProjectDirectory, properties["RunWorkingDirectory"]);
    }

    private static string GetExpectedDotNetRunCommand(RunHookProject project)
    {
        var executableName = OperatingSystem.IsWindows() ? "AppHost.exe" : "AppHost";

        return Path.Combine(project.ProjectDirectory, "bin", "Debug", "net8.0", executableName);
    }

    private static string GetPathEnvironmentVariableName() => OperatingSystem.IsWindows() ? "Path" : "PATH";

    private static Dictionary<string, string> CreatePathEnvironment(string directory, bool includeCurrentPath = true)
    {
        var pathEnvironmentVariable = GetPathEnvironmentVariableName();
        var path = includeCurrentPath
            ? $"{directory}{Path.PathSeparator}{Environment.GetEnvironmentVariable(pathEnvironmentVariable)}"
            : directory;

        return new Dictionary<string, string>
        {
            [pathEnvironmentVariable] = path
        };
    }

    private static string CreatePathWithoutAspire(string firstDirectory)
        => CreatePathWithoutCommand(firstDirectory, "aspire");

    private static string CreatePathWithoutDnx(string firstDirectory)
        => CreatePathWithoutCommand(firstDirectory, "dnx");

    private static string CreatePathWithoutCommand(string firstDirectory, string commandName)
    {
        var pathEnvironmentVariable = GetPathEnvironmentVariableName();
        var currentPath = Environment.GetEnvironmentVariable(pathEnvironmentVariable) ?? string.Empty;
        var pathDirectories = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(directory => !ContainsCommand(directory, commandName));

        return string.Join(Path.PathSeparator, pathDirectories.Prepend(firstDirectory));
    }

    private static bool ContainsCommand(string directory, string commandName)
    {
        var executableNames = OperatingSystem.IsWindows()
            ? new[] { $"{commandName}.exe", $"{commandName}.cmd", $"{commandName}.bat", commandName }
            : [commandName];

        return executableNames.Any(executableName => File.Exists(Path.Combine(directory.Trim().Trim('"'), executableName)));
    }

    private static async Task<(int ExitCode, string Output)> RunDotNetAsync(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        });

        Assert.NotNull(process);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {arguments} timed out after 3 minutes.");
        }

        var output = await outputTask;
        var error = await errorTask;

        return (process.ExitCode, output + error);
    }

    private static async Task<DotNetResult> RunDotNetWithArgumentsAsync(string workingDirectory, string[] arguments, IDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        startInfo.Environment["MSBUILDTERMINALLOGGER"] = "false";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo);

        Assert.NotNull(process);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} timed out after 3 minutes.");
        }

        return new DotNetResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string GetRepoRoot()
    {
        var directory = AppContext.BaseDirectory;

        while (directory is not null && !Directory.Exists(Path.Combine(directory, ".git")) && !File.Exists(Path.Combine(directory, ".git")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed record RunHookProject(string ProjectDirectory, string ProjectFile);

    private sealed record DotNetResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Output => StandardOutput + StandardError;
    }
}