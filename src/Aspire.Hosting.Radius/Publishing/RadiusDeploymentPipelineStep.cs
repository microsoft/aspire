// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
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
    internal const string RadInstallUrl = "https://docs.radapp.io/installation/";

    // Builds the user-facing exception thrown when the `rad` CLI is not found on PATH.
    // Centralized so both this deploy step and RadCredentialRegisterStep emit an identical
    // message that always includes the install link and PATH remediation — a dropped link is
    // then caught by RadCliDetectionTests.RadCliNotFoundException_ContainsInstallLinkAndRemediation exercising this method.
    internal static InvalidOperationException CreateRadCliNotFoundException() =>
        new($"The 'rad' CLI was not found. Please install it from {RadInstallUrl} and ensure it is available on your PATH.");

    // The oldest Radius control plane whose schemas match the Bicep this integration emits.
    // v0.60 renamed the `Radius.Core/recipePacks` fields `recipeKind`/`recipeLocation` to
    // `kind`/`source`. The Radius API server *drops* fields it does not recognize rather than
    // rejecting them, so deploying these artifacts to an older control plane silently produces an
    // empty recipe pack: `rad deploy` reports success and every backing resource then fails to
    // resolve a recipe. That failure mode is why this is a hard preflight rather than a README note.
    internal static Version MinimumControlPlaneVersion { get; } = new(0, 60);

    internal static InvalidOperationException CreateUnsupportedControlPlaneException(Version controlPlaneVersion) =>
        new($"The Radius control plane is v{controlPlaneVersion}, but this integration requires " +
            $"v{MinimumControlPlaneVersion} or later. Deploying to an older control plane appears to succeed while " +
            $"silently discarding the recipe pack, leaving every backing resource without a recipe. Run " +
            $"'rad upgrade kubernetes' to bring the control plane up to date, then deploy again. " +
            $"The version is read with 'rad version', which inspects the cluster in your current Kubernetes " +
            $"context — if your rad workspace targets a different cluster, switch to that cluster's context " +
            $"before deploying. " +
            $"Diagnostic: ASPIRERADIUS091.");

    /// <summary>
    /// Extracts the control plane version from <c>rad version -o json</c>.
    /// </summary>
    /// <remarks>
    /// The payload is the CLI's <c>CombinedVersionInfo</c>:
    /// <code>
    /// { "cli": { "release": "0.60.0", "version": "v0.60.0", "bicep": "0.35.1", "commit": "abc123" },
    ///   "controlPlane": { "version": "0.60.0", "status": "Installed" } }
    /// </code>
    /// When the cluster is unreachable or Radius is not installed the CLI still exits 0 and reports
    /// <c>{ "version": "Not installed", "status": "Not connected" }</c>, and an edge/dev build
    /// reports a non-numeric version such as <c>edge</c>. Every one of those is "unknown" rather
    /// than "unsupported": the gate must not fail a deploy on a version it could not read.
    /// See https://github.com/radius-project/radius/blob/main/pkg/cli/cmd/version/version.go.
    /// </remarks>
    internal static bool TryParseControlPlaneVersion(string json, out Version? version)
    {
        version = null;

        try
        {
            // Read the node as a JsonValue and ask for a string rather than calling
            // GetValue<string>() directly: a `version` that is a number, object or array would make
            // GetValue<string>() throw InvalidOperationException, which would defeat the fail-open
            // policy this method exists to implement.
            if (JsonNode.Parse(json) is not JsonObject root ||
                root["controlPlane"] is not JsonObject controlPlane ||
                controlPlane["version"] is not JsonValue versionValue ||
                !versionValue.TryGetValue<string>(out var rawVersion))
            {
                return false;
            }

            // Releases are reported bare (`0.60.0`) but tolerate the `v` prefix the CLI uses for its
            // own version, in case the two ever converge on one format.
            return Version.TryParse(rawVersion.TrimStart('v', 'V'), out version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fails the deploy when the target cluster runs a control plane older than
    /// <see cref="MinimumControlPlaneVersion"/>. A version that cannot be determined is allowed
    /// through: the gate exists to convert one specific silent failure into a loud one, not to
    /// become a new way for deploys to fail.
    /// </summary>
    /// <remarks>
    /// <c>rad version</c> takes no context flag and leaves the CLI's <c>KubeContext</c> unset, so it
    /// reports the control plane in the ambient (current) Kubernetes context. That is the same
    /// cluster <c>rad init</c> bound the workspace to in the normal case, but a user who switched
    /// contexts without switching workspaces would have a workspace pointing elsewhere. The
    /// exception message says which cluster was inspected for exactly that reason.
    /// </remarks>
    internal static async Task VerifyControlPlaneVersionAsync(ILogger logger, CancellationToken cancellationToken)
    {
        string output;

        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "rad",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("version");
            process.StartInfo.ArgumentList.Add("--output");
            process.StartInfo.ArgumentList.Add("json");

            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                // `rad` is not on PATH. The deploy step's own detection reports that; this gate
                // has nothing to check.
                logger.LogDebug(ex, "Could not run 'rad version --output json'; skipping the control plane version check.");
                return;
            }

            // Read both pipes concurrently, and start reading before waiting: draining only stdout
            // lets a chatty stderr fill its pipe buffer, block the child, and hang the wait forever.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                output = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposing Process only releases the handle, so without this the cancelled deploy
                // would leave `rad` — and the Helm/Kubernetes work it started — running.
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Race: the process exited between the HasExited check and Kill. Nothing to do.
                    }
                }

                throw;
            }

            if (process.ExitCode != 0)
            {
                logger.LogDebug("'rad version --output json' exited with code {ExitCode}; skipping the control plane version check.", process.ExitCode);
                return;
            }
        }

        if (!TryParseControlPlaneVersion(output, out var controlPlaneVersion) || controlPlaneVersion is null)
        {
            logger.LogDebug("Could not determine the Radius control plane version; skipping the control plane version check.");
            return;
        }

        if (controlPlaneVersion < MinimumControlPlaneVersion)
        {
            throw CreateUnsupportedControlPlaneException(controlPlaneVersion);
        }

        logger.LogInformation("Radius control plane v{ControlPlaneVersion} detected.", controlPlaneVersion);
    }

    private readonly RadiusEnvironmentResource _environment;

    internal RadiusDeploymentPipelineStep(RadiusEnvironmentResource environment)
    {
        _environment = environment;
    }

    internal static async Task<bool> DetectRadCliAsync(CancellationToken cancellationToken = default)
    {
        // Honour cancellation up front so a pre-cancelled token surfaces as
        // OperationCanceledException regardless of whether `rad` is present. Otherwise, when
        // `rad` is not on PATH, process.Start() throws Win32Exception first — which the catch
        // filter below swallows into a `false` — and cancellation is never observed.
        cancellationToken.ThrowIfCancellationRequested();

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
            // Probe with `rad version --cli`: this prints only the CLI version and exits 0 when
            // `rad` is installed, without contacting the Radius control plane — so detection
            // doesn't depend on a reachable cluster. Note the Radius CLI uses the `version`
            // subcommand, not a global `--version` flag. See https://docs.radapp.io/reference/cli/rad_version/
            process.StartInfo.ArgumentList.Add("version");
            process.StartInfo.ArgumentList.Add("--cli");
            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or OperationCanceledException)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return false;
        }
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
            throw CreateRadCliNotFoundException();
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

        // Declared outside the try so the finally can always clean up the temp secrets file, even
        // if resolving/writing it (below) fails partway.
        string? parametersFilePath = null;

        var deployTask = await context.ReportingStep.CreateTaskAsync(
            $"Deploying Radius environment '{_environment.Name}' via rad deploy...",
            cancellationToken).ConfigureAwait(false);

        try
        {
            // Resolve secret/parameter values at deploy time and write them to an owner-only
            // temporary ARM JSON parameters file. This supplies the `@secure()` Bicep params
            // emitted by publish without inlining secrets into the artifact or exposing them on the
            // `rad` command line. Written inside the try so the finally always deletes it.
            parametersFilePath = await WriteDeployParametersFileAsync(logger, cancellationToken).ConfigureAwait(false);

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
            if (parametersFilePath is not null)
            {
                // `@<file>` instructs rad to read an ARM JSON parameter file. Forwarded verbatim via
                // ArgumentList (no shell), so the leading '@' and path survive intact.
                process.StartInfo.ArgumentList.Add("--parameters");
                process.StartInfo.ArgumentList.Add("@" + parametersFilePath);
            }
            logger.LogInformation("Running: rad {Args}", string.Join(' ', process.StartInfo.ArgumentList));

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
        finally
        {
            DeleteDeployParametersFile(parametersFilePath, logger);
        }
    }

    // Resolves the deploy-time parameter values (from RadiusDeployParametersAnnotation) and writes
    // them to an owner-only temporary ARM JSON parameters file. Returns the file path, or null when
    // there are no parameters to supply. The caller is responsible for deleting the file. Internal so
    // tests can exercise the real file contract (ARM JSON shape + owner-only permissions).
    internal async Task<string?> WriteDeployParametersFileAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (!_environment.TryGetAnnotationsOfType<RadiusDeployParametersAnnotation>(out var annotations))
        {
            return null;
        }

        var parameters = annotations.Last().Parameters;
        if (parameters.Count == 0)
        {
            return null;
        }

        // ARM JSON deployment parameter file:
        //   { "$schema": "...", "contentVersion": "1.0.0.0",
        //     "parameters": { "<bicepParam>": { "value": "<resolved>" } } }
        var parametersNode = new JsonObject();
        foreach (var (identifier, parameter) in parameters)
        {
            var value = await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
            parametersNode[identifier] = new JsonObject { ["value"] = value };
        }

        var document = new JsonObject
        {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = parametersNode,
        };

        // CreateTempSubdirectory creates the directory with owner-only permissions (0700) on Unix.
        var directory = Directory.CreateTempSubdirectory("radius-deploy-");
        var filePath = Path.Combine(directory.FullName, "parameters.json");
        try
        {
            await File.WriteAllTextAsync(
                filePath,
                document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);

            // Restrict the file itself to the owner (read/write) on Unix; on Windows it inherits the
            // per-user temp directory ACL.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Don't leave a partially-written secrets file behind if writing/perm-setting fails
            // (e.g. on cancellation). The caller only cleans up files we successfully return.
            DeleteDeployParametersFile(filePath, logger);
            throw;
        }

        logger.LogInformation(
            "Wrote {Count} deploy parameter value(s) to a temporary owner-only file for environment '{EnvironmentName}'",
            parameters.Count, _environment.Name);

        return filePath;
    }

    // Internal so tests can assert file cleanup.
    internal static void DeleteDeployParametersFile(string? parametersFilePath, ILogger logger)
    {
        if (parametersFilePath is null)
        {
            return;
        }

        try
        {
            // Delete the whole temp subdirectory so nothing (file or dir) is left behind.
            var directory = Path.GetDirectoryName(parametersFilePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a leftover temp parameters file must not fail an otherwise
            // successful deploy.
            logger.LogWarning(ex, "Failed to delete temporary deploy parameters file '{Path}'", parametersFilePath);
        }
    }
}
