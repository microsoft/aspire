@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource api1 'Microsoft.App/containerApps@2026-07-01' = {
  name: 'api1-my'
  location: location
  properties: {
    environmentId: env_outputs_azure_container_apps_environment_id
    configuration: {
      activeRevisionsMode: 'Single'
    }
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api1'
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
}