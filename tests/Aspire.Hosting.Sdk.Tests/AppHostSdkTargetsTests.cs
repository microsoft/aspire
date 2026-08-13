// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aspire.Hosting.Sdk.Tests;

public class AppHostSdkTargetsTests(ITestOutputHelper outputHelper)
{
    private const string AspireCliVersion = "13.5.0";
    private const string HangingCommandPidEnvironmentVariable = "ASPIRE_TEST_HANG_PID_PATH";
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
    [InlineData("DnxPinned")]
    [InlineData("dNxPiNnEd")]
    public async Task ComputeRunArgumentsUsesPinnedDnxAspireCliWhenSelected(string? invocationMode)
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

    [Theory]
    [InlineData("Dnx")]
    [InlineData("dNx")]
    public async Task ComputeRunArgumentsUsesManifestAwareDnxAspireCliWhenSelected(string invocationMode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        var dnxPath = await CreateFakeDnxAsync(fakeCliDirectory.FullName);
        await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true,
            $$"""
              <PropertyGroup>
                <AspireCliInvocationMode>{{invocationMode}}</AspireCliInvocationMode>
              </PropertyGroup>
            """);

        var properties = await GetComputeRunArgumentsPropertiesAsync(
            project,
            ["-p:RunArguments=--custom foo"],
            CreatePathEnvironment(fakeCliDirectory.FullName));

