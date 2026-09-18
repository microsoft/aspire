// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting;

/// <summary>Deploys an authenticated Prometheus collector for health-model metrics.</summary>
public static class AzureHealthModelCollectorExtensions
{
    /// <summary>
    /// Adds a publish-only collector that scrapes a metrics endpoint and remote-writes to the health
    /// model's Azure Monitor workspace using its dedicated managed identity.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The collector's unique resource name.</param>
    /// <param name="healthModel">The health model supplying the ingestion rule and identity.</param>
    /// <param name="metricsEndpoint">The internal endpoint exposing Prometheus metrics at <c>/metrics</c>.</param>
    /// <returns>The collector container builder.</returns>
    /// <remarks>
    /// Requires an Azure Container Apps environment in publish mode. The collector has no public
    /// ingress and is kept at one replica so scraping does not stop when application traffic stops.
    /// </remarks>
    [AspireExport]
    [Experimental("ASPIREAZUREHEALTH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<ContainerResource> AddAzureContainerAppsHealthModelCollector(
        this IDistributedApplicationBuilder builder, [ResourceName] string name,
        IResourceBuilder<AzureHealthModelResource> healthModel, EndpointReference metricsEndpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(healthModel);
        ArgumentNullException.ThrowIfNull(metricsEndpoint);

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(new ContainerResource(name));
        }

        return builder.AddContainer(name, "prom/prometheus", "v3.5.0")
            .WithEnvironment("METRICS_ENDPOINT", metricsEndpoint.Property(EndpointProperty.Url))
            .WithEnvironment("REMOTE_WRITE_ENDPOINT", healthModel.Resource.RemoteWriteEndpoint)
            .WithEnvironment("AZURE_CLIENT_ID", healthModel.Resource.CollectorClientId)
            .WithEntrypoint("/bin/sh")
            .WithArgs("-ec", CollectorCommand.ReplaceLineEndings("\n"))
            .PublishAsAzureContainerApp((infrastructure, app) =>
            {
                var identityId = healthModel.Resource.CollectorIdentityId.AsProvisioningParameter(infrastructure);
                var identityKey = BicepFunction.Interpolate($"{identityId}").Compile().ToString();
                app.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned;
                app.Identity.UserAssignedIdentities[identityKey] = new UserAssignedIdentityDetails();
                app.Template.Scale.MinReplicas = 1;
                app.Template.Scale.MaxReplicas = 1;
            });
    }

    // The image includes BusyBox but not envsubst. Expand only known endpoint/client-id inputs into
    // the config at startup. They remain deferred Bicep outputs in publish artifacts, never local URLs.
    // Normalize source line endings when passing the command to the Linux shell.
    // Accepted METRICS_ENDPOINT shape: https://health-metrics.<environment-domain> or http://host:port.
    internal const string CollectorCommand = """
        : "${METRICS_ENDPOINT:?A metrics endpoint is required}"
        : "${REMOTE_WRITE_ENDPOINT:?An Azure remote-write endpoint is required}"
        : "${AZURE_CLIENT_ID:?The collector managed identity client ID is required}"
        case "$METRICS_ENDPOINT" in
          https://*) scheme=https; target="${METRICS_ENDPOINT#https://}" ;;
          http://*) scheme=http; target="${METRICS_ENDPOINT#http://}" ;;
          *) echo "METRICS_ENDPOINT must be an HTTP or HTTPS endpoint" >&2; exit 1 ;;
        esac
        target="${target%/}"
        printf '%s' "$target" | grep -Eq '^[a-zA-Z0-9._:-]+$' || { echo "Invalid metrics target" >&2; exit 1; }
        printf '%s' "$AZURE_CLIENT_ID" | grep -Eq '^[a-fA-F0-9-]+$' || { echo "Invalid managed identity client ID" >&2; exit 1; }
        printf '%s' "$REMOTE_WRITE_ENDPOINT" | grep -Eq '^https://[a-zA-Z0-9.-]+/[a-zA-Z0-9/_?=.&%-]+$' || { echo "Invalid remote-write endpoint" >&2; exit 1; }
        cat > /tmp/health-prometheus.yml <<EOF
        global:
          scrape_interval: 30s
          scrape_timeout: 10s
        scrape_configs:
          - job_name: aspire-health-model
            metrics_path: /metrics
            scheme: $scheme
            static_configs:
              - targets: ["$target"]
        remote_write:
          - url: "$REMOTE_WRITE_ENDPOINT"
            azuread:
              cloud: AzurePublic
              managed_identity:
                client_id: "$AZURE_CLIENT_ID"
        EOF
        exec /bin/prometheus --config.file=/tmp/health-prometheus.yml --storage.tsdb.path=/prometheus --storage.tsdb.retention.time=1h
        """;
}
