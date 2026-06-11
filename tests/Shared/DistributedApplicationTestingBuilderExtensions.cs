// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Extensions for <see cref="IDistributedApplicationTestingBuilder"/>.
/// </summary>
public static class DistributedApplicationTestingBuilderExtensions
{
    // Returns the unique prefix used for volumes from unnamed volumes this builder
    public static string GetVolumePrefix(this IDistributedApplicationTestingBuilder builder) =>
        $"{VolumeNameGenerator.Sanitize(builder.Environment.ApplicationName).ToLowerInvariant()}-{builder.Configuration["AppHost:Sha256"]!.ToLowerInvariant()[..10]}";

    public static T WithTestAndResourceLogging<T>(this T builder, ITestOutputHelper testOutputHelper) where T : IDistributedApplicationBuilder
    {
        builder.Services.AddTestAndResourceLogging(testOutputHelper, builder.Configuration, builder.Environment.ApplicationName, isPublishMode: builder.ExecutionContext.IsPublishMode);
        return builder;
    }

    public static IServiceCollection AddTestAndResourceLogging(this IServiceCollection services, ITestOutputHelper testOutputHelper, IConfigurationManager configuration, string? applicationName = null, bool isPublishMode = false)
    {
        services.AddXunitLogging(testOutputHelper);
        services.AddLogging(builder =>
        {
            builder.AddFilter("Aspire.Hosting", LogLevel.Trace);
            // Suppress all console logging during tests to reduce noise
            builder.AddFilter<ConsoleLoggerProvider>(null, LogLevel.None);
        });

        if (!isPublishMode)
        {
            services.AddDcpDiagnostics(configuration, applicationName, testOutputHelper);
        }

        return services;
    }

    public static IDistributedApplicationTestingBuilder WithTempAspireStore(this IDistributedApplicationTestingBuilder builder, string? path = null)
    {
        // We create the Aspire Store in a folder with user-only access. This way non-root containers won't be able
        // to access the files unless they correctly assign the required permissions for the container to work.

        builder.Configuration["Aspire:Store:Path"] = path ?? Directory.CreateTempSubdirectory().FullName;
        return builder;
    }

    public static IDistributedApplicationTestingBuilder WithResourceCleanUp(this IDistributedApplicationTestingBuilder builder, bool? resourceCleanup = null)
    {
        builder.Configuration["DcpPublisher:WaitForResourceCleanup"] = resourceCleanup.ToString();
        return builder;
    }

    private static IServiceCollection AddDcpDiagnostics(this IServiceCollection services, IConfigurationManager configuration, string? applicationName, ITestOutputHelper testOutputHelper)
    {
        // Use Aspire:Test:DcpLogBasePath as the base path (set externally, e.g., in CI via env var ASPIRE__TEST__DCPLOGBASEPATH)
        var baseDcpLogFolder = configuration["Aspire:Test:DcpLogBasePath"];
        if (!string.IsNullOrEmpty(baseDcpLogFolder))
        {
            var uniqueId = Guid.NewGuid().ToString("N")[..8];
            var folderName = !string.IsNullOrEmpty(applicationName)
                ? $"{VolumeNameGenerator.Sanitize(applicationName).ToLowerInvariant()}-{uniqueId}"
                : uniqueId;
            var uniqueFolder = Path.Combine(baseDcpLogFolder, folderName);
            configuration["DcpPublisher:DiagnosticsLogFolder"] = uniqueFolder;
            configuration["DcpPublisher:DiagnosticsLogLevel"] = "debug";
            configuration["DcpPublisher:PreserveExecutableLogs"] = "true";

            // Register as hosted service to forward DCP logs to test output when app stops
            services.AddSingleton<IHostedService>(sp => new DcpLogForwarder(testOutputHelper, uniqueFolder));
        }

        return services;
    }

    /// <summary>
    /// Adds xunit logging and suppresses console logging for a host application builder used in tests.
    /// This redirects logs to the xunit test output and prevents console clutter during test runs.
    /// </summary>
    public static IHostApplicationBuilder AddTestLogging(this IHostApplicationBuilder builder, ITestOutputHelper testOutputHelper)
    {
        builder.Logging.AddXunit(testOutputHelper);
        builder.Logging.AddFilter<ConsoleLoggerProvider>(null, LogLevel.None);
        return builder;
    }
}

/// <summary>
/// Forwards DCP log files to xUnit test output when stopped.
/// Implements IHostedService so it gets automatically resolved and stopped when the app shuts down.
/// </summary>
/// <remarks>
/// DCP is not started in publish mode, so no logs will be available.
/// </remarks>
internal sealed class DcpLogForwarder : IHostedService
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly string _logFolder;

    public DcpLogForwarder(ITestOutputHelper testOutputHelper, string logFolder)
    {
        _testOutputHelper = testOutputHelper;
        _logFolder = logFolder;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_logFolder))
        {
            _testOutputHelper.WriteLine($"DCP log folder not found: {_logFolder}");
            return;
        }

        foreach (var logFile in Directory.GetFiles(_logFolder, "*.log"))
        {
            try
            {
                _testOutputHelper.WriteLine($"=== DCP Log: {Path.GetFileName(logFile)} ===");
                var content = await File.ReadAllTextAsync(logFile, cancellationToken);
                _testOutputHelper.WriteLine(content);
            }
            catch (Exception ex)
            {
                _testOutputHelper.WriteLine($"Failed to read DCP log {logFile}: {ex.Message}");
            }
        }
    }
}
