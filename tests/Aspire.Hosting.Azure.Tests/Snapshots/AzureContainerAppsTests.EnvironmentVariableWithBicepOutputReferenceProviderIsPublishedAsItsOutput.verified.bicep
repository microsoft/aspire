@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param identity_outputs_clientid string

resource api 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api'
          env: [
            {
              name: 'CLIENT_ID'
              value: identity_outputs_clientid
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
}