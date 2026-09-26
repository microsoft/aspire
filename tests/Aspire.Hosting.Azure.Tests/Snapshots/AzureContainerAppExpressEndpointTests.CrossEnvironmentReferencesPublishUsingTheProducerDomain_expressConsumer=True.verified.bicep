@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param consumer_outputs_azure_container_apps_environment_default_domain string

param consumer_outputs_azure_container_apps_environment_id string

param producer_outputs_azure_container_apps_environment_default_domain string

resource web 'Microsoft.App/containerApps@2026-03-02-preview' = {
  name: 'web'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
      }
    }
    environmentId: consumer_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'web'
          env: [
            {
              name: 'API_HTTP'
              value: 'https://api.${producer_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__api__http__0'
              value: 'https://api.${producer_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'HOST'
              value: 'api.${producer_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'EARLY_HOST'
              value: 'api.${producer_outputs_azure_container_apps_environment_default_domain}'
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