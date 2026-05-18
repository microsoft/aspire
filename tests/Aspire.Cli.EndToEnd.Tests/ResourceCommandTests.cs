// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for aspire resource command execution.
/// </summary>
public sealed class ResourceCommandTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task ResourceCommand_SetAndDeleteParameterUpdatesDescribeOutput()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"ResourceParameterCmdApp_{projectSuffix}";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace);
            await auto.InstallAspireCliAsync(strategy, counter);
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);

            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);
            var sdkLine = content.Split('\n', 2)[0].TrimEnd('\r');

            var newContent = $$"""
                {{sdkLine}}

                var builder = DistributedApplication.CreateBuilder(args);

                builder.AddParameter("greeting");

                builder.Build().Run();
                """;

            File.WriteAllText(appHostFilePath, newContent);

            await auto.TypeAsync("aspire start");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire describe greeting --format json > greeting-unset.json");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("jq -er '.resources[0].state' greeting-unset.json");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("ValueMissing", timeout: TimeSpan.FromSeconds(10));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire resource greeting set-parameter --value 'Hello world'");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Resource 'greeting' set successfully.", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("aspire describe greeting --format json > greeting-set.json");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("jq -er '.resources[0].state' greeting-set.json");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Running", timeout: TimeSpan.FromSeconds(10));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("jq -er '.resources[0].properties.Value' greeting-set.json");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Hello world", timeout: TimeSpan.FromSeconds(10));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire resource greeting delete-parameter");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Resource 'greeting' deleted successfully.", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("aspire describe greeting --format json > greeting-deleted.json");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("jq -er '.resources[0].state' greeting-deleted.json");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("ValueMissing", timeout: TimeSpan.FromSeconds(10));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire stop");
            await auto.EnterAsync();
            await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
            await auto.WaitForSuccessPromptAsync(counter);
        }
        catch
        {
            testBodyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                await auto.CaptureAspireDiagnosticsAsync(counter, workspace);
            }
            catch
            {
                // Best effort diagnostics capture.
            }

            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch
            {
                if (!testBodyFailed)
                {
                    throw;
                }
            }
        }
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task ResourceCommand_FailsWhenInteractionServiceIsRequired()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"ResourceCmdApp_{projectSuffix}";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
            await auto.InstallAspireCliAsync(strategy, counter);
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);

            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Read the generated apphost.cs so we can extract the #:sdk line with the
            // resolved version, then replace the entire file with a minimal AppHost
            // that has a placeholder resource and a command that uses IInteractionService.
            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);

            // Extract the first line (#:sdk directive) so the replacement uses the same SDK version.
            var sdkLine = content.Split('\n', 2)[0].TrimEnd('\r');

            var newContent = $$"""
                {{sdkLine}}

                #pragma warning disable ASPIREINTERACTION001

                var builder = DistributedApplication.CreateBuilder(args);

                var cache = builder.AddContainer("cache", "redis");

                cache.WithCommand(
                    name: "needs-interaction",
                    displayName: "Needs interaction",
                    executeCommand: async context =>
                    {
                        var interactionService = (IInteractionService)context.ServiceProvider.GetService(typeof(IInteractionService))!;

                        try
                        {
                            // This should throw because InteractionService is not available in non-interactive mode.
                            // Bound the wait to avoid hanging the E2E run if behavior regresses.
                            _ = await interactionService.PromptInputAsync(
                                title: "Prompt title",
                                message: "Prompt message",
                                inputLabel: "Name",
                                placeHolder: "placeholder").WaitAsync(TimeSpan.FromSeconds(10));

                            // We're looking for a failure. Treat a successful prompt completion as a test failure since that would
                            // indicate the interaction service is available when it shouldn't be.
                            return CommandResults.Success("Prompt unexpectedly completed without throwing.");
                        }
                        catch (TimeoutException)
                        {
                            // We're looking for a failure, and the most likely failure mode if the interaction service is incorrectly
                            // available would be that the prompt hangs waiting for input that will never come.
                            // Treat a timeout as a success since that indicates the interaction service is not available to show the prompt.
                            return CommandResults.Success("Prompt timed out after 10 seconds.");
                        }
                    });

                builder.Build().Run();
                """;

            File.WriteAllText(appHostFilePath, newContent);

            await auto.TypeAsync("aspire start");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire resource cache needs-interaction");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Failed to execute command 'needs-interaction' on resource 'cache'", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("InteractionService is not available", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("See logs at", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("See AppHost logs at", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

            await auto.TypeAsync($"if [ $? -eq {CliExitCodes.FailedToExecuteResourceCommand} ]; then echo RESOURCE_CMD_EXIT_CODE_CORRECT; else echo RESOURCE_CMD_UNEXPECTED_EXIT_CODE; fi");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("RESOURCE_CMD_EXIT_CODE_CORRECT", timeout: TimeSpan.FromSeconds(10));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire stop");
            await auto.EnterAsync();
            await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
            await auto.WaitForSuccessPromptAsync(counter);
        }
        catch
        {
            testBodyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                await auto.CaptureAspireDiagnosticsAsync(counter, workspace);
            }
            catch
            {
                // Best effort diagnostics capture.
            }

            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch
            {
                if (!testBodyFailed)
                {
                    throw;
                }
            }
        }
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task ResourceCommand_FailedExecution_DisplaysAppHostLogPathAndLogContainsEntries()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectSuffix = Guid.NewGuid().ToString("N")[..6];
        var projectName = $"ResourceCmdLogApp_{projectSuffix}";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        var testBodyFailed = false;

        try
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
            await auto.InstallAspireCliAsync(strategy, counter);
            await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.EmptyAppHost);

            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Replace the generated apphost.cs with a minimal AppHost that has a
            // command writing to context.Logger before returning a failure result.
            var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);

            // Extract the first line (#:sdk directive) so the replacement uses the same SDK version.
            var sdkLine = content.Split('\n', 2)[0].TrimEnd('\r');

            var newContent = $$"""
                {{sdkLine}}

                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.Logging;

                var builder = DistributedApplication.CreateBuilder(args);

                var cache = builder.AddContainer("cache", "redis");

                cache.WithCommand(
                    name: "fail-with-log",
                    displayName: "Fail with log",
                    executeCommand: context =>
                    {
                        var logger = context.ServiceProvider.GetRequiredService<ILogger<Program>>();
                        logger.LogInformation("CUSTOM_E2E_LOG_ENTRY_FOR_VERIFICATION");

                        return Task.FromResult(CommandResults.Failure("Command failed intentionally."));
                    });

                builder.Build().Run();
                """;

            File.WriteAllText(appHostFilePath, newContent);

            // Start the AppHost in detached mode.
            await auto.TypeAsync("aspire start");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter);

            // Run the failing resource command, capturing stdout and stderr so we can extract the
            // AppHost log path from the "See AppHost logs at <path>" message.
            await auto.TypeAsync("aspire resource cache fail-with-log > /tmp/resource-cmd-output.txt 2>&1");
            await auto.EnterAsync();
            await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

            await auto.TypeAsync($"if [ $? -eq {CliExitCodes.FailedToExecuteResourceCommand} ]; then echo RESOURCE_CMD_EXIT_CODE_CORRECT; else echo RESOURCE_CMD_UNEXPECTED_EXIT_CODE; fi");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("RESOURCE_CMD_EXIT_CODE_CORRECT", timeout: TimeSpan.FromSeconds(10));
            await auto.WaitForSuccessPromptAsync(counter);

            // Extract the AppHost log file path from the captured output and verify
            // it exists and is non-empty.
            // Extract the AppHost log file path from the captured output. The CLI
            // wraps paths in OSC 8 terminal hyperlinks (\e]8;...;\e\\path\e]8;;\e\\),
            // so we must strip those escape sequences before extracting the path.
            await auto.RunCommandAsync(
                "APPHOST_LOG=$(sed 's/\\x1b\\][^\\x1b]*\\x1b\\\\//g' /tmp/resource-cmd-output.txt | grep 'See AppHost logs at' | sed 's/.*See AppHost logs at //')",
                counter);

            // Debug: Show what was captured in stderr and the extracted APPHOST_LOG value.
            // Run these in a single command with || true so they always complete even if one step fails.
            await auto.RunCommandAsync(
                "echo '=== Contents of /tmp/resource-cmd-output.txt ===' && cat /tmp/resource-cmd-output.txt && echo && echo '=== APPHOST_LOG value ===' && echo \"$APPHOST_LOG\" && echo '=== End debug output ===' || true",
                counter);

            await auto.RunCommandFailFastAsync(
                "test -n \"$APPHOST_LOG\" && test -s \"$APPHOST_LOG\"",
                counter,
                TimeSpan.FromSeconds(10));

            // Verify the log file contains the custom log entry written by the
            // command handler via ILogger before returning the failure result.
            await auto.RunCommandFailFastAsync(
                "grep -q 'CUSTOM_E2E_LOG_ENTRY_FOR_VERIFICATION' \"$APPHOST_LOG\"",
                counter,
                TimeSpan.FromSeconds(10));

            // Stop the AppHost.
            await auto.TypeAsync("aspire stop");
            await auto.EnterAsync();
            await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
            await auto.WaitForSuccessPromptAsync(counter);
        }
        catch
        {
            testBodyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                await auto.CaptureAspireDiagnosticsAsync(counter, workspace);
            }
            catch
            {
                // Best effort diagnostics capture.
            }

            try
            {
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun;
            }
            catch
            {
                if (!testBodyFailed)
                {
                    throw;
                }
            }
        }
    }
}
