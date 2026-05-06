@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param chat_app_containerimage string

param chat_app_containerport string

param proj_myproject_outputs_endpoint string

param env_outputs_azure_container_registry_endpoint string

param env_outputs_azure_container_registry_managed_identity_id string

resource chat_app 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'chat-app'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: int(chat_app_containerport)
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
          image: chat_app_containerimage
          name: 'chat-app'
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
              value: chat_app_containerport
            }
            {
              name: 'ConnectionStrings__joker-agent'
              value: '${proj_myproject_outputs_endpoint}/agents/joker-agent'
            }
            {
              name: 'JOKER_AGENT_AGENTNAME'
              value: 'joker-agent'
            }
            {
              name: 'JOKER_AGENT_PROJECTENDPOINT'
              value: proj_myproject_outputs_endpoint
            }
            {
              name: 'JOKER_AGENT_CONNECTIONSTRING'
              value: '${proj_myproject_outputs_endpoint}/agents/joker-agent'
            }
            {
              name: 'ConnectionStrings__research-agent'
              value: '${proj_myproject_outputs_endpoint}/agents/research-agent'
            }
            {
              name: 'RESEARCH_AGENT_AGENTNAME'
              value: 'research-agent'
            }
            {
              name: 'RESEARCH_AGENT_PROJECTENDPOINT'
              value: proj_myproject_outputs_endpoint
            }
            {
              name: 'RESEARCH_AGENT_CONNECTIONSTRING'
              value: '${proj_myproject_outputs_endpoint}/agents/research-agent'
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