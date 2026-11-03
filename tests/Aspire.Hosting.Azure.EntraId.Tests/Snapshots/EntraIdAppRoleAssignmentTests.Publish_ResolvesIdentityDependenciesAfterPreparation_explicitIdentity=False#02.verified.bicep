@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param worker_identity_outputs_id string

param worker_identity_outputs_clientid string

resource worker 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'worker'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'image:latest'
          name: 'worker'
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: worker_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
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
      '${worker_identity_outputs_id}': { }
    }
  }
}