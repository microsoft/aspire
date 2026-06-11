@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource manual_job 'Microsoft.App/jobs@2025-07-01' = {
  name: 'manual-job'
  location: location
  properties: {
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'manual-job'
        }
      ]
    }
  }
}