// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Diagnostics;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class DistributedApplicationLifecycle(
    ILogger<DistributedApplication> logger,
    IConfiguration configuration,
    ProfilingTelemetry profilingTelemetry,
    DistributedApplicationExecutionContext executionContext,
    LocaleOverrideContext localeOverrideContext) : IHostedLifecycleService
{
    private ProfilingTelemetry.ActivityScope _hostStartupActivity;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _hostStartupActivity.AddAppHostHostStarted();
        DisposeHostStartupActivity();

        if (executionContext.IsRunMode && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Distributed application started. Press Ctrl+C to shut down.");
        }

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _hostStartupActivity = profilingTelemetry.StartAppHostHostStartup();
        _hostStartupActivity.AddAppHostHostStarting();

        if (AssemblyVersionHelper.GetInformationalVersion(GetType().Assembly) is { Length: > 0 } informationalVersion)
        {
            // Write version at info level so it's written to the console by default. Help us debug user issues.
            // Display version and commit like 8.0.0-preview.2.23619.3+17dd83f67c6822954ec9a918ef2d048a78ad4697
            logger.LogInformation("Aspire AppHost version: {Version}", informationalVersion);
        }

        if (executionContext.IsRunMode)
        {
            logger.LogInformation("Distributed application starting.");
            logger.LogInformation("Application host directory is: {AppHostDirectory}", configuration["AppHost:Directory"]);
        }

        if (localeOverrideContext.OverrideErrorMessage is { Length: > 0 } localOverrideError)
        {
            logger.LogWarning(localOverrideError);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeHostStartupActivity();
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        DisposeHostStartupActivity();
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        DisposeHostStartupActivity();
        return Task.CompletedTask;
    }

    private void DisposeHostStartupActivity()
    {
        _hostStartupActivity.Dispose();
        _hostStartupActivity = default;
    }
}
