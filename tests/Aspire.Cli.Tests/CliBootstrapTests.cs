// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Cli.Tests;

/// <summary>
/// Integration tests for the bootstrap wiring: the running CLI's
/// <see cref="CliExecutionContext.IdentityChannel"/> is sourced from the binary's
/// <c>[AssemblyMetadata("AspireCliChannel")]</c> value via
/// <see cref="IIdentityChannelReader"/>, registered in DI by
/// <see cref="Aspire.Cli.Program.BuildApplicationAsync"/>.
/// </summary>
public class CliBootstrapTests(ITestOutputHelper outputHelper)
{
    private static readonly string[] s_fixedChannels = ["stable", "staging", "daily", "local"];

    private static async Task<IHost> BuildHostAsync()
    {
        var loggingOptions = Program.ParseLoggingOptions([]);
        var errorWriter = new TestStartupErrorWriter();
        var logBufferContext = new ConsoleLogBufferContext();
        var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory([], loggingOptions, errorWriter, logBufferContext);
        var identityChannelReader = new IdentityChannelReader(typeof(Program).Assembly);
        var startupContext = new Program.CliStartupContext(loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, logBufferContext, loggerFactory.CreateLogger(Program.RootLoggerName), new ConsoleCancellationManager(processTerminationTimeout: Timeout.InfiniteTimeSpan), identityChannelReader);
        return await Program.BuildApplicationAsync([], startupContext);
    }

    private static string GetBakedEntryAssemblyChannel()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        Assert.NotNull(entryAssembly);
        var bakedChannel = entryAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "AspireCliChannel", StringComparison.Ordinal))
            .Value;
        Assert.False(string.IsNullOrEmpty(bakedChannel));
        return bakedChannel!;
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
    public async Task BuildApplication_RegistersIIdentityChannelReader_AsIdentityChannelReaderInstance()
    {
        // Program.BuildApplicationAsync registers IIdentityChannelReader as a singleton,
        // backed by the default IdentityChannelReader (which reads from
        // typeof(Aspire.Cli.Program).Assembly).
        using var host = await BuildHostAsync();

        var reader = host.Services.GetRequiredService<IIdentityChannelReader>();

        Assert.NotNull(reader);
        Assert.IsType<IdentityChannelReader>(reader);
    }

    [Fact]
    public async Task BuildApplication_PopulatesCliExecutionContextChannel_FromIdentityChannelReader()
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
    }

    [Fact]
    public async Task BuildApplication_CliExecutionContextChannel_MatchesAssemblyMetadataAttribute()
    {
        // End-to-end coherence: the channel flowing through the DI container must equal the
        // value baked into the entry assembly by [AssemblyMetadata("AspireCliChannel", "...")].
        // IdentityChannelReader reads from typeof(Aspire.Cli.Program).Assembly; this test
        // reads Assembly.GetEntryAssembly() directly and the comparison works because
        // Aspire.Cli.csproj and the test csproj forward the same $(AspireCliChannel) MSBuild
        // property — keeping both assemblies in lockstep regardless of the build configuration
        // (so this test is also correct on /p:AspireCliChannel=stable or pr-<N> CI builds).
        var bakedChannel = GetBakedEntryAssemblyChannel();

        using var host = await BuildHostAsync();

        var context = host.Services.GetRequiredService<CliExecutionContext>();

        Assert.Equal(bakedChannel, context.IdentityChannel);
    }

    [Fact]
    public void ParseLoggingOptions_PrInstall_UsesInstallPrefixForDefaultLogsDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var installPrefix = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire-pr-test");
        var binaryPath = WriteBinaryWithSidecar(Path.Combine(installPrefix, "dogfood", "pr-17159", "bin"), InstallSourceExtensions.PrWire);

        var loggingOptions = Program.ParseLoggingOptions([], binaryPath);

        Assert.Equal(Path.Combine(installPrefix, "logs"), loggingOptions.LogsDirectory);
        Assert.Equal(loggingOptions.LogsDirectory, Path.GetDirectoryName(loggingOptions.LogFilePath));
    }

    [Fact]
    public void BuildCliExecutionContext_PrInstall_UsesInstallPrefixForStateDirectoriesAndKeepsIdentityChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var envVars = new Dictionary<string, string?> { [IdentityResolver.NuGetServiceIndexEnvVar] = "http://localhost:5000/v3/index.json" };
        var environment = new TestEnvironment(envVars);
        var resolver = new IdentityResolver(
            CliTestHelper.CreateSidecarReader(outputHelper),
            typeof(Program).Assembly,
            binaryDir: null,
            environment);

        var context = Program.BuildCliExecutionContext(
            debugMode: false,
            logsDirectory: Path.Combine(workspace.WorkspaceRoot.FullName, "logs"),
            logFilePath: Path.Combine(workspace.WorkspaceRoot.FullName, "logs", "aspire.log"),
            identityResolver: resolver);

        Assert.True(context.IdentityOverridden);
        Assert.Equal("http://localhost:5000/v3/index.json", context.NuGetServiceIndexOverride);
    }

    [Fact]
    public void BuildCliExecutionContext_NoOverrides_DoesNotMarkIdentityOverridden()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var environment = new TestEnvironment();
        var resolver = new IdentityResolver(
            CliTestHelper.CreateSidecarReader(outputHelper),
            typeof(Program).Assembly,
            binaryDir: null,
            environment);

        var context = Program.BuildCliExecutionContext(
            debugMode: false,
            logsDirectory: Path.Combine(workspace.WorkspaceRoot.FullName, "logs"),
            logFilePath: Path.Combine(workspace.WorkspaceRoot.FullName, "logs", "aspire.log"),
            identityResolver: resolver);

        Assert.False(context.IdentityOverridden);
        Assert.Null(context.NuGetServiceIndexOverride);
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
