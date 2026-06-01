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

        var entries = BuildEntries(annotation).ToList();
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

    // The 'rad credential register' grammar uses TWO positional tokens — provider
    // then auth-mode — and has NO '--name' flag (credentials are per-provider,
    // per-installation). For example:
    //   rad credential register azure sp  --client-id <id> --client-secret <secret> --tenant-id <tenant>
    //   rad credential register azure wi  --client-id <id> --tenant-id <tenant>
    //   rad credential register aws access-key --access-key-id <id> --secret-access-key <secret>
    //   rad credential register aws irsa  --iam-role <roleArn>
    // See https://docs.radapp.io/reference/cli/rad_credential_register/ and its subcommands.
    internal static IEnumerable<CredentialEntry> BuildEntries(
        RadiusCloudProvidersAnnotation annotation)
    {
        if (annotation.Azure?.Credential is AzureRadiusCredential.ServicePrincipal sp)
        {
            yield return new CredentialEntry(
                Provider: "azure",
                Mode: "sp",
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
                Provider: "azure",
                Mode: "wi",
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
                Provider: "aws",
                Mode: "access-key",
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
                Provider: "aws",
                Mode: "irsa",
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
        // Collect the resolved secret values so they can be scrubbed from any stderr
        // the 'rad' CLI emits — it may echo a rejected flag's value back in an error,
        // which would otherwise leak the secret into logs and the thrown exception.
        var secretValues = ExtractSecretValues(args, secretFlags);
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
                logger.LogInformation("rad (stdout): {Output}", RedactSecretValues(e.Data, secretValues));
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                var redacted = RedactSecretValues(e.Data, secretValues);
                stderr.AppendLine(redacted);
                logger.LogWarning("rad (stderr): {Error}", redacted);
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

    // Pulls the value token that follows each secret flag so the literal secret can be
    // scrubbed from free-form text (e.g. stderr). Empty/whitespace values are skipped
    // to avoid turning every character of a line into '***'.
    internal static IReadOnlyList<string> ExtractSecretValues(
        IReadOnlyList<string> args, HashSet<string> secretFlags)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (secretFlags.Contains(args[i]) && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                values.Add(args[i + 1]);
            }
        }
        return values;
    }

    internal static string RedactSecretValues(string text, IReadOnlyList<string> secretValues)
    {
        if (string.IsNullOrEmpty(text) || secretValues.Count == 0)
        {
            return text;
        }

        // Redact longest-first (and de-duplicated) so that a secret which is a substring of
        // another secret cannot mask the longer one and leave its remainder exposed.
        foreach (var value in secretValues
            .Where(static v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static v => v.Length))
        {
            text = text.Replace(value, "***", StringComparison.Ordinal);
        }
        return text;
    }

    internal sealed record CredentialEntry(
        string Provider,
        string Mode,
        IReadOnlyList<Func<CancellationToken, Task<(string Flag, string Value)>>> ArgumentFactories,
        HashSet<string> SecretArgFlagSet)
    {
        internal async Task<IReadOnlyList<string>> ResolveArgumentsAsync(CancellationToken cancellationToken)
        {
            // Provider and Mode are two separate positional tokens; there is no --name flag.
            var args = new List<string> { "credential", "register", Provider, Mode };
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