        Assert.Equal("Dnx", properties["_AspireResolvedCliInvocationMode"]);
        Assert.Equal("13.5.0", properties["_AspireResolvedCliVersion"]);
        Assert.Equal("true", properties["_AspireCliVersionSupportsRunHook"]);
        Assert.Equal(GetExpectedDnxRunCommand(dnxPath), properties["RunCommand"]);
        Assert.Equal(GetExpectedDnxRunArguments(dnxPath, project, "--custom foo", pinned: false), properties["RunArguments"]);
        Assert.Contains("--yes aspire.cli -- --version", properties["_AspireCliVersionCommand"]);
        Assert.Equal(project.ProjectDirectory, properties["RunWorkingDirectory"]);
    }

    [Theory]
    [InlineData("Dnx")]
    [InlineData("DnxPinned")]
    public async Task ComputeRunArgumentsFailsWhenDnxModeIsConfiguredAndDnxIsMissing(string invocationMode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var emptyPathDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "empty-path"));
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true,
            $$"""
              <PropertyGroup>
                <AspireCliInvocationMode>{{invocationMode}}</AspireCliInvocationMode>
              </PropertyGroup>
            """);

        var result = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["msbuild", "-nologo", "-restore", "-t:ComputeRunArguments", project.ProjectFile],
            CreatePathEnvironment(emptyPathDirectory.FullName, includeCurrentPath: false));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ASPIRE011", result.Output);
        Assert.Contains($"AspireCliInvocationMode={invocationMode}", result.Output);
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

    [Theory]
    [InlineData("Dnx")]
    [InlineData("DnxPinned")]
    public async Task ComputeRunArgumentsUsesConfiguredAspireCliPathWhenDnxModeIsConfigured(string invocationMode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var emptyPathDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "empty-path"));
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true,
            $$"""
              <PropertyGroup>
                <AspireCliInvocationMode>{{invocationMode}}</AspireCliInvocationMode>
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
        var environment = new Dictionary<string, string>
        {
            [GetPathEnvironmentVariableName()] = CreatePathWithoutAspire(fakeCliDirectory.FullName)
        };
        var aspireHome = Path.Combine(workspace.Path, "aspire-home");
        environment["ASPIRE_HOME"] = aspireHome;

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
        AssertCliBundleExists(aspireHome, buildResult.Output);
    }

    [Fact]
    public async Task ResolveAspireCliBundlePathsUsesDnxBundleWhenDnxModeIsSelected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var staleCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "stale-cli"));
        await CreateFakeAspireCliWithVersionAsync(staleCliDirectory.FullName, AspireCliVersion);
        var staleBundle = CreateFakeCliBundle(staleCliDirectory.FullName);
        var dnxDirectoryName = OperatingSystem.IsWindows()
            ? "dnx"
            : "dnx $HOME `aspire-test-command`";
        var dnxDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, dnxDirectoryName));
        await CreateFakeDnxAsync(dnxDirectory.FullName);
        var aspireHomeDirectoryName = OperatingSystem.IsWindows()
            ? "aspire-home"
            : "aspire-home $HOME `aspire-test-command`";
        var aspireHome = Path.Combine(workspace.Path, aspireHomeDirectoryName);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true,
            """
              <PropertyGroup>
                <AspireCliInvocationMode>Dnx</AspireCliInvocationMode>
              </PropertyGroup>
            """,
            includeBundlePaths: false);
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_HOME"] = aspireHome,
            [GetPathEnvironmentVariableName()] = CreatePathWithoutAspire(staleCliDirectory.FullName, dnxDirectory.FullName)
        };

        // Explicit Dnx mode can use an unversioned package reference so a tool manifest can select
        // the CLI version. Force that shape here because the fake setup must support both forms.
        var properties = await GetResolveAspireCliBundlePathPropertiesAsync(
            project,
            environment,
            ["-p:_AspireCliDnxPackageReference=aspire.cli"]);
        var dnxBundle = GetFakeCliBundlePaths(aspireHome);

        Assert.Equal("Dnx", properties["_AspireResolvedCliInvocationMode"]);
        Assert.Equal(dnxBundle.DcpDirectory, Path.TrimEndingDirectorySeparator(properties["DcpDir"]));
        Assert.Equal(dnxBundle.ManagedDirectory, Path.TrimEndingDirectorySeparator(properties["AspireDashboardDir"]));
        Assert.Equal(dnxBundle.ManagedPath, properties["AspireDashboardPath"]);
        Assert.NotEqual(staleBundle.DcpDirectory, Path.TrimEndingDirectorySeparator(properties["DcpDir"]));
        AssertCliBundleExists(aspireHome, JsonSerializer.Serialize(properties));
    }

    [Fact]
    public async Task ResolveAspireCliBundlePathsPreservesUnixShellMetacharactersInAspireCliPath()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This test validates Unix process argument handling.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var fakeCliDirectory = Directory.CreateDirectory(
            Path.Combine(workspace.Path, "cli $HOME `aspire-test-command`"));
        var aspireCliPath = await CreateFakeAspireCliThatSetsUpBundleAsync(fakeCliDirectory.FullName);
        var aspireHome = Path.Combine(workspace.Path, "empty-aspire-home");
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, includeBundlePaths: false);
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_HOME"] = aspireHome,
            ["AspireCliPath"] = aspireCliPath
        };

        var properties = await GetResolveAspireCliBundlePathPropertiesAsync(project, environment, ["-warnaserror"]);
        var selectedBundle = GetFakeCliBundlePaths(fakeCliDirectory.FullName);

        Assert.Equal(aspireCliPath, properties["_AspireResolvedCliPath"]);
        Assert.Equal(selectedBundle.DcpDirectory, Path.TrimEndingDirectorySeparator(properties["DcpDir"]));
        Assert.Equal(selectedBundle.ManagedDirectory, Path.TrimEndingDirectorySeparator(properties["AspireDashboardDir"]));
        Assert.Equal(selectedBundle.ManagedPath, properties["AspireDashboardPath"]);
        AssertCliBundleExists(fakeCliDirectory.FullName, JsonSerializer.Serialize(properties));
    }

    [Fact]
    public async Task ResolveAspireCliBundlePathsUsesSelectedAspireCliWhenLaterPathCandidateHasBundleWithWarningsAsErrors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var selectedCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "selected-cli"));
        var selectedCliPath = await CreateFakeAspireCliThatSetsUpBundleAsync(selectedCliDirectory.FullName);
        var staleCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "stale-cli"));
        await CreateFakeAspireCliWithVersionAsync(staleCliDirectory.FullName, AspireCliVersion);
        var staleBundle = CreateFakeCliBundle(staleCliDirectory.FullName);
        var aspireHome = Path.Combine(workspace.Path, "aspire-home");
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, includeBundlePaths: false);
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_HOME"] = aspireHome,
            [GetPathEnvironmentVariableName()] = CreatePathWithoutAspire(selectedCliDirectory.FullName, staleCliDirectory.FullName)
        };

        var properties = await GetResolveAspireCliBundlePathPropertiesAsync(project, environment, ["-warnaserror"]);
        var selectedBundle = GetFakeCliBundlePaths(selectedCliDirectory.FullName);

        Assert.Equal("Aspire", properties["_AspireResolvedCliInvocationMode"]);
        Assert.Equal(selectedCliPath, properties["_AspireResolvedCliPath"]);
        Assert.Equal(selectedBundle.DcpDirectory, Path.TrimEndingDirectorySeparator(properties["DcpDir"]));
        Assert.Equal(selectedBundle.ManagedDirectory, Path.TrimEndingDirectorySeparator(properties["AspireDashboardDir"]));
        Assert.Equal(selectedBundle.ManagedPath, properties["AspireDashboardPath"]);
        Assert.NotEqual(staleBundle.DcpDirectory, Path.TrimEndingDirectorySeparator(properties["DcpDir"]));
        AssertCliBundleExists(selectedCliDirectory.FullName, JsonSerializer.Serialize(properties));
    }

    [Fact]
    public async Task ResolveAspireCliBundlePathsEscapesPercentSignsInNativeAspireCliPathOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates native Windows executable setup.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "native%ASPIRE_TEST_LITERAL%"));
        var aspireCliPath = CreateFakeNativeAspireCli(fakeCliDirectory.FullName);
        var aspireHome = Path.Combine(workspace.Path, "aspire-home");
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, includeBundlePaths: false);
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_HOME"] = aspireHome,
            ["ASPIRE_TEST_LITERAL"] = "expanded",
            ["AspireCliPath"] = aspireCliPath
        };

        var properties = await GetResolveAspireCliBundlePathPropertiesAsync(project, environment, ["-warnaserror"]);
        var selectedBundle = GetFakeCliBundlePaths(fakeCliDirectory.FullName);

        Assert.Equal(selectedBundle.DcpDirectory, Path.TrimEndingDirectorySeparator(properties["DcpDir"]));
        Assert.Equal(selectedBundle.ManagedDirectory, Path.TrimEndingDirectorySeparator(properties["AspireDashboardDir"]));
        Assert.Equal(selectedBundle.ManagedPath, properties["AspireDashboardPath"]);
        AssertCliBundleExists(fakeCliDirectory.FullName, JsonSerializer.Serialize(properties));
        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "nativeexpanded")));
    }

    [Fact]
    public async Task BuildFallsBackToDnxWhenPathAspireCliSetupTimesOut()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, includeBundlePaths: false);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        await CreateFakeAspireCliThatHangsOnSetupAsync(fakeCliDirectory.FullName);
        await CreateFakeDnxAsync(fakeCliDirectory.FullName);
        var aspireHome = Path.Combine(workspace.Path, "aspire-home");
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_HOME"] = aspireHome,
            [GetPathEnvironmentVariableName()] = CreatePathWithoutAspire(fakeCliDirectory.FullName)
        };

        var buildResult = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["build", "-nologo", project.ProjectFile, "-p:_AspireCliBundleSetupTimeout=100", "-warnaserror"],
            environment);

        Assert.True(buildResult.ExitCode == 0, buildResult.Output);
        AssertCliBundleExists(aspireHome, buildResult.Output);
    }

    [Fact]
    public async Task RunAspireCliCommandKillsCommandShimProcessTreeOnTimeoutInFullFrameworkMsBuild()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates the net472 task under full-framework MSBuild.");

        var msbuildPath = await FindFullFrameworkMSBuildAsync();
        Assert.SkipUnless(msbuildPath is not null, "Full-framework MSBuild could not be found with vswhere.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var fakeCommandDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-command"));
        CopyFakeCommandHost(fakeCommandDirectory.FullName);
        var commandShimPath = Path.Combine(fakeCommandDirectory.FullName, "aspire.cmd");
        await File.WriteAllTextAsync(commandShimPath, """
            @echo off
            "%~dp0dotnet.exe" --hang
            """);

        var childPidPath = Path.Combine(workspace.Path, "child.pid");
        var taskAssemblyPath = GetAssemblyMetadataPath("AspireHostingTasksNetFrameworkAssemblyPath");
        var projectPath = Path.Combine(workspace.Path, "RunAspireCliCommand.proj");
        await File.WriteAllTextAsync(projectPath, $$"""
            <Project>
              <UsingTask TaskName="RunAspireCliCommand" AssemblyFile="{{SecurityElement.Escape(taskAssemblyPath)}}" />

              <Target Name="Test">
                <RunAspireCliCommand FileName="{{SecurityElement.Escape(commandShimPath)}}" TimeoutMilliseconds="5000">
                  <Output TaskParameter="TimedOut" PropertyName="CommandTimedOut" />
                  <Output TaskParameter="FailureMessage" PropertyName="CommandFailureMessage" />
                </RunAspireCliCommand>
                <Error Condition="'$(CommandTimedOut)' != 'true'" Text="The command did not report a timeout." />
                <Error Condition="'$(CommandFailureMessage)' == ''" Text="The command did not report a timeout failure." />
                <Message Text="CommandTimedOut=$(CommandTimedOut)" Importance="High" />
              </Target>
            </Project>
            """);

        int? childProcessId = null;
        try
        {
            var result = await RunProcessWithArgumentsAsync(
                msbuildPath!,
                workspace.Path,
                [projectPath, "-nologo", "-t:Test"],
                new Dictionary<string, string>
                {
                    [HangingCommandPidEnvironmentVariable] = childPidPath
                },
                TimeSpan.FromSeconds(30));

            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Contains("CommandTimedOut=true", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(childPidPath), $"The child process did not record its PID. MSBuild output:{Environment.NewLine}{result.Output}");

            childProcessId = int.Parse(await File.ReadAllTextAsync(childPidPath), CultureInfo.InvariantCulture);
            using var survivingChild = TryGetProcessById(childProcessId.Value);
            Assert.Null(survivingChild);
            childProcessId = null;
        }
        finally
        {
            if (childProcessId is { } processId)
            {
                using var childProcess = TryGetProcessById(processId);
                if (childProcess is not null && !childProcess.HasExited)
                {
                    childProcess.Kill(entireProcessTree: true);
                }
            }
        }
    }

    [Fact]
    public async Task BuildUsesEnvironmentAspireHomeWhenMsBuildPropertyDiffers()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var project = await CreateRunHookProjectAsync(workspace.Path, aspireUseCliBundle: true, includeBundlePaths: false);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "fake-cli"));
        await CreateFakeDnxAsync(fakeCliDirectory.FullName);
        var environmentAspireHome = Path.Combine(workspace.Path, "environment-aspire-home");
        var propertyAspireHome = Path.Combine(workspace.Path, "property-aspire-home");
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_HOME"] = environmentAspireHome,
            [GetPathEnvironmentVariableName()] = CreatePathWithoutAspire(fakeCliDirectory.FullName)
        };

        var buildResult = await RunDotNetWithArgumentsAsync(
            project.ProjectDirectory,
            ["build", "-nologo", project.ProjectFile, $"-p:ASPIRE_HOME={propertyAspireHome}"],
            environment);

        Assert.True(buildResult.ExitCode == 0, buildResult.Output);
        AssertCliBundleExists(environmentAspireHome, buildResult.Output);
        Assert.False(Directory.Exists(Path.Combine(propertyAspireHome, "bundle")));
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
        return await GetTargetPropertiesAsync(
            project,
            "ComputeRunArguments",
            "RunCommand,RunArguments,RunWorkingDirectory,_AspireResolvedCliInvocationMode,_AspireResolvedCliPath,_AspireResolvedDnxPath,_AspireResolvedDnxHostPath,_AspireResolvedDnxHostArguments,_AspireResolvedCliVersion,_AspireCliVersionSupportsRunHook,_AspireCliVersionCommand",
            extraArguments,
            environment);
    }

    private static async Task<Dictionary<string, string>> GetResolveAspireCliBundlePathPropertiesAsync(
        RunHookProject project,
        IDictionary<string, string> environment,
        string[]? extraArguments = null)
    {
        return await GetTargetPropertiesAsync(
            project,
            "ResolveAspireCliBundlePaths",
            "DcpDir,AspireDashboardDir,AspireDashboardPath,_AspireResolvedCliInvocationMode,_AspireResolvedCliPath",
            extraArguments,
            environment: environment);
    }

    private static async Task<Dictionary<string, string>> GetTargetPropertiesAsync(
        RunHookProject project,
        string target,
        string propertyNames,
        string[]? extraArguments,
        IDictionary<string, string>? environment)
    {
        var arguments = new List<string>
        {
            "msbuild",
            "-nologo",
            "-restore",
            $"-t:{target}",
            $"-getProperty:{propertyNames}",
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

    private static async Task<string> CreateFakeAspireCliThatSetsUpBundleAsync(string fakeCliDirectory)
    {
        var aspirePath = Path.Combine(fakeCliDirectory, OperatingSystem.IsWindows() ? "aspire.cmd" : "aspire");
        var contents = OperatingSystem.IsWindows()
            ? """
                @echo off
                if not "%~1"=="setup" exit /b 2
                mkdir "%~dp0bundle\dcp"
                mkdir "%~dp0bundle\managed"
                type nul > "%~dp0bundle\dcp\dcp.exe"
                type nul > "%~dp0bundle\managed\aspire-managed.exe"
                """
            : """
                #!/bin/sh
                if [ "$1" != "setup" ]; then
                    exit 2
                fi
                install_path="$(dirname "$0")"
                mkdir -p "$install_path/bundle/dcp" "$install_path/bundle/managed"
                : > "$install_path/bundle/dcp/dcp"
                : > "$install_path/bundle/managed/aspire-managed"
                """;

        await File.WriteAllTextAsync(aspirePath, contents.ReplaceLineEndings(OperatingSystem.IsWindows() ? "\r\n" : "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(aspirePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return aspirePath;
    }

    private static (string DcpDirectory, string ManagedDirectory, string ManagedPath) CreateFakeCliBundle(string layoutRoot)
    {
        var bundle = GetFakeCliBundlePaths(layoutRoot);
        Directory.CreateDirectory(bundle.DcpDirectory);
        Directory.CreateDirectory(bundle.ManagedDirectory);
        File.WriteAllText(Path.Combine(bundle.DcpDirectory, OperatingSystem.IsWindows() ? "dcp.exe" : "dcp"), "");
        File.WriteAllText(bundle.ManagedPath, "");
        return bundle;
    }

    private static (string DcpDirectory, string ManagedDirectory, string ManagedPath) GetFakeCliBundlePaths(string layoutRoot)
    {
        var bundleRoot = Path.Combine(layoutRoot, "bundle");
        var dcpDirectory = Path.Combine(bundleRoot, "dcp");
        var managedDirectory = Path.Combine(bundleRoot, "managed");
        var managedPath = Path.Combine(managedDirectory, OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed");
        return (dcpDirectory, managedDirectory, managedPath);
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
                if "%~1"=="--yes" if "%~2"=="aspire.cli" if "%~3"=="--" if "%~4"=="--version" (
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
                if [ "$1" = "--yes" ] && { [ "$2" = "aspire.cli@13.5.0" ] || [ "$2" = "aspire.cli" ]; } && [ "$3" = "--" ] && [ "$4" = "--version" ]; then
                    echo "13.5.0"
                    exit 0
                fi
                if [ "$1" = "--yes" ] && { [ "$2" = "aspire.cli@13.5.0" ] || [ "$2" = "aspire.cli" ]; } && [ "$3" = "--" ] && [ "$4" = "setup" ] && [ "$5" = "--install-path" ]; then
                    mkdir -p "$6/bundle/dcp" "$6/bundle/managed"
                    : > "$6/bundle/dcp/dcp"
                    : > "$6/bundle/managed/aspire-managed"
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
                exit /b 42
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
            exit 42
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

    private static async Task CreateFakeAspireCliThatHangsOnSetupAsync(string fakeCliDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(fakeCliDirectory, "aspire.cmd"), """
                @echo off
                if "%~1"=="--version" (
                    echo 13.5.0
                    exit /b 0
                )
                if "%~1"=="setup" (
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
                echo "13.5.0"
                exit 0
            fi
            if [ "$1" = "setup" ]; then
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

    private static async Task<string?> FindFullFrameworkMSBuildAsync()
    {
        var vswherePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");
        if (!File.Exists(vswherePath))
        {
            return null;
        }

        var result = await RunProcessWithArgumentsAsync(
            vswherePath,
            Path.GetDirectoryName(vswherePath)!,
            ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"],
            environment: null,
            TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
        {
            return null;
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(File.Exists);
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

    private static string GetExpectedDnxRunArguments(string dnxPath, RunHookProject project, string? extraArguments = null, bool pinned = true)
    {
        var prefix = OperatingSystem.IsWindows()
            ? $"exec \"{Path.Combine(Path.GetDirectoryName(dnxPath)!, "sdk", AspireCliVersion, "dotnet.dll")}\" dnx "
            : string.Empty;
        var packageReference = pinned ? $"aspire.cli@{AspireCliVersion}" : "aspire.cli";
        var arguments = $"{prefix}--yes {packageReference} -- run --project \"{project.ProjectFile}\" --no-build --";

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

    private static void AssertCliBundleExists(string aspireHome, string output)
    {
        Assert.True(
            File.Exists(Path.Combine(aspireHome, "bundle", "dcp", OperatingSystem.IsWindows() ? "dcp.exe" : "dcp")),
            output);
        Assert.True(
            File.Exists(Path.Combine(aspireHome, "bundle", "managed", OperatingSystem.IsWindows() ? "aspire-managed.exe" : "aspire-managed")),
            output);
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

    private static string CreatePathWithoutAspire(params string[] firstDirectories)
        => CreatePathWithoutCommand(firstDirectories, "aspire");

    private static string CreatePathWithoutDnx(params string[] firstDirectories)
        => CreatePathWithoutCommand(firstDirectories, "dnx");

    private static string CreatePathWithoutCommand(string[] firstDirectories, string commandName)
    {
        var pathEnvironmentVariable = GetPathEnvironmentVariableName();
        var currentPath = Environment.GetEnvironmentVariable(pathEnvironmentVariable) ?? string.Empty;
        var pathDirectories = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(directory => !ContainsCommand(directory, commandName));

        return string.Join(Path.PathSeparator, firstDirectories.Concat(pathDirectories));
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

    private static Task<DotNetResult> RunDotNetWithArgumentsAsync(string workingDirectory, string[] arguments, IDictionary<string, string>? environment = null)
        => RunProcessWithArgumentsAsync("dotnet", workingDirectory, arguments, environment, TimeSpan.FromMinutes(3));

    private static async Task<DotNetResult> RunProcessWithArgumentsAsync(
        string fileName,
        string workingDirectory,
        string[] arguments,
        IDictionary<string, string>? environment,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
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

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', arguments)} timed out after {timeout}.");
        }

        return new DotNetResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static Process? TryGetProcessById(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
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