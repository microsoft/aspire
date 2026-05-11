// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a local Kubernetes cluster running via k3s inside a Docker container.
/// </summary>
/// <remarks>
/// The cluster is started automatically when the Aspire application runs and torn down
/// when the session ends. The kubeconfig is written to a host temp directory and exposed
/// to dependent resources via <c>KUBECONFIG</c> (for local processes) or
/// <c>KUBECONFIG_DATA</c> (for containers).
/// </remarks>
public sealed class K3sClusterResource : ContainerResource, IResourceWithConnectionString
{
    internal const string ApiEndpointName = "api";

    internal K3sClusterResource(string name, string kubeconfigHostPath) : base(name)
    {
        KubeconfigHostPath = kubeconfigHostPath;
    }

    private EndpointReference? _apiEndpoint;

    /// <summary>
    /// Gets the endpoint reference for the Kubernetes API server.
    /// </summary>
    public EndpointReference ApiEndpoint => _apiEndpoint ??= new(this, ApiEndpointName);

    /// <summary>
    /// Gets the directory on the host where the kubeconfig file is written.
    /// The kubeconfig file is located at <c>{KubeconfigHostPath}/admin.yaml</c>.
    /// </summary>
    public string KubeconfigHostPath { get; }

    /// <summary>
    /// Gets the full path to the kubeconfig file on the host.
    /// Server URL is set to <c>https://localhost:{port}</c> for use by local processes and host CLIs.
    /// </summary>
    public string KubeconfigFilePath => Path.Combine(KubeconfigHostPath, "admin.yaml");

    /// <summary>
    /// Gets the full path to the container-variant kubeconfig file on the host.
    /// Server URL is set to <c>https://host.docker.internal:{port}</c> so ephemeral bootstrap
    /// containers can reach the host-mapped API server port.
    /// </summary>
    public string ContainerKubeconfigFilePath => Path.Combine(KubeconfigHostPath, "admin-container.yaml");

    /// <summary>
    /// Gets or sets the base64-encoded kubeconfig YAML with the server URL rewritten
    /// to point at <c>localhost:{allocated-port}</c>. Set by the health check on first
    /// successful readiness probe; <see langword="null"/> until then.
    /// </summary>
    public string? KubeconfigData { get; internal set; }

    /// <summary>
    /// Gets the <c>alpine/kubectl</c> image tag to use for the bootstrap container,
    /// derived from the k3s image tag so kubectl stays within Kubernetes' ±1 minor version skew policy.
    /// Falls back to <c>latest</c> when the tag cannot be parsed (e.g. <c>latest</c> or a digest).
    /// </summary>
    internal string KubectlBootstrapTag { get; init; } = "latest";

    /// <summary>
    /// Gets the connection string expression, which resolves to the kubeconfig file path on the host.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{KubeconfigFilePath}");
}
