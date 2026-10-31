@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param env_outputs_azure_container_apps_environment_default_domain string

param env_outputs_azure_container_apps_environment_id string

resource scheduled_job 'Microsoft.App/jobs@2026-07-01' = {
  name: 'scheduled-job'
  tags: {
    metadata: 'metadata-value'
  }
  location: location
  properties: {
    environmentId: env_outputs_azure_container_apps_environment_id
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1800
      scheduleTriggerConfig: {
        cronExpression: '0 0 * * *'
      }
    }
    template: {
      containers: [
        {
          image: 'myimage:latest'
          name: 'scheduled-job'
        }
      ]
    }
  }
}