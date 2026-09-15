@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param web_containerimage string

param web_containerport string

param enabled_value string

resource web 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: 'web'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(web_containerport)
        transport: 'http'
      }
      registries: [
        {
          server: env_outputs_azure_container_registry_endpoint
          identity: env_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: web_containerimage
          name: 'web'
          args: [
            '--api=${'https://api.${env_outputs_azure_container_apps_environment_default_domain}'}'
          ]
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
              value: web_containerport
            }
            {
              name: 'API_HTTP'
              value: 'https://api.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__api__http__0'
              value: 'https://api.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'URL'
              value: 'https://api.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'HOST'
              value: 'api.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'IPV4HOST'
              value: 'api.${env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'HOSTANDPORT'
              value: 'api.${env_outputs_azure_container_apps_environment_default_domain}:443'
            }
            {
              name: 'PORT'
              value: '443'
            }
            {
              name: 'TARGETPORT'
              value: '8080'
            }
            {
              name: 'SCHEME'
              value: 'https'
            }
            {
              name: 'TLSENABLED'
              value: 'True'
            }
            {
              name: 'CONDITIONAL'
              value: (toLower(enabled_value) == 'true') ? 'prefix/${'https://api.${env_outputs_azure_container_apps_environment_default_domain}'}/health' : 'disabled'
            }
            {
              name: 'SELF_TARGET_PORT'
              value: web_containerport
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
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