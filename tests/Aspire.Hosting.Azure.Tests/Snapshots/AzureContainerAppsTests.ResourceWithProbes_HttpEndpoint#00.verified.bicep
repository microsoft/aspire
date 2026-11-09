@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource api 'Microsoft.App/containerApps@2026-07-01' = {
  name: 'api'
  location: location
  properties: {
    environmentId: env_outputs_azure_container_apps_environment_id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8000
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'api'
          probes: [
            {
              failureThreshold: 3
              httpGet: {
                path: '/ready'
                port: int('8000')
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 5
              successThreshold: 1
              timeoutSeconds: 1
              type: 'Readiness'
            }
            {
              failureThreshold: 3
              httpGet: {
                path: '/health'
                port: int('8000')
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 5
              successThreshold: 1
              timeoutSeconds: 1
              type: 'Liveness'
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