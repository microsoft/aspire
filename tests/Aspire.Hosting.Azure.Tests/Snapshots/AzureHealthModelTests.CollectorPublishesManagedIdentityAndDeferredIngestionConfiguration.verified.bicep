@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param health_outputs_remotewriteendpoint string

param health_outputs_collectorclientid string

param health_outputs_collectoridentityid string

resource collector 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'collector'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'prom/prometheus:v3.5.0'
          name: 'collector'
          command: [
            '/bin/sh'
          ]
          args: [
            '-ec'
            ': "\${METRICS_ENDPOINT:?A metrics endpoint is required}"\n: "\${REMOTE_WRITE_ENDPOINT:?An Azure remote-write endpoint is required}"\n: "\${AZURE_CLIENT_ID:?The collector managed identity client ID is required}"\ncase "\$METRICS_ENDPOINT" in\n  https://*) scheme=https; target="\${METRICS_ENDPOINT#https://}" ;;\n  http://*) scheme=http; target="\${METRICS_ENDPOINT#http://}" ;;\n  *) echo "METRICS_ENDPOINT must be an HTTP or HTTPS endpoint" >&2; exit 1 ;;\nesac\ntarget="\${target%/}"\nprintf \'%s\' "\$target" | grep -Eq \'^[a-zA-Z0-9._:-]+\$\' || { echo "Invalid metrics target" >&2; exit 1; }\nprintf \'%s\' "\$AZURE_CLIENT_ID" | grep -Eq \'^[a-fA-F0-9-]+\$\' || { echo "Invalid managed identity client ID" >&2; exit 1; }\nprintf \'%s\' "\$REMOTE_WRITE_ENDPOINT" | grep -Eq \'^https://[a-zA-Z0-9.-]+/[a-zA-Z0-9/_?=.&%-]+\$\' || { echo "Invalid remote-write endpoint" >&2; exit 1; }\ncat > /tmp/health-prometheus.yml <<EOF\nglobal:\n  scrape_interval: 30s\n  scrape_timeout: 10s\nscrape_configs:\n  - job_name: aspire-health-model\n    metrics_path: /metrics\n    scheme: \$scheme\n    static_configs:\n      - targets: ["\$target"]\nremote_write:\n  - url: "\$REMOTE_WRITE_ENDPOINT"\n    azuread:\n      cloud: AzurePublic\n      managed_identity:\n        client_id: "\$AZURE_CLIENT_ID"\nEOF\nexec /bin/prometheus --config.file=/tmp/health-prometheus.yml --storage.tsdb.path=/prometheus --storage.tsdb.retention.time=1h'
          ]
          env: [
            {
              name: 'METRICS_ENDPOINT'
              value: 'https://metrics.internal.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'REMOTE_WRITE_ENDPOINT'
              value: health_outputs_remotewriteendpoint
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: health_outputs_collectorclientid
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${health_outputs_collectoridentityid}': { }
    }
  }
}