@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

param webstory_containerimage string

param webstory_containerport string

@secure()
param chat_gh_apikey_value string

resource webstory 'Microsoft.App/containerApps@2025-02-02-preview' = {
  name: 'webstory'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--chat'
          value: 'Endpoint=https://models.github.ai/inference;Key=${chat_gh_apikey_value};Model=openai/gpt-4o-mini'
        }
        {
          name: 'chat-key'
          value: chat_gh_apikey_value
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(webstory_containerport)
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
          image: webstory_containerimage
          name: 'webstory'
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
              value: webstory_containerport
            }
            {
              name: 'ConnectionStrings__chat'
              secretRef: 'connectionstrings--chat'
            }
            {
              name: 'CHAT_URI'
              value: 'https://models.github.ai/inference'
            }
            {
              name: 'CHAT_KEY'
              secretRef: 'chat-key'
            }
            {
              name: 'CHAT_MODEL'
              value: 'openai/gpt-4o-mini'
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