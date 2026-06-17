@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource messaging 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'messaging'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 15672
        transport: 'http'
        additionalPortMappings: [
          {
            external: false
            targetPort: 5672
          }
        ]
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'rabbitmq:management'
          name: 'messaging'
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
}