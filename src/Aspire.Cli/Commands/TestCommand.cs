// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Runs the app host like <c>aspire run</c>, but the CLI bounds the lifetime of the run to the
/// resources marked as test resources (via <c>WithTestRun()</c>). The CLI streams test progress over
/// the backchannel, ticks off each result as it completes, and once the stream finishes asks the app
/// host to stop. The command's exit code reflects whether the tests passed.
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
        // Signal the app host's TestRunCoordinator to observe the test resources and stream their results.
        environmentVariables[KnownConfigNames.CliTestMode] = "true";
    }

    protected override async Task<bool> TryRunCommandInteractionAsync(IAppHostCliBackchannel backchannel, int longestLocalizedLengthWithColon, CancellationToken cancellationToken)
    {
        _ = longestLocalizedLengthWithColon;

        InteractionService.DisplayEmptyLine();
        InteractionService.DisplayMessage(KnownEmojis.Microscope, TestCommandStrings.WaitingForTestResources);

        var passed = 0;
        var failed = 0;
        var sawAnyResource = false;

        try
        {
            // The stream completes once every test resource has reached a terminal state. That completion
            // is the signal to stop the app host. Running updates arrive first (so we know the set of test
            // resources); Passed/Failed updates are the terminal results we tick off as they arrive.
            await foreach (var update in backchannel.GetTestRunUpdatesAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (update.Status)
                {
                    case KnownTestRunStatuses.Running:
                        sawAnyResource = true;
                        break;

                    case KnownTestRunStatuses.Passed:
                        passed++;
                        InteractionService.DisplayMessage(
                            KnownEmojis.CheckMarkButton,
                            string.Format(CultureInfo.CurrentCulture, TestCommandStrings.TestResourcePassed, update.Resource));
                        break;

                    case KnownTestRunStatuses.Failed:
                        failed++;
                        InteractionService.DisplayMessage(
                            KnownEmojis.CrossMark,
                            string.Format(CultureInfo.CurrentCulture, TestCommandStrings.TestResourceFailed, update.Resource));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User cancelled (Ctrl+C); fall through to stop the app host below.
        }

        if (!sawAnyResource)
        {
            // No resources were marked as test resources, so the run was effectively a no-op. Surface a
            // warning but treat it as success so an app host without tests does not fail the command.
            InteractionService.DisplayMessage(KnownEmojis.Warning, TestCommandStrings.NoTestResources);
        }
        else
        {
            InteractionService.DisplayEmptyLine();
            InteractionService.DisplayMessage(
                failed == 0 ? KnownEmojis.CheckMarkButton : KnownEmojis.CrossMark,
                string.Format(CultureInfo.CurrentCulture, TestCommandStrings.TestRunSummary, passed, failed));
        }

        SetCommandOutcome(failed == 0);

        // The CLI owns the run's lifetime: now that results are in, ask the app host to stop so the
        // pending run completes and the command returns.
        await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
