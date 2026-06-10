@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

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
          command: [
            '/bin/sh'
          ]
          args: [
            'my'
            'args with space'
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
}