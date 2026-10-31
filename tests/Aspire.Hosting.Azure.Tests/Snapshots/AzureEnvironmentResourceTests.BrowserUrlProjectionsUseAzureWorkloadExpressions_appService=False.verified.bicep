@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param backend_containerimage string

resource backend 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'backend'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
      registries: [
        {
          server: env_outputs_azure_container_registry_endpoint
          identity: env_outputs_azure_container_registry_managed_identity_id
        }
      ]
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: backend_containerimage
          name: 'backend'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: '8080'
            }
            {
              name: 'ORIGIN'
              value: 'https://frontend.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'ORIGINS'
              value: 'https://frontend.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'ORIGINS_JSON'
              value: '["https://frontend.${env_outputs_azure_container_apps_environment_default_domain}"]'
            }
            {
              name: 'CONFIG'
              value: 'origin=${'https://frontend.${env_outputs_azure_container_apps_environment_default_domain}'}'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}