@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

param a_b_c_browserorigin_value string

param a_b_c_browserorigin_1b1c82807515af0d_value string

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
              value: a_b_c_browserorigin_value
            }
            {
              name: 'SECOND'
              value: a_b_c_browserorigin_1b1c82807515af0d_value
            }
            {
              name: 'REPEATED'
              value: a_b_c_browserorigin_value
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