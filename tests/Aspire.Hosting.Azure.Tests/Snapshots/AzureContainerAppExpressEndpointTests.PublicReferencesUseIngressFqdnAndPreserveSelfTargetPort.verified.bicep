@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param web_containerimage string

param web_containerport string

param api_containerapp_outputs_azure_container_app_ingress_fqdn string

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
            '--api=${'https://${api_containerapp_outputs_azure_container_app_ingress_fqdn}'}'
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
              value: 'https://${api_containerapp_outputs_azure_container_app_ingress_fqdn}'
            }
            {
              name: 'services__api__http__0'
              value: 'https://${api_containerapp_outputs_azure_container_app_ingress_fqdn}'
            }
            {
              name: 'URL'
              value: 'https://${api_containerapp_outputs_azure_container_app_ingress_fqdn}'
            }
            {
              name: 'HOST'
              value: '${api_containerapp_outputs_azure_container_app_ingress_fqdn}'
            }
            {
              name: 'IPV4HOST'
              value: '${api_containerapp_outputs_azure_container_app_ingress_fqdn}'
            }
            {
              name: 'HOSTANDPORT'
              value: '${api_containerapp_outputs_azure_container_app_ingress_fqdn}:443'
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
              value: (toLower(enabled_value) == 'true') ? 'prefix/${'https://${api_containerapp_outputs_azure_container_app_ingress_fqdn}'}/health' : 'disabled'
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

output AZURE_CONTAINER_APP_INGRESS_FQDN string = web.properties.configuration.ingress.fqdn