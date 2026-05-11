// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Kubernetes;

internal sealed partial class K3sReadinessHealthCheck(K3sClusterResource resource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(resource.KubeconfigFilePath))
        {
            return HealthCheckResult.Unhealthy("Kubeconfig not yet written by k3s.");
        }

        var endpoint = resource.Annotations
            .OfType<EndpointAnnotation>()
            .FirstOrDefault(a => a.Name == K3sClusterResource.ApiEndpointName);

        var port = endpoint?.AllocatedEndpoint?.Port;
        if (port is null)
        {
            return HealthCheckResult.Unhealthy("API server port not yet allocated.");
        }

        if (resource.KubeconfigData is null)
        {
            try
            {
                var yaml = await File.ReadAllTextAsync(resource.KubeconfigFilePath, cancellationToken).ConfigureAwait(false);

                // Host variant: server URL uses localhost so local processes and host CLIs can reach the API server.
                var hostKubeconfig = RewriteServerUrl(yaml, $"https://localhost:{port}");
                await File.WriteAllTextAsync(resource.KubeconfigFilePath, hostKubeconfig, cancellationToken).ConfigureAwait(false);
                resource.KubeconfigData = Convert.ToBase64String(Encoding.UTF8.GetBytes(hostKubeconfig));

                // Container variant: server URL uses host.docker.internal so ephemeral bootstrap containers
                // (helm-kubectl, alpine/k8s, etc.) can reach the host-mapped API server port.
                // On Linux, docker run adds --add-host=host.docker.internal:host-gateway automatically.
                var containerKubeconfig = RewriteServerUrl(yaml, $"https://host.docker.internal:{port}");
                await File.WriteAllTextAsync(resource.ContainerKubeconfigFilePath, containerKubeconfig, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy($"Failed to process kubeconfig: {ex.Message}");
            }
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync(
                new Uri($"https://localhost:{port}/readyz"),
                cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"k3s API server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Cannot reach k3s API server: {ex.Message}");
        }
    }

    private static string RewriteServerUrl(string kubeconfig, string serverUrl) =>
        ServerUrlPattern().Replace(kubeconfig, $"server: {serverUrl}");

    [GeneratedRegex(@"server: https://[\w.\-]+:\d+")]
    private static partial Regex ServerUrlPattern();
}
