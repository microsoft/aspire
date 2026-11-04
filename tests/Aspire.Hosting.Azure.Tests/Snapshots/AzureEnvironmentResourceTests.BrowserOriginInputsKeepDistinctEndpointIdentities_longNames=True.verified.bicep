@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_b_c_browserorigins_value string

param aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_b_5de8d567342ddf01_value string

resource consumer 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'consumer'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'image:latest'
          name: 'consumer'
          env: [
            {
              name: 'FIRST'
              value: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_b_c_browserorigins_value
            }
            {
              name: 'SECOND'
              value: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_b_5de8d567342ddf01_value
            }
            {
              name: 'REPEATED'
              value: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_b_c_browserorigins_value
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