@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param dbsetup_containerport string

@secure()
param pg_password_value string

param env_outputs_azure_container_registry_managed_identity_id string

param env_outputs_azure_container_apps_environment_id string

param env_outputs_azure_container_registry_endpoint string

param dbsetup_containerimage string

resource dbsetup 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'dbsetup'
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
        external: false
        targetPort: dbsetup_containerport
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
          image: dbsetup_containerimage
          name: 'dbsetup'
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
              value: dbsetup_containerport
            }
            {
              name: 'ConnectionStrings__db'
              secretRef: 'connectionstrings--db'
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