@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param consumer_outputs_azure_container_apps_environment_default_domain string

param consumer_outputs_azure_container_apps_environment_id string

param api_containerapp_outputs_azure_container_app_ingress_fqdn string

resource web 'Microsoft.App/containerApps@2025-07-01' = {
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
              value: 'https://${api_containerapp_outputs_azure_container_app_ingress_fqdn}'
            }
            {
              name: 'services__api__http__0'
              value: 'https://${api_containerapp_outputs_azure_container_app_ingress_fqdn}'
            }
            {
              name: 'HOST'
              value: api_containerapp_outputs_azure_container_app_ingress_fqdn
            }
            {
              name: 'EARLY_HOST'
              value: api_containerapp_outputs_azure_container_app_ingress_fqdn
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