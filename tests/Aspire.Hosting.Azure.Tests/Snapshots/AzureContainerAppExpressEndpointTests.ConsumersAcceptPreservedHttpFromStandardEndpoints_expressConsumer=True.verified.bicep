@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param consumer_outputs_azure_container_apps_environment_default_domain string

param consumer_outputs_azure_container_apps_environment_id string

param producer_outputs_azure_container_apps_environment_default_domain string

resource web 'Microsoft.App/containerApps@2026-07-01' = {
  name: 'web'
  location: location
  properties: {
    environmentId: consumer_outputs_azure_container_apps_environment_id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'web'
          env: [
            {
              name: 'API_HTTP'
              value: 'http://api.${producer_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__api__http__0'
              value: 'http://api.${producer_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'API_URL'
              value: 'http://api.${producer_outputs_azure_container_apps_environment_default_domain}'
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