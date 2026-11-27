// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sigstore;
using Tuf;
#if DEBUG
using System.Globalization;
using Aspire.Cli.Resources;
#endif

namespace Aspire.Cli.Tests;

/// <summary>
/// Integration tests for the production registrations in
/// <see cref="Program.BuildApplicationAsync"/>.
/// </summary>
[Collection(EnvVarMutatingTestCollection.Name)]
public class CliBootstrapTests(ITestOutputHelper outputHelper)
{
    private static readonly string[] s_fixedChannels = ["stable", "staging", "daily", "local"];

    private void RunIsolated(Func<string, Task> test)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // Stop settings discovery at the fixture rather than walking into temporary-directory ancestors.
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), "{}");
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workspace.Path;
        options.StartInfo.Environment[CliPathHelper.AspireHomeEnvironmentVariable] = Path.Combine(workspace.Path, ".aspire");
        options.StartInfo.Environment["HOME"] = workspace.Path;
        options.StartInfo.Environment["USERPROFILE"] = workspace.Path;
        // Startup can migrate both global settings and settings discovered above the cwd.
        // A fixture-owned cwd, config file and home keep those writes out of the developer's directories.
        RemoteExecutor.Invoke(test, workspace.Path, options).Dispose();
    }

    private static async Task<IHost> BuildHostAsync(Dictionary<string, string?>? configurationValues = null)
    {
        var loggingOptions = Program.ParseLoggingOptions([]);
        var errorWriter = new TestStartupErrorWriter();
        var logBufferContext = new ConsoleLogBufferContext();
        var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory([], loggingOptions, errorWriter, logBufferContext);
        var identityChannelReader = new IdentityChannelReader(typeof(Program).Assembly);
        var startupContext = new Program.CliStartupContext(loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, logBufferContext, loggerFactory.CreateLogger(Program.RootLoggerName), new ConsoleCancellationManager(finalDrainBudget: Timeout.InfiniteTimeSpan), identityChannelReader);
        return await Program.BuildApplicationAsync([], startupContext, configurationValues);
    }

    private static string GetBakedEntryAssemblyChannel()
    {
        var entryAssembly = typeof(CliBootstrapTests).Assembly;
        var bakedChannel = entryAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireCliChannel", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(bakedChannel));
        return bakedChannel!;
    }

    [Fact]
    public void BuildApplication_UsesPrivateTemporaryStorageUnderExecutionContextHome()
    {
        RunIsolated(static async workspacePath =>
        {
            var home = Path.Combine(workspacePath, ".aspire");
            var configuredHome = Path.Combine(workspacePath, "workspace-config-home");
            using var host = await BuildHostAsync(new Dictionary<string, string?> { ["ASPIRE_HOME"] = configuredHome });
            var context = host.Services.GetRequiredService<CliExecutionContext>();
            var storage = host.Services.GetRequiredService<ITemporaryStorage>();
            Assert.IsType<PrivateTemporaryStorage>(storage);
            Assert.Equal(home, context.AspireHomeDirectory.FullName);
            Assert.StartsWith(Path.Combine(context.AspireHomeDirectory.FullName, "tmp") + Path.DirectorySeparatorChar, storage.GetTempPath());
            Assert.False(Directory.Exists(configuredHome));
            Assert.NotNull(host.Services.GetRequiredService<IDcpConnectionChecker>());
        });
    }

    [Fact]
    public void IdentityChannelReader_OnRunningCliAssembly_ReturnsKnownChannel()
    {
        var reader = new IdentityChannelReader(typeof(Aspire.Cli.Program).Assembly);

        Assert.True(reader.TryReadChannel(out var channel, out _));

        // Test host can be built with /p:AspireCliChannel=<anything in the accepted set>;
        // assert shape, not a single literal, so this test stops being an accidental
        // regression for non-default builds (including pr-<N> when the test host is a PR build).
        Assert.True(
            s_fixedChannels.Contains(channel) || channel.StartsWith("pr-", StringComparison.Ordinal),
            $"Unexpected channel '{channel}'; expected one of stable|staging|daily|local|pr-<N>.");
    }

    [Fact]
    public void BuildApplication_RegistersIIdentityChannelReader_AsIdentityChannelReaderInstance()
    {
        RunIsolated(static async workspacePath =>
        {
            // Program.BuildApplicationAsync registers IIdentityChannelReader as a singleton,
            // backed by the default IdentityChannelReader (which reads from
            // typeof(Aspire.Cli.Program).Assembly).
            using var host = await BuildHostAsync();

            var reader = host.Services.GetRequiredService<IIdentityChannelReader>();

            Assert.NotNull(reader);
            Assert.IsType<IdentityChannelReader>(reader);
        });
    }

    [Fact]
    public void BuildApplication_PopulatesCliExecutionContextChannel_FromIdentityChannelReader()
    {
        RunIsolated(static async workspacePath =>
        {
            // The CliExecutionContext factory delegate must source Channel from
            // IIdentityChannelReader.ReadChannel() rather than the constructor default.
            // Without this wiring, the entire reseed chain would write "daily" for every
            // CLI build regardless of the baked AspireCliChannel.
            using var host = await BuildHostAsync();

            var reader = host.Services.GetRequiredService<IIdentityChannelReader>();
            var context = host.Services.GetRequiredService<CliExecutionContext>();

            Assert.True(reader.TryReadChannel(out var channel, out _));
            Assert.Equal(channel, context.IdentityChannel);
        });
    }

    [Fact]
    public void BuildApplication_SharesSigstoreVerifierAcrossScopes()
    {
        RunIsolated(static async workspacePath =>
        {
            var aspireHome = Directory.CreateDirectory(Path.Combine(workspacePath, "aspire-home"));
            using var aspireHomeOverride = new EnvVarOverride(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome.FullName);
            using var host = await BuildHostAsync();
            var verifier = host.Services.GetRequiredService<SigstoreVerifier>();
            var trustRootProvider = host.Services.GetRequiredService<ITrustRootProvider>();

            for (var i = 0; i < 2; i++)
            {
                using var scope = host.Services.CreateScope();
                Assert.Same(verifier, scope.ServiceProvider.GetRequiredService<SigstoreVerifier>());
                Assert.Same(trustRootProvider, scope.ServiceProvider.GetRequiredService<ITrustRootProvider>());
                var npmChecker = Assert.IsType<SigstoreNpmProvenanceChecker>(
                    scope.ServiceProvider.GetRequiredService<INpmProvenanceChecker>());

                // Inspect the dependency actually retained by the client, rather than only
                // resolving the singleton twice. Match field types, not compiler-generated
                // closure/primary-constructor field names, without adding test-only accessors.
                var verifyBundle = GetInstanceField<SigstoreBundleVerificationHandler>(npmChecker);
                Assert.NotNull(verifyBundle.Target);
                Assert.Same(verifier, GetInstanceField<SigstoreVerifier>(verifyBundle.Target));
            }
        });
    }

    [Fact]
    public void BuildApplication_CreatesPrivateTufCacheUnderAspireHome_AndDisposesProvider()
    {
        RunIsolated(static async workspacePath =>
        {
            var aspireHome = Directory.CreateDirectory(Path.Combine(workspacePath, "aspire-home"));
            using var aspireHomeOverride = new EnvVarOverride(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome.FullName);
            TufTrustRootProvider provider;
            using (var host = await BuildHostAsync())
            {
                var context = host.Services.GetRequiredService<CliExecutionContext>();
                Assert.Equal(Path.Combine(aspireHome.FullName, "cache"), context.CacheDirectory.FullName);
                provider = Assert.IsType<TufTrustRootProvider>(host.Services.GetRequiredService<ITrustRootProvider>());

                // Cache construction creates these directories without contacting the TUF service.
                var cacheDirectory = Path.Combine(context.CacheDirectory.FullName, "tuf");
                foreach (var directory in new[] { cacheDirectory, Path.Combine(cacheDirectory, "targets") })
                {
                    Assert.True(Directory.Exists(directory), $"Expected TUF cache directory: {directory}");
                    if (!OperatingSystem.IsWindows())
                    {
                        Assert.Equal(
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                            File.GetUnixFileMode(directory));
                    }
                }
            }

            // Cancellation prevents network access even if host disposal stops disposing the provider.
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                provider.GetTrustRootAsync(new CancellationToken(canceled: true)));
        });
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.Windows)]
    public void BuildApplication_UnwritableTufCache_DoesNotPreventCommandTreeCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunIsolated(static async workspacePath =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            var aspireHome = Directory.CreateDirectory(Path.Combine(workspacePath, "aspire-home"));
            var tufDirectory = Directory.CreateDirectory(Path.Combine(aspireHome.FullName, "cache", "tuf"));
            Directory.CreateDirectory(Path.Combine(tufDirectory.FullName, "targets"));
            using var identity = WindowsIdentity.GetCurrent();
            var denyFileCreation = new FileSystemAccessRule(
                identity.User!,
                FileSystemRights.CreateFiles,
                AccessControlType.Deny);
            var security = tufDirectory.GetAccessControl();
            security.AddAccessRule(denyFileCreation);
            tufDirectory.SetAccessControl(security);

            try
            {
                using var aspireHomeOverride = new EnvVarOverride(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome.FullName);
                using var host = await BuildHostAsync();

                Assert.NotNull(host.Services.GetRequiredService<RootCommand>());
                Assert.IsType<TufTrustRootProvider>(host.Services.GetRequiredService<ITrustRootProvider>());
            }
            finally
            {
                security.RemoveAccessRuleSpecific(denyFileCreation);
                tufDirectory.SetAccessControl(security);
            }
        });
    }

    [Fact]
    [OuterloopTest("Requires network access to the public npm registry and Sigstore TUF service")]
    public void BuildApplication_VerifiesLatestPlaywrightNpmProvenance_UsingConfiguredTufCache()
    {
        RunIsolated(static async workspacePath =>
        {
            var aspireHome = Directory.CreateDirectory(Path.Combine(workspacePath, "aspire-home"));
            using var aspireHomeOverride = new EnvVarOverride(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome.FullName);
            using var host = await BuildHostAsync();
            using var timeoutSource = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(milliseconds: 120_000);
            var cancellationToken = timeoutSource.Token;
            using var httpClient = new HttpClient();

            // Intentionally use the live latest release in outerloop: recorded bundles cannot
            // detect npm provenance changes that would break secure Playwright installation.
            var package = await httpClient.GetFromJsonAsync<JsonElement>(
                $"https://registry.npmjs.org/{Uri.EscapeDataString(PlaywrightCliInstaller.PackageName)}/latest",
                cancellationToken);
            Assert.Equal(PlaywrightCliInstaller.PackageName, package.GetProperty("name").GetString());
            var version = package.GetProperty("version").GetString();
            Assert.False(string.IsNullOrEmpty(version));
            Console.WriteLine($"Verifying {PlaywrightCliInstaller.PackageName}@{version}");

            var distribution = package.GetProperty("dist");
            var tarballUrl = distribution.GetProperty("tarball").GetString();
            Assert.NotNull(tarballUrl);
            using var tarball = await httpClient.GetStreamAsync(tarballUrl, cancellationToken);
            var digest = await SHA512.HashDataAsync(tarball, cancellationToken);
            var integrity = $"sha512-{Convert.ToBase64String(digest)}";
            Assert.Equal(distribution.GetProperty("integrity").GetString(), integrity);

            var checker = host.Services.GetRequiredService<INpmProvenanceChecker>();
            var result = await checker.VerifyProvenanceAsync(
                PlaywrightCliInstaller.PackageName,
                version,
                PlaywrightCliInstaller.ExpectedSourceRepository,
                PlaywrightCliInstaller.ExpectedWorkflowPath,
                PlaywrightCliInstaller.ExpectedBuildType,
                refInfo => string.Equals(refInfo.Kind, "tags", StringComparison.Ordinal) &&
                           (string.Equals(refInfo.Name, version, StringComparison.Ordinal) ||
                            string.Equals(refInfo.Name, $"v{version}", StringComparison.Ordinal)),
                integrity,
                cancellationToken);

            Assert.True(result.IsVerified, $"Provenance verification failed for {PlaywrightCliInstaller.PackageName}@{version}: {result.Outcome}");

            var context = host.Services.GetRequiredService<CliExecutionContext>();
            Assert.Equal(Path.Combine(aspireHome.FullName, "cache"), context.CacheDirectory.FullName);
            var cacheDirectory = Path.Combine(context.CacheDirectory.FullName, "tuf");
            var cache = new FileSystemTufCache(cacheDirectory);
            foreach (var role in new[] { "root", "timestamp", "snapshot", "targets" })
            {
                Assert.NotEmpty(Assert.IsType<byte[]>(cache.LoadMetadata(role)));
            }
            Assert.NotEmpty(Assert.IsType<byte[]>(cache.LoadTarget("trusted_root.json")));

            if (!OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories))
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                }
            }
        });
    }

    [Fact]
    public void BuildApplication_CliExecutionContextChannel_MatchesAssemblyMetadataAttribute()
    {
        RunIsolated(static async workspacePath =>
        {
            // End-to-end coherence: the channel flowing through the DI container must equal the
            // value baked into the test assembly by [AssemblyMetadata("AspireCliChannel", "...")].
            // IdentityChannelReader reads from typeof(Aspire.Cli.Program).Assembly; this test
            // reads the test assembly directly (RemoteExecutor has its own entry assembly) and the comparison works because
            // Aspire.Cli.csproj and the test csproj forward the same $(AspireCliChannel) MSBuild
            // property — keeping both assemblies in lockstep regardless of the build configuration
            // (so this test is also correct on /p:AspireCliChannel=stable or pr-<N> CI builds).
            var bakedChannel = GetBakedEntryAssemblyChannel();

            using var host = await BuildHostAsync();

            var context = host.Services.GetRequiredService<CliExecutionContext>();

            Assert.Equal(bakedChannel, context.IdentityChannel);
        });
    }

    [Fact]
    public void BuildApplication_ConfigurationValuesOverrideGlobalSettingsFile()
    {
        RunIsolated(static async workspacePath =>
        {
            var aspireHome = Directory.CreateDirectory(Path.Combine(workspacePath, "aspire-home"));
            var ambientNssDbDirectory = Directory.CreateDirectory(Path.Combine(workspacePath, "ambient-nssdb"));
            var configuredNssDbDirectory = Directory.CreateDirectory(Path.Combine(workspacePath, "configured-nssdb"));
            var globalConfig = JsonSerializer.Serialize(new
            {
                certificates = new
                {
                    nssDbPaths = $"firefox={ambientNssDbDirectory.FullName}"
                }
            });
            File.WriteAllText(Path.Combine(aspireHome.FullName, AspireConfigFile.FileName), globalConfig);

            using var aspireHomeOverride = new EnvVarOverride(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome.FullName);
            using var host = await BuildHostAsync(new Dictionary<string, string?>
            {
                [CertificateConfiguration.NssDbPathsConfigPath] = $"firefox={configuredNssDbDirectory.FullName}"
            });

            var configuration = host.Services.GetRequiredService<IConfiguration>();

            Assert.Equal($"firefox={configuredNssDbDirectory.FullName}", configuration[CertificateConfiguration.NssDbPathsConfigPath]);
        });
    }

    [Fact]
    public void BuildApplication_ConfiguresCertificateManagerWithNssDbPaths()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "NSS certificate trust is only configured on Linux.");

        RunIsolated(static async workspacePath =>
        {
            var nssDbDirectory = Directory.CreateDirectory(Path.Combine(workspacePath, "nssdb"));
            using var host = await BuildHostAsync(new Dictionary<string, string?>
            {
                [CertificateConfiguration.NssDbPathsConfigPath] = $"firefox={nssDbDirectory.FullName}"
            });

            var manager = Assert.IsType<UnixCertificateManager>(host.Services.GetRequiredService<CertificateManager>());
            var nssDb = Assert.Single(manager.GetNssDbs(workspacePath));

            Assert.Equal(nssDbDirectory.FullName, nssDb.Path);
        });
    }

    [Fact]
    public void ParseLoggingOptions_PrInstall_UsesInstallPrefixForDefaultLogsDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var installPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire-pr-test");
        var binaryPath = WriteBinaryWithSidecar(Path.Combine(installPrefix, "dogfood", "pr-17159", "bin"), InstallSourceExtensions.PrWire);

        var loggingOptions = Program.ParseLoggingOptions([], binaryPath);

        Assert.Equal(Path.Combine(installPrefix, "logs"), loggingOptions.LogsDirectory);
        Assert.Equal(loggingOptions.LogsDirectory, Path.GetDirectoryName(loggingOptions.LogFilePath));
    }

    [Fact]
    public void BuildCliExecutionContext_PrInstall_UsesInstallPrefixForStateDirectoriesAndKeepsIdentityChannel()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var installPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire-pr-test");
        var binaryDir = Path.Combine(installPrefix, "dogfood", "pr-17159", "bin");
        var binaryPath = WriteBinaryWithSidecar(binaryDir, InstallSourceExtensions.PrWire, channel: "pr-17159");
        var logsDirectory = Path.Combine(installPrefix, "logs");
        var logFilePath = Path.Combine(logsDirectory, "aspire.log");

        var environment = new TestEnvironment();
        var resolver = new IdentityResolver(
            CliTestHelper.CreateSidecarReader(outputHelper),
            typeof(Program).Assembly,
            binaryDir,
            environment);

        var context = Program.BuildCliExecutionContext(
            debugMode: true,
            consoleLogLevel: null,
            logsDirectory: logsDirectory,
            logFilePath: logFilePath,
            identityResolver: resolver,
            processPath: binaryPath);

        Assert.Equal(Path.Combine(installPrefix, "hives"), context.HivesDirectory.FullName);
        Assert.Equal(Path.Combine(installPrefix, "cache"), context.CacheDirectory.FullName);
        Assert.Equal(Path.Combine(installPrefix, "sdks"), context.SdksDirectory.FullName);
        Assert.Equal(Path.Combine(installPrefix, "packages"), context.PackagesDirectory?.FullName);
        Assert.Equal(installPrefix, context.AspireHomeDirectory.FullName);
        Assert.Equal(logsDirectory, context.LogsDirectory.FullName);
        Assert.Equal(logFilePath, context.LogFilePath);
        Assert.True(context.DebugMode);
        Assert.Equal("pr-17159", context.IdentityChannel);
    }

    [Fact]
    public void BuildCliExecutionContext_NuGetServiceIndexOverrideFromEnv_MarksIdentityOverridden()
    {
        // Setting only ASPIRE_CLI_NUGET_SERVICE_INDEX must still flag the run as an emulation so the
        // startup override notice fires and tooling does not mistake a diagnostic run for a real build.
        // Regression guard: this source was previously omitted from the identityOverridden computation.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var envVars = new Dictionary<string, string?> { [IdentityResolver.NuGetServiceIndexEnvVar] = "http://localhost:5000/v3/index.json" };
        var environment = new TestEnvironment(envVars);
        var resolver = new IdentityResolver(
            CliTestHelper.CreateSidecarReader(outputHelper),
            typeof(Program).Assembly,
            binaryDir: null,
            environment);

        var context = Program.BuildCliExecutionContext(
            debugMode: false,
            consoleLogLevel: null,
            logsDirectory: Path.Combine(workspace.WorkspaceRoot.FullName, "logs"),
            logFilePath: Path.Combine(workspace.WorkspaceRoot.FullName, "logs", "aspire.log"),
            identityResolver: resolver);

        Assert.True(context.IdentityOverridden);
        Assert.Equal("http://localhost:5000/v3/index.json", context.NuGetServiceIndexOverride);
    }

    [Fact]
    public void BuildCliExecutionContext_NoOverrides_DoesNotMarkIdentityOverridden()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = new TestEnvironment();
        var resolver = new IdentityResolver(
            CliTestHelper.CreateSidecarReader(outputHelper),
            typeof(Program).Assembly,
            binaryDir: null,
            environment);

        var context = Program.BuildCliExecutionContext(
            debugMode: false,
            consoleLogLevel: null,
            logsDirectory: Path.Combine(workspace.WorkspaceRoot.FullName, "logs"),
            logFilePath: Path.Combine(workspace.WorkspaceRoot.FullName, "logs", "aspire.log"),
            identityResolver: resolver);

        Assert.False(context.IdentityOverridden);
        Assert.Null(context.NuGetServiceIndexOverride);
    }

