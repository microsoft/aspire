// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Secret-store types are experimental; consumed internally by the integration.
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
/// <c>ASPIRERADIUS045</c>; an unresolvable kube-context fails with <c>ASPIRERADIUS059</c>;
/// a never-synced <c>SealedSecret</c> fails with <c>ASPIRERADIUS058</c>
/// (before <c>rad deploy</c>). No-op when no sealed store is declared (FR-008, FR-009, FR-010).
/// </summary>
internal sealed class SealedSecretApplyStep
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);
    private const string KubeContextOverrideEnvironmentVariable = "ASPIRE_RADIUS_KUBE_CONTEXT";

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

        var workspaceConfigPath = GetWorkspaceConfigPath();
        var parsedKubeContext = await ResolveWorkspaceKubeContextAsync(workspaceConfigPath, cancellationToken).ConfigureAwait(false);
        var kubeContext = RequireKubeContext(
            Environment.GetEnvironmentVariable(KubeContextOverrideEnvironmentVariable),
            parsedKubeContext,
            workspaceConfigPath);

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

        var appliedGeneration = await ApplyManifestAsync(manifestPath, applyNamespace, kubeContext, store.Name, metadata.Namespace, metadata.Name, logger, cancellationToken)
            .ConfigureAwait(false);

        await WaitForSealedSecretSyncedAsync(
            store.Name, metadata.Namespace, metadata.Name, appliedGeneration, store.MaterializationTimeout, s_pollInterval,
            ct => GetSealedSecretStatusAsync(metadata.Namespace, metadata.Name, kubeContext, ct),
            ct => SecretExistsAsync(metadata.Namespace, metadata.Name, kubeContext, ct),
            cancellationToken).ConfigureAwait(false);

        // The SealedSecret controller can report Synced=True and create a Secret that is missing keys
        // the store declares (e.g. the manifest's encryptedData omits a key, or a stale Secret from a
        // prior seal is reused). The declared keys are the contract downstream recipeConfig/envSecrets
        // wiring reads, so verify each one is present in the materialized Secret before rad deploy.
        if (store.Population.Keys.Count > 0)
        {
            var dataKeys = await GetSecretDataKeysAsync(metadata.Namespace, metadata.Name, kubeContext, cancellationToken)
                .ConfigureAwait(false);
            var missing = FindMissingDeclaredKeys(store.Population.Keys, dataKeys);
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The Secret '{metadata.Namespace}/{metadata.Name}' materialized by sealed secret store " +
                    $"'{store.Name}' is missing the declared key(s) {string.Join(", ", missing.Select(k => $"'{k}'"))}. " +
                    "Ensure the sealed manifest's spec.encryptedData contains every key declared with WithSealedSecret. " +
                    "Diagnostic: ASPIRERADIUS061.");
            }
        }
    }

    /// <summary>Returns the declared keys that are absent from the materialized Secret's data keys, preserving declared order.</summary>
    internal static IReadOnlyList<string> FindMissingDeclaredKeys(IEnumerable<string> declaredKeys, IReadOnlySet<string> presentKeys) =>
        declaredKeys.Where(k => !presentKeys.Contains(k)).ToList();

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
        // controller surfaces later as a status/materialization timeout (ASPIRERADIUS058).
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
        var args = new List<string> { "apply", "-f", manifestPath, "-o", "json" };
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            args.Add("-n");
            args.Add(@namespace);
        }
        AddContext(args, kubeContext);
        return args;
    }

    /// <summary>Builds the <c>kubectl get sealedsecret</c> status-probe argument list.</summary>
    internal static IReadOnlyList<string> BuildGetSealedSecretArgs(string ns, string name, string? kubeContext)
    {
        var args = new List<string> { "get", "sealedsecret", name, "-n", ns, "-o", "json" };
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

    /// <summary>
    /// Builds the <c>kubectl get secret ... -o json</c> argument list used to read the materialized
    /// Secret's <c>data</c> keys. The response contains the (base64) secret values, so its stdout is
    /// never logged and only the key names are extracted.
    /// </summary>
    internal static IReadOnlyList<string> BuildGetSecretDataArgs(string ns, string name, string? kubeContext)
    {
        var args = new List<string> { "get", "secret", name, "-n", ns, "-o", "json" };
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

    /// <summary>Polls until the applied <c>SealedSecret</c> generation is synced and the <c>Secret</c> exists.</summary>
    internal static async Task WaitForSealedSecretSyncedAsync(
        string storeName,
        string ns,
        string name,
        long appliedGeneration,
        TimeSpan timeout,
        TimeSpan interval,
        Func<CancellationToken, Task<SealedSecretStatusSnapshot>> getStatus,
        Func<CancellationToken, Task<bool>> secretExists,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var status = await InvokeProbeWithRemainingBudgetAsync(
                getStatus,
                RemainingBudget(deadline),
                cancellationToken,
                () => CreateSealedSecretSyncTimeoutException(storeName, ns, name, appliedGeneration, timeout))
                .ConfigureAwait(false);
            var decision = EvaluateSealedSecretSync(status, appliedGeneration);
            if (decision.Kind == SealedSecretSyncDecisionKind.Synced)
            {
                if (await InvokeProbeWithRemainingBudgetAsync(
                    secretExists,
                    RemainingBudget(deadline),
                    cancellationToken,
                    () => CreateSealedSecretSyncTimeoutException(storeName, ns, name, appliedGeneration, timeout))
                    .ConfigureAwait(false))
                {
                    return;
                }
            }
            else if (decision.Kind == SealedSecretSyncDecisionKind.Failed)
            {
                throw new InvalidOperationException(
                    $"The SealedSecret '{ns}/{name}' referenced by sealed secret store '{storeName}' " +
                    $"failed to sync generation {appliedGeneration}: {decision.Message}. Diagnostic: ASPIRERADIUS058.");
            }

            var remaining = RemainingBudget(deadline);
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateSealedSecretSyncTimeoutException(storeName, ns, name, appliedGeneration, timeout);
            }

            try
            {
                await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreateSealedSecretSyncTimeoutException(storeName, ns, name, appliedGeneration, timeout);
            }
        }
    }

    private static async Task<T> InvokeProbeWithRemainingBudgetAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        TimeSpan remaining,
        CancellationToken cancellationToken,
        Func<InvalidOperationException> createTimeoutException)
    {
        if (remaining <= TimeSpan.Zero)
        {
            throw createTimeoutException();
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(remaining);

        try
        {
            return await probe(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linkedCts.IsCancellationRequested)
        {
            throw createTimeoutException();
        }
    }

    private static TimeSpan RemainingBudget(DateTimeOffset deadline) => deadline - DateTimeOffset.UtcNow;

    private static InvalidOperationException CreateSealedSecretSyncTimeoutException(
        string storeName,
        string ns,
        string name,
        long appliedGeneration,
        TimeSpan timeout) =>
        new(
            $"The SealedSecret '{ns}/{name}' referenced by sealed secret store '{storeName}' did not " +
            $"report Synced=True for generation {appliedGeneration} and materialize its Secret within " +
            $"{timeout.TotalSeconds:0}s. The Sealed Secrets controller must have status updates enabled " +
            "(do not disable them with '--update-status=false' or Helm 'updateStatus: false'). Likely " +
            "causes: the Sealed Secrets controller is not installed, the manifest was sealed for a " +
            "different namespace, or decryption failed. Diagnostic: ASPIRERADIUS058.");

    internal static SealedSecretSyncDecision EvaluateSealedSecretSync(SealedSecretStatusSnapshot status, long appliedGeneration)
    {
        if (status.Generation is not null && status.Generation.Value > appliedGeneration)
        {
            return SealedSecretSyncDecision.Failed(
                $"the live SealedSecret generation advanced to {status.Generation} before generation {appliedGeneration} was observed, which indicates a concurrent modification");
        }

        if (status.ObservedGeneration == appliedGeneration)
        {
            foreach (var condition in status.Conditions)
            {
                if (string.Equals(condition.Type, "Synced", StringComparison.Ordinal) &&
                    string.Equals(condition.Status, "False", StringComparison.Ordinal))
                {
                    return SealedSecretSyncDecision.Failed(
                        string.IsNullOrWhiteSpace(condition.Message) ? "the Sealed Secrets controller reported Synced=False" : condition.Message);
                }
            }

            foreach (var condition in status.Conditions)
            {
                if (string.Equals(condition.Type, "Synced", StringComparison.Ordinal) &&
                    string.Equals(condition.Status, "True", StringComparison.Ordinal))
                {
                    return SealedSecretSyncDecision.Synced();
                }
            }
        }

        return SealedSecretSyncDecision.Waiting();
    }

    internal static long ParseGeneration(string json, string storeName, string ns, string name)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("generation", out var generation) &&
            generation.TryGetInt64(out var value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"'kubectl apply' for the SealedSecret '{ns}/{name}' referenced by sealed secret store " +
            $"'{storeName}' did not return metadata.generation (unexpected or truncated kubectl output). " +
            "Diagnostic: ASPIRERADIUS058.");
    }

    internal static SealedSecretStatusSnapshot ParseSealedSecretStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        long? generation = null;
        if (root.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("generation", out var generationElement) &&
            generationElement.TryGetInt64(out var generationValue))
        {
            generation = generationValue;
        }

        long? observedGeneration = null;
        var conditions = new List<SealedSecretCondition>();
        if (root.TryGetProperty("status", out var status))
        {
            if (status.TryGetProperty("observedGeneration", out var observedGenerationElement) &&
                observedGenerationElement.TryGetInt64(out var observedGenerationValue))
            {
                observedGeneration = observedGenerationValue;
            }

            if (status.TryGetProperty("conditions", out var conditionsElement) &&
                conditionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var condition in conditionsElement.EnumerateArray())
                {
                    var type = condition.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                        ? typeElement.GetString()
                        : null;
                    var conditionStatus = condition.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                        ? statusElement.GetString()
                        : null;

                    if (type is null || conditionStatus is null)
                    {
                        continue;
                    }

                    var message = condition.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : null;
                    conditions.Add(new SealedSecretCondition(type, conditionStatus, message));
                }
            }
        }

        return new SealedSecretStatusSnapshot(generation, observedGeneration, conditions);
    }

    private static async Task<long> ApplyManifestAsync(string manifestPath, string? @namespace, string? kubeContext, string storeName, string ns, string name, ILogger logger, CancellationToken cancellationToken)
    {
        var args = BuildApplyArgs(manifestPath, kubeContext, @namespace);
        var (exitCode, stdout, stderr) = await RunKubectlAsync(args, logger, cancellationToken, logStdout: false).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'kubectl apply -f {manifestPath}' failed with exit code {exitCode}: {stderr.Trim()}");
        }

        return ParseGeneration(stdout, storeName, ns, name);
    }

    private static async Task<SealedSecretStatusSnapshot> GetSealedSecretStatusAsync(string ns, string name, string? kubeContext, CancellationToken cancellationToken)
    {
        var args = BuildGetSealedSecretArgs(ns, name, kubeContext);
        var (exitCode, stdout, stderr) = await RunKubectlAsync(args, logger: null, cancellationToken, logStdout: false).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to query the SealedSecret '{ns}/{name}' with 'kubectl get sealedsecret': {stderr.Trim()}");
        }

        return ParseSealedSecretStatus(stdout);
    }

    // Reads the materialized Secret's data-key names to verify the declared keys are present.
    // The Secret's `data` values are base64 secret material, so RunKubectlAsync is called with
    // logStdout: false and only the key names (never the values) are extracted.
    private static async Task<IReadOnlySet<string>> GetSecretDataKeysAsync(string ns, string name, string? kubeContext, CancellationToken cancellationToken)
    {
        var args = BuildGetSecretDataArgs(ns, name, kubeContext);
        var (exitCode, stdout, stderr) = await RunKubectlAsync(args, logger: null, cancellationToken, logStdout: false).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to query the Secret '{ns}/{name}' with 'kubectl get secret -o json': {stderr.Trim()}");
        }

        return ParseSecretDataKeys(stdout);
    }

    // Parses the `data` (and `stringData`, defensively — it is write-only and normally absent on read)
    // object key names from a `kubectl get secret -o json` response. Values are ignored; only names
    // are returned so no secret material leaves this method.
    internal static IReadOnlySet<string> ParseSecretDataKeys(string json)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var property in new[] { "data", "stringData" })
        {
            if (root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in element.EnumerateObject())
                {
                    keys.Add(member.Name);
                }
            }
        }

        return keys;
    }

    private static async Task<bool> SecretExistsAsync(string ns, string name, string? kubeContext, CancellationToken cancellationToken)
    {
        var args = BuildGetSecretArgs(ns, name, kubeContext);
        var (exitCode, _, stderr) = await RunKubectlAsync(args, logger: null, cancellationToken).ConfigureAwait(false);
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
    // same cluster rad deploy will hit (not kubectl's ambient current-context). Reads the Radius
    // workspace config; returns null when unresolved so the caller can fail closed.
    private static string GetWorkspaceConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".rad", "config.yaml");
    }

    private static async Task<string?> ResolveWorkspaceKubeContextAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
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
    // `context:` occurrence. Dependency-free: any miss returns null and the caller fails closed.
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

            // Once rad names an active workspace, guessing from another workspace would fail open to
            // the wrong cluster. Return null so the caller requires an explicit override/remediation.
            return null;
        }

        // Fallback: first `context:` anywhere (single-workspace configs without a default selector).
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"(?m)^\s*context:\s*(?<v>[^\s#]+)\s*$");
        return match.Success ? match.Groups["v"].Value.Trim('\'', '"') : null;
    }

    internal static string RequireKubeContext(string? overrideContext, string? parsedContext, string attemptedConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(overrideContext))
        {
            return overrideContext.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parsedContext))
        {
            return parsedContext.Trim();
        }

        throw new InvalidOperationException(
            $"Could not resolve the active Radius workspace kube-context from '{attemptedConfigPath}'. Configure " +
            $"the active rad workspace, or set {KubeContextOverrideEnvironmentVariable} to the kubectl context " +
            "that targets the same cluster before re-running deploy. Diagnostic: ASPIRERADIUS059.");
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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunKubectlAsync(
        IReadOnlyList<string> args, ILogger? logger, CancellationToken cancellationToken, bool logStdout = true)
    {
        var stdout = new StringBuilder();
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

        // Some kubectl calls return full SealedSecret JSON, including spec.template. Capture stdout
        // for parsing, but only log it when the caller has confirmed the output shape is safe.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
                if (logStdout)
                {
                    logger?.LogInformation("kubectl (stdout): {Output}", e.Data);
                }
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
            process.WaitForExit();
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

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    internal sealed record SealedSecretStatusSnapshot(
        long? Generation,
        long? ObservedGeneration,
        IReadOnlyList<SealedSecretCondition> Conditions);

    internal readonly record struct SealedSecretCondition(string Type, string Status, string? Message);

    internal enum SealedSecretSyncDecisionKind
    {
        Waiting,
        Synced,
        Failed,
    }

    internal sealed record SealedSecretSyncDecision(SealedSecretSyncDecisionKind Kind, string? Message)
    {
        public static SealedSecretSyncDecision Waiting() => new(SealedSecretSyncDecisionKind.Waiting, null);

        public static SealedSecretSyncDecision Synced() => new(SealedSecretSyncDecisionKind.Synced, null);

        public static SealedSecretSyncDecision Failed(string message) => new(SealedSecretSyncDecisionKind.Failed, message);
    }
}
