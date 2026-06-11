@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param api_containerport string

@secure()
param pg_password_value string

param azpg_outputs_connectionstring string

param env_outputs_azure_container_registry_managed_identity_id string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param api_containerimage string

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--db'
          value: 'Host=pg;Port=5432;Username=postgres;Password=${pg_password_value};Database=db'
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: api_containerport
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
          image: api_containerimage
          name: 'api'
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
              value: api_containerport
            }
            {
              name: 'ConnectionStrings__db'
              secretRef: 'connectionstrings--db'
            }
            {
              name: 'ConnectionStrings__azdb'
              value: '${azpg_outputs_connectionstring};Database=azdb'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 2
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