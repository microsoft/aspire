@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource api 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'external-secret'
          identity: '/subscriptions/example/resourceGroups/example/providers/Microsoft.ManagedIdentity/userAssignedIdentities/example'
          keyVaultUrl: 'https://example.vault.azure.net/secrets/example'
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api'
        }
      ]
      scale: {
        minReplicas: 0
      }
    }
  }
}