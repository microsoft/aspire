// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides extension methods for adding a k3s Kubernetes cluster to the Aspire application model.
/// </summary>
public static partial class K3sBuilderExtensions
{
    /// <summary>
    /// Adds a k3s Kubernetes cluster resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name. Used as the connection string name when referenced by dependents.</param>
    /// <param name="apiServerPort">
    /// The host port to expose the Kubernetes API server on.
    /// When <see langword="null"/> (default), DCP allocates a free port automatically.
    /// </param>
    /// <param name="configure">Optional callback to configure cluster options.</param>
    /// <returns>A builder for the <see cref="K3sClusterResource"/>.</returns>
    /// <remarks>
    /// <para>
    /// This resource includes a built-in health check that polls the k3s <c>/readyz</c> endpoint.
    /// Use <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// on dependent resources to ensure the cluster is fully ready before they start.
    /// </para>
    /// <para>
    /// The cluster requires Docker with privileged container support. The kubeconfig is written
    /// to a host temp directory and automatically rewritten to reference <c>localhost:{port}</c>.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the canonical addK3sCluster export without options callback.")]
    public static IResourceBuilder<K3sClusterResource> AddK3sCluster(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? apiServerPort = null,
        Action<K3sClusterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var options = new K3sClusterOptions();
        configure?.Invoke(options);

        var kubeconfigDir = Path.Combine(Path.GetTempPath(), $"aspire-k3s-{name}");
        Directory.CreateDirectory(kubeconfigDir);

        var resource = new K3sClusterResource(name, kubeconfigDir)
        {
            KubectlBootstrapTag = TryParseK8sVersionFromK3sTag(options.ImageTag, out var k8sVersion)
                ? k8sVersion
                : "latest"
        };

        var healthCheckKey = $"{name}_k3s_check";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            _ => new K3sReadinessHealthCheck(resource),
            failureStatus: null,
            tags: null,
            timeout: TimeSpan.FromSeconds(10)));

        var resourceBuilder = builder.AddResource(resource)
            .WithImage(K3sContainerImageTags.Image, options.ImageTag)
            .WithImageRegistry(K3sContainerImageTags.Registry)
            .WithEndpoint(port: apiServerPort, targetPort: 6443, name: K3sClusterResource.ApiEndpointName, scheme: "https")
            .WithEnvironment("K3S_KUBECONFIG_OUTPUT", "/kubeconfig/admin.yaml")
            .WithBindMount(kubeconfigDir, "/kubeconfig")
            .WithArgs(ctx =>
            {
                ctx.Args.Add("server");

                foreach (var component in options.DisabledComponents)
                {
                    ctx.Args.Add($"--disable={component}");
                }

                foreach (var san in options.TlsSans)
                {
                    ctx.Args.Add($"--tls-san={san}");
                }

                if (options.PodSubnet is not null)
                {
                    ctx.Args.Add($"--cluster-cidr={options.PodSubnet}");
                }

                if (options.ServiceSubnet is not null)
                {
                    ctx.Args.Add($"--service-cidr={options.ServiceSubnet}");
                }

                foreach (var arg in options.ExtraArgs)
                {
                    ctx.Args.Add(arg);
                }

                return Task.CompletedTask;
            })
            .WithContainerRuntimeArgs("--privileged")
            .WithContainerRuntimeArgs("--tmpfs", "/run", "--tmpfs", "/var/run")
            .WithHealthCheck(healthCheckKey);

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(resource, async (@event, ct) =>
        {
            var logger = @event.Services.GetRequiredService<ILogger<K3sClusterResource>>();
            await RunWorkloadsAsync(resource, logger, ct).ConfigureAwait(false);
        });

        return resourceBuilder;
    }

    /// <summary>
    /// Deploys a Helm chart release into the k3s cluster before it transitions to the running state.
    /// </summary>
    /// <param name="builder">The k3s cluster resource builder.</param>
    /// <param name="releaseName">The Helm release name.</param>
    /// <param name="chart">The chart name or OCI reference (e.g. <c>oci://ghcr.io/org/chart</c>).</param>
    /// <param name="repo">Optional Helm repository URL. Required when <paramref name="chart"/> is a short name.</param>
    /// <param name="version">Optional chart version. Defaults to latest.</param>
    /// <param name="namespace">Kubernetes namespace for the release. Defaults to <c>default</c>.</param>
    /// <param name="valuesFile">Optional path to a values YAML file on the host.</param>
    [AspireExport(Description = "Installs a Helm chart release into the k3s cluster before startup")]
    public static IResourceBuilder<K3sClusterResource> WithHelmRelease(
        this IResourceBuilder<K3sClusterResource> builder,
        string releaseName,
        string chart,
        string? repo = null,
        string? version = null,
        string? @namespace = null,
        string? valuesFile = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithAnnotation(new HelmReleaseAnnotation(releaseName, chart, repo, version, @namespace, valuesFile));
    }

    /// <summary>
    /// Applies a Kustomize overlay into the k3s cluster before it transitions to the running state.
    /// </summary>
    /// <param name="builder">The k3s cluster resource builder.</param>
    /// <param name="path">
    /// Path to the Kustomize directory or URL (e.g. <c>./k8s/overlays/local</c>).
    /// Relative paths are resolved against the current working directory.
    /// </param>
    [AspireExport(Description = "Applies a Kustomize overlay into the k3s cluster before startup")]
    public static IResourceBuilder<K3sClusterResource> WithKustomize(
        this IResourceBuilder<K3sClusterResource> builder,
        string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithAnnotation(new KustomizeAnnotation(path));
    }

    /// <summary>
    /// Installs the Kubernetes Dashboard into the cluster and logs the access token
    /// and port-forward command to the resource output.
    /// </summary>
    /// <param name="builder">The k3s cluster resource builder.</param>
    /// <param name="version">Dashboard Helm chart version. Defaults to <c>7.10.0</c>.</param>
    /// <remarks>
    /// Requires <c>helm</c> and <c>kubectl</c> on the host PATH.
    /// If either is absent, a warning is logged and the dashboard is skipped.
    /// </remarks>
    [AspireExport(Description = "Installs the Kubernetes Dashboard into the k3s cluster")]
    public static IResourceBuilder<K3sClusterResource> WithKubernetesDashboard(
        this IResourceBuilder<K3sClusterResource> builder,
        string version = "7.10.0")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithHelmRelease(
                releaseName: "kubernetes-dashboard",
                chart: "kubernetes-dashboard",
                repo: "https://kubernetes.github.io/dashboard/",
                version: version,
                @namespace: "kubernetes-dashboard")
            .WithAnnotation(new KubernetesDashboardAnnotation(version));
    }

    /// <summary>
    /// Mounts a named Docker volume at <c>/var/lib/rancher/k3s</c> to preserve cluster state
    /// across Aspire session restarts.
    /// </summary>
    /// <param name="builder">The k3s cluster resource builder.</param>
    /// <param name="volumeName">
    /// Named Docker volume to use. When <see langword="null"/> (default),
    /// a name is derived from the cluster resource name.
    /// </param>
    [AspireExport(Description = "Mounts a named Docker volume to persist k3s cluster state across restarts")]
    public static IResourceBuilder<K3sClusterResource> WithPersistentState(
        this IResourceBuilder<K3sClusterResource> builder,
        string? volumeName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var name = volumeName ?? $"aspire-k3s-{builder.Resource.Name}-data";
        return builder.WithVolume(name, "/var/lib/rancher/k3s");
    }

    /// <summary>
    /// Injects the kubeconfig from the k3s cluster into the dependent resource's environment.
    /// </summary>
    /// <typeparam name="TDestination">The dependent resource type.</typeparam>
    /// <param name="builder">The dependent resource builder.</param>
    /// <param name="source">The k3s cluster resource builder.</param>
    /// <param name="strategy">
    /// How to inject the kubeconfig. <see cref="KubeconfigInjectionStrategy.Auto"/> (default)
    /// uses <see cref="KubeconfigInjectionStrategy.FilePath"/> for local processes and
    /// <see cref="KubeconfigInjectionStrategy.EnvVar"/> for containers.
    /// </param>
    /// <param name="envPrefix">
    /// Optional prefix for injected environment variable names. Useful when referencing
    /// multiple clusters from a single resource (e.g. <c>"HUB_"</c> → <c>HUB_KUBECONFIG_DATA</c>).
    /// </param>
    [AspireExportIgnore(Reason = "Custom WithReference overload for K3sClusterResource is not ATS-compatible due to enum strategy parameter.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<K3sClusterResource> source,
        KubeconfigInjectionStrategy strategy = KubeconfigInjectionStrategy.Auto,
        string envPrefix = "")
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        var cluster = source.Resource;

        return builder.WithEnvironment(context =>
        {
            var effectiveStrategy = strategy == KubeconfigInjectionStrategy.Auto
                ? (builder.Resource is ContainerResource
                    ? KubeconfigInjectionStrategy.EnvVar
                    : KubeconfigInjectionStrategy.FilePath)
                : strategy;

            switch (effectiveStrategy)
            {
                case KubeconfigInjectionStrategy.FilePath:
                    context.EnvironmentVariables[$"{envPrefix}KUBECONFIG"] = cluster.KubeconfigFilePath;
                    break;

                case KubeconfigInjectionStrategy.EnvVar:
                    if (cluster.KubeconfigData is { } data)
                    {
                        context.EnvironmentVariables[$"{envPrefix}KUBECONFIG_DATA"] = data;
                    }
                    break;
            }
        });
    }

    // Well-known Alpine images used as ephemeral bootstrap containers when the host
    // binary is not found on PATH. Each image carries only the tool it needs.
    // Helm is version-agnostic across Kubernetes versions; kubectl is pinned per-resource
    // to match the embedded Kubernetes version and respect the ±1 minor skew policy.
    private const string HelmBootstrapImage = "alpine/helm:latest";
    private const string KubectlBootstrapImageBase = "alpine/kubectl";

    private static async Task RunWorkloadsAsync(K3sClusterResource resource, ILogger logger, CancellationToken ct)
    {
        foreach (var helm in resource.Annotations.OfType<HelmReleaseAnnotation>())
        {
            await RunHelmReleaseAsync(helm, resource, logger, ct).ConfigureAwait(false);
        }

        foreach (var kustomize in resource.Annotations.OfType<KustomizeAnnotation>())
        {
            await RunKustomizeAsync(kustomize, resource, logger, ct).ConfigureAwait(false);
        }

        if (resource.TryGetLastAnnotation<KubernetesDashboardAnnotation>(out _))
        {
            await SetupDashboardAccessAsync(resource, logger, ct).ConfigureAwait(false);
        }
    }

    private static async Task RunHelmReleaseAsync(HelmReleaseAnnotation helm, K3sClusterResource resource, ILogger logger, CancellationToken ct)
    {
        var parts = new List<string> { "helm", "install", helm.ReleaseName, helm.Chart };

        if (helm.Repo is not null)
        {
            parts.Add("--repo");
            parts.Add(helm.Repo);
        }

        if (helm.Version is not null)
        {
            parts.Add("--version");
            parts.Add(helm.Version);
        }

        parts.Add("--namespace");
        parts.Add(helm.Namespace);
        parts.Add("--create-namespace");
        parts.Add("--wait");

        if (helm.ValuesFile is not null)
        {
            parts.Add("--values");
            parts.Add(helm.ValuesFile);
        }

        var arguments = string.Join(' ', parts.Skip(1).Select(p => p.Contains(' ', StringComparison.Ordinal) ? $"\"{p}\"" : p));

        logger.LogInformation("Installing Helm release '{ReleaseName}' (chart: {Chart})...", helm.ReleaseName, helm.Chart);

        if (TryFindExecutable("helm", out var helmPath))
        {
            await RunHostProcessAsync(helmPath!, arguments, resource.KubeconfigFilePath, logger, ct).ConfigureAwait(false);
        }
        else
        {
            logger.LogInformation("'helm' not found on PATH — using bootstrap container ({Image}).", HelmBootstrapImage);
            await RunBootstrapContainerAsync($"helm {arguments}", HelmBootstrapImage, resource, logger, ct).ConfigureAwait(false);
        }
    }

    private static async Task RunKustomizeAsync(KustomizeAnnotation kustomize, K3sClusterResource resource, ILogger logger, CancellationToken ct)
    {
        var arguments = $"apply -k \"{kustomize.Path}\"";

        logger.LogInformation("Applying Kustomize overlay '{Path}'...", kustomize.Path);

        if (TryFindExecutable("kubectl", out var kubectlPath))
        {
            await RunHostProcessAsync(kubectlPath!, arguments, resource.KubeconfigFilePath, logger, ct).ConfigureAwait(false);
        }
        else
        {
            logger.LogInformation("'kubectl' not found on PATH — using bootstrap container ({Image}).", $"{KubectlBootstrapImageBase}:{resource.KubectlBootstrapTag}");
            await RunBootstrapContainerAsync($"kubectl {arguments}", $"{KubectlBootstrapImageBase}:{resource.KubectlBootstrapTag}", resource, logger, ct).ConfigureAwait(false);
        }
    }

    private static async Task SetupDashboardAccessAsync(K3sClusterResource resource, ILogger logger, CancellationToken ct)
    {
        const string serviceAccountYaml = """
            apiVersion: v1
            kind: ServiceAccount
            metadata:
              name: aspire-admin
              namespace: kubernetes-dashboard
            ---
            apiVersion: rbac.authorization.k8s.io/v1
            kind: ClusterRoleBinding
            metadata:
              name: aspire-admin
            roleRef:
              apiGroup: rbac.authorization.k8s.io
              kind: ClusterRole
              name: cluster-admin
            subjects:
            - kind: ServiceAccount
              name: aspire-admin
              namespace: kubernetes-dashboard
            """;

        // Write the YAML to the kubeconfig dir so the bootstrap container can reach it via the mounted volume.
        var rbacYamlPath = Path.Combine(resource.KubeconfigHostPath, "dashboard-rbac.yaml");
        await File.WriteAllTextAsync(rbacYamlPath, serviceAccountYaml, ct).ConfigureAwait(false);

        logger.LogInformation("Creating Kubernetes Dashboard service account...");

        const string applyArgs = "kubectl apply -f /kubeconfig/dashboard-rbac.yaml";
        const string tokenArgs = "kubectl create token aspire-admin --namespace kubernetes-dashboard --duration=87600h";

        if (TryFindExecutable("kubectl", out var kubectlPath))
        {
            await RunHostProcessAsync(kubectlPath!, $"apply -f \"{rbacYamlPath}\"", resource.KubeconfigFilePath, logger, ct).ConfigureAwait(false);
            var token = (await RunHostProcessCaptureAsync(kubectlPath!, "create token aspire-admin --namespace kubernetes-dashboard --duration=87600h", resource.KubeconfigFilePath, ct).ConfigureAwait(false)).Trim();
            LogDashboardInfo(logger, token);
        }
        else
        {
            logger.LogInformation("'kubectl' not found on PATH — using bootstrap container ({Image}).", $"{KubectlBootstrapImageBase}:{resource.KubectlBootstrapTag}");
            await RunBootstrapContainerAsync(applyArgs, $"{KubectlBootstrapImageBase}:{resource.KubectlBootstrapTag}", resource, logger, ct).ConfigureAwait(false);
            var token = (await RunBootstrapContainerCaptureAsync(tokenArgs, $"{KubectlBootstrapImageBase}:{resource.KubectlBootstrapTag}", resource, ct).ConfigureAwait(false)).Trim();
            LogDashboardInfo(logger, token);
        }

        static void LogDashboardInfo(ILogger logger, string token) =>
            logger.LogInformation(
                "Kubernetes Dashboard ready.{NewLine}" +
                "  Port-forward: kubectl port-forward -n kubernetes-dashboard svc/kubernetes-dashboard-kong-proxy 8443:443{NewLine}" +
                "  Token:        {Token}",
                Environment.NewLine, Environment.NewLine, token);
    }

    private static async Task RunBootstrapContainerAsync(string command, string image, K3sClusterResource resource, ILogger logger, CancellationToken ct)
    {
        if (!TryFindExecutable("docker", out var dockerPath))
        {
            logger.LogWarning("'docker' not found on PATH. Cannot run bootstrap container. Skipping.");
            return;
        }

        var dockerArgs = BuildBootstrapDockerArgs(command, image, resource.KubeconfigHostPath);
        await RunHostProcessAsync(dockerPath!, dockerArgs, kubeconfig: null, logger, ct).ConfigureAwait(false);
    }

    private static async Task<string> RunBootstrapContainerCaptureAsync(string command, string image, K3sClusterResource resource, CancellationToken ct)
    {
        if (!TryFindExecutable("docker", out var dockerPath))
        {
            return string.Empty;
        }

        var dockerArgs = BuildBootstrapDockerArgs(command, image, resource.KubeconfigHostPath);
        return await RunHostProcessCaptureAsync(dockerPath!, dockerArgs, kubeconfig: null, ct).ConfigureAwait(false);
    }

    private static string BuildBootstrapDockerArgs(string command, string image, string kubeconfigHostPath)
    {
        var parts = new List<string>
        {
            "run", "--rm",
            "-v", $"{kubeconfigHostPath}:/kubeconfig:ro",
            "-e", "KUBECONFIG=/kubeconfig/admin-container.yaml",
        };

        // On Linux, host.docker.internal is not automatically resolvable — add it via host-gateway.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            parts.Add("--add-host=host.docker.internal:host-gateway");
        }

        parts.Add(image);

        // Append the actual command (e.g. "helm install ..." or "kubectl apply -k ...")
        parts.AddRange(command.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return string.Join(' ', parts);
    }

    private static async Task RunHostProcessAsync(string executable, string arguments, string? kubeconfig, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (kubeconfig is not null)
        {
            psi.Environment["KUBECONFIG"] = kubeconfig;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{executable}'.");
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("'{Executable} {Arguments}' exited with code {ExitCode}.{NewLine}{Error}",
                executable, arguments, process.ExitCode, Environment.NewLine, error);
        }
        else if (!string.IsNullOrWhiteSpace(output))
        {
            logger.LogDebug("{Output}", output);
        }
    }

    private static async Task<string> RunHostProcessCaptureAsync(string executable, string arguments, string? kubeconfig, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        if (kubeconfig is not null)
        {
            psi.Environment["KUBECONFIG"] = kubeconfig;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{executable}'.");
        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return output;
    }

    private static bool TryFindExecutable(string name, out string? fullPath)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = Path.PathSeparator;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT").Split(';')
            : [""];

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }
        }

        fullPath = null;
        return false;
    }

    // k3s image tags embed the Kubernetes version: v1.32.3-k3s1, v1.31.0-k3s2, etc.
    // We extract the semver part so alpine/kubectl can be pinned to the same version,
    // staying within Kubernetes' ±1 minor version skew policy.
    private static bool TryParseK8sVersionFromK3sTag(string k3sTag, out string k8sVersion)
    {
        var match = K3sVersionPattern().Match(k3sTag);
        if (match.Success)
        {
            k8sVersion = match.Groups[1].Value; // e.g. "1.32.3"
            return true;
        }

        k8sVersion = "latest";
        return false;
    }

    [GeneratedRegex(@"^v?(\d+\.\d+\.\d+)-k3s\d+$")]
    private static partial Regex K3sVersionPattern();
}
