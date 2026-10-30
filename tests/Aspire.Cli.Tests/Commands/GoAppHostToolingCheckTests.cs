// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text.RegularExpressions;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Commands;

public sealed class GoAppHostToolingCheckTests(ITestOutputHelper outputHelper)
{
    private const string GoInstallLink = "https://go.dev/doc/install";

    private static readonly LanguageInfo s_goLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.Go),
        DisplayName: KnownLanguageId.GoDisplayName,
        PackageName: "Aspire.Hosting.CodeGeneration.Go",
        DetectionPatterns: ["apphost.go"],
        CodeGenerator: "Go",
        AppHostFileName: "apphost.go");

    [Theory]
    [InlineData("go version go1.26.1 windows/amd64", "1.26.1")]
    [InlineData("go version go1.27.0 darwin/arm64", "1.27.0")]
    [InlineData("go version go1.26rc1 linux/amd64", "1.26")]
    [InlineData("go version devel go1.27-4d2f8e1a5b Tue Jan 6 10:00:00 2026 +0000 linux/amd64", "1.27")]
    public async Task CheckAsync_ReturnsPass_WhenGoMeetsMinimumVersion(string versionOutput, string expectedVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        string? versionResolverPath = null;

        var check = CreateCheck(
            workspace,
            appHostFile,
            commandResolver: command => $"/usr/bin/{command}",
            versionResolver: (path, _) =>
            {
                versionResolverPath = path;
                return Task.FromResult<string?>(versionOutput);
            });

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(GoAppHostToolingCheck.ToolsCheckName, result.Name);
        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal($"Go AppHost tooling found (go {expectedVersion}).", result.Message);
        Assert.Equal("/usr/bin/go", versionResolverPath);
        Assert.NotNull(result.Metadata);
        Assert.Equal(KnownLanguageId.Go, result.Metadata["language"]?.GetValue<string>());
        Assert.Equal(expectedVersion, result.Metadata["version"]?.GetValue<string>());
        Assert.Equal(appHostFile.FullName, result.Metadata["appHostPath"]?.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenGoAppHostIsDiscoveredInWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);

        var check = CreateCheck(workspace, appHostFile: null);

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.NotNull(result.Metadata);
        Assert.Equal(appHostFile.FullName, result.Metadata["appHostPath"]?.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_Skips_WhenNoGoAppHostExists()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var check = CreateCheck(workspace, appHostFile: null, commandResolver: _ => null);

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_Skips_WhenAppHostIsNotGo()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        File.WriteAllText(appHostPath, "// apphost");

        var check = CreateCheck(workspace, new FileInfo(appHostPath), commandResolver: _ => null);

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_Skips_WhenConfiguredAppHostIsNotGoAndGoAppHostIsDiscoverable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        CreateGoAppHost(workspace);

        // Settings select a TypeScript AppHost outside the working directory, as in `../ts/apphost.mts`.
        var configuredAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "..", "ts", "apphost.mts"));
        var check = CreateCheck(
            workspace,
            configuredAppHost,
            commandResolver: _ => null,
            languageDiscovery: CreateLanguageDiscovery(goEnabled: true));

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenGoAppHostSitsBesideCSharpProjectAndGoIsMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHostBesideCSharpProject(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile: null,
            commandResolver: _ => null,
            languageDiscovery: CreateLanguageDiscovery(goEnabled: true));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(GoAppHostToolingCheck.GoCommandCheckName, result.Name);
        Assert.NotNull(result.Metadata);
        Assert.Equal(appHostFile.FullName, result.Metadata["appHostPath"]?.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_Skips_WhenGoFeatureIsDisabled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        CreateGoAppHostBesideCSharpProject(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile: null,
            commandResolver: _ => null,
            languageDiscovery: CreateLanguageDiscovery(goEnabled: false));

        var results = await check.CheckAsync().DefaultTimeout();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenGoIsMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(workspace, appHostFile, commandResolver: _ => null);

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Equal(GoAppHostToolingCheck.GoCommandCheckName, result.Name);
        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal("Go AppHost requires 'go'.", result.Message);
        Assert.Equal("go is not installed or not found in PATH. Please install Go and try again.", result.Details);
        Assert.Equal("Install Go and rerun 'aspire doctor'.", result.Fix);
        Assert.Equal(GoInstallLink, result.Link);
        Assert.NotNull(result.Metadata);
        Assert.Equal(KnownLanguageId.Go, result.Metadata["language"]?.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenGoIsOlderAndCanDownloadToolchain()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            toolchainResolver: ReturnsToolchain("auto"));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertToolchainDownloadAllowed(result);
        Assert.NotNull(result.Metadata);
        Assert.Equal("1.24.1", result.Metadata["version"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("go1.25.0+auto")]
    [InlineData("go1.24.1")] // A pinned toolchain is not modelled; the check validates the Go on PATH.
    public async Task CheckAsync_ReturnsWarning_WhenEnvironmentToolchainAllowsDownload(string toolchain)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            environment: new Dictionary<string, string?> { ["GOTOOLCHAIN"] = toolchain });

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertToolchainDownloadAllowed(result);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenEnvironmentToolchainIsLocal()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            environment: new Dictionary<string, string?> { ["GOTOOLCHAIN"] = "local" });

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertToolchainIsLocal(result);
    }

    // The path modes only fail when the toolchain that go.mod names is missing from PATH, and doctor does
    // not read go.mod, so they are inconclusive.
    [Theory]
    [InlineData("path")]
    [InlineData("local+path")]
    [InlineData("go1.25.0+path")]
    public async Task CheckAsync_ReturnsWarning_WhenEnvironmentToolchainOnlySwitchesToPathToolchain(string toolchain)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            environment: new Dictionary<string, string?> { ["GOTOOLCHAIN"] = toolchain });

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertToolchainOnlySwitchesToPathToolchain(result, toolchain);
    }

    // Without the environment variable, Go reads GOTOOLCHAIN from the user config written by `go env -w`
    // and then from $GOROOT/go.env, where Fedora's Go package sets `local`.
    [Theory]
    [InlineData(null, "local")]
    [InlineData("", "local")]
    [InlineData(null, "local\r\n")]
    public async Task CheckAsync_ReturnsFail_WhenGoEnvToolchainIsLocal(string? environmentValue, string probeOutput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            environment: new Dictionary<string, string?> { ["GOTOOLCHAIN"] = environmentValue },
            toolchainResolver: ReturnsToolchain(probeOutput));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertToolchainIsLocal(result);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("local+path")]
    [InlineData("go1.25.0+path")]
    public async Task CheckAsync_ReturnsWarning_WhenGoEnvToolchainOnlySwitchesToPathToolchain(string probeOutput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            toolchainResolver: ReturnsToolchain(probeOutput));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertToolchainOnlySwitchesToPathToolchain(result, probeOutput);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("go1.25.0+auto")]
    [InlineData("")]
    [InlineData(null)] // The probe failed or timed out.
    public async Task CheckAsync_ReturnsWarning_WhenGoEnvToolchainAllowsDownload(string? probeOutput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            toolchainResolver: ReturnsToolchain(probeOutput));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertToolchainDownloadAllowed(result);
    }

    [Fact]
    public async Task CheckAsync_ProbesToolchainInEmptyDirectoryAndDeletesIt()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        string? probedGoPath = null;
        string? probeDirectory = null;
        var probeDirectoryWasEmpty = false;

        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.24.1 windows/amd64"),
            toolchainResolver: (goPath, workingDirectory, _) =>
            {
                probedGoPath = goPath;
                probeDirectory = workingDirectory;
                probeDirectoryWasEmpty = Directory.Exists(workingDirectory) && !Directory.EnumerateFileSystemEntries(workingDirectory).Any();
                return Task.FromResult<string?>("auto");
            });

        await check.CheckAsync().DefaultTimeout();

        // The AppHost directory holds apphost.go, so an empty probe directory cannot be it.
        Assert.Equal("/usr/bin/go", probedGoPath);
        Assert.True(probeDirectoryWasEmpty);
        Assert.False(Directory.Exists(probeDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    public async Task CheckAsync_ReturnsFail_WhenGoPredatesToolchainSwitching(string? toolchain)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(
            workspace,
            appHostFile,
            versionResolver: ReturnsVersion("go version go1.20.14 linux/amd64"),
            environment: new Dictionary<string, string?> { ["GOTOOLCHAIN"] = toolchain });

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertVersionResult(result, EnvironmentCheckStatus.Fail);
        Assert.Equal("Go 1.20.14 is installed, and Go versions before 1.21 cannot download a newer toolchain.", result.Details);
        Assert.Equal("Install Go 1.26 or later and rerun 'aspire doctor'.", result.Fix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("go: command output without a version")]
    public async Task CheckAsync_ReturnsWarning_WhenGoVersionCannotBeDetermined(string? versionOutput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateGoAppHost(workspace);
        var check = CreateCheck(workspace, appHostFile, versionResolver: ReturnsVersion(versionOutput));

        var result = Assert.Single(await check.CheckAsync().DefaultTimeout());

        AssertVersionResult(result, EnvironmentCheckStatus.Warning);
        Assert.Equal("The installed Go version could not be determined.", result.Details);
        Assert.Equal("Check that 'go version' prints a version and rerun 'aspire doctor'.", result.Fix);
        Assert.NotNull(result.Metadata);
        Assert.Null(result.Metadata["version"]);
    }

    [Theory]
    [InlineData("go version go1.26.1 windows/amd64", "1.26.1")]
    [InlineData("go version go1.26 linux/amd64", "1.26")]
    [InlineData("go version go1.26rc1 linux/amd64", "1.26")]
    [InlineData("go version go1.27beta2 darwin/arm64", "1.27")]
    [InlineData("go version devel go1.27-4d2f8e1a5b Tue Jan 6 10:00:00 2026 +0000 linux/amd64", "1.27")]
    [InlineData("go version go1.21.13 X:boringcrypto linux/amd64\n", "1.21.13")]
    public void TryParseGoVersion_ParsesVersionOutput(string output, string expectedVersion)
    {
        Assert.True(GoAppHostToolingCheck.TryParseGoVersion(output, out var version));
        Assert.Equal(expectedVersion, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("go version")]
    [InlineData("go version go1 linux/amd64")]
    [InlineData("'go' is not recognized as an internal or external command")]
    public void TryParseGoVersion_ReturnsFalse_WhenOutputHasNoVersion(string? output)
    {
        Assert.False(GoAppHostToolingCheck.TryParseGoVersion(output, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void CreateVersionProbeStartInfo_PinsToolchainToLocal()
    {
        var startInfo = GoAppHostToolingCheck.CreateVersionProbeStartInfo("/usr/bin/go");

        Assert.Equal("/usr/bin/go", startInfo.FileName);
        Assert.Equal(["version"], startInfo.ArgumentList);
        Assert.Equal("local", startInfo.Environment["GOTOOLCHAIN"]);
    }

    [Fact]
    public void CreateToolchainProbeStartInfo_DoesNotOverrideToolchain()
    {
        var startInfo = GoAppHostToolingCheck.CreateToolchainProbeStartInfo("/usr/bin/go", "/tmp/aspire-go-doctor");

        Assert.Equal("/usr/bin/go", startInfo.FileName);
        Assert.Equal(["env", "GOTOOLCHAIN"], startInfo.ArgumentList);
        Assert.Equal("/tmp/aspire-go-doctor", startInfo.WorkingDirectory);

        // The probe reports the effective setting, so the child must inherit the caller's value untouched.
        startInfo.Environment.TryGetValue("GOTOOLCHAIN", out var toolchain);
        Assert.Equal(Environment.GetEnvironmentVariable("GOTOOLCHAIN"), toolchain);
    }

    [Fact]
    public async Task RunProbeAsync_ReturnsStandardOutput_WhenGoSucceeds()
    {
        var processExecutionFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, options) =>
            {
                // Go reports notices such as a toolchain download on standard error; they must not reach the parsed answer.
                options.StandardErrorCallback?.Invoke("go: downloading go1.26.1");
                return (0, "go version go1.26.1 windows/amd64");
            }
        };

        var output = await RunVersionProbeAsync(processExecutionFactory, new CapturingLogger<GoAppHostToolingCheck>());

        Assert.Equal($"go version go1.26.1 windows/amd64{Environment.NewLine}", output);
        var execution = Assert.IsType<TestProcessExecution>(Assert.Single(processExecutionFactory.CreatedExecutions));
        Assert.Equal("/usr/bin/go", execution.FileName);
        Assert.Equal(["version"], execution.Arguments);
        Assert.Equal("local", execution.EnvironmentVariables["GOTOOLCHAIN"]);
        Assert.Equal(1, execution.DisposeCount);
    }

    [Fact]
    public async Task RunProbeAsync_ReturnsNullAndLogsStandardError_WhenGoFails()
    {
        var processExecutionFactory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, options) =>
            {
                options.StandardErrorCallback?.Invoke("go: cannot find main module");
                return (2, null);
            }
        };
        var logger = new CapturingLogger<GoAppHostToolingCheck>();

        var output = await RunVersionProbeAsync(processExecutionFactory, logger);

        Assert.Null(output);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("'go version' failed with exit code 2: go: cannot find main module", entry.Message);
    }

    [Fact]
    public async Task RunProbeAsync_ReturnsNull_WhenGoDoesNotStart()
    {
        var processExecutionFactory = CreateProbeFactory(startReturnValue: false);

        var output = await RunVersionProbeAsync(processExecutionFactory, new CapturingLogger<GoAppHostToolingCheck>());

        Assert.Null(output);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public async Task RunProbeAsync_ReturnsNullAndLogs_WhenGoCannotBeStarted(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "go could not be started")!;
        var processExecutionFactory = CreateProbeFactory(startException: exception);
        var logger = new CapturingLogger<GoAppHostToolingCheck>();

        var output = await RunVersionProbeAsync(processExecutionFactory, logger);

        Assert.Null(output);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("Could not start 'go version' using '/usr/bin/go'.", entry.Message);
    }

    [Fact]
    public async Task RunProbeAsync_ReturnsNullAndLogs_WhenGoOutlastsTimeout()
    {
        var processExecutionFactory = CreateProbeFactory(waitForExitAsyncCallback: async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        });
        var logger = new CapturingLogger<GoAppHostToolingCheck>();

        var output = await RunVersionProbeAsync(processExecutionFactory, logger, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Null(output);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("'go version' timed out after 0.1s.", entry.Message);
        var execution = Assert.IsType<TestProcessExecution>(Assert.Single(processExecutionFactory.CreatedExecutions));
        Assert.Equal(1, execution.DisposeCount);
    }

    [Fact]
    public async Task RunProbeAsync_PropagatesCancellation_WhenCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        var processExecutionFactory = CreateProbeFactory(waitForExitAsyncCallback: async (_, cancellationToken) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        });
        var logger = new CapturingLogger<GoAppHostToolingCheck>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunVersionProbeAsync(processExecutionFactory, logger, cancellationToken: cts.Token));

        Assert.Empty(logger.Entries);
        var execution = Assert.IsType<TestProcessExecution>(Assert.Single(processExecutionFactory.CreatedExecutions));
        Assert.Equal(1, execution.DisposeCount);
    }

    [Theory]
    [InlineData("AtsGoCodeGenerator.cs")]
    [InlineData("GoLanguageSupport.cs")]
    public void MinimumGoVersion_MatchesGoModTemplate(string sourceFileName)
    {
        var sourcePath = Path.Combine(GetRepoRoot(), "src", "Aspire.Hosting.CodeGeneration.Go", sourceFileName);
        Assert.True(File.Exists(sourcePath), $"Expected the Go code generation source at {sourcePath}");

        // Both generators emit a go.mod whose directive sits on its own line: `go 1.26`.
        var directive = Regex.Match(File.ReadAllText(sourcePath), @"^\s*go (\d+\.\d+)\s*$", RegexOptions.Multiline);

        Assert.True(directive.Success, $"Expected a 'go <major>.<minor>' directive in the go.mod template of {sourceFileName}.");
        Assert.Equal(GoAppHostToolingCheck.MinimumGoVersion.ToString(), directive.Groups[1].Value);
    }

    private static FileInfo CreateGoAppHost(TemporaryWorkspace workspace)
    {
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.go");
        File.WriteAllText(appHostPath, "package main");
        return new FileInfo(appHostPath);
    }

    // DefaultLanguageDiscovery detects any *.csproj as C#, including an ordinary service project.
    private static FileInfo CreateGoAppHostBesideCSharpProject(TemporaryWorkspace workspace)
    {
        var serviceDirectory = workspace.WorkspaceRoot.CreateSubdirectory("api");
        File.WriteAllText(Path.Combine(serviceDirectory.FullName, "Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        return CreateGoAppHost(workspace);
    }

    private static DefaultLanguageDiscovery CreateLanguageDiscovery(bool goEnabled) =>
        new(new TestFeatures().SetFeature(KnownFeatures.ExperimentalPolyglotGo, goEnabled));

    private static GoAppHostToolingCheck CreateCheck(
        TemporaryWorkspace workspace,
        FileInfo? appHostFile,
        Func<string, string?>? commandResolver = null,
        Func<string, CancellationToken, Task<string?>>? versionResolver = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Func<string, string, CancellationToken, Task<string?>>? toolchainResolver = null,
        ILanguageDiscovery? languageDiscovery = null)
    {
        var projectLocator = new TestProjectLocator
        {
            GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult(appHostFile)
        };

        return new GoAppHostToolingCheck(
            projectLocator,
            languageDiscovery ?? new TestLanguageDiscovery(s_goLanguage),
            CreateExecutionContext(workspace),
            new TestEnvironment(environment),
            commandResolver ?? (command => $"/usr/bin/{command}"),
            versionResolver ?? ReturnsVersion("go version go1.26.1 windows/amd64"),
            toolchainResolver ?? ThrowsIfToolchainIsProbed);
    }

    private static Task<string?> RunVersionProbeAsync(
        TestProcessExecutionFactory processExecutionFactory,
        CapturingLogger<GoAppHostToolingCheck> logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GoAppHostToolingCheck.RunProbeAsync(
            processExecutionFactory,
            GoAppHostToolingCheck.CreateVersionProbeStartInfo("/usr/bin/go"),
            timeout ?? TimeSpan.FromMinutes(1),
            logger,
            cancellationToken).DefaultTimeout();

    // Scenarios that need to control how the fake process starts or waits; the default attempt callback is never used.
    private static TestProcessExecutionFactory CreateProbeFactory(
        bool startReturnValue = true,
        Exception? startException = null,
        Func<ProcessInvocationOptions, CancellationToken, Task<int>>? waitForExitAsyncCallback = null) =>
        new()
        {
            CreateExecutionWithFileNameCallback = (fileName, args, env, workingDirectory, options) =>
                new TestProcessExecution(fileName, args, env, options, (_, _, _) => Task.FromResult((0, (string?)null)), () => 1)
                {
                    StartReturnValue = startReturnValue,
                    StartException = startException,
                    WaitForExitAsyncCallback = waitForExitAsyncCallback
                }
        };

    private static Func<string, CancellationToken, Task<string?>> ReturnsVersion(string? versionOutput) =>
        (_, _) => Task.FromResult(versionOutput);

    private static Func<string, string, CancellationToken, Task<string?>> ReturnsToolchain(string? toolchainOutput) =>
        (_, _, _) => Task.FromResult(toolchainOutput);

    // Scenarios that never reach the `go env GOTOOLCHAIN` probe use this default so that an unexpected
    // child process fails the test.
    private static Task<string?> ThrowsIfToolchainIsProbed(string goPath, string workingDirectory, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The effective GOTOOLCHAIN must not be probed in this scenario.");

    private static void AssertToolchainDownloadAllowed(EnvironmentCheckResult result)
    {
        AssertVersionResult(result, EnvironmentCheckStatus.Warning);
        Assert.Equal("Go 1.24.1 is installed. Go downloads the Go 1.26 toolchain the first time the AppHost runs, which needs network access.", result.Details);
        Assert.Equal("Install Go 1.26 or later to avoid the download, then rerun 'aspire doctor'.", result.Fix);
    }

    private static void AssertToolchainIsLocal(EnvironmentCheckResult result)
    {
        AssertVersionResult(result, EnvironmentCheckStatus.Fail);
        Assert.Equal("Go 1.24.1 is installed, and GOTOOLCHAIN=local prevents Go from switching to a newer toolchain.", result.Details);
        Assert.Equal("Install Go 1.26 or later, or change GOTOOLCHAIN to auto, then rerun 'aspire doctor'.", result.Fix);
    }

    private static void AssertToolchainOnlySwitchesToPathToolchain(EnvironmentCheckResult result, string toolchain)
    {
        AssertVersionResult(result, EnvironmentCheckStatus.Warning);
        Assert.Equal($"Go 1.24.1 is installed, and GOTOOLCHAIN={toolchain} only lets Go switch to a newer toolchain that is already on PATH (for example go1.26.0).", result.Details);
        Assert.Equal("Install Go 1.26 or later, or put the go1.26.0 toolchain on PATH, then rerun 'aspire doctor'.", result.Fix);
    }

    private static void AssertVersionResult(EnvironmentCheckResult result, EnvironmentCheckStatus expectedStatus)
    {
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(GoAppHostToolingCheck.VersionCheckName, result.Name);
        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal("Go AppHost requires Go 1.26 or later.", result.Message);
        Assert.Equal(GoInstallLink, result.Link);
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace) =>
        new(
            workingDirectory: workspace.WorkspaceRoot,
            hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-hives"),
            cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-cache"),
            sdksDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-sdks"),
            logsDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-logs"),
            logFilePath: "test.log",
            identityChannel: "local");

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
