// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Runs the app host like <c>aspire run</c>, but bounds the lifetime of the run to the resources
/// marked as test resources (via <c>WithTestRun()</c>). Once those resources reach a terminal state
/// the app host shuts down automatically.
/// </summary>
internal sealed class TestCommand : RunCommand
{
    public TestCommand(
        IDotNetCliRunner runner,
        ICertificateService certificateService,
        IProjectLocator projectLocator,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger<RunCommand> logger,
        IAppHostProjectFactory projectFactory,
        AppHostLauncher appHostLauncher,
        FileLoggerProvider fileLoggerProvider,
        ICliHostEnvironment hostEnvironment,
        ProfilingTelemetry profilingTelemetry,
        TimeProvider timeProvider,
        CommonCommandServices services)
        : base("test", TestCommandStrings.Description, runner, certificateService, projectLocator, configuration, serviceProvider, logger, projectFactory, appHostLauncher, fileLoggerProvider, hostEnvironment, profilingTelemetry, timeProvider, services)
    {
    }

    protected override void ConfigureAppHostEnvironment(IDictionary<string, string> environmentVariables)
    {
        // Signal the app host's TestLoopCoordinator to bound the run to the test resources.
        environmentVariables[KnownConfigNames.CliTestMode] = "true";
    }
}
