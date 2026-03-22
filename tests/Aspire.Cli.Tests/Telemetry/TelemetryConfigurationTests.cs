// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if DEBUG
using Microsoft.AspNetCore.InternalTesting;
#endif

namespace Aspire.Cli.Tests.Telemetry;

public class TelemetryConfigurationTests
{
    private static async Task<IHost> BuildHostAsync(Dictionary<string, string?>? config = null)
    {
        var loggingOptions = Program.ParseLoggingOptions([]);
        var errorWriter = new TestStartupErrorWriter();
        var (loggerFactory, fileLoggerProvider) = Program.CreateLoggerFactory([], loggingOptions, errorWriter);
        var startupContext = new Program.CliStartupContext(loggingOptions, errorWriter, loggerFactory, fileLoggerProvider, loggerFactory.CreateLogger<Program>());
        return await Program.BuildApplicationAsync([], startupContext, config);
    }

    [Fact]
    public async Task AzureMonitor_Enabled_ByDefault()
    {
        // The Application Insights connection string is now hardcoded, so Azure Monitor
        // should be enabled by default when telemetry is not opted out
        using var host = await BuildHostAsync();

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
    public async Task OtlpExporter_EnabledInDebugOnly_WhenEndpointProvided()
    {
        var config = new Dictionary<string, string?>
        {
            [AspireCliTelemetry.OtlpExporterEndpointConfigKey] = "http://localhost:4317"
        };

        using var host = await BuildHostAsync(config);

        var telemetryManager = host.Services.GetRequiredService<TelemetryManager>();

#if DEBUG
        Assert.True(telemetryManager.HasDiagnosticProvider, "Expected TelemetryManager to have diagnostic provider enabled when OTLP endpoint is configured in DEBUG mode");
        // Azure Monitor is also enabled since connection string is hardcoded
        Assert.True(telemetryManager.HasAzureMonitor, "Expected TelemetryManager to have Azure Monitor enabled (connection string is hardcoded)");
#else
        // In RELEASE mode, OTLP exporter is disabled, Azure Monitor is enabled by default
        Assert.True(telemetryManager.HasAzureMonitor, "Expected Azure Monitor to be enabled (connection string is hardcoded)");
#endif
    }

#if DEBUG
    [Fact]
    public async Task DiagnosticProvider_IncludesReportedActivitySource()
    {
        // Configure console exporter at Diagnostic level to enable the diagnostic provider
        var config = new Dictionary<string, string?>
        {
            [AspireCliTelemetry.ConsoleExporterLevelConfigKey] = "Diagnostic"
        };

        using var host = await BuildHostAsync(config);

        var telemetryManager = host.Services.GetRequiredService<TelemetryManager>();
        Assert.True(telemetryManager.HasDiagnosticProvider);

        var telemetry = host.Services.GetRequiredService<AspireCliTelemetry>();
        await telemetry.InitializeAsync().DefaultTimeout();

        // The diagnostic provider should listen to both activity sources.
        // Verify reported activities are captured by starting one and checking it's not null.
        using var reportedActivity = telemetry.StartReportedActivity("TestReportedActivity");
        Assert.NotNull(reportedActivity);

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
}
