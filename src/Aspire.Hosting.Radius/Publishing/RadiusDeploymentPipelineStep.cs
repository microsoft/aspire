// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.Diagnostics;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Pipeline step that deploys a Radius application by invoking <c>rad deploy app.bicep</c>.
/// Depends on the publish step only (not <see cref="WellKnownPipelineSteps.Push"/>)
/// to support kind clusters without a container registry.
/// </summary>
internal sealed class RadiusDeploymentPipelineStep
{
    private const string RadInstallUrl = "https://docs.radapp.io/installation/";

    private readonly RadiusEnvironmentResource _environment;

    internal RadiusDeploymentPipelineStep(RadiusEnvironmentResource environment)
    {
        _environment = environment;
    }

    /// <summary>
    /// Creates a <see cref="PipelineStep"/> that deploys a Radius application via <c>rad deploy</c>.
    /// The step depends on the publish step for this environment (Bicep must be generated first).
    /// </summary>
    internal PipelineStep CreatePipelineStep()
    {
        var step = new PipelineStep
        {
            Name = $"deploy-radius-{_environment.Name}",
            Description = $"Deploy Radius environment '{_environment.Name}' via rad CLI",
            Action = ExecuteAsync
        };
        step.DependsOn($"publish-radius-{_environment.Name}");
        step.RequiredBy(WellKnownPipelineSteps.Deploy);
        step.DependsOn(WellKnownPipelineSteps.DeployPrereq);
        return step;
    }

    internal async Task ExecuteAsync(PipelineStepContext context)
    {
        var cancellationToken = context.CancellationToken;
        var logger = context.Logger;

        // Detect rad CLI availability
        var radAvailable = await DetectRadCliAsync(cancellationToken).ConfigureAwait(false);
        if (!radAvailable)
        {
            logger.LogError("The 'rad' CLI was not found on PATH. Install it from {InstallUrl}", RadInstallUrl);
            throw new InvalidOperationException(
                $"The 'rad' CLI was not found. Please install it from {RadInstallUrl} and ensure it is available on your PATH.");
        }

        logger.LogInformation("rad CLI detected on PATH for environment '{EnvironmentName}'", _environment.Name);

        // Resolve the output directory where Bicep was generated
        var outputDir = PublishingContextUtils.GetEnvironmentOutputPath(context, _environment);
        var bicepPath = Path.Combine(outputDir, "app.bicep");

        if (!File.Exists(bicepPath))
        {
            logger.LogError("Bicep file not found at {BicepPath}. Ensure the publish step completed successfully.", bicepPath);
            throw new InvalidOperationException(
                $"Bicep file not found at '{bicepPath}'. Ensure the publish step completed successfully before deploying.");
        }

        logger.LogInformation("Starting rad deploy with Bicep file '{BicepPath}'", bicepPath);

        var deployTask = await context.ReportingStep.CreateTaskAsync(
            $"Deploying Radius environment '{_environment.Name}' via rad deploy...",
            cancellationToken).ConfigureAwait(false);

        try
        {
            var stderrBuilder = new System.Text.StringBuilder();

            using var process = new Process();
            // Use ArgumentList rather than Arguments so the bicep path doesn't need shell-style
            // quoting and is forwarded verbatim — paths containing spaces or special characters
            // (e.g. `C:\Program Files\…`) survive intact on every platform without double-escaping.
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "rad",
                WorkingDirectory = outputDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("deploy");
            process.StartInfo.ArgumentList.Add(bicepPath);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    logger.LogInformation("rad (stdout): {Output}", e.Data);
                    context.ReportingStep.Log(LogLevel.Information, e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderrBuilder.AppendLine(e.Data);
                    logger.LogWarning("rad (stderr): {Error}", e.Data);
                    context.ReportingStep.Log(LogLevel.Warning, e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    logger.LogWarning("Cancellation requested — terminating rad deploy process for environment '{EnvironmentName}'", _environment.Name);
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Race: the process exited between HasExited check and Kill. Nothing to do.
                    }
                }

                throw;
            }

            var exitCode = process.ExitCode;

            logger.LogInformation("rad deploy exited with code {ExitCode} for environment '{EnvironmentName}'", exitCode, _environment.Name);

            if (exitCode != 0)
            {
                var stderrText = stderrBuilder.ToString().Trim();
                var errorMessage = string.IsNullOrEmpty(stderrText)
                    ? $"rad deploy failed with exit code {exitCode}"
                    : $"rad deploy failed with exit code {exitCode}: {stderrText}";

                logger.LogError("rad deploy failed for environment '{EnvironmentName}': {ErrorMessage}", _environment.Name, errorMessage);
                context.ReportingStep.Log(LogLevel.Error, errorMessage);

                throw new InvalidOperationException(errorMessage);
            }

            await deployTask.CompleteAsync(
                $"Radius deployment complete for '{_environment.Name}'",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected error during rad deploy for environment '{EnvironmentName}'", _environment.Name);
            context.ReportingStep.Log(LogLevel.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Detects whether the <c>rad</c> CLI is available on the system PATH.
    /// </summary>
    internal static async Task<bool> DetectRadCliAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "rad",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("version");

            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            // Honour cancellation — never swallow this; the caller (and CTRL-C) must observe it.
            throw;
        }
        catch
        {
            // rad is not found on PATH, or some other launch failure (e.g. EACCES).
            // Treat as "not available" so the deploy step surfaces a clear install message instead
            // of a stack trace pointing at Process.Start.
            return false;
        }
    }
}
