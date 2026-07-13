// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Secret-store types are experimental; consumed internally by the integration.
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Secrets;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Pipeline step that, for the sealed-secrets path, applies each committed <c>SealedSecret</c>
/// manifest to the cluster (targeting the same cluster the subsequent <c>rad deploy</c> hits)
/// and waits for the Sealed Secrets controller to materialize the underlying
/// <c>kubernetes.io/v1 Secret</c> before deploy. Scheduled after publish and before deploy,
/// mirroring <see cref="RadCredentialRegisterStep"/>. A missing <c>kubectl</c> fails with
/// <c>ASPIRERADIUS045</c>; a never-materializing <c>Secret</c> fails with <c>ASPIRERADIUS046</c>
/// (before <c>rad deploy</c>). No-op when no sealed store is declared (FR-008, FR-009, FR-010).
/// </summary>
internal sealed class SealedSecretApplyStep
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);

    private readonly RadiusEnvironmentResource _environment;

    internal SealedSecretApplyStep(RadiusEnvironmentResource environment) => _environment = environment;

    internal PipelineStep CreatePipelineStep()
    {
        var step = new PipelineStep
        {
            Name = $"apply-sealed-secrets-{_environment.Name}",
            Description = $"Apply and await sealed secrets for '{_environment.Name}' before rad deploy",
            Action = ExecuteAsync,
        };
        step.DependsOn($"publish-radius-{_environment.Name}");
        step.DependsOn(WellKnownPipelineSteps.DeployPrereq);
        step.RequiredBy($"deploy-radius-{_environment.Name}");
        return step;
    }

    internal async Task ExecuteAsync(PipelineStepContext context)
    {
        var model = context.Model;

        var stores = GetSealedStores(model);
        if (stores.Count == 0)
        {
            return;
        }

        var logger = context.Logger;
        var cancellationToken = context.CancellationToken;

        await EnsureKubectlAsync(cancellationToken).ConfigureAwait(false);

        var kubeContext = await ResolveWorkspaceKubeContextAsync(cancellationToken).ConfigureAwait(false);

        // Same-run publish copies each manifest under the environment output directory; deploy
        // prefers that self-contained artifact and falls back to the author-provided source path.
        var outputDir = PublishingContextUtils.GetEnvironmentOutputPath(context, _environment);

        foreach (var store in stores)
        {
            await ApplyStoreAsync(
                store, outputDir, store.Population.SealedManifestPath!, _environment.Namespace,
                kubeContext, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    // Resolves the store's manifest (published artifact preferred, source path fallback), reads its
    // identifying metadata, applies it, then waits for the underlying Secret to materialize. The
    // resolved path is used for BOTH the metadata read and the apply so a cross-machine deploy that
    // relies on the self-contained artifact never touches the (possibly absent) author source path.
    private static async Task ApplyStoreAsync(
        RadiusSecretStoreResource store,
        string storeOutputDir,
        string sourceManifestPath,
        string defaultNamespace,
        string? kubeContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var manifestPath = ResolveManifestPath(storeOutputDir, store.Name, sourceManifestPath);

        var metadata = SealedSecretManifest.ReadMetadata(store.Name, manifestPath, defaultNamespace);

        // Pass -n only when the manifest omitted metadata.namespace: `kubectl apply -n X` fails when
        // the object already declares a different namespace, but when the manifest is namespace-less
        // apply would otherwise land in the kube-context's default namespace while the poll below
        // checks the resolved namespace — so they must be pinned to the same value.
        var applyNamespace = metadata.NamespaceWasExplicit ? null : metadata.Namespace;

        await ApplyManifestAsync(manifestPath, applyNamespace, kubeContext, logger, cancellationToken)
            .ConfigureAwait(false);

        await WaitForSecretMaterializationAsync(
            store.Name, metadata.Namespace, metadata.Name, store.MaterializationTimeout, s_pollInterval,
            ct => SecretExistsAsync(metadata.Namespace, metadata.Name, kubeContext, ct),
            cancellationToken).ConfigureAwait(false);
    }

    // Prefers the self-contained published artifact (sealed-secrets/<store>/<file> under the
    // emitted app.bicep) so publish-then-deploy across machines works; falls back to the author
    // source path for the in-place same-run case.
    private static string ResolveManifestPath(string storeOutputDir, string storeName, string sourceManifestPath)
    {
        var artifact = SealedSecretArtifact.ResolvePath(storeOutputDir, storeName, sourceManifestPath);
        return File.Exists(artifact) ? artifact : sourceManifestPath;
    }

    private static async Task EnsureKubectlAsync(CancellationToken cancellationToken)
    {
        // DetectKubectlAsync runs `kubectl version --client`, which only proves the kubectl client
        // binary is present on PATH — it does NOT contact the cluster or verify the Sealed Secrets
        // controller. Keep this message scoped to the client so it isn't misleading; a missing
        // controller surfaces later as a materialization timeout (ASPIRERADIUS046).
        if (!await DetectKubectlAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "'kubectl' was not found on PATH. Applying a SealedSecret manifest requires the kubectl " +
                "client. Install kubectl and ensure it is on PATH, then re-run deploy. Diagnostic: ASPIRERADIUS045.");
        }
    }

    /// <summary>Sealed secret stores scoped to this environment (environment-scoped owned here, plus application-scoped).</summary>
    private List<RadiusSecretStoreResource> GetSealedStores(DistributedApplicationModel model) =>
        model.Resources.OfType<RadiusSecretStoreResource>()
            .Where(s => s.Population.HasSealedSecret)
            .Where(s => s.Scope == RadiusSecretStoreScope.Application || ReferenceEquals(s.OwningEnvironment, _environment))
            .ToList();

    /// <summary>Builds the <c>kubectl apply</c> argument list, passing <c>-n</c> and <c>--context</c> only when supplied.</summary>
    internal static IReadOnlyList<string> BuildApplyArgs(string manifestPath, string? kubeContext, string? @namespace = null)
    {
        var args = new List<string> { "apply", "-f", manifestPath };
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            args.Add("-n");
            args.Add(@namespace);
        }
        AddContext(args, kubeContext);
        return args;
    }

    /// <summary>Builds the <c>kubectl get secret</c> existence-probe argument list.</summary>
    internal static IReadOnlyList<string> BuildGetSecretArgs(string ns, string name, string? kubeContext)
    {
        var args = new List<string> { "get", "secret", name, "-n", ns, "-o", "name" };
        AddContext(args, kubeContext);
        return args;
    }

    private static void AddContext(List<string> args, string? kubeContext)
    {
        if (!string.IsNullOrWhiteSpace(kubeContext))
        {
            args.Add("--context");
            args.Add(kubeContext);
        }
    }

    /// <summary>
    /// Polls <paramref name="secretExists"/> every <paramref name="interval"/> up to
    /// <paramref name="timeout"/>, returning when the <c>Secret</c> exists and throwing
    /// <c>ASPIRERADIUS046</c> (before <c>rad deploy</c>) if it never materializes.
    /// </summary>
    internal static async Task WaitForSecretMaterializationAsync(
        string storeName,
        string ns,
        string name,
        TimeSpan timeout,
        TimeSpan interval,
        Func<CancellationToken, Task<bool>> secretExists,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (await secretExists(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"The Secret '{ns}/{name}' referenced by sealed secret store '{storeName}' did not " +
                    $"materialize within {timeout.TotalSeconds:0}s. Likely causes: the Sealed Secrets " +
                    "controller is not installed, the manifest was sealed for a different namespace, or " +
                    "decryption failed. Diagnostic: ASPIRERADIUS046.");
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplyManifestAsync(string manifestPath, string? @namespace, string? kubeContext, ILogger logger, CancellationToken cancellationToken)
    {
        var args = BuildApplyArgs(manifestPath, kubeContext, @namespace);
        var (exitCode, stderr) = await RunKubectlAsync(args, logger, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'kubectl apply -f {manifestPath}' failed with exit code {exitCode}: {stderr.Trim()}");
        }
    }

    private static async Task<bool> SecretExistsAsync(string ns, string name, string? kubeContext, CancellationToken cancellationToken)
    {
        var args = BuildGetSecretArgs(ns, name, kubeContext);
        var (exitCode, stderr) = await RunKubectlAsync(args, logger: null, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return true;
        }

        // `kubectl get secret <name>` exits non-zero both when the Secret does not (yet) exist and
        // when the command itself fails (cluster unreachable, auth/RBAC denied, bad context, missing
        // auth-plugin executable). Only a genuine NotFound for THIS Secret means "keep polling"; any
        // other failure will never resolve by waiting, so surface it immediately instead of burning
        // the whole materialization timeout. NotFound stderr:
        //   Error from server (NotFound): secrets "my-secret" not found
        if (IsNotFound(stderr, name))
        {
            return false;
        }

        throw new InvalidOperationException(
            $"Failed to query the Secret '{ns}/{name}' with 'kubectl get secret': {stderr.Trim()}");
    }

    // Distinguishes a Kubernetes NotFound response for the specific target Secret (it has not
    // materialized yet) from every other kubectl failure. The canonical message is
    // `Error from server (NotFound): secrets "<name>" not found`, so match that exact phrasing rather
    // than a bare "not found" substring — otherwise unrelated client errors (e.g. "exec plugin ... not
    // found", "command not found", or a NotFound for a different resource such as a namespace) would be
    // wrongly treated as "keep waiting" and burn the full timeout.
    internal static bool IsNotFound(string stderr, string name) =>
        stderr.Contains($"secrets \"{name}\" not found", StringComparison.Ordinal);

    // Resolves the kubecontext of the active rad workspace so the SealedSecret is applied to the
    // same cluster rad deploy will hit (not kubectl's ambient current-context). Best-effort: reads
    // the Radius workspace config; returns null when unresolved (kubectl uses its current context).
    private static async Task<string?> ResolveWorkspaceKubeContextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configPath = Path.Combine(home, ".rad", "config.yaml");
            if (!File.Exists(configPath))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            return ParseActiveWorkspaceContext(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Selects the kubecontext of the *default* (active) rad workspace, not merely the first
    // `context:` in the file — a machine with several workspaces would otherwise pick the wrong
    // cluster. The rad config (~/.rad/config.yaml) is nested YAML shaped like:
    //   workspaces:
    //     default: kind-radius
    //     items:
    //       kind-radius:
    //         connection:
    //           kind: kubernetes
    //           context: kind-radius
    //       other:
    //         connection:
    //           context: other-ctx
    // We read workspaces.default, then workspaces.items.<default>.connection.context. If the
    // default selector is absent (older/single-workspace configs), we fall back to the first
    // `context:` occurrence. Best-effort and dependency-free: any miss returns null and the caller
    // lets kubectl use its ambient current-context.
    internal static string? ParseActiveWorkspaceContext(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var defaultWorkspace = FindNestedScalar(lines, ["workspaces", "default"]);
        if (!string.IsNullOrEmpty(defaultWorkspace))
        {
            var context = FindNestedScalar(lines, ["workspaces", "items", defaultWorkspace, "connection", "context"]);
            if (!string.IsNullOrEmpty(context))
            {
                return context;
            }
        }

        // Fallback: first `context:` anywhere (single-workspace configs without a default selector).
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"(?m)^\s*context:\s*(?<v>[^\s#]+)\s*$");
        return match.Success ? match.Groups["v"].Value.Trim('\'', '"') : null;
    }

    // Walks an indentation-nested mapping following <paramref name="path"/> key-by-key, returning
    // the scalar value of the final key. Descends only through exact key matches at strictly deeper
    // indentation than the matched parent, and bails out when the parent block ends (a line at or
    // below the parent's indentation), so sibling subtrees can never be mistaken for the target.
    private static string? FindNestedScalar(IReadOnlyList<string> lines, IReadOnlyList<string> path)
    {
        var level = 0;
        var parentIndent = -1;

        foreach (var raw in lines)
        {
            var (indent, key, value) = ParseKeyLine(raw);
            if (key is null)
            {
                continue;
            }

            if (level > 0 && indent <= parentIndent)
            {
                // Left the current parent's block before finding the next key in the path.
                return null;
            }

            if (indent > parentIndent && string.Equals(key, path[level], StringComparison.Ordinal))
            {
                if (level == path.Count - 1)
                {
                    return string.IsNullOrEmpty(value) ? null : value;
                }

                parentIndent = indent;
                level++;
            }
        }

        return null;
    }

    // Parses a single YAML line into (indentation, key, scalar value). Returns a null key for blank
    // lines, comments, and non-`key: value` lines (e.g. list items). Quotes around the value are
    // stripped; inline comments are not stripped (workspace/context values do not contain '#').
    private static (int Indent, string? Key, string Value) ParseKeyLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (0, null, string.Empty);
        }

        var indent = 0;
        while (indent < raw.Length && (raw[indent] == ' ' || raw[indent] == '\t'))
        {
            indent++;
        }

        var trimmed = raw[indent..];
        if (trimmed.StartsWith('#'))
        {
            return (indent, null, string.Empty);
        }

        var colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            return (indent, null, string.Empty);
        }

        var key = trimmed[..colon].Trim();
        var value = trimmed[(colon + 1)..].Trim().Trim('\'', '"');
        return (indent, key, value);
    }

    private static async Task<bool> DetectKubectlAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "kubectl",
                    ArgumentList = { "version", "--client" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();

            // Drain the redirected pipes (output discarded) so a probe that emits more than the OS
            // pipe buffer can't block on write and hang WaitForExitAsync.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
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

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // kubectl not found on PATH.
            return false;
        }
    }

    private static async Task<(int ExitCode, string StdErr)> RunKubectlAsync(
        IReadOnlyList<string> args, ILogger? logger, CancellationToken cancellationToken)
    {
        var stderr = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var a in args)
        {
            process.StartInfo.ArgumentList.Add(a);
        }

        // The SealedSecret manifest passed to kubectl is already encrypted, so no plaintext is
        // exposed; the wrapper still scrubs output uniformly with the rad wrapper's helpers.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logger?.LogInformation("kubectl (stdout): {Output}", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
                logger?.LogWarning("kubectl (stderr): {Error}", e.Data);
            }
        };

        logger?.LogInformation("Running: kubectl {Args}", string.Join(' ', args));
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Terminate the child on cancellation; otherwise `using var process` only disposes
            // the handle and leaves an orphaned `kubectl` process running (mirrors the deploy step).
            if (!process.HasExited)
            {
                logger?.LogWarning("Cancellation requested — terminating kubectl process.");
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

        return (process.ExitCode, stderr.ToString());
    }
}