#if DEBUG
    [Theory]
    [InlineData("ls --cli-wait-for-debugger")]
    [InlineData("run --cli-wait-for-debugger")]
    [InlineData("doctor --cli-wait-for-debugger")]
    public void WaitForDebuggerIfRequested_WithSubcommand_CallsShowStatus(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var parseResult = command.Parse(commandLine);

        var waitActionCalled = false;
        Program.WaitForDebuggerIfRequested(parseResult, provider, waitAction: () => waitActionCalled = true);

        Assert.True(waitActionCalled);
        var expectedStatus = string.Format(CultureInfo.CurrentCulture, RootCommandStrings.WaitingForDebugger, Environment.ProcessId);
        Assert.Collection(testInteractionService.ShownStatuses, status => Assert.Equal(expectedStatus, status));
    }

    [Fact]
    public void WaitForDebuggerIfRequested_WithoutFlag_DoesNotCallShowStatus()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var parseResult = command.Parse("ls");

        var waitActionCalled = false;
        Program.WaitForDebuggerIfRequested(parseResult, provider, waitAction: () => waitActionCalled = true);

        Assert.False(waitActionCalled);
        Assert.Empty(testInteractionService.ShownStatuses);
    }
#endif

    private static T GetInstanceField<T>(object instance)
    {
        var field = Assert.Single(instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType == typeof(T));

        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static string WriteBinaryWithSidecar(string binaryDir, string source, string? channel = null)
    {
        Directory.CreateDirectory(binaryDir);
        var binaryPath = Path.Combine(binaryDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(binaryPath, string.Empty);
        var channelField = channel is not null ? $",\"channel\":\"{channel}\"" : "";
        File.WriteAllText(Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName), $$"""{"source":"{{source}}"{{channelField}}}""");

        return binaryPath;
    }
}
