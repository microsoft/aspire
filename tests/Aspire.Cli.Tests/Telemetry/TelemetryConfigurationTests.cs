// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if DEBUG
using Microsoft.AspNetCore.InternalTesting;
#endif

namespace Aspire.Cli.Tests.Telemetry;

public class TelemetryConfigurationTests
{
    // The Aspire.Cli.Tests assembly opts out of Azure Monitor telemetry by default
    // (see TestTelemetryDefaults). Tests that need the Azure Monitor branch override
    // the env-var-derived opt-out by passing this in their in-memory configuration —
    // AddInMemoryCollection is added AFTER AddEnvironmentVariables in
    // Program.BuildApplicationAsync, so the in-memory value wins.
    private static readonly KeyValuePair<string, string?> s_telemetryOptInOverride =
        new(AspireCliTelemetry.TelemetryOptOutConfigKey, "false");

    private static Dictionary<string, string?> WithTelemetryOptIn(Dictionary<string, string?>? config = null)
    {
        var result = config is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(config);
        result[s_telemetryOptInOverride.Key] = s_telemetryOptInOverride.Value;
        return result;
    }

    private static async Task<IHost> BuildHostAsync(Dictionary<string, string?>? config = null)
    {
        var loggingOptions = Program.ParseLoggingOptions([]);
        var errorWriter = new TestStartupErrorWriter();
        var logBufferContext = new ConsoleLogBufferContext();
        var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory([], loggingOptions, errorWriter, logBufferContext);
        var identityChannelReader = new IdentityChannelReader(typeof(Program).Assembly);
        var startupContext = new Program.CliStartupContext(loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, logBufferContext, loggerFactory.CreateLogger(Program.RootLoggerName), new ConsoleCancellationManager(processTerminationTimeout: Timeout.InfiniteTimeSpan), identityChannelReader);
        return await Program.BuildApplicationAsync([], startupContext, config);
    }

    [Fact]
    public async Task AzureMonitor_Enabled_ByDefault()
    {
        // The Application Insights connection string is hardcoded, so Azure Monitor
        // should be enabled when telemetry is not opted out. The test process opts out
        // by default (see TestTelemetryDefaults); we explicitly opt back in here to
        // exercise the Azure Monitor branch.
        using var host = await BuildHostAsync(WithTelemetryOptIn());

        var telemetryManager = host.Services.GetService<TelemetryManager>();
        Assert.NotNull(telemetryManager);
        Assert.True(telemetryManager.HasAzureMonitor, "Expected TelemetryManager to have Azure Monitor enabled by default");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public async Task AzureMonitor_Disabled_WhenOptOutSetToTrueValues(string optOutValue)
    {
        var config = new Dictionary<string, string?>
        {
            [AspireCliTelemetry.TelemetryOptOutConfigKey] = optOutValue
        };

        using var host = await BuildHostAsync(config);

        var telemetryManager = host.Services.GetRequiredService<TelemetryManager>();
        // When telemetry is opted out, Azure Monitor should not be enabled
        Assert.False(telemetryManager.HasAzureMonitor, $"Expected Azure Monitor to be disabled when telemetry opt-out is '{optOutValue}'");
    }

    [Fact]
    public async Task OtlpExporter_WithoutProfiling_EnablesOnlyDebugDiagnostics_WhenEndpointProvided()
    {
        var config = WithTelemetryOptIn(new Dictionary<string, string?>
        {
            [AspireCliTelemetry.OtlpExporterEndpointConfigKey] = "http://localhost:4317"
        });

        using var host = await BuildHostAsync(config);

        var telemetryManager = host.Services.GetRequiredService<TelemetryManager>();

#if DEBUG
        Assert.True(telemetryManager.HasDiagnosticProvider, "Expected TelemetryManager to have diagnostic provider enabled when OTLP endpoint is configured in DEBUG mode");
        Assert.False(telemetryManager.HasProfilingProvider, "Expected profiling export to require explicit profiling opt-in");
        // Azure Monitor is also enabled since connection string is hardcoded
        Assert.True(telemetryManager.HasAzureMonitor, "Expected TelemetryManager to have Azure Monitor enabled (connection string is hardcoded)");
#else
        // In RELEASE mode, diagnostic OTLP export requires explicit profiling opt-in.
        Assert.False(telemetryManager.HasDiagnosticProvider, "Expected TelemetryManager to require profiling before enabling diagnostic OTLP export in RELEASE mode");
        Assert.False(telemetryManager.HasProfilingProvider, "Expected profiling export to require explicit profiling opt-in");
        Assert.True(telemetryManager.HasAzureMonitor, "Expected Azure Monitor to be enabled (connection string is hardcoded)");
#endif
    }

    [Fact]
    public void OtlpExporter_WithDetachedNonProfilingContext_DoesNotEnableProfilingProvider()
    {
        using var listener = CreateActivityListener("test-detached-non-profiling-context");
        using var source = new ActivitySource("test-detached-non-profiling-context");
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);

        var config = AppHostLauncher.CreateDetachedChildEnvironment(activity);
        config[AspireCliTelemetry.OtlpExporterEndpointConfigKey] = "http://localhost:4317";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

        using var manager = new TelemetryManager(configuration);

        Assert.False(manager.HasProfilingProvider, "Expected detached child profiling export to require an actual profiling session");
    }

