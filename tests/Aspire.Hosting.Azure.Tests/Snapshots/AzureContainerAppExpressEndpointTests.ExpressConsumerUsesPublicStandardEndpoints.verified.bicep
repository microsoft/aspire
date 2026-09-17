@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param express_outputs_azure_container_apps_environment_default_domain string

param express_outputs_azure_container_apps_environment_id string

param standard_outputs_azure_container_apps_environment_default_domain string

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
    environmentId: express_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'web'
          env: [
            {
              name: 'API_HTTP'
              value: 'https://api.${standard_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__api__http__0'
              value: 'https://api.${standard_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'API_HOST'
              value: 'api.${standard_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'API_AUTHORITY'
              value: 'api=api.${standard_outputs_azure_container_apps_environment_default_domain}:443'
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