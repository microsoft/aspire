// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Aspire.Hosting;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

[Collection(EnvVarMutatingTestCollection.Name)]
public class NativeAppHostServerProjectTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task FactoryUsesTheTargetAppHostConfigAndIgnoresOverrideWithoutOptIn()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var enabled = Directory.CreateDirectory(Path.Combine(workspace.Path, "enabled"));
        var nested = enabled.CreateSubdirectory("nested");
        var disabled = Directory.CreateDirectory(Path.Combine(workspace.Path, "disabled"));
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "server");
        new AspireConfigFile { Features = new() { [KnownFeatures.AppHostServerV2] = true } }.Save(enabled.FullName);
        new AspireConfigFile { Features = new() { [KnownFeatures.AppHostServerV2] = false } }.Save(disabled.FullName);
        var factory = CreateFactory(new TestEnvironment(new Dictionary<string, string?>
        {
            [NativeAppHostServerProject.ExecutableEnvironmentVariable] = executable
        }), new NullBundleService(), new TestProcessExecutionFactory());

        using var project = Assert.IsType<NativeAppHostServerProject>(await factory.CreateAsync(nested.FullName));
        Assert.Equal(nested.FullName, project.AppDirectoryPath);
        var managed = await factory.CreateAsync(disabled.FullName);
        Assert.IsNotType<NativeAppHostServerProject>(managed);
        (managed as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task FactoryDoesNotFallBackWhenOptedInBundleLacksV2()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        new AspireConfigFile { Features = new() { [KnownFeatures.AppHostServerV2] = true } }.Save(workspace.Path);
        var factory = CreateFactory(new TestEnvironment(), new NullBundleService(), new TestProcessExecutionFactory());

        Assert.Equal(workspace.Path, factory.GetInstanceIdentifier(workspace.Path));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(workspace.Path));
        Assert.Contains("does not contain AppHost Server v2", exception.Message);
    }

    [Fact]
    public async Task FactoryOverrideRetainsBundleToolingForProviderPreparation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        new AspireConfigFile { Features = new() { [KnownFeatures.AppHostServerV2] = true } }.Save(workspace.Path);
        var serverDirectory = workspace.CreateDirectory("developer-server");
        var executable = Path.Combine(serverDirectory.FullName, "server");
        var contract = Path.Combine(serverDirectory.FullName, "ats-contract.json");
        await File.WriteAllTextAsync(executable, "");
        await File.WriteAllTextAsync(contract, "{}");
        var managedDirectory = workspace.CreateDirectory("managed");
        var managed = Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName("aspire-managed"));
        await File.WriteAllTextAsync(managed, "");
        var providerDirectory = workspace.CreateDirectory("provider");
        var entryPoint = Path.Combine(providerDirectory.FullName, "host.mjs");
        await File.WriteAllTextAsync(entryPoint, "");
        await File.WriteAllTextAsync(Path.Combine(providerDirectory.FullName, "metadata.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(providerDirectory.FullName, "aspire.apphost-server.json"),
            """{"protocolVersion":2,"packageName":"example.provider","metadata":"metadata.json","sdkFiles":{},"exports":[]}""");
        var layout = new LayoutConfiguration { LayoutPath = workspace.Path };
        var lease = new BundleLayoutLease(layout, null);
        var bundle = new TestBundleService(true) { CreateLayoutLease = () => lease };
        var processes = new TestProcessExecutionFactory();
        var invocations = 0;
        processes.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            invocations++;
            Assert.Equal(managed, start.FileName);
            Assert.Equal("apphost-codegen-v2", start.ArgumentList[0]);
            var request = JsonNode.Parse(File.ReadAllText(start.ArgumentList[1]))!;
            Assert.Equal(contract, request["coreContract"]!.GetValue<string>());
            Directory.CreateDirectory(request["output"]!.GetValue<string>());
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        };
        var factory = CreateFactory(new TestEnvironment(new Dictionary<string, string?>
        {
            [NativeAppHostServerProject.ExecutableEnvironmentVariable] = executable
        }), bundle, processes);
        using var project = Assert.IsType<NativeAppHostServerProject>(await factory.CreateAsync(workspace.Path));

        Assert.Same(lease, project.LayoutLease);
        Assert.Equal(1, bundle.AcquireLayoutCallCount);
        Assert.True((await project.PrepareAsync("17.0", [IntegrationReference.FromNpm("example.provider", entryPoint)])).Success);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void RejectsMissingExecutable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Assert.Throws<ArgumentException>(() => new NativeAppHostServerProject(workspace.Path,
            Path.Combine(workspace.Path, "missing"), new TestProcessExecutionFactory(), NullLogger.Instance));
    }

    [Fact]
    public async Task PreparesSupportedToolingAndRejectsUnportedIntegrations()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        using var project = new NativeAppHostServerProject(workspace.Path, executable, new TestProcessExecutionFactory(), NullLogger.Instance);
        Assert.True((await project.PrepareAsync("13.6", [
            IntegrationReference.FromPackage("Aspire.Hosting", "13.6"),
            IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.6")
        ])).NeedsCodeGeneration);
        await Assert.ThrowsAsync<NotSupportedException>(() => project.PrepareAsync("13.6",
            [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.6")]));
        await Assert.ThrowsAsync<NotSupportedException>(() => project.PrepareAsync("13.6",
            [IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.Python", "13.6")]));
        await Assert.ThrowsAsync<NotSupportedException>(() => project.PrepareAsync("13.6",
            [IntegrationReference.FromProject("Aspire.Hosting.Redis", Path.Combine(workspace.Path, "Redis.csproj"))]));
    }

    [Theory]
    [InlineData(false, "aspire.hosting.codegeneration.typescript")]
    [InlineData(true, "Aspire.Hosting.CodeGeneration.TypeScript")]
    [InlineData(true, "aspire.hosting.codegeneration.typescript")]
    [InlineData(true, "Aspire.Hosting")]
    public async Task ToolingReferencesUseCaseInsensitiveIdentitiesForPackagesAndProjects(bool projectReference, string name)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        using var project = new NativeAppHostServerProject(workspace.Path, executable, new TestProcessExecutionFactory(), NullLogger.Instance);
        var reference = projectReference
            ? IntegrationReference.FromProject(name, Path.Combine(workspace.Path, "tooling.csproj"))
            : IntegrationReference.FromPackage(name, "13.6");

        Assert.True((await project.PrepareAsync("13.6", [reference])).NeedsCodeGeneration);
    }

    [Fact]
    public async Task UsesProcessFactoryAuthenticationPrivateSocketAndRunLifetime()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        var factory = new TestProcessExecutionFactory();
        string? directory = null;
        List<string> socketPaths = [];
        factory.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            Assert.Equal(executable, start.FileName);
            Assert.Equal(workspace.Path, start.WorkingDirectory);
            Assert.Equal(["server", "--contentRoot", workspace.Path], start.ArgumentList);
            Assert.Equal("test-token", start.Environment["ASPIRE_REMOTE_APPHOST_TOKEN"]);
            if (!OperatingSystem.IsWindows())
            {
                directory = Path.GetDirectoryName(start.Environment["REMOTE_APP_HOST_SOCKET_PATH"])!;
                socketPaths.Add(start.Environment["REMOTE_APP_HOST_SOCKET_PATH"]!);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            }
            else
            {
                Assert.False(Path.IsPathFullyQualified(start.Environment["REMOTE_APP_HOST_SOCKET_PATH"]!));
            }
            Assert.Equal(ChildProcessLifetime.AppHost, options.Lifetime);
            Assert.True(options.IsolateConsole);
            Assert.True(options.KillEntireProcessTreeOnCancel);
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        };
        using (var project = new NativeAppHostServerProject(workspace.Path, executable, factory, NullLogger.Instance))
        {
            var result = await project.RunAsync(42, new Dictionary<string, string> { ["ASPIRE_REMOTE_APPHOST_TOKEN"] = "test-token" },
                [], false, new AppHostServerRunControl(IsolateConsole: true));
            await result.Execution.DisposeAsync();
            if (!OperatingSystem.IsWindows())
            {
                Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.SocketPath), 1, 104);
                await File.WriteAllTextAsync(result.SocketPath, "stale bootstrap socket");
            }
            var finalSession = await project.RunAsync(42,
                new Dictionary<string, string> { ["ASPIRE_REMOTE_APPHOST_TOKEN"] = "test-token" },
                [], false, new AppHostServerRunControl(IsolateConsole: true));
            Assert.NotEqual(result.SocketPath, finalSession.SocketPath);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(directory, Path.GetDirectoryName(finalSession.SocketPath));
                Assert.True(File.Exists(result.SocketPath));
            }
            await finalSession.Execution.DisposeAsync();
        }
        Assert.All(socketPaths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task RunReplacesInheritedParentIdentityWithTheLaunchingProcess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        var processes = new TestProcessExecutionFactory();
        var pid = Environment.ProcessId;
        var started = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(pid);
        Assert.NotNull(started);
        processes.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            Assert.Equal(pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                start.Environment[KnownConfigNames.CliProcessId]);
            Assert.Equal(start.Environment[KnownConfigNames.CliProcessId],
                start.Environment[KnownConfigNames.RemoteAppHostProcessId]);
            Assert.Equal(started.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                start.Environment[KnownConfigNames.CliProcessStartedStable]);
            Assert.Equal(start.Environment[KnownConfigNames.CliProcessStartedStable],
                start.Environment[KnownConfigNames.RemoteAppHostProcessStarted]);
            Assert.Equal(ProcessStartTimeHelper.TryGetRuntimeProcessStartTimeUnixSeconds(pid)!.Value
                .ToString(System.Globalization.CultureInfo.InvariantCulture), start.Environment[KnownConfigNames.CliProcessStarted]);
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        };
        using var project = new NativeAppHostServerProject(workspace.Path, executable, processes, NullLogger.Instance);
        var result = await project.RunAsync(pid, new Dictionary<string, string>
        {
            [KnownConfigNames.CliProcessId] = "123",
            [KnownConfigNames.CliProcessStarted] = "1",
            [KnownConfigNames.CliProcessStartedStable] = "1000",
            [KnownConfigNames.RemoteAppHostProcessId] = "456",
            [KnownConfigNames.RemoteAppHostProcessStarted] = "2000"
        }, [], false, null);
        await result.Execution.DisposeAsync();
    }

    [Theory]
    [InlineData("run")]
    [InlineData("publish")]
    [InlineData("inspect")]
    public async Task BootstrapDoesNotLaunchBundledRuntimeComponents(string operation)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        var factory = new TestProcessExecutionFactory();
        factory.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            Assert.False(start.Environment.ContainsKey("ASPIRE_NATIVE_DCP_PATH"));
            Assert.False(start.Environment.ContainsKey("ASPIRE_DASHBOARD_PATH"));
            Assert.False(start.Environment.ContainsKey("ASPIRE_NATIVE_DASHBOARD_PORT"));
            Assert.False(start.Environment.ContainsKey("AppHost__FilePath"));
            Assert.False(start.Environment.ContainsKey(KnownConfigNames.UnixSocketPath));
            Assert.Equal(operation, start.Environment[NativeAppHostServerProject.OperationEnvironmentVariable]);
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        };
        using var project = new NativeAppHostServerProject(workspace.Path, executable, factory, NullLogger.Instance)
        {
            LayoutLease = new BundleLayoutLease(new LayoutConfiguration { LayoutPath = workspace.Path }, null)
        };

        var result = await project.RunAsync(42, new Dictionary<string, string>
        {
            [KnownConfigNames.IntegrationHostBootstrap] = "true",
            [NativeAppHostServerProject.OperationEnvironmentVariable] = operation,
            ["ASPIRE_NATIVE_DCP_PATH"] = "inherited-dcp",
            ["ASPIRE_DASHBOARD_PATH"] = "inherited-dashboard",
            ["ASPIRE_NATIVE_DASHBOARD_PORT"] = "0",
            ["AppHost__FilePath"] = "apphost.mts",
            [KnownConfigNames.UnixSocketPath] = "final-backchannel"
        }, [], false, null);
        await result.Execution.DisposeAsync();
    }

    [Theory]
    [InlineData("publish", false, false)]
    [InlineData("publish", false, true)]
    [InlineData("publish", true, false)]
    [InlineData("publish", true, true)]
    [InlineData("inspect", false, false)]
    [InlineData("inspect", false, true)]
    [InlineData("inspect", true, false)]
    [InlineData("inspect", true, true)]
    [InlineData("run", false, false)]
    [InlineData("run", false, true)]
    [InlineData("run", true, false)]
    [InlineData("run", true, true)]
    [InlineData("foo", false, false)]
    [InlineData("foo", false, true)]
    [InlineData("foo", true, false)]
    [InlineData("foo", true, true)]
    public async Task SessionOperationControlsRuntimeComponents(string operation, bool useLayout, bool useSessionPaths)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        using var inheritedOperation = new EnvVarOverride(NativeAppHostServerProject.OperationEnvironmentVariable, "inspect");
        using var inheritedDcp = new EnvVarOverride("ASPIRE_NATIVE_DCP_PATH", "ambient-dcp");
        using var inheritedDashboard = new EnvVarOverride("ASPIRE_DASHBOARD_PATH", "ambient-dashboard");
        using var inheritedPort = new EnvVarOverride("ASPIRE_NATIVE_DASHBOARD_PORT", "1234");
        var layout = new LayoutConfiguration { LayoutPath = workspace.Path };
        var factory = new TestProcessExecutionFactory();
        factory.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            Assert.Equal(["server", "--contentRoot", workspace.Path], start.ArgumentList);
            Assert.Equal(operation, start.Environment[NativeAppHostServerProject.OperationEnvironmentVariable]);
            Assert.Equal("final-backchannel", start.Environment[KnownConfigNames.UnixSocketPath]);
            Assert.Equal("apphost.mts", start.Environment["AppHost__FilePath"]);
            if (operation == "run")
            {
                Assert.Equal(useLayout ? BundleDiscovery.GetDcpExecutablePath(layout.GetDcpPath()!)
                    : useSessionPaths ? "session-dcp" : "ambient-dcp", start.Environment["ASPIRE_NATIVE_DCP_PATH"]);
                Assert.Equal(useLayout ? layout.GetDashboardPath()
                    : useSessionPaths ? "session-dashboard" : "ambient-dashboard", start.Environment["ASPIRE_DASHBOARD_PATH"]);
                Assert.Equal(useLayout ? "0" : useSessionPaths ? "5678" : "1234", start.Environment["ASPIRE_NATIVE_DASHBOARD_PORT"]);
            }
            else
            {
                Assert.False(start.Environment.ContainsKey("ASPIRE_NATIVE_DCP_PATH"));
                Assert.False(start.Environment.ContainsKey("ASPIRE_DASHBOARD_PATH"));
                Assert.False(start.Environment.ContainsKey("ASPIRE_NATIVE_DASHBOARD_PORT"));
            }
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        };
        using var project = new NativeAppHostServerProject(workspace.Path, executable, factory, NullLogger.Instance)
        {
            LayoutLease = useLayout ? new BundleLayoutLease(layout, null) : null
        };
        var environment = new Dictionary<string, string>
        {
            [KnownConfigNames.UnixSocketPath] = "final-backchannel",
            ["AppHost__FilePath"] = "apphost.mts"
        };
        if (operation != "run")
        {
            environment[NativeAppHostServerProject.OperationEnvironmentVariable] = operation;
        }
        if (useSessionPaths)
        {
            environment["ASPIRE_NATIVE_DCP_PATH"] = "session-dcp";
            environment["ASPIRE_DASHBOARD_PATH"] = "session-dashboard";
            environment["ASPIRE_NATIVE_DASHBOARD_PORT"] = "5678";
        }

        var result = await project.RunAsync(42, environment, [], false, null);

        await result.Execution.DisposeAsync();
    }

    [Fact]
    public async Task SocketCleanupFailuresAreLoggedWithoutReplacingTheProcessExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix socket files are only tracked on Unix.");
            return;
        }
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        var logger = new CapturingLogger<NativeAppHostServerProject>();
        var processes = new TestProcessExecutionFactory
        {
            CreateExecutionFromStartInfoCallback = (start, options) => new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((7, (string?)null)), () => 1)
        };
        using var project = new NativeAppHostServerProject(workspace.Path, executable, processes, logger);
        var result = await project.RunAsync(42, null, [], false, null);
        Directory.CreateDirectory(result.SocketPath);
        try
        {
            Assert.Equal(7, await result.Execution.WaitForExitAsync(TestContext.Current.CancellationToken));
            await result.Execution.DisposeAsync();
            project.Dispose();

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
            Assert.Contains(result.SocketPath, entry.Message);
        }
        finally
        {
            Directory.Delete(result.SocketPath);
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("""{"protocolVersion":"2","packageName":"example","metadata":"contract.json","sdkFiles":{},"exports":[]}""")]
    [InlineData("""{"protocolVersion":2,"packageName":"example","metadata":"contract.json","sdkFiles":{"provider.mts":null},"exports":[]}""")]
    [InlineData("""{"protocolVersion":2,"packageName":"example","metadata":"contract.json","sdkFiles":{},"exports":[null]}""")]
    public void MalformedProviderDescriptorsFailAtTheParsingBoundary(string json)
    {
        Assert.Throws<InvalidDataException>(() => NativeProviderDescriptor.Read(json));
    }

    [Fact]
    public void ProviderDescriptorParsingDoesNotRequireAssetsToExist()
    {
        var descriptor = NativeProviderDescriptor.Read("""
            {"protocolVersion":2,"packageName":"example.provider","metadata":"contract.json",
            "sdkFiles":{"provider.mts":"src/provider.mts"},"exports":["provider.mts"]}
            """);
        Assert.Equal("example.provider", descriptor.PackageName);
        Assert.Equal("contract.json", descriptor.Metadata);
        var file = Assert.Single(descriptor.SdkFiles);
        Assert.Equal("provider.mts", file.Key);
        Assert.Equal("src/provider.mts", file.Value);
        Assert.Equal(["provider.mts"], descriptor.Exports);
    }

    [Theory]
    [InlineData(1, "metadata.json")]
    [InlineData(2, "../outside.json")]
    public async Task RejectsIncompatibleOrEscapingProviderDescriptors(int protocolVersion, string metadata)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        var root = Directory.CreateDirectory(Path.Combine(workspace.Path, "provider"));
        var entryPoint = Path.Combine(root.FullName, "host.mjs");
        await File.WriteAllTextAsync(entryPoint, "");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "aspire.apphost-server.json"), new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["packageName"] = "example.provider",
            ["metadata"] = metadata,
            ["sdkFiles"] = new JsonObject(),
            ["exports"] = new JsonArray()
        }.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "outside.json"), "{}");
        using var project = new NativeAppHostServerProject(workspace.Path, executable,
            new TestProcessExecutionFactory(), NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => project.PrepareAsync("17.0",
            [IntegrationReference.FromNpm("example.provider", entryPoint)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ProviderPreparationResetsSdkSelectionAndDoesNotAdvertiseFailedOutput(int exitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        var managedDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "managed"));
        await File.WriteAllTextAsync(executable, "");
        await File.WriteAllTextAsync(Path.Combine(managedDirectory.FullName, BundleDiscovery.GetExecutableFileName("aspire-managed")), "");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "ats-contract.json"), "{}");
        var providerDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "provider"));
        var entryPoint = Path.Combine(providerDirectory.FullName, "host.mjs");
        await File.WriteAllTextAsync(entryPoint, "");
        await File.WriteAllTextAsync(Path.Combine(providerDirectory.FullName, "metadata.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(providerDirectory.FullName, "aspire.apphost-server.json"),
            """{"protocolVersion":2,"packageName":"example.provider","metadata":"metadata.json","sdkFiles":{},"exports":[]}""");
        var processes = new TestProcessExecutionFactory();
        string? firstOutput = null;
        processes.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            if (start.FileName == executable)
            {
                start.Environment.TryGetValue("ASPIRE_APPHOST_SERVER_V2_SDK_DIRECTORY", out var sdk);
                Assert.Equal(exitCode == 0 ? firstOutput : null, sdk);
            }
            else
            {
                var request = JsonNode.Parse(File.ReadAllText(start.ArgumentList[1]))!;
                var output = request["output"]!.GetValue<string>();
                Assert.NotEqual(firstOutput, output);
                firstOutput ??= output;
                Directory.CreateDirectory(output);
            }
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((start.FileName == executable ? 0 : exitCode, (string?)null)), () => 1);
        };
        var logger = new CapturingLogger<NativeAppHostServerProject>();
        using var project = new NativeAppHostServerProject(workspace.Path, executable, processes, logger)
        {
            LayoutLease = new BundleLayoutLease(new LayoutConfiguration { LayoutPath = workspace.Path }, null)
        };
        var references = new[] { IntegrationReference.FromNpm("example.provider", entryPoint) };

        Assert.Equal(exitCode == 0, (await project.PrepareAsync("17.0", references)).Success);
        var firstRun = await project.RunAsync(42, null, [], false, null);
        await firstRun.Execution.DisposeAsync();
        Assert.Equal(exitCode == 0, (await project.PrepareAsync("17.0", references)).Success);
        Assert.True((await project.PrepareAsync("17.0", [])).Success);
        processes.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            Assert.False(start.Environment.ContainsKey("ASPIRE_APPHOST_SERVER_V2_SDK_DIRECTORY"));
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        };
        var finalRun = await project.RunAsync(42,
            new Dictionary<string, string> { ["ASPIRE_APPHOST_SERVER_V2_SDK_DIRECTORY"] = "stale-sdk" },
            [], false, null);
        await finalRun.Execution.DisposeAsync();

        var temporaryDirectory = Path.GetDirectoryName(firstOutput)!;
        Directory.Delete(temporaryDirectory, recursive: true);
        await File.WriteAllTextAsync(temporaryDirectory, "Simulate a cleanup path replaced by a file.");
        try
        {
            project.Dispose();

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
            Assert.Contains(temporaryDirectory, entry.Message);
        }
        finally
        {
            File.Delete(temporaryDirectory);
        }
    }

    private static AppHostServerProjectFactory CreateFactory(TestEnvironment environment, IBundleService bundle,
        TestProcessExecutionFactory processes)
        => new(new TestDotNetCliRunner(), MockPackagingServiceFactory.Create(), bundle,
            new BundleNuGetService(NullLogger<BundleNuGetService>.Instance, new FakeNuGetClient()),
            new TestDotNetSdkInstaller(), TestExecutionContextFactory.CreateTestContext(), environment,
            processes, NullLoggerFactory.Instance);
}
