@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource api 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: 'api'
  location: location
  properties: {
    environmentId: env_outputs_azure_container_apps_environment_id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api'
          env: [
            {
              name: 'SELF_URL'
              value: 'prefix/https://api.${env_outputs_azure_container_apps_environment_default_domain}'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
      }
    }
  }
}