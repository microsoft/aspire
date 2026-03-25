@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource scheduled_job 'Microsoft.App/jobs@2025-07-01' = {
  name: 'scheduled-job'
  location: location
  properties: {
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1800
      scheduleTriggerConfig: {
        cronExpression: '0 0 * * *'
      }
    }
    environmentId: env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'scheduled-job'
        }
      ]
    }
  }
  tags: {
    metadata: 'metadata-value'
  }
}