    [Fact]
    public async Task OtlpExporter_WithProfiling_EnablesProfilingProviderWhenTelemetryOptedOut()
    {
        var config = new Dictionary<string, string?>
        {
            [AspireCliTelemetry.TelemetryOptOutConfigKey] = "true",
            [AspireCliTelemetry.OtlpExporterEndpointConfigKey] = "http://localhost:4317",
            [Aspire.Hosting.KnownConfigNames.ProfilingEnabled] = "true"
        };

        using var host = await BuildHostAsync(config);

        var telemetryManager = host.Services.GetRequiredService<TelemetryManager>();

        Assert.False(telemetryManager.HasAzureMonitor, "Expected Azure Monitor to honor telemetry opt-out");
        Assert.False(telemetryManager.HasDiagnosticProvider, "Expected profiling export to stay separate from debug diagnostics");
        Assert.True(telemetryManager.HasProfilingProvider, "Expected profiling OTLP export to work even when reported telemetry is opted out");
    }

    [Fact]
    public async Task OtlpExporter_WithProfiling_KeepsReportedTelemetryAndProfilingSeparate()
    {
        var config = WithTelemetryOptIn(new Dictionary<string, string?>
        {
            [AspireCliTelemetry.OtlpExporterEndpointConfigKey] = "http://localhost:4317",
            [Aspire.Hosting.KnownConfigNames.ProfilingEnabled] = "true"
        });

        using var host = await BuildHostAsync(config);

        var telemetryManager = host.Services.GetRequiredService<TelemetryManager>();

        Assert.True(telemetryManager.HasAzureMonitor, "Expected reported telemetry to keep using the Azure Monitor provider");
        Assert.True(telemetryManager.HasProfilingProvider, "Expected profiling telemetry to use the profiling provider");
        Assert.False(telemetryManager.HasDiagnosticProvider, "Expected profiling OTLP export to avoid the debug diagnostics provider");
    }

#if DEBUG
    [Fact]
    public async Task DiagnosticProvider_IncludesDiagnosticActivitySource()
    {
        // Configure console exporter at Diagnostic level to enable the diagnostic provider
        var config = new Dictionary<string, string?>
        {
            [AspireCliTelemetry.TelemetryOptOutConfigKey] = "true",
            [AspireCliTelemetry.ConsoleExporterLevelConfigKey] = "Diagnostic"
        };

        using var host = await BuildHostAsync(config);

        var telemetryManager = host.Services.GetRequiredService<TelemetryManager>();
        Assert.False(telemetryManager.HasAzureMonitor);
        Assert.True(telemetryManager.HasDiagnosticProvider);
        Assert.False(telemetryManager.HasProfilingProvider);

        var telemetry = host.Services.GetRequiredService<AspireCliTelemetry>();
        await telemetry.InitializeAsync().DefaultTimeout();

        using var diagnosticActivity = telemetry.StartDiagnosticActivity("TestDiagnosticActivity");
        Assert.NotNull(diagnosticActivity);
    }
#endif

    [Fact]
    public void AzureMonitor_Disabled_WhenVersionFlagProvided()
    {
        var configuration = new ConfigurationBuilder().Build();

        var manager = new TelemetryManager(configuration, ["--version"]);

        Assert.False(manager.HasAzureMonitor);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void AzureMonitor_Disabled_ForAllHelpFlags(string flag)
    {
        var configuration = new ConfigurationBuilder().Build();

        var manager = new TelemetryManager(configuration, [flag]);

        Assert.False(manager.HasAzureMonitor);
    }

    private static ActivityListener CreateActivityListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
