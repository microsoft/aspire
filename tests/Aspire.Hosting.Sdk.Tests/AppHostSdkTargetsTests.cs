// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.Sdk.Tests;

public class AppHostSdkTargetsTests
{
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
    public async Task AddReferenceToDashboardAndDcpUsesSdkRidSelectionTask()
    {
        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(extraProjectXml: null);

        Assert.Contains("UseSdkPickBestRid=true", packageReferences);
        Assert.Contains("RunRidToolFallback=false", packageReferences);
        AssertDashboardAndOrchestrationReferences(packageReferences);
    }

    [Fact]
    public async Task AddReferenceToDashboardAndDcpFallsBackToRuntimeIdentifierToolForOlderSdks()
    {
        // Force the pre-.NET 10 code path by disabling the in-proc PickBestRid task and pointing the
        // Exec call at the locally-built Aspire.RuntimeIdentifier.Tool assembly (which is normally
        // resolved out of the packed SDK's tools folder).
        var ridToolPath = GetAspireRuntimeIdentifierToolPath();

        var extraProjectXml = $"""
              <PropertyGroup>
                <_AspireUseSdkPickBestRid>false</_AspireUseSdkPickBestRid>
                <AspireRidToolExecutable>{SecurityElement.Escape(ridToolPath)}</AspireRidToolExecutable>
              </PropertyGroup>
            """;

        var packageReferences = await RunAddReferenceToDashboardAndDcpAsync(extraProjectXml);

        Assert.Contains("UseSdkPickBestRid=false", packageReferences);
        Assert.Contains("RunRidToolFallback=true", packageReferences);
        AssertDashboardAndOrchestrationReferences(packageReferences);
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesAspireCliWhenCliBundleIsEnabled()
    {
        using var tempDirectory = new TestTempDirectory();
        var project = await CreateRunHookProjectAsync(tempDirectory.Path, aspireUseCliBundle: true);

        var properties = await GetComputeRunArgumentsPropertiesAsync(project, ["-p:RunArguments=--custom foo"]);

        Assert.Equal(GetExpectedAspireRunCommand(), properties["RunCommand"]);
        Assert.Equal(GetExpectedAspireRunArguments(project, "--custom foo"), properties["RunArguments"]);
        Assert.Equal(project.ProjectDirectory, properties["RunWorkingDirectory"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesConfiguredAspireCliPathWhenCliBundleIsEnabled()
    {
        using var tempDirectory = new TestTempDirectory();
        var project = await CreateRunHookProjectAsync(tempDirectory.Path, aspireUseCliBundle: true);
        var aspireCliPath = Path.Combine(tempDirectory.Path, "custom-aspire");

        var properties = await GetComputeRunArgumentsPropertiesAsync(project, [$"-p:AspireCliPath={aspireCliPath}"]);

        Assert.Equal(aspireCliPath, properties["RunCommand"]);
        Assert.Equal($"run --project \"{project.ProjectFile}\" --no-build --", properties["RunArguments"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsUsesFileBasedAppHostPathWhenCliBundleIsEnabled()
    {
        using var tempDirectory = new TestTempDirectory();
        var appHostDirectory = Path.Combine(tempDirectory.Path, "FileApp");
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
        var project = await CreateRunHookProjectAsync(tempDirectory.Path, aspireUseCliBundle: true, extraProjectXml: extraProjectXml);

        var properties = await GetComputeRunArgumentsPropertiesAsync(project, ["-p:RunArguments=--custom foo"]);

        Assert.Equal(GetExpectedAspireRunCommand(), properties["RunCommand"]);
        Assert.Equal(GetExpectedAspireRunArguments(appHostFile, "--custom foo"), properties["RunArguments"]);
        Assert.Equal(appHostDirectory, properties["RunWorkingDirectory"]);
    }

    [Fact]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenCliBundleIsDisabled()
    {
        using var tempDirectory = new TestTempDirectory();
        var project = await CreateRunHookProjectAsync(tempDirectory.Path, aspireUseCliBundle: false);

        var properties = await GetComputeRunArgumentsPropertiesAsync(project);

        Assert.NotEqual(GetExpectedAspireRunCommand(), properties["RunCommand"]);
        Assert.NotEqual(GetExpectedAspireRunArguments(project), properties["RunArguments"]);
    }

    [Theory]
    [InlineData(SuppressCliRunHookEnvironmentVariable, "true")]
    [InlineData(SuppressCliRunHookEnvironmentVariable, "1")]
    [InlineData("_AspireSuppressCliRunHook", "true")]
    [InlineData("_AspireSuppressCliRunHook", "1")]
    public async Task ComputeRunArgumentsDoesNotUseAspireCliWhenHookIsSuppressed(string suppressionPropertyName, string suppressionValue)
    {
        using var tempDirectory = new TestTempDirectory();
        var project = await CreateRunHookProjectAsync(tempDirectory.Path, aspireUseCliBundle: true);

        Dictionary<string, string>? environment = suppressionPropertyName == SuppressCliRunHookEnvironmentVariable
            ? new Dictionary<string, string> { [SuppressCliRunHookEnvironmentVariable] = suppressionValue }
            : null;
        var extraArguments = suppressionPropertyName == "_AspireSuppressCliRunHook"
            ? new[] { $"-p:_AspireSuppressCliRunHook={suppressionValue}" }
            : [];

        var properties = await GetComputeRunArgumentsPropertiesAsync(project, extraArguments, environment);

        Assert.NotEqual(GetExpectedAspireRunCommand(), properties["RunCommand"]);
        Assert.NotEqual(GetExpectedAspireRunArguments(project), properties["RunArguments"]);
    }

    [Fact]
    public async Task DotNetRunUsesAspireCliWhenCliBundleIsEnabled()
    {
        using var tempDirectory = new TestTempDirectory();
        var project = await CreateRunHookProjectAsync(tempDirectory.Path, aspireUseCliBundle: true);
        var fakeCliDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "fake-cli"));
        var captureFile = Path.Combine(tempDirectory.Path, "aspire-args.txt");
        await CreateFakeAspireCliAsync(fakeCliDirectory.FullName);

        var pathEnvironmentVariable = GetPathEnvironmentVariableName();
        var environment = new Dictionary<string, string>
        {
            ["ASPIRE_TEST_CAPTURE_PATH"] = captureFile,
            [pathEnvironmentVariable] = $"{fakeCliDirectory.FullName}{Path.PathSeparator}{Environment.GetEnvironmentVariable(pathEnvironmentVariable)}"
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
    }

    private static async Task<string[]> RunAddReferenceToDashboardAndDcpAsync(string? extraProjectXml)
    {
        var repoRoot = GetRepoRoot();
        using var tempDirectory = new TestTempDirectory();

        var projectDirectory = Path.Combine(tempDirectory.Path, "AppHost");
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

    private static async Task<RunHookProject> CreateRunHookProjectAsync(string tempDirectory, bool aspireUseCliBundle, string? extraProjectXml = null)
    {
        var repoRoot = GetRepoRoot();
        var projectDirectory = Path.Combine(tempDirectory, "AppHost");
        Directory.CreateDirectory(projectDirectory);

        var appHostTargetsPath = SecurityElement.Escape(Path.Combine(repoRoot, "src", "Aspire.Hosting.AppHost", "build", "Aspire.Hosting.AppHost.in.targets"));
        var projectFile = Path.Combine(projectDirectory, "AppHost.csproj");

        await File.WriteAllTextAsync(projectFile,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <IsAspireHost>true</IsAspireHost>
                <AspireHostingSDKVersion>9.0.0</AspireHostingSDKVersion>
                <AspireUseCliBundle>{{aspireUseCliBundle.ToString().ToLowerInvariant()}}</AspireUseCliBundle>
                <AspireDashboardPath>$(MSBuildProjectDirectory)/Aspire.Dashboard.dll</AspireDashboardPath>
                <DcpDir>$(MSBuildProjectDirectory)</DcpDir>
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
            "-getProperty:RunCommand,RunArguments,RunWorkingDirectory",
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

    private static async Task CreateFakeAspireCliAsync(string fakeCliDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(fakeCliDirectory, "aspire.cmd"), """
                @echo off
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
        await File.WriteAllTextAsync(aspirePath, """
            #!/bin/sh
            printf '%s\n' "$@" > "$ASPIRE_TEST_CAPTURE_PATH"
            """);
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

    private static string GetPackageRid(string packageReference, string prefix)
    {
        var equalsIndex = packageReference.IndexOf('=');
        Assert.True(equalsIndex > prefix.Length, $"Package reference '{packageReference}' did not contain a RID.");

        return packageReference[prefix.Length..equalsIndex];
    }

    private static string GetExpectedAspireRunCommand() => OperatingSystem.IsWindows() ? "cmd" : "aspire";

    private static string GetExpectedAspireRunArguments(RunHookProject project, string? extraArguments = null)
        => GetExpectedAspireRunArguments(project.ProjectFile, extraArguments);

    private static string GetExpectedAspireRunArguments(string projectPath, string? extraArguments = null)
    {
        var prefix = OperatingSystem.IsWindows() ? "/C aspire " : string.Empty;
        var arguments = $"{prefix}run --project \"{projectPath}\" --no-build --";

        return string.IsNullOrEmpty(extraArguments) ? arguments : $"{arguments} {extraArguments}";
    }

    private static string GetPathEnvironmentVariableName() => OperatingSystem.IsWindows() ? "Path" : "PATH";

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
