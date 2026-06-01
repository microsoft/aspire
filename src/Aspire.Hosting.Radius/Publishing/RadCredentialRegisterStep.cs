// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.Diagnostics;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.CloudProviders;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Pipeline step that registers cloud-provider credentials with the local
/// Radius workspace via <c>rad credential register</c> before
/// <see cref="RadiusDeploymentPipelineStep"/> runs. Secret values supplied
/// through <see cref="ParameterResource"/> are resolved at execution time
/// and forwarded directly to the CLI argument list — they are never
/// written to the publish artifact and are redacted from log output.
/// A non-zero <c>rad</c> exit aborts the entire deploy pipeline
/// (FR-010, FR-010a, FR-010b).
/// </summary>
internal sealed class RadCredentialRegisterStep
{
    private readonly RadiusEnvironmentResource _environment;

    internal RadCredentialRegisterStep(RadiusEnvironmentResource environment)
    {
        _environment = environment;
    }

    /// <summary>
    /// Creates a <see cref="PipelineStep"/> that runs <c>rad credential register</c>
    /// for each configured cloud provider. Scheduled to run after publish and
    /// before deploy so that failures surface before any cluster mutation.
    /// </summary>
    internal PipelineStep CreatePipelineStep()
    {
        var step = new PipelineStep
        {
            Name = $"register-radius-credentials-{_environment.Name}",
            Description = $"Register Radius cloud-provider credentials for '{_environment.Name}' via rad credential register",
            Action = ExecuteAsync,
        };
        step.DependsOn($"publish-radius-{_environment.Name}");
        step.DependsOn(WellKnownPipelineSteps.DeployPrereq);
        step.RequiredBy($"deploy-radius-{_environment.Name}");
        return step;
    }

    internal async Task ExecuteAsync(PipelineStepContext context)
    {
        var cancellationToken = context.CancellationToken;
        var logger = context.Logger;

        var annotation = _environment.Annotations
            .OfType<RadiusCloudProvidersAnnotation>()
            .FirstOrDefault();
        if (annotation is null)
        {
            return;
        }

        var entries = BuildEntries(annotation, _environment.Name).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var radAvailable = await RadiusDeploymentPipelineStep.DetectRadCliAsync(cancellationToken).ConfigureAwait(false);
        if (!radAvailable)
        {
            const string installUrl = "https://docs.radapp.io/installation/";
            logger.LogError("The 'rad' CLI was not found on PATH. Install it from {InstallUrl}", installUrl);
            throw new InvalidOperationException(
                $"The 'rad' CLI was not found. Install it from {installUrl} and ensure it is on PATH.");
        }

        foreach (var entry in entries)
        {
            var args = await entry.ResolveArgumentsAsync(cancellationToken).ConfigureAwait(false);
            await RunRadAsync(args, entry.SecretArgFlagSet, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<CredentialEntry> BuildEntries(
        RadiusCloudProvidersAnnotation annotation, string envName)
    {
        if (annotation.Azure?.Credential is AzureRadiusCredential.ServicePrincipal sp)
        {
            yield return new CredentialEntry(
                Kind: "azure-sp",
                Name: $"aspire-{envName}-azure",
                ArgumentFactories:
                [
                    _ => Task.FromResult(("--tenant-id", sp.TenantId)),
                    _ => Task.FromResult(("--client-id", sp.ClientId)),
                    async ct => ("--client-secret", await ResolveParameterAsync(sp.ClientSecret, ct).ConfigureAwait(false)),
                ],
                SecretArgFlagSet: ["--client-secret"]);
        }

        if (annotation.Azure?.Credential is AzureRadiusCredential.WorkloadIdentity wi)
        {
            yield return new CredentialEntry(
                Kind: "azure-wi",
                Name: $"aspire-{envName}-azure",
                ArgumentFactories:
                [
                    _ => Task.FromResult(("--client-id", wi.ClientId)),
                    _ => Task.FromResult(("--tenant-id", wi.TenantId)),
                ],
                SecretArgFlagSet: []);
        }

        if (annotation.Aws?.Credential is AwsRadiusCredential.AccessKey ak)
        {
            yield return new CredentialEntry(
                Kind: "aws-access-key",
                Name: $"aspire-{envName}-aws",
                ArgumentFactories:
                [
                    async ct => ("--access-key-id", await ResolveParameterAsync(ak.AccessKeyId, ct).ConfigureAwait(false)),
                    async ct => ("--secret-access-key", await ResolveParameterAsync(ak.SecretAccessKey, ct).ConfigureAwait(false)),
                ],
                SecretArgFlagSet: ["--access-key-id", "--secret-access-key"]);
        }

        if (annotation.Aws?.Credential is AwsRadiusCredential.Irsa irsa)
        {
            yield return new CredentialEntry(
                Kind: "aws-irsa",
                Name: $"aspire-{envName}-aws",
                ArgumentFactories:
                [
                    _ => Task.FromResult(("--iam-role", irsa.IamRoleArn)),
                ],
                SecretArgFlagSet: []);
        }
    }

    private static async Task<string> ResolveParameterAsync(
        IResourceBuilder<ParameterResource> parameter,
        CancellationToken cancellationToken)
    {
        return await parameter.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
    }

    private static async Task RunRadAsync(
        IReadOnlyList<string> args,
        HashSet<string> secretFlags,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var stderr = new StringBuilder();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "rad",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            process.StartInfo.ArgumentList.Add(a);
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logger.LogInformation("rad (stdout): {Output}", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
                logger.LogWarning("rad (stderr): {Error}", e.Data);
            }
        };

        var redactedArgs = RedactSecretArgs(args, secretFlags);
        logger.LogInformation("Running: rad {Args}", string.Join(' ', redactedArgs));

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var msg = $"rad credential register failed with exit code {process.ExitCode}: {stderr.ToString().Trim()}";
            logger.LogError("{Message}", msg);
            throw new InvalidOperationException(msg);
        }
    }

    internal static IReadOnlyList<string> RedactSecretArgs(
        IReadOnlyList<string> args, HashSet<string> secretFlags)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            result.Add(args[i]);
            if (secretFlags.Contains(args[i]) && i + 1 < args.Count)
            {
                result.Add("***");
                i++;
            }
        }
        return result;
    }

    private sealed record CredentialEntry(
        string Kind,
        string Name,
        IReadOnlyList<Func<CancellationToken, Task<(string Flag, string Value)>>> ArgumentFactories,
        HashSet<string> SecretArgFlagSet)
    {
        internal async Task<IReadOnlyList<string>> ResolveArgumentsAsync(CancellationToken cancellationToken)
        {
            var args = new List<string> { "credential", "register", Kind, "--name", Name };
            foreach (var factory in ArgumentFactories)
            {
                var (flag, value) = await factory(cancellationToken).ConfigureAwait(false);
                args.Add(flag);
                args.Add(value);
            }
            return args;
        }
    }
}